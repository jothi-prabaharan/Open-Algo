using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Contracts.V1;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Options;

namespace EPTradeBot.Infrastructure.OpenAlgoApi;

/// <summary>
/// /optiongreeks and /multioptiongreeks, ported from services/option_greeks_service.py. The Black-76 forward is
/// the per-expiry synthetic future (ATM strike + call - put) and the spot only when that cannot be computed;
/// a forward given in the request, or a named underlying, skips it.
/// </summary>
public sealed partial class OpenAlgoApiService
{
    private static readonly string[] NseGreeksIndices = ["NIFTY", "BANKNIFTY", "FINNIFTY", "MIDCPNIFTY", "NIFTYNXT50", "NIFTYIT", "NIFTYPHARMA", "NIFTYBANK"];
    private static readonly string[] BseGreeksIndices = ["SENSEX", "BANKEX", "SENSEX50"];
    private static readonly string[] CurrencyUnderlyings = ["USDINR", "EURINR", "GBPINR", "JPYINR"];

    private static readonly string[] CommodityUnderlyings =
    [
        "GOLD", "GOLDM", "GOLDPETAL", "SILVER", "SILVERM", "SILVERMIC", "CRUDEOIL", "CRUDEOILM", "NATURALGAS", "COPPER", "ZINC", "LEAD", "ALUMINIUM", "NICKEL", "COTTONCANDY", "MENTHAOIL",
    ];

    /// <summary>The exchange an option's underlying is quoted on.</summary>
    internal static string GreeksUnderlyingExchange(string baseSymbol, string optionsExchange)
    {
        if (NseGreeksIndices.Contains(baseSymbol)) return "NSE_INDEX";
        if (BseGreeksIndices.Contains(baseSymbol)) return "BSE_INDEX";
        if (CurrencyUnderlyings.Contains(baseSymbol) || optionsExchange == "CDS") return "CDS";
        if (CommodityUnderlyings.Contains(baseSymbol) || optionsExchange == "MCX") return "MCX";
        if (optionsExchange.Equals("CRYPTO", StringComparison.OrdinalIgnoreCase)) return "CRYPTO";
        return "NSE";
    }

    private DateTime NowIst() => clock.GetUtcNow().UtcDateTime + EPTradeBot.Core.Time.Ist.Offset;

    private static double? Truthy(JsonNode? node) => BookFormatting.Number(node) is { } n && n != 0 ? n : null;

