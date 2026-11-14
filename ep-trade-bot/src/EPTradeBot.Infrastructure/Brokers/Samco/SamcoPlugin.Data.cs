using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Samco;

/// <summary>
/// Samco market data (broker/samco/api/data.py): /quote/getQuote, /quote/indexQuote, POST /marketDepth and POST /quote/multiQuote (25 instruments a call, one call every 0.2 s). MCX derivatives are asked for under
/// the exchange code MFO, which the master contract keeps as the broker exchange. A read that is rate limited or answered with a server error is tried again up to three times, one, two and four seconds apart.
/// </summary>
public sealed partial class SamcoPlugin
{
    private static readonly Dictionary<string, string> IndexNames = new(StringComparer.Ordinal)
    {
        ["NIFTY"] = "NIFTY 50", ["BANKNIFTY"] = "NIFTY BANK", ["FINNIFTY"] = "NIFTY FIN SERVICE", ["MIDCPNIFTY"] = "NIFTY MID SELECT", ["NIFTYNXT50"] = "NIFTY NEXT 50", ["INDIAVIX"] = "INDIA VIX", ["SENSEX"] = "SENSEX", ["BANKEX"] = "BANKEX",
    };

    private static bool IsIndex(string exchange) => exchange is "NSE_INDEX" or "BSE_INDEX";

    /// <summary>urllib.parse.quote: everything but letters, digits and _.-~/ is percent-encoded.</summary>
    private static string Escape(string text) => Uri.EscapeDataString(text).Replace("%2F", "/", StringComparison.Ordinal);

    private static string ApiExchange(SymbolRecord record) => !string.IsNullOrEmpty(record.BrokerExchange) ? record.BrokerExchange : record.Exchange;

    private sealed record Quote(double Bid, double Ask, double Open, double High, double Low, double Ltp, double PrevClose, long Volume, long Oi);

