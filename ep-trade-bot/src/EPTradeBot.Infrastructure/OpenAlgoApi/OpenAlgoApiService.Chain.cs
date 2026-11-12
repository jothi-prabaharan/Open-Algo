using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Contracts.V1;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.Legacy.Core;
using EPTradeBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EPTradeBot.Infrastructure.OpenAlgoApi;

/// <summary>
/// /multiquotes, /syntheticfuture and /optionchain, ported from services/quotes_service.py
/// (get_multiquotes), synthetic_future_service.py and option_chain_service.py. Several quotes
/// are asked of the broker in its own batched call where it has one, else one at a time.
/// </summary>
public sealed partial class OpenAlgoApiService
{
    private const string ValidExchanges = "NSE, NFO, CDS, BSE, BFO, BCD, MCX, NCDEX, NCO, NSE_INDEX, BSE_INDEX, MCX_INDEX, GLOBAL_INDEX, CRYPTO";

    public Task<V1Result> MultiQuotesAsync(BrokerAccount account, MultiQuotesRequest r, CancellationToken ct) =>
        QuotesForAsync(account, r.Symbols.Select(s => (s.Symbol, s.Exchange)).ToList(), ct);

    /// <summary>
    /// Quotes for several instruments, as /multiquotes answers. A symbol the master contract
    /// lacks is answered with its reason ahead of the rest; when none is known, the request fails.
    /// </summary>
    internal async Task<V1Result> QuotesForAsync(BrokerAccount account, IReadOnlyList<(string Symbol, string Exchange)> wanted, CancellationToken ct)
    {
        if (!Broker<IBrokerQuotes>(account, out var quotes, out var session, out var missing)) return missing!;
        var invalid = new JsonArray();
        var valid = new List<SymbolRecord>();
        foreach (var (symbol, exchange) in wanted)
        {
            string? error = null;
            SymbolRecord? record = null;
            if (string.IsNullOrEmpty(symbol) || string.IsNullOrEmpty(exchange)) error = "Missing symbol or exchange in request";
            else if (!ValidExchanges.Split(", ").Contains(exchange.ToUpperInvariant())) error = $"Invalid exchange '{exchange}'. Must be one of: {ValidExchanges}";
            else if ((record = await symbols.FindAsync(quotes!.Name, symbol, exchange.ToUpperInvariant(), ct)) is null)
                error = $"Symbol '{symbol}' not found for exchange '{exchange}'. Please verify the symbol name and ensure master contracts are downloaded.";
            if (error is null) valid.Add(record!);
            else invalid.Add(new JsonObject { ["symbol"] = symbol, ["exchange"] = exchange, ["error"] = error });
        }

        if (valid.Count == 0)
        {
            return new V1Result(400, new JsonObject
            {
                ["status"] = "error", ["message"] = invalid.Count > 0 ? Text(invalid[0]!["error"]) : "No valid symbols provided", ["invalid_symbols"] = invalid,
            });
        }

        return await Guarded(async () =>
        {
            var results = new JsonArray(invalid.Select(i => i!.DeepClone()).ToArray());
            if (quotes is IBrokerMultiQuotes batched)
            {
                foreach (var item in await batched.GetMultiQuotesAsync(session!, valid, ct)) results.Add(item?.DeepClone());
            }
            else
            {
                // No batched call at this broker (Delta Exchange): one quote at a time, a failure named per symbol.
                foreach (var record in valid)
                {
                    try
                    {
                        results.Add(new JsonObject { ["symbol"] = record.Symbol, ["exchange"] = record.Exchange, ["data"] = await quotes!.GetQuoteAsync(session!, record.Symbol, record.Exchange, ct) });
                    }
                    catch (BrokerException ex) when (ex.HttpStatus != 429)
                    {
                        results.Add(new JsonObject { ["symbol"] = record.Symbol, ["exchange"] = record.Exchange, ["error"] = ex.Message });
                    }
                }
            }

            return new V1Result(200, new JsonObject { ["status"] = "success", ["results"] = results });
        });
    }

    // ---- synthetic future ------------------------------------------------------------------------------------

