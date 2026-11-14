using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Arrow;

/// <summary>
/// Arrow market data (broker/arrow/api/data.py). Quotes are POSTed one instrument at a time (or 100 at a time for several) with the exchange and trading symbol; prices come in paise.
/// An index is quoted under the INDEX exchange with the name Arrow accepts, which is searched for (the symbol, its upper-cased display name, the display name) and remembered by token.
/// Candles come from a separate host, in chunks of 60 days (2000 for daily and longer), with their own ISO times.
/// </summary>
public sealed partial class ArrowPlugin
{
    private const string HistoryHost = "https://historical-api.arrow.trade";
    private const int BatchSize = 100;

    private static readonly HashSet<string> QuoteUnsupported = ["CDS", "BCD", "NCO"];
    private static readonly HashSet<string> IndexExchanges = ["NSE_INDEX", "BSE_INDEX", "MCX_INDEX"];

    private readonly Dictionary<string, string> indexNames = [];
    private readonly HashSet<string> indexUnsupported = [];

    private static string QuoteExchange(string exchange) => exchange switch { "MCX" => "MCXFO", "NSE_INDEX" or "BSE_INDEX" or "MCX_INDEX" => "INDEX", _ => exchange };

    private static string HistoryExchange(string exchange) => exchange switch { "NSE_INDEX" => "nse", "BSE_INDEX" => "bse", "MCX_INDEX" => "mcx", _ => exchange.ToLowerInvariant() };

    private static readonly Dictionary<string, string> Intervals = new()
    {
        ["1m"] = "min", ["3m"] = "3min", ["5m"] = "5min", ["10m"] = "10min", ["15m"] = "15min", ["30m"] = "30min", ["1h"] = "hour", ["2h"] = "2hours", ["3h"] = "3hours", ["4h"] = "4hours",
        ["D"] = "day", ["W"] = "week", ["M"] = "month",
    };

    public IReadOnlyList<string> HistoryTimeframes { get; } = [.. Intervals.Keys];

    private static double Scale(JsonNode? value) => BookFormatting.Number(value) is { } n ? n / 100.0 : Str(value) is { } t && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d / 100.0 : 0.0;

    /// <summary>A refusal by status (an error status, as the HTTP library raises it), kept apart from Arrow's own error answers.</summary>
    private sealed class StatusRefusal(int status, string message) : Exception(message)
    {
        public int Status { get; } = status;
    }

    private sealed class ApiRefusal(string message) : Exception(message);

    private async Task<JsonObject> QuoteAsync(BrokerSession session, string mode, string exchange, string symbol, CancellationToken cancellationToken)
    {
        var (status, body) = await CallAsync(session, HttpMethod.Post, $"/info/quote/{mode}", new JsonObject { ["exchange"] = exchange, ["symbol"] = symbol }, cancellationToken);
        if (status >= 400) throw new StatusRefusal(status, Message(body, "no reason was given."));
        var payload = body as JsonObject ?? [];
        if (Str(payload["status"]) is { } s && s != "success") throw new ApiRefusal(Message(payload, "Quote request failed"));
        return payload["data"] as JsonObject ?? [];
    }

    private async Task<(string BrokerSymbol, string Token, string ArrowExchange)> LookupAsync(string symbol, string exchange, CancellationToken cancellationToken)
    {
        var record = await symbols.FindAsync(Name, symbol, exchange, cancellationToken);
        if (record is null || string.IsNullOrEmpty(record.BrokerSymbol)) throw new ApiRefusal($"Could not find instrument for {exchange}:{symbol}");
        return (record.BrokerSymbol, record.Token, QuoteExchange(exchange));
    }