    /// <summary>A read of market data: tried again when Samco is rate limiting or failing, and an answer that is not a JSON object is a refusal.</summary>
    private async Task<JsonObject> DataAsync(BrokerSession session, HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var (status, answer) = await SendAsync(session, method, path, body, cancellationToken);
            if (status == 403) throw new BrokerException($"{DisplayName} did not accept the session. Sign in to {DisplayName} again, then try again.", 500);
            if ((status == 429 || status >= 500) && attempt < 3)
            {
                await Task.Delay(sessions.RetryBackoff(Name, TimeSpan.FromSeconds(1 << attempt)), cancellationToken);
                continue;
            }

            if (status == 429) throw new BrokerException($"{DisplayName} is limiting how often it can be asked. Wait a moment, then try again.", 500);
            if (status >= 500) throw new BrokerException($"{DisplayName} could not answer just now. Try again in a few minutes.", 500);
            if (answer.Count == 0) throw new BrokerException($"{DisplayName} sent an answer that could not be read. Try again in a moment.", 500);
            return answer;
        }
    }

    private static string IndexName(SymbolRecord record) => !string.IsNullOrEmpty(record.BrokerSymbol) ? record.BrokerSymbol : IndexNames.GetValueOrDefault(record.Symbol.ToUpperInvariant(), record.Symbol);

    private async Task<Quote> IndexQuoteAsync(BrokerSession session, SymbolRecord record, CancellationToken cancellationToken)
    {
        var answer = await DataAsync(session, HttpMethod.Get, $"/quote/indexQuote?indexName={Escape(IndexName(record))}", null, cancellationToken);
        if (!Succeeded(answer)) throw new BrokerException($"{DisplayName} could not give the index quote: {Reason(answer, "no reason was given.")}", 500);
        if ((answer["indexDetails"] as JsonArray)?.FirstOrDefault() is not JsonObject quote) throw new BrokerException($"{DisplayName} sent no index data for {record.Symbol}.", 500);
        return new Quote(0, 0, SafeFloat(quote["openValue"]), SafeFloat(quote["highValue"]), SafeFloat(quote["lowValue"]), SafeFloat(quote["spotPrice"]), SafeFloat(quote["closeValue"]), SafeInt(quote["totalTradedVolume"]), 0);
    }

    private async Task<Quote> QuoteAsync(BrokerSession session, SymbolRecord record, CancellationToken cancellationToken)
    {
        if (IsIndex(record.Exchange)) return await IndexQuoteAsync(session, record, cancellationToken);
        var exchange = ApiExchange(record);
        var path = $"/quote/getQuote?symbolName={Escape(record.BrokerSymbol)}" + (exchange is not ("" or "NSE") ? $"&exchange={exchange}" : "");
        var answer = await DataAsync(session, HttpMethod.Get, path, null, cancellationToken);
        if (!Succeeded(answer)) throw new BrokerException($"{DisplayName} could not give the quote: {Reason(answer, "no reason was given.")}", 500);
        if (answer["quoteDetails"] is not JsonObject { Count: > 0 } quote) throw new BrokerException($"{DisplayName} sent no quote for {record.Symbol}.", 500);
        var bid = (quote["bestBids"] as JsonArray)?.FirstOrDefault() as JsonObject;
        var ask = (quote["bestAsks"] as JsonArray)?.FirstOrDefault() as JsonObject;
        return new Quote(bid is null ? 0 : SafeFloat(bid["price"]), ask is null ? 0 : SafeFloat(ask["price"]), SafeFloat(quote["openValue"]), SafeFloat(quote["highValue"]), SafeFloat(quote["lowValue"]),
            SafeFloat(quote["lastTradedPrice"]), SafeFloat(quote["previousClose"]), SafeInt(quote["totalTradedVolume"]), SafeInt(quote["openInterest"]));
    }

    private static JsonObject QuoteObject(Quote q) => new()
    {
        ["bid"] = q.Bid, ["ask"] = q.Ask, ["open"] = q.Open, ["high"] = q.High, ["low"] = q.Low, ["ltp"] = q.Ltp, ["prev_close"] = q.PrevClose, ["volume"] = q.Volume, ["oi"] = q.Oi,
    };

    private async Task<SymbolRecord> InstrumentAsync(string symbol, string exchange, CancellationToken cancellationToken) =>
        await symbols.FindAsync(Name, symbol, exchange, cancellationToken) ?? throw new BrokerException($"{symbol} on {exchange} is not in the {DisplayName} master contract. Check the symbol, or download the master contract again.", 400);

    public async Task<JsonObject> GetQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken) =>
        QuoteObject(await QuoteAsync(session, await InstrumentAsync(symbol, exchange, cancellationToken), cancellationToken));

    private static JsonArray Levels(JsonNode? list, string priceKey, string sizeKey)
    {
        var rows = (list as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var result = new JsonArray();
        for (var i = 0; i < 5; i++)
            result.Add(i < rows.Count ? new JsonObject { ["price"] = SafeFloat(rows[i][priceKey]), ["quantity"] = SafeInt(rows[i][sizeKey]) } : new JsonObject { ["price"] = 0, ["quantity"] = 0 });
        return result;
    }

    public async Task<JsonObject> GetDepthAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var record = await InstrumentAsync(symbol, exchange, cancellationToken);
        if (IsIndex(exchange))
        {
            var index = await IndexQuoteAsync(session, record, cancellationToken);
            return new JsonObject
            {
                ["bids"] = Levels(null, "", ""), ["asks"] = Levels(null, "", ""), ["high"] = index.High, ["low"] = index.Low, ["ltp"] = index.Ltp, ["ltq"] = 0, ["open"] = index.Open, ["prev_close"] = index.PrevClose,
                ["volume"] = index.Volume, ["oi"] = 0, ["totalbuyqty"] = 0, ["totalsellqty"] = 0,
            };
        }

        var payload = new JsonObject { ["symbolName"] = record.BrokerSymbol };
        if (ApiExchange(record) is { Length: > 0 } and not "NSE") payload["exchange"] = ApiExchange(record);
        var answer = await DataAsync(session, HttpMethod.Post, "/marketDepth", payload, cancellationToken);
        if (!Succeeded(answer)) throw new BrokerException($"{DisplayName} could not give the market depth: {Reason(answer, "no reason was given.")}", 500);
        if ((answer["MarketDepthDetails"] as JsonObject)?["marketDepth"] is not JsonObject { Count: > 0 } depth) throw new BrokerException($"{DisplayName} sent no market depth for {symbol}.", 500);

        Quote quote;
        try
        {
            quote = await QuoteAsync(session, record, cancellationToken);
        }
        catch (BrokerException)
        {
            quote = new Quote(0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        return new JsonObject
        {
            ["bids"] = Levels(depth["bestFiveBid"], "bidPrice", "bidSize"), ["asks"] = Levels(depth["bestFiveAsk"], "askPrice", "askSize"), ["high"] = quote.High, ["low"] = quote.Low, ["ltp"] = quote.Ltp, ["ltq"] = 0,
            ["open"] = quote.Open, ["prev_close"] = quote.PrevClose, ["volume"] = quote.Volume, ["oi"] = quote.Oi, ["totalbuyqty"] = SafeInt(depth["tBuyQty"]), ["totalsellqty"] = SafeInt(depth["tSellQty"]),
        };
    }

    private const int BatchSize = 25;

    public async Task<JsonArray> GetMultiQuotesAsync(BrokerSession session, IReadOnlyList<SymbolRecord> instruments, CancellationToken cancellationToken)
    {
        var regular = instruments.Where(i => !IsIndex(i.Exchange)).ToList();
        var results = new JsonArray();
        for (var i = 0; i < regular.Count; i += BatchSize)
        {
            foreach (var item in await BatchAsync(session, regular.Skip(i).Take(BatchSize).ToList(), cancellationToken)) results.Add(item);
            if (i + BatchSize < regular.Count) await Task.Delay(sessions.RetryBackoff(Name, TimeSpan.FromMilliseconds(200)), cancellationToken);
        }

        foreach (var index in instruments.Where(i => IsIndex(i.Exchange)))
        {
            try
            {
                results.Add(new JsonObject { ["symbol"] = index.Symbol, ["exchange"] = index.Exchange, ["data"] = QuoteObject(await IndexQuoteAsync(session, index, cancellationToken)) });
            }
            catch (BrokerException ex)
            {
                results.Add(new JsonObject { ["symbol"] = index.Symbol, ["exchange"] = index.Exchange, ["error"] = ex.Message });
            }
        }

        return results;
    }

    private async Task<List<JsonObject>> BatchAsync(BrokerSession session, List<SymbolRecord> batch, CancellationToken cancellationToken)
    {
        var payload = new JsonObject();
        foreach (var record in batch)
        {
            var exchange = ApiExchange(record);
            if (payload[exchange] is not JsonArray list) payload[exchange] = list = [];
            list.Add(record.BrokerSymbol);
        }

        var answer = await DataAsync(session, HttpMethod.Post, "/quote/multiQuote", payload, cancellationToken);
        if (!Succeeded(answer)) throw new BrokerException($"{DisplayName} could not give the quotes: {Reason(answer, "no reason was given.")}", 500);

        var byToken = new Dictionary<string, JsonObject>();
        var byName = new Dictionary<string, JsonObject>();
        foreach (var quote in (answer["multiQuotes"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            if (Truthy(quote["symbol"])) byToken[Str(quote["symbol"])!] = quote;
            if (Truthy(quote["exchange"]))
            {
                var exchange = Str(quote["exchange"]);
                if (Truthy(quote["tradingSymbol"])) byName[$"{exchange}:{Str(quote["tradingSymbol"])}"] = quote;
                if (Truthy(quote["symbolName"])) byName.TryAdd($"{exchange}:{Str(quote["symbolName"])}", quote);
            }
        }

        var results = new List<JsonObject>();
        foreach (var record in batch)
        {
            JsonObject? quote = !string.IsNullOrEmpty(record.Token) && byToken.TryGetValue(record.Token, out var hit) ? hit : null;
            quote ??= byName.GetValueOrDefault($"{ApiExchange(record)}:{record.BrokerSymbol}") ?? byName.GetValueOrDefault($"{record.Exchange}:{record.BrokerSymbol}");
            if (quote is null)
            {
                results.Add(new JsonObject { ["symbol"] = record.Symbol, ["exchange"] = record.Exchange, ["error"] = "No quote data available" });
                continue;
            }

            results.Add(new JsonObject
            {
                ["symbol"] = record.Symbol, ["exchange"] = record.Exchange, ["data"] = new JsonObject
                {
                    ["bid"] = SafeFloat(quote["bidPrice"]), ["ask"] = SafeFloat(quote["askPrice"]), ["bid_qty"] = SafeInt(quote["bidSize"]), ["ask_qty"] = SafeInt(quote["askSize"]), ["open"] = SafeFloat(quote["open"]),
                    ["high"] = SafeFloat(quote["high"]), ["low"] = SafeFloat(quote["low"]), ["ltp"] = SafeFloat(quote["lastTradePrice"]), ["prev_close"] = SafeFloat(quote["previousClose"]), ["volume"] = SafeInt(quote["totalTradeVolume"]),
                    ["oi"] = SafeInt(quote["openInterest"]),
                },
            });
        }

        return results;
    }
}