    /// <summary>The synthetic future of an underlying and expiry, or null when it cannot be computed.</summary>
    private async Task<double?> ResolveForwardAsync(BrokerAccount account, string baseSymbol, string underlyingExchange, DateTime expiry,
        Dictionary<(string, string, string), double?>? cache, CancellationToken ct)
    {
        var code = expiry.ToString("ddMMMyy", CultureInfo.InvariantCulture).ToUpperInvariant();
        var key = (baseSymbol, underlyingExchange, code);
        if (cache is not null && cache.TryGetValue(key, out var cached)) return cached;
        double? price = null;
        try
        {
            var answer = await SyntheticFutureAsync(account, new SyntheticFutureRequest { Underlying = baseSymbol, Exchange = underlyingExchange, ExpiryDate = code }, ct);
            if (answer.Status == 200) price = BookFormatting.Number(answer.Body["synthetic_future_price"]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            price = null;
        }

        if (cache is not null) cache[key] = price;
        return price;
    }

    public async Task<V1Result> OptionGreeksAsync(BrokerAccount account, OptionGreeksRequest r, CancellationToken ct)
    {
        string baseSymbol;
        DateTime expiry;
        try
        {
            (baseSymbol, expiry, _, _) = OptionSymbols.Parse(r.Symbol, r.Exchange, r.ExpiryTime);
        }
        catch (OptionSymbols.RefusedException e)
        {
            return V1Result.Error(500, $"Failed to get option Greeks: {e.Message}");
        }

        double spot;
        if (r.ForwardPrice is { } given && given != 0)
        {
            spot = given;
        }
        else
        {
            var spotSymbol = string.IsNullOrEmpty(r.UnderlyingSymbol) ? baseSymbol : r.UnderlyingSymbol;
            var spotExchange = string.IsNullOrEmpty(r.UnderlyingExchange) ? GreeksUnderlyingExchange(baseSymbol, r.Exchange) : r.UnderlyingExchange;
            double? forward = null;
            if (string.IsNullOrEmpty(r.UnderlyingSymbol) || r.UnderlyingSymbol == baseSymbol)
                forward = await ResolveForwardAsync(account, baseSymbol, spotExchange, expiry, null, ct);

            if (forward is { } f && f != 0)
            {
                spot = f;
            }
            else
            {
                var quote = await QuoteAsync(account, spotSymbol, spotExchange, ct);
                if (quote.Status != 200)
                    return V1Result.Error(quote.Status, $"Failed to fetch underlying price: {Text(quote.Body["message"]) ?? "Unknown error"}");
                if (Truthy(quote.Body["data"]?["ltp"]) is not { } price) return V1Result.Error(404, "Underlying LTP not available");
                spot = price;
            }
        }

        var optionQuote = await QuoteAsync(account, r.Symbol, r.Exchange, ct);
        if (optionQuote.Status != 200)
            return V1Result.Error(optionQuote.Status, $"Failed to fetch option price: {Text(optionQuote.Body["message"]) ?? "Unknown error"}");
        if (Truthy(optionQuote.Body["data"]?["ltp"]) is not { } optionPrice) return V1Result.Error(404, "Option LTP not available");

        var (_, response, code) = OptionGreeks.Calculate(r.Symbol, r.Exchange, spot, optionPrice, r.InterestRate, r.ExpiryTime, NowIst());
        return new V1Result(code, response);
    }

    public async Task<V1Result> MultiOptionGreeksAsync(BrokerAccount account, MultiOptionGreeksRequest r, CancellationToken ct)
    {
        var legs = r.Symbols;
        var results = new JsonObject?[legs.Count];
        int success = 0, failed = 0;
        var parsed = new Dictionary<int, (string Base, DateTime Expiry, double Strike, string Type)>();
        var spotKeys = new List<(string Symbol, string Exchange)>();
        var spots = new Dictionary<(string, string), double?>();
        var legSpotKey = new Dictionary<int, (string Symbol, string Exchange)>();
        var legForward = new Dictionary<int, double>();
        var synthCache = new Dictionary<(string, string, string), double?>();

        for (var i = 0; i < legs.Count; i++)
        {
            var leg = legs[i];
            try
            {
                var p = OptionSymbols.Parse(leg.Symbol, leg.Exchange, r.ExpiryTime);
                parsed[i] = p;
                var passed = leg.UnderlyingSymbol;
                var spotSymbol = string.IsNullOrEmpty(passed) ? p.Base : passed;
                var spotExchange = string.IsNullOrEmpty(leg.UnderlyingExchange) ? GreeksUnderlyingExchange(p.Base, leg.Exchange) : leg.UnderlyingExchange;
                var key = (spotSymbol, spotExchange);
                if (!spots.ContainsKey(key)) { spots[key] = null; spotKeys.Add(key); }
                legSpotKey[i] = key;
                if (string.IsNullOrEmpty(passed) || passed == p.Base)
                {
                    if (await ResolveForwardAsync(account, p.Base, spotExchange, p.Expiry, synthCache, ct) is { } fwd && fwd != 0) legForward[i] = fwd;
                }
            }
            catch (OptionSymbols.RefusedException e)
            {
                failed++;
                results[i] = new JsonObject { ["status"] = "error", ["symbol"] = leg.Symbol, ["exchange"] = leg.Exchange, ["message"] = $"Failed to parse option symbol: {e.Message}" };
            }
        }

        foreach (var key in spotKeys)
        {
            try
            {
                var quote = await QuoteAsync(account, key.Symbol, key.Exchange, ct);
                if (quote.Status == 200 && Truthy(quote.Body["data"]?["ltp"]) is { } price) spots[key] = price;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A spot that cannot be read leaves its legs to fail one by one.
            }
        }

        var wanted = new List<(string Symbol, string Exchange)>();
        for (var i = 0; i < legs.Count; i++)
        {
            if (parsed.ContainsKey(i) && !wanted.Contains((legs[i].Symbol, legs[i].Exchange))) wanted.Add((legs[i].Symbol, legs[i].Exchange));
        }

        // A symbol requested on two exchanges has no fallback exchange, so only its legs go unpriced.
        var requestedExchange = new Dictionary<string, string?>();
        foreach (var (symbol, exchange) in wanted) requestedExchange[symbol] = requestedExchange.ContainsKey(symbol) ? null : exchange;

        var prices = new Dictionary<(string, string?), double>();
        if (wanted.Count > 0)
        {
            var quoted = await QuotesForAsync(account, wanted, ct);
            if (quoted.Status == 200)
            {
                foreach (var item in (quoted.Body["results"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var symbol = Text(item["symbol"]);
                    if (symbol is null || item["data"] is not JsonObject data) continue;
                    var exchange = Text(item["exchange"]) ?? (requestedExchange.TryGetValue(symbol, out var e) ? e : null);
                    if (Truthy(data["ltp"]) is { } ltp) prices[(symbol, exchange)] = ltp;
                }
            }
        }

        for (var i = 0; i < legs.Count; i++)
        {
            if (!parsed.TryGetValue(i, out var p)) continue;
            var leg = legs[i];
            var key = legSpotKey[i];
            double? spot = legForward.TryGetValue(i, out var fwd) ? fwd : spots.GetValueOrDefault(key);
            if (spot is not { } spotPrice || spotPrice == 0)
            {
                failed++;
                results[i] = new JsonObject { ["status"] = "error", ["symbol"] = leg.Symbol, ["exchange"] = leg.Exchange, ["message"] = $"Failed to fetch underlying price for {key.Symbol}" };
                continue;
            }

            if (!prices.TryGetValue((leg.Symbol, leg.Exchange), out var optionPrice))
            {
                failed++;
                results[i] = new JsonObject { ["status"] = "error", ["symbol"] = leg.Symbol, ["exchange"] = leg.Exchange, ["message"] = "Option LTP not available" };
                continue;
            }

            var (ok, response, _) = OptionGreeks.Calculate(leg.Symbol, leg.Exchange, spotPrice, optionPrice, r.InterestRate, r.ExpiryTime, NowIst());
            if (ok)
            {
                success++;
            }
            else if (OptionGreeks.IsExpiredResponse(response))
            {
                response = OptionGreeks.ExpiredResponse(leg.Symbol, leg.Exchange, p.Base, p.Expiry, p.Strike, p.Type, spotPrice, optionPrice, r.InterestRate);
                success++;
            }
            else
            {
                failed++;
                response.TryAdd("symbol", leg.Symbol);
                response.TryAdd("exchange", leg.Exchange);
            }

            results[i] = response;
        }

        var status = failed == 0 ? "success" : success > 0 ? "partial" : "error";
        var body = new JsonObject
        {
            ["status"] = status,
            ["data"] = new JsonArray(results.Select(x => (JsonNode?)x).ToArray()),
            ["summary"] = new JsonObject { ["total"] = legs.Count, ["success"] = success, ["failed"] = failed },
        };
        if (failed > 0)
        {
            var messages = new List<string>();
            foreach (var x in results)
            {
                if (Text(x?["status"]) == "error" && Text(x?["message"]) is { Length: > 0 } m && !messages.Contains(m)) messages.Add(m);
            }

            if (messages.Count > 0)
            {
                var joined = string.Join("; ", messages.Take(3));
                body["message"] = status == "error" ? $"All option Greeks calculations failed: {joined}" : $"{failed} option Greeks calculation(s) failed: {joined}";
            }
        }

        return new V1Result(200, body);
    }
}