    public async Task<V1Result> SyntheticFutureAsync(BrokerAccount account, SyntheticFutureRequest r, CancellationToken ct)
    {
        var (call, callError) = await ResolveOptionAsync(account, r.Underlying, r.Exchange, r.ExpiryDate, null, "ATM", "CE", null, ct);
        if (callError is not null) return callError;

        // The ATM strike, read back from the call's symbol as OpenAlgo does (NIFTY30DEC3024000CE is 24000).
        var rest = ReplaceFirst(ReplaceFirst(call!.Symbol, r.Underlying.ToUpperInvariant()), r.ExpiryDate.ToUpperInvariant()).Replace("CE", "").Replace("PE", "");
        if (!double.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out var strike))
            return V1Result.Error(500, $"Failed to parse strike from option symbol: {call.Symbol}");

        var (put, putError) = await ResolveOptionAsync(account, r.Underlying, r.Exchange, r.ExpiryDate, null, "ATM", "PE", call.UnderlyingLtp, ct);
        if (putError is not null) return putError;

        var quotes = await QuotesForAsync(account, [(call.Symbol, call.Exchange), (put!.Symbol, put.Exchange)], ct);
        if (quotes.Status != 200) return quotes;
        double? callLtp = null, putLtp = null;
        foreach (var item in (quotes.Body["results"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (Text(item["symbol"]) == call.Symbol) callLtp = BookFormatting.Number(item["data"]?["ltp"]);
            else if (Text(item["symbol"]) == put.Symbol) putLtp = BookFormatting.Number(item["data"]?["ltp"]);
        }

        if (callLtp is null) return V1Result.Error(500, $"Could not fetch LTP for Call option: {call.Symbol}");
        if (putLtp is null) return V1Result.Error(500, $"Could not fetch LTP for Put option: {put.Symbol}");
        return new V1Result(200, new JsonObject
        {
            ["status"] = "success", ["underlying"] = r.Underlying, ["underlying_ltp"] = call.UnderlyingLtp?.DeepClone(), ["expiry"] = r.ExpiryDate,
            ["atm_strike"] = strike, ["synthetic_future_price"] = BookFormatting.RoundPy(strike + callLtp.Value - putLtp.Value, 2),
        });
    }

    private static string ReplaceFirst(string text, string part)
    {
        var at = part.Length == 0 ? -1 : text.IndexOf(part, StringComparison.Ordinal);
        return at < 0 ? text : text.Remove(at, part.Length);
    }

    // ---- option chain ----------------------------------------------------------------------------------------

    private static readonly string[] QuoteFieldNames = ["ltp", "bid", "ask", "bid_qty", "ask_qty", "open", "high", "low", "prev_close", "volume", "oi"];

    public async Task<V1Result> OptionChainAsync(BrokerAccount account, OptionChainRequest r, CancellationToken ct)
    {
        var (baseSymbol, embedded) = ParseUnderlying(r.Underlying);
        var expiry = embedded ?? r.ExpiryDate;
        if (string.IsNullOrEmpty(expiry)) return V1Result.Error(400, "Expiry date is required.");

        var upper = r.Exchange.ToUpperInvariant();
        var quoteExchange = QuoteExchange(baseSymbol, r.Exchange);
        string quoteSymbol;
        await using (var db = await contexts.CreateDbContextAsync(ct))
        {
            if (upper == "CRYPTO")
            {
                quoteExchange = upper;
                quoteSymbol = await PerpetualAsync(db, account, baseSymbol, upper, ct) ?? "";
                if (quoteSymbol.Length == 0) return V1Result.Error(404, $"No perpetual futures found for {baseSymbol} on {r.Exchange}");
            }
            else if (NoSpotExchanges.Contains(upper))
            {
                quoteExchange = upper;
                quoteSymbol = await NearMonthFutureAsync(db, account, baseSymbol, upper, ct) ?? "";
                if (quoteSymbol.Length == 0)
                {
                    return V1Result.Error(404, $"No unexpired futures found for {baseSymbol} on {upper}. {upper} options are priced against the near-month future, "
                        + "which this product does not currently have.");
                }
            }
            else
            {
                quoteSymbol = embedded is not null ? baseSymbol : r.Underlying;
            }
        }

        var quote = await QuoteAsync(account, quoteSymbol, quoteExchange, ct);
        if (quote.Status != 200) return V1Result.Error(quote.Status, $"Failed to fetch LTP for {quoteSymbol}: {Text(quote.Body["message"]) ?? "Unknown error"}");
        var underlyingData = quote.Body["data"] as JsonObject ?? [];
        var ltpNode = underlyingData["ltp"];
        if (ltpNode is null) return V1Result.Error(500, $"Could not determine LTP for {quoteSymbol}");

        var optionsExchange = OptionExchange(quoteExchange);
        var strikes = await ListedStrikesAsync(account, baseSymbol, expiry, "CE", optionsExchange, ct);
        if (strikes.Count == 0)
            return V1Result.Error(404, $"No strikes found for {baseSymbol} expiring {expiry}. Please check expiry date or update master contract.");
        if (BookFormatting.Number(ltpNode) is not { } ltp || !double.IsFinite(ltp)) return V1Result.Error(500, "Failed to determine ATM strike");
        var atm = strikes.MinBy(s => Math.Abs(s - ltp));

        var rows = await ChainRowsAsync(account, baseSymbol, expiry, optionsExchange, Ladder(strikes, atm, r.StrikeCount), ct);
        var toFetch = rows.SelectMany(x => new[] { x.Ce, x.Pe }).Where(l => l.Row is not null).Select(l => (l.Symbol, optionsExchange)).ToList();
        if (toFetch.Count == 0) return V1Result.Error(404, "No valid option symbols found for the given parameters");

        // Fyers answers the strikes around ATM in one call; any other broker, or a wider chain, is quoted symbol by symbol in batches.
        var quotesBySymbol = new Dictionary<string, JsonObject>();
        if (upper != "CRYPTO" && r.StrikeCount is <= 50 && registry.Get<IBrokerOptionChain>(account.Broker) is { } chainCall && sessions.Create(account) is { } session
            && await symbols.FindAsync(chainCall.Name, quoteSymbol, quoteExchange, ct) is { } underlying)
        {
            var native = await chainCall.GetOptionChainAsync(session, underlying, r.StrikeCount.Value + 2, ct);
            foreach (var row in rows)
            {
                if (row.Ce.Row is not null && native.TryGetValue((row.Strike, "CE"), out var ce)) quotesBySymbol[row.Ce.Symbol] = ce;
                if (row.Pe.Row is not null && native.TryGetValue((row.Strike, "PE"), out var pe)) quotesBySymbol[row.Pe.Symbol] = pe;
            }
        }

        if (quotesBySymbol.Count == 0)
        {
            var fetched = await QuotesForAsync(account, toFetch, ct);
            // OpenAlgo answers a failed quote call with a chain of zeros marked success; the failure is reported here instead.
            if (fetched.Status != 200) return V1Result.Error(fetched.Status, $"Option quotes could not be read: {Text(fetched.Body["message"]) ?? "Unknown error"}");
            foreach (var item in (fetched.Body["results"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (Text(item["symbol"]) is not { } s) continue;
                if (item["data"] is JsonObject d) quotesBySymbol[s] = d;
                else if (item["error"] is null) quotesBySymbol[s] = item;
            }
        }

        var chain = new JsonArray();
        foreach (var row in rows)
        {
            chain.Add(new JsonObject
            {
                ["strike"] = row.Strike, ["ce"] = Leg(row.Ce, quotesBySymbol), ["pe"] = Leg(row.Pe, quotesBySymbol),
            });
        }

        var expiryTimestamp = ExpiryTimestamp(expiry, optionsExchange);
        double? forward = null;
        if (r.WithGreeks)
        {
            // Black-76 prices off the forward, and the money pair already in hand gives it by put-call parity.
            forward = EPTradeBot.Core.Options.ChainGreeks.ForwardFromChain(chain, atm, ltp);
            var expiryIst = expiryTimestamp is { } ts ? DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime + EPTradeBot.Core.Time.Ist.Offset : (DateTime?)null;
            EPTradeBot.Core.Options.ChainGreeks.Attach(chain, expiryIst, forward.Value, r.InterestRate, clock.GetUtcNow().UtcDateTime + EPTradeBot.Core.Time.Ist.Offset);
        }

        return new V1Result(200, new JsonObject
        {
            ["status"] = "success", ["underlying"] = baseSymbol, ["underlying_symbol"] = quoteSymbol, ["underlying_exchange"] = quoteExchange,
            ["underlying_ltp"] = ltpNode.DeepClone(), ["underlying_prev_close"] = underlyingData.ContainsKey("prev_close") ? underlyingData["prev_close"]?.DeepClone() : 0,
            ["expiry_date"] = expiry, ["expiry_ts"] = expiryTimestamp, ["server_ts"] = clock.GetUtcNow().ToUnixTimeSeconds(),
            ["atm_strike"] = atm, ["quotes_included"] = true, ["greeks_included"] = r.WithGreeks, ["forward_price"] = forward, ["chain"] = chain,
        });
    }

    private sealed record ChainLeg(string Symbol, string Label, SymToken? Row);

    private sealed record ChainRow(double Strike, ChainLeg Ce, ChainLeg Pe);

    /// <summary>The strikes around ATM, labelled per side: below ATM a call is in the money and a put out of it.</summary>
    internal static List<(double Strike, string Ce, string Pe)> Ladder(IReadOnlyList<double> strikes, double atm, int? count)
    {
        var index = -1;
        for (var i = 0; i < strikes.Count; i++)
        {
            if (strikes[i] == atm) { index = i; break; }
        }

        var selected = count is { } n ? strikes.Skip(Math.Max(0, index - n)).Take(Math.Min(strikes.Count, index + n + 1) - Math.Max(0, index - n)) : strikes;
        return selected.Select(s =>
        {
            var position = Math.Abs(strikes.ToList().IndexOf(s) - index);
            return s == atm ? (s, "ATM", "ATM") : s < atm ? (s, $"ITM{position}", $"OTM{position}") : (s, $"OTM{position}", $"ITM{position}");
        }).ToList();
    }

    private async Task<List<ChainRow>> ChainRowsAsync(BrokerAccount account, string baseSymbol, string expiry, string exchange,
        List<(double Strike, string Ce, string Pe)> ladder, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var dashed = expiry.Length >= 7 ? $"{expiry[..2]}-{expiry[2..5]}-{expiry[5..]}".ToUpperInvariant() : expiry.ToUpperInvariant();
        var rows = new List<ChainRow>();
        foreach (var (strike, ceLabel, peLabel) in ladder)
        {
            async Task<ChainLeg> LegAsync(string type, string label)
            {
                if (exchange == "CRYPTO")
                {
                    var prefix = baseSymbol.ToUpperInvariant();
                    var row = await Master(db, account).Where(s => s.Symbol.StartsWith(prefix) && s.Expiry == dashed && s.Strike == strike && s.Instrumenttype == type && s.Exchange == "CRYPTO")
                        .OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
                    var whole = strike == Math.Truncate(strike) ? ((long)strike).ToString(CultureInfo.InvariantCulture) : strike.ToString("R", CultureInfo.InvariantCulture);
                    return new ChainLeg(row?.Symbol ?? $"{baseSymbol}-UNKNOWN-{whole}-{type}", label, row);
                }

                var symbol = OptionSymbol(baseSymbol, expiry, strike, type);
                return new ChainLeg(symbol, label, await Master(db, account).Where(s => s.Symbol == symbol && s.Exchange == exchange).OrderBy(s => s.Id).FirstOrDefaultAsync(ct));
            }

            rows.Add(new ChainRow(strike, await LegAsync("CE", ceLabel), await LegAsync("PE", peLabel)));
        }

        return rows;
    }

    private static JsonNode? Leg(ChainLeg leg, Dictionary<string, JsonObject> quotes)
    {
        if (leg.Row is null) return null;
        quotes.TryGetValue(leg.Symbol, out var q);
        var o = new JsonObject { ["symbol"] = leg.Symbol, ["label"] = leg.Label };
        foreach (var field in QuoteFieldNames) o[field] = q is not null && q.ContainsKey(field) ? q[field]?.DeepClone() : 0;
        o["lotsize"] = leg.Row.Lotsize;
        o["tick_size"] = leg.Row.TickSize is { } t ? JsonValue.Create(t) : null;
        return o;
    }

    /// <summary>The expiry instant: 15:30 IST for NSE and BSE derivatives, 12:30 for CDS, 23:30 for MCX (OpenAlgo's EXCHANGE_EXPIRY_TIMES).</summary>
    internal static long? ExpiryTimestamp(string expiry, string exchange)
    {
        // DDMMMYY, the year always in 20YY, as OpenAlgo reads it (a two-digit year parser would put 50 to 99 in the 1900s).
        var m = System.Text.RegularExpressions.Regex.Match(expiry.Trim().ToUpperInvariant(), "^([0-9]{2})([A-Z]{3})([0-9]{2})$");
        if (!m.Success) return null;
        var month = Array.IndexOf(["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"], m.Groups[2].Value) + 1;
        var year = 2000 + int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        var day = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        if (month == 0 || day < 1 || day > DateTime.DaysInMonth(year, month)) return null;
        var (hour, minute) = exchange switch { "MCX" => (23, 30), "CDS" => (12, 30), _ => (15, 30) };
        return new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.FromHours(5.5)).ToUnixTimeSeconds();
    }
}