    /// <summary>Quotes an index under the name Arrow accepts: the symbol, its upper-cased display name, the display name. A 400 moves on to the next; the name that works is remembered.</summary>
    private async Task<JsonObject> QuoteIndexAsync(BrokerSession session, string mode, string symbol, string brokerSymbol, string token, CancellationToken cancellationToken)
    {
        if (indexNames.TryGetValue(token, out var cached)) return await QuoteAsync(session, mode, "INDEX", cached, cancellationToken);
        if (indexUnsupported.Contains(token)) throw new ApiRefusal($"Arrow's quote API does not serve index {symbol} (the live feed still works)");

        var candidates = new List<string>();
        foreach (var candidate in new[] { symbol, brokerSymbol.ToUpperInvariant(), brokerSymbol })
        {
            if (candidate.Length > 0 && !candidates.Contains(candidate)) candidates.Add(candidate);
        }

        foreach (var candidate in candidates)
        {
            try
            {
                var data = await QuoteAsync(session, mode, "INDEX", candidate, cancellationToken);
                indexNames[token] = candidate;
                return data;
            }
            catch (StatusRefusal r) when (r.Status == 400)
            {
            }
        }

        indexUnsupported.Add(token);
        throw new ApiRefusal($"Arrow's quote API did not accept any name for the index {symbol}, so its quote cannot be read. The live feed still works.");
    }

