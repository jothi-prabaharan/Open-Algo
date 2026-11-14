using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.IiflCapital;

/// <summary>
/// IIFL Capital market data (broker/iiflcapital/api/data.py). Quotes are POST /marketdata/marketquotes (several instruments in one call), depth is /marketdata/marketdepth, and open interest is a
/// separate single-instrument call, /marketdata/openinterest, made for futures and options only and best effort (zero when it fails). The parsing is deliberately tolerant: IIFL's rows and containers come
/// in several shapes (plain fields, other names for them, or an XTS-style touchline), and each is read as OpenAlgo reads it.
/// </summary>
public sealed partial class IiflCapitalPlugin
{
    private static readonly HashSet<string> OiExchanges = ["NFO", "BFO", "CDS", "BCD", "MCX"];
    private static readonly string[] ErrorStatuses = ["error", "failed", "failure", "false", "ko"];
    private static readonly string[] OkStatuses = ["ok", "success", "true", "200"];

    private static JsonNode? TryJson(JsonNode? node)
    {
        if (node is not JsonValue v || !v.TryGetValue<string>(out var text)) return node;
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '[')) return node;
        try
        {
            return JsonNode.Parse(trimmed);
        }
        catch (System.Text.Json.JsonException)
        {
            return node;
        }
    }

    private static JsonObject SafeDict(JsonNode? node) => TryJsonKeep(node) as JsonObject ?? [];

    private static JsonNode? TryJsonKeep(JsonNode? node) => node;

    /// <summary>The value of the first key present and neither null nor empty, else the fallback.</summary>
    private static JsonNode? First(JsonObject? row, string[] keys, JsonNode? fallback = null)
    {
        if (row is null) return fallback;
        foreach (var key in keys)
        {
            if (row.ContainsKey(key) && row[key] is { } v && !(v is JsonValue jv && jv.TryGetValue<string>(out var s) && s.Length == 0)) return v;
        }

        return fallback;
    }

    private static double Dbl(JsonNode? node, double fallback = 0.0)
    {
        if (node is null || Str(node) is "" or "-") return fallback;
        if (BookFormatting.Number(node) is { } n) return n;
        return Str(node) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
    }

    private static long Lng(JsonNode? node, long fallback = 0) => node is null || Str(node) is "" or "-" ? fallback : (BookFormatting.Number(node) is { } n ? (long)Math.Truncate(n) : (Str(node) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (long)Math.Truncate(d) : fallback));

    private static readonly string[] MarketRowKeys =
    [
        "ltp", "lastTradedPrice", "lastPrice", "open", "high", "low", "close", "tradedVolume", "volume", "bestBidPrice", "bestAskPrice", "besAskPrice", "marketDepth", "depth", "instrumentId",
    ];

    private static bool LooksLikeMarketRow(JsonNode? node)
    {
        if (node is not JsonObject o) return false;
        if (MarketRowKeys.Any(o.ContainsKey)) return true;
        return (Truthy(o["touchline"]) ? o["touchline"] : o["Touchline"]) is JsonObject;
    }

    private static List<JsonNode?> MarketRows(JsonNode? payloadNode)
    {
        var payload = TryJson(payloadNode);
        if (payload is JsonArray list) return [.. list];
        if (payload is not JsonObject o) return [];
        foreach (var key in new[] { "result", "data", "quotes", "candles", "historicalData", "marketDepth" })
        {
            var value = TryJson(o[key]);
            if (value is JsonArray inner) return [.. inner];
            if (value is JsonObject container)
            {
                foreach (var sub in new[] { "data", "rows", "quotes", "candles", "historicalData", "marketDepth", "listQuotes" })
                {
                    var subValue = TryJson(container[sub]);
                    if (subValue is JsonArray subList) return [.. subList];
                    if (subValue is JsonObject && LooksLikeMarketRow(subValue)) return [subValue];
                    if (subValue is JsonValue sv && sv.TryGetValue<string>(out var text) && text.Contains('|', StringComparison.Ordinal)) return [subValue];
                }

                if (LooksLikeMarketRow(container)) return [container];
            }
        }

        return LooksLikeMarketRow(o) ? [o] : [];
    }

    private static string NormalizeExchange(string exchange) => (exchange ?? "").ToUpperInvariant() switch
    {
        "NSE" => "NSEEQ", "BSE" => "BSEEQ", "NFO" => "NSEFO", "BFO" => "BSEFO", "CDS" => "NSECURR", "BCD" => "BSECURR", "MCX" => "MCXCOMM", "NSE_INDEX" => "NSEEQ", "BSE_INDEX" => "BSEEQ", "MCX_INDEX" => "MCXCOMM",
        var other => other,
    };

    private static bool Succeeded(int status, JsonNode? payload)
    {
        if (payload is JsonObject o)
        {
            var s = (Str(o["status"]) ?? "").ToLowerInvariant();
            if (ErrorStatuses.Contains(s)) return false;
            if (OkStatuses.Contains(s)) return true;
            JsonObject? nested = o["result"] as JsonObject ?? (o["result"] is JsonArray { Count: > 0 } a ? a[0] as JsonObject : null);
            if (nested is not null)
            {
                var n = (Str(nested["status"]) ?? "").ToLowerInvariant();
                if (ErrorStatuses.Contains(n)) return false;
                if (OkStatuses.Contains(n)) return true;
            }
        }

        return status == 200;
    }

    /// <summary>One data call: an answer that is not JSON or that is not a success is an error carrying the broker's own words; a rate limit is tried again first.</summary>
    private async Task<JsonNode?> DataPostAsync(BrokerSession session, string path, JsonNode payload, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var (status, body, text, wait) = await SendAsync(session, HttpMethod.Post, path, payload, cancellationToken);
            if (body is JsonObject { Count: 2 } raw && raw["status"] is JsonValue sv && sv.TryGetValue<string>(out var rs) && rs == "error" && raw["message"] is JsonValue mv && mv.TryGetValue<string>(out var mt) && mt == text && !IsJsonText(text))
                throw new BrokerException($"{DisplayName} sent an answer EP Trade bot could not read. Try again in a moment.", 502);

            if (Succeeded(status, body)) return body;
            var message = (body as JsonObject) is { } o ? (Truthy(o["message"]) ? Str(o["message"]) : Truthy(o["error"]) ? Str(o["error"]) : null) : null;
            message ??= $"Request failed with HTTP {status}";
            if (RateLimited(status, message) && attempt < 3)
            {
                await Task.Delay(sessions.RetryBackoff(Name, wait ?? TimeSpan.FromSeconds(1 << attempt)), cancellationToken);
                continue;
            }

            throw new BrokerException(message, 500);
        }
    }

    private static bool IsJsonText(string text)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(text) && JsonNode.Parse(text) is not null;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static JsonObject Instrument(SymbolRecord r)
    {
        var exchange = (r.BrokerExchange ?? "").ToUpperInvariant();
        if (exchange.Length == 0 || exchange == "INDICES") exchange = NormalizeExchange(r.Exchange);
        return new JsonObject { ["exchange"] = exchange, ["instrumentId"] = r.Token };
    }

    private async Task<long> OpenInterestAsync(BrokerSession session, JsonObject instrument, CancellationToken cancellationToken)
    {
        JsonNode? response;
        try
        {
            response = await DataPostAsync(session, "/marketdata/openinterest", instrument.DeepClone(), cancellationToken);
        }
        catch (BrokerException)
        {
            return 0;
        }

        if (response is not JsonObject o) return 0;
        var result = o.ContainsKey("result") ? o["result"] : o;
        if (result is JsonArray { Count: > 0 } a) result = a[0];
        return result is JsonObject r ? Lng(First(r, ["openInterest", "oi"], 0)) : 0;
    }

    private static string? RowError(JsonObject row)
    {
        var status = (Str(First(row, ["status", "Status"], "")) ?? "").ToLowerInvariant();
        return ErrorStatuses.Contains(status) ? Str(First(row, ["message", "error", "description", "emsg"], "Request failed")) : null;
    }

    private static JsonObject ParseQuote(JsonObject row)
    {
        var touchline = SafeDict(First(row, ["touchline", "Touchline", "quote", "Quote"], new JsonObject()));
        var depth = SafeDict(First(row, ["depth", "marketDepth", "Depth"], new JsonObject()));
        var bids = First(depth, ["buy", "bids", "Buy"], new JsonArray()) as JsonArray;
        var asks = First(depth, ["sell", "asks", "Sell"], new JsonArray()) as JsonArray;
        var bid1 = bids is { Count: > 0 } ? SafeDict(bids[0]) : [];
        var ask1 = asks is { Count: > 0 } ? SafeDict(asks[0]) : [];
        var bidInfo = SafeDict(First(touchline, ["BidInfo", "bidInfo"], new JsonObject()));
        var askInfo = SafeDict(First(touchline, ["AskInfo", "askInfo"], new JsonObject()));
        return new JsonObject
        {
            ["ask"] = Dbl(First(row, ["ask", "askPrice", "bestAsk", "bestAskPrice", "besAskPrice"], First(askInfo, ["Price", "price"], First(ask1, ["price", "Price"], 0)))),
            ["bid"] = Dbl(First(row, ["bid", "bidPrice", "bestBid", "bestBidPrice"], First(bidInfo, ["Price", "price"], First(bid1, ["price", "Price"], 0)))),
            ["high"] = Dbl(First(row, ["high", "highPrice", "dayHigh", "High"], First(touchline, ["High", "high"], 0))),
            ["low"] = Dbl(First(row, ["low", "lowPrice", "dayLow", "Low"], First(touchline, ["Low", "low"], 0))),
            ["ltp"] = Dbl(First(row, ["ltp", "LTP", "lastTradedPrice", "lastPrice", "last_price"], First(touchline, ["LastTradedPrice", "lastTradedPrice"], 0))),
            ["open"] = Dbl(First(row, ["open", "openPrice", "dayOpen", "Open"], First(touchline, ["Open", "open"], 0))),
            ["prev_close"] = Dbl(First(row, ["close", "closePrice", "previousClose", "previousClosePrice", "prevClose", "prev_close", "Close"], First(touchline, ["Close", "close"], 0))),
            ["volume"] = Lng(First(row, ["volume", "tradedVolume", "totalTradedVolume", "totalTradedQuantity"], First(touchline, ["TotalTradedQuantity", "totalTradedQuantity"], 0))),
            ["oi"] = Lng(First(row, ["oi", "openInterest", "OpenInterest", "OI"], 0)),
        };
    }

    private async Task<List<JsonNode?>> QuoteRowsAsync(BrokerSession session, JsonArray instruments, CancellationToken cancellationToken)
    {
        var rows = MarketRows(await DataPostAsync(session, "/marketdata/marketquotes", instruments, cancellationToken));
        if (rows.Count > 0) return rows;
        throw new BrokerException("No quote rows in broker response", 500);
    }

    public async Task<JsonObject> GetQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var record = await symbols.FindAsync(Name, symbol, exchange, cancellationToken) ?? throw new BrokerException($"Could not find instrument token for {exchange}:{symbol}", 400);
        var instrument = Instrument(record);
        var rows = await QuoteRowsAsync(session, new JsonArray(instrument.DeepClone()), cancellationToken);
        var first = TryJson(rows[0]);
        if (first is JsonValue) throw new BrokerException("Invalid quote row format received from broker", 500);
        var row = SafeDict(first);
        if (RowError(row) is { } error) throw new BrokerException(error, 500);
        if (!LooksLikeMarketRow(row)) throw new BrokerException("No quote data received from broker", 500);
        var parsed = ParseQuote(row);
        if (OiExchanges.Contains(exchange.ToUpperInvariant())) parsed["oi"] = await OpenInterestAsync(session, instrument, cancellationToken);
        return parsed;
    }

    public async Task<JsonArray> GetMultiQuotesAsync(BrokerSession session, IReadOnlyList<SymbolRecord> instruments, CancellationToken cancellationToken)
    {
        var valid = instruments.Select(r => (Record: r, Instrument: Instrument(r))).ToList();
        var rows = await QuoteRowsAsync(session, new JsonArray(valid.Select(v => (JsonNode?)v.Instrument.DeepClone()).ToArray()), cancellationToken);

        var oiMap = new Dictionary<string, long>();
        foreach (var v in valid.Where(v => OiExchanges.Contains(v.Record.Exchange.ToUpperInvariant())))
            oiMap[$"{Str(v.Instrument["exchange"])!.ToUpperInvariant()}:{Str(v.Instrument["instrumentId"])}"] = await OpenInterestAsync(session, v.Instrument, cancellationToken);

        var parsedRows = new List<JsonObject>();
        var byIdentity = new Dictionary<string, JsonObject>();
        foreach (var node in rows)
        {
            var item = TryJson(node);
            if (item is JsonValue) continue;
            var row = SafeDict(item);
            parsedRows.Add(row);
            var rowExchange = (Str(First(row, ["exchange", "exchangeSegment"], "")) ?? "").ToUpperInvariant();
            var rowToken = Str(First(row, ["instrumentId", "token", "exchangeInstrumentID"], "")) ?? "";
            if (rowExchange.Length > 0 && rowToken.Length > 0) byIdentity[$"{rowExchange}:{rowToken}"] = row;
        }

        var results = new JsonArray();
        for (var index = 0; index < valid.Count; index++)
        {
            var (record, instrument) = valid[index];
            var id = Str(instrument["instrumentId"]);
            var identityKey = $"{Str(instrument["exchange"])}:{id}";
            var originalKey = $"{record.Exchange.ToUpperInvariant()}:{id}";
            JsonObject? row = byIdentity.Count > 0
                ? byIdentity.GetValueOrDefault(identityKey) ?? byIdentity.GetValueOrDefault(originalKey)
                : index < parsedRows.Count ? parsedRows[index] : null;
            if (row is null || row.Count == 0)
            {
                results.Add(new JsonObject { ["symbol"] = record.Symbol, ["exchange"] = record.Exchange, ["data"] = null, ["error"] = "No quote data available" });
                continue;
            }

            if (RowError(row) is { } error)
            {
                results.Add(new JsonObject { ["symbol"] = record.Symbol, ["exchange"] = record.Exchange, ["data"] = null, ["error"] = error });
                continue;
            }

            var parsed = ParseQuote(row);
            if (oiMap.TryGetValue($"{Str(instrument["exchange"])!.ToUpperInvariant()}:{id}", out var oi)) parsed["oi"] = oi;
            results.Add(new JsonObject { ["symbol"] = record.Symbol, ["exchange"] = record.Exchange, ["data"] = parsed });
        }

        return results;
    }

    private static List<JsonObject> DepthLevels(JsonNode? levels)
    {
        var result = new List<JsonObject>();
        if (levels is not JsonArray list) return result;
        foreach (var level in list.Take(20))
        {
            if (level is JsonObject o)
                result.Add(new JsonObject { ["price"] = Dbl(First(o, ["price", "Price"], 0)), ["quantity"] = Lng(First(o, ["quantity", "qty", "Quantity"], 0)), ["orders"] = Lng(First(o, ["orders", "numOrders", "Orders"], 0)) });
            else if (level is JsonArray a && a.Count >= 2)
                result.Add(new JsonObject { ["price"] = Dbl(a[0]), ["quantity"] = Lng(a[1]), ["orders"] = a.Count > 2 ? Lng(a[2]) : 0 });
        }

        return result;
    }

    private static List<JsonObject> TopFive(JsonNode? levels)
    {
        var five = DepthLevels(levels).Take(5).ToList();
        while (five.Count < 5) five.Add(new JsonObject { ["price"] = 0.0, ["quantity"] = 0, ["orders"] = 0 });
        return five;
    }

    private static JsonArray ToArray(IEnumerable<JsonObject> levels) => new(levels.Select(l => (JsonNode?)l.DeepClone()).ToArray());

    public async Task<JsonObject> GetDepthAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var record = await symbols.FindAsync(Name, symbol, exchange, cancellationToken) ?? throw new BrokerException($"Could not find instrument token for {exchange}:{symbol}", 400);
        var instrument = Instrument(record);
        var rows = MarketRows(await DataPostAsync(session, "/marketdata/marketdepth", instrument.DeepClone(), cancellationToken));
        if (rows.Count == 0) throw new BrokerException("No depth data received from broker", 500);
        var first = TryJson(rows[0]);
        if (first is JsonValue) throw new BrokerException("Invalid depth row format received from broker", 500);
        var row = SafeDict(first);

        var depth = SafeDict(First(row, ["depth", "marketDepth", "Depth"], new JsonObject()));
        var buy = TopFive(First(depth, ["buy", "bids", "Buy"], new JsonArray()));
        var sell = TopFive(First(depth, ["sell", "asks", "Sell"], new JsonArray()));
        var oi = Lng(First(row, ["oi", "openInterest"], 0));
        if (oi == 0 && OiExchanges.Contains(exchange.ToUpperInvariant())) oi = await OpenInterestAsync(session, instrument, cancellationToken);
        return new JsonObject
        {
            ["bids"] = ToArray(buy), ["asks"] = ToArray(sell), ["buy"] = ToArray(buy), ["sell"] = ToArray(sell), ["depth"] = new JsonObject { ["buy"] = ToArray(buy), ["sell"] = ToArray(sell) }, ["ltp"] = Dbl(First(row, ["ltp", "lastTradedPrice"], 0)), ["open"] = Dbl(First(row, ["open", "openPrice"], 0)),
            ["high"] = Dbl(First(row, ["high", "highPrice"], 0)), ["low"] = Dbl(First(row, ["low", "lowPrice"], 0)), ["prev_close"] = Dbl(First(row, ["close", "previousClose"], 0)),
            ["ltq"] = Lng(First(row, ["ltq", "lastTradedQuantity", "lastTradeQty"], 0)), ["volume"] = Lng(First(row, ["volume", "tradedVolume"], 0)), ["oi"] = oi,
            ["totalbuyqty"] = Lng(First(row, ["totalBidQuantity", "totalBuyQuantity", "totBuyQuan", "totalbuyqty"], buy.Sum(l => Lng(l["quantity"])))),
            ["totalsellqty"] = Lng(First(row, ["totalAskQuantity", "totalSellQuantity", "totSellQuan", "totalsellqty"], sell.Sum(l => Lng(l["quantity"])))),
        };
    }
}