    private async Task<JsonObject> FetchQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        if (QuoteUnsupported.Contains(exchange))
            throw new ApiRefusal($"Arrow's quote API does not serve the {exchange} exchange. Use the live feed for prices on this exchange.");
        var (brokerSymbol, token, arrowExchange) = await LookupAsync(symbol, exchange, cancellationToken);
        return arrowExchange == "INDEX"
            ? await QuoteIndexAsync(session, "full", symbol, brokerSymbol, token, cancellationToken)
            : await QuoteAsync(session, "full", arrowExchange, brokerSymbol, cancellationToken);
    }

    private static BrokerException Failure(string what, string fetching, Exception ex) => ex switch
    {
        StatusRefusal r => new BrokerException($"Arrow could not send the {what}: {r.Message}", 500, ex),
        ApiRefusal a => new BrokerException($"Error fetching {fetching}: {a.Message}", 500, ex),
        BrokerException b => b,
        _ => new BrokerException($"Error fetching {fetching}: {ex.Message}", 500, ex),
    };

    private static JsonObject QuoteBody(JsonObject q)
    {
        var bids = q["bids"] as JsonArray is { Count: > 0 } b ? b : [new JsonObject()];
        var asks = q["asks"] as JsonArray is { Count: > 0 } a ? a : [new JsonObject()];
        return new JsonObject
        {
            ["ask"] = Scale((asks[0] as JsonObject)?["price"]), ["bid"] = Scale((bids[0] as JsonObject)?["price"]), ["high"] = Scale(q["high"]), ["low"] = Scale(q["low"]), ["ltp"] = Scale(q["ltp"]),
            ["open"] = Scale(q["open"]), ["prev_close"] = Scale(q["close"]), ["volume"] = q.ContainsKey("volume") ? Same(q["volume"]) : 0, ["oi"] = q.ContainsKey("oi") ? Same(q["oi"]) : 0,
        };
    }

    public async Task<JsonObject> GetQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        try
        {
            return QuoteBody(await FetchQuoteAsync(session, symbol, exchange, cancellationToken));
        }
        catch (Exception ex) when (ex is StatusRefusal or ApiRefusal)
        {
            throw Failure("quote", "quotes", ex);
        }
    }

    private static JsonArray Levels(JsonNode? side)
    {
        var rows = (side as JsonArray) ?? [];
        var result = new JsonArray();
        for (var i = 0; i < 5; i++)
        {
            result.Add(i < rows.Count
                ? new JsonObject { ["price"] = Scale((rows[i] as JsonObject)?["price"]), ["quantity"] = (rows[i] as JsonObject)?.ContainsKey("quantity") == true ? Same((rows[i] as JsonObject)!["quantity"]) : 0 }
                : new JsonObject { ["price"] = 0, ["quantity"] = 0 });
        }

        return result;
    }

    public async Task<JsonObject> GetDepthAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        try
        {
            var q = await FetchQuoteAsync(session, symbol, exchange, cancellationToken);
            return new JsonObject
            {
                ["asks"] = Levels(q["asks"]), ["bids"] = Levels(q["bids"]), ["high"] = Scale(q["high"]), ["low"] = Scale(q["low"]), ["ltp"] = Scale(q["ltp"]),
                ["ltq"] = q.ContainsKey("ltq") ? Same(q["ltq"]) : 0, ["oi"] = q.ContainsKey("oi") ? Same(q["oi"]) : 0, ["open"] = Scale(q["open"]), ["prev_close"] = Scale(q["close"]),
                ["totalbuyqty"] = q.ContainsKey("totalBuyQty") ? Same(q["totalBuyQty"]) : 0, ["totalsellqty"] = q.ContainsKey("totalSellQty") ? Same(q["totalSellQty"]) : 0,
                ["volume"] = q.ContainsKey("volume") ? Same(q["volume"]) : 0,
            };
        }
        catch (Exception ex) when (ex is StatusRefusal or ApiRefusal)
        {
            throw Failure("depth", "market depth", ex);
        }
    }

    // ---- several quotes --------------------------------------------------------------------------------------

    /// <summary>The name an index is quoted under, found with a cheap last-price quote; null when Arrow does not serve the index.</summary>
    private async Task<string?> IndexNameAsync(BrokerSession session, string symbol, string brokerSymbol, string token, CancellationToken cancellationToken)
    {
        if (indexNames.TryGetValue(token, out var known)) return known;
        if (indexUnsupported.Contains(token)) return null;
        try
        {
            await QuoteIndexAsync(session, "ltp", symbol, brokerSymbol, token, cancellationToken);
            return indexNames.GetValueOrDefault(token);
        }
        catch (ApiRefusal)
        {
            return null;
        }
    }

    public async Task<JsonArray> GetMultiQuotesAsync(BrokerSession session, IReadOnlyList<SymbolRecord> instruments, CancellationToken cancellationToken)
    {
        var results = new JsonArray();
        for (var i = 0; i < instruments.Count; i += BatchSize)
        {
            try
            {
                foreach (var entry in await QuoteBatchAsync(session, instruments.Skip(i).Take(BatchSize).ToList(), cancellationToken)) results.Add(entry);
            }
            catch (StatusRefusal r)
            {
                throw new BrokerException($"Arrow could not send the quotes: {r.Message}", 500, r);
            }
        }

        return results;
    }

    private async Task<List<JsonObject>> QuoteBatchAsync(BrokerSession session, List<SymbolRecord> batch, CancellationToken cancellationToken)
    {
        var body = new JsonArray();
        var tokenMap = new Dictionary<string, SymbolRecord>();
        var skipped = new List<JsonObject>();
        JsonObject Error(SymbolRecord item, string message) => new() { ["symbol"] = item.Symbol, ["exchange"] = item.Exchange, ["error"] = message };

        foreach (var item in batch)
        {
            if (QuoteUnsupported.Contains(item.Exchange))
            {
                skipped.Add(Error(item, "Exchange not supported by Arrow quotes"));
                continue;
            }

            var arrowExchange = QuoteExchange(item.Exchange);
            if (arrowExchange == "INDEX")
            {
                var name = await IndexNameAsync(session, item.Symbol, item.BrokerSymbol, item.Token, cancellationToken);
                if (name is null)
                {
                    skipped.Add(Error(item, "Index not served by Arrow quote API"));
                    continue;
                }

                body.Add(new JsonObject { ["exchange"] = "INDEX", ["symbol"] = name });
            }
            else body.Add(new JsonObject { ["exchange"] = arrowExchange, ["symbol"] = item.BrokerSymbol });
            tokenMap[item.Token] = item;
        }

        if (tokenMap.Count == 0) return skipped;

        // One request for the batch; a failure leaves every leg with an error rather than nothing.
        var quotes = new Dictionary<string, JsonObject>();
        try
        {
            var (status, reply) = await CallAsync(session, HttpMethod.Post, "/info/quotes/full", body, cancellationToken);
            if (status < 400 && reply is JsonObject payload)
            {
                foreach (var q in (payload["data"] as JsonArray ?? []).OfType<JsonObject>()) quotes[Str(q["token"]) ?? ""] = q;
            }
        }
        catch (BrokerException)
        {
        }

        var results = new List<JsonObject>(skipped);
        foreach (var (token, original) in tokenMap)
        {
            results.Add(quotes.TryGetValue(token, out var quote)
                ? new JsonObject { ["symbol"] = original.Symbol, ["exchange"] = original.Exchange, ["data"] = QuoteBody(quote) }
                : Error(original, "No quote data available"));
        }

        return results;
    }

    // ---- history ---------------------------------------------------------------------------------------------

    /// <summary>Epoch seconds of an ISO time with an offset such as +0530; a time without one is read as UTC.</summary>
    private static long EpochSeconds(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var normalised = System.Text.RegularExpressions.Regex.Replace(text, @"([+-]\d{2})(\d{2})$", "$1:$2");
        return DateTimeOffset.TryParse(normalised, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed.ToUnixTimeSeconds() : 0;
    }

    public async Task<JsonArray> GetHistoryAsync(BrokerSession session, SymbolRecord instrument, string interval, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        if (!Intervals.TryGetValue(interval, out var arrowInterval))
            throw new BrokerException($"Arrow does not offer {interval} candles. Choose one of: {string.Join(", ", Intervals.Keys)}.", 400);

        var token = instrument.Token;
        var exchange = HistoryExchange(instrument.Exchange);
        var wantOi = instrument.Exchange is "NFO" or "BFO";
        var chunkDays = arrowInterval is "day" or "week" or "month" ? 2000 : 60;
        var daily = interval is "D" or "W" or "M";

        var bars = new SortedDictionary<long, JsonObject>();
        var currentStart = start;
        while (currentStart <= end)
        {
            var currentEnd = currentStart.AddDays(chunkDays - 1) < end ? currentStart.AddDays(chunkDays - 1) : end;
            var path = $"/candle/{exchange}/{token}/{arrowInterval}?from={currentStart:yyyy-MM-dd}T00%3A00%3A00&to={currentEnd:yyyy-MM-dd}T23%3A59%3A59" + (wantOi ? "&oi=1" : "");
            using var request = new HttpRequestMessage(HttpMethod.Get, HistoryBase() + path);
            if (session.Key("ApiKey") is { } appId) request.Headers.TryAddWithoutValidation("appID", appId);
            request.Headers.TryAddWithoutValidation("token", session.AuthToken);
            HttpResponseMessage response;
            try
            {
                response = await Client().SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new BrokerException($"{DisplayName} did not answer. Check your connection, then try again.", 502, ex);
            }

            using (response)
            {
                var body = await JsonRead.ReadAsync(response, cancellationToken);
                if ((int)response.StatusCode >= 400)
                    throw new BrokerException($"Arrow could not send the candles: {Message(body, "no reason was given.")}", 500);
                if (body is JsonObject envelope) throw new BrokerException($"Error fetching historical data: {Message(envelope, "Historical request failed")}", 500);
                var candles = (body as JsonArray ?? []).OfType<JsonArray>().ToList();
                var hasOi = wantOi && candles.Count > 0 && candles[0].Count > 6;
                foreach (var c in candles)
                {
                    var seconds = EpochSeconds(Str(c[0]));
                    var timestamp = daily ? seconds + 19800 : seconds;
                    // The first row of a timestamp is kept.
                    bars.TryAdd(timestamp, new JsonObject
                    {
                        ["timestamp"] = timestamp, ["open"] = Float(c[1]) / 100.0, ["high"] = Float(c[2]) / 100.0, ["low"] = Float(c[3]) / 100.0, ["close"] = Float(c[4]) / 100.0,
                        ["volume"] = Int(c[5]), ["oi"] = hasOi && c.Count > 6 ? Int(c[6]) : 0,
                    });
                }
            }

            currentStart = currentEnd.AddDays(1);
        }

        return new JsonArray(bars.Values.Select(v => (JsonNode?)v).ToArray());
    }

    private string HistoryBase() => sessions.BaseUrl(Name + "_history", HistoryHost);
}
