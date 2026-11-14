using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.FivePaisa;

/// <summary>
/// 5paisa market data (broker/fivepaisa/api/data.py): quotes from MarketSnapshot (the best bid and ask from MarketDepth, buy orders flagged 66 and sell orders 83), several quotes in one MarketSnapshot call (50 a call, half a
/// second apart; a quote that cannot be matched to a request is left out), depth as the five best of each side from MarketDepth beside the snapshot. A scrip with token 0 is asked for by its name instead.
/// </summary>
public sealed partial class FivePaisaPlugin
{
    private sealed record Quote(double Bid, double Ask, double Open, double High, double Low, double Ltp, double PrevClose, long Volume, long Oi);

    private static JsonObject Scrip(SymbolRecord record, string exchange, string? normalizedExchange = null) => new()
    {
        ["Exchange"] = MapExchange(exchange), ["ExchangeType"] = MapExchangeType(normalizedExchange ?? record.Exchange), ["ScripCode"] = record.Token, ["ScripData"] = record.Token == "0" ? record.BrokerSymbol : "",
    };

    private static double PreviousClose(JsonObject quote)
    {
        var close = Float(quote["PClose"]);
        if (close == 0) close = Float(quote["PreviousClose"]);
        return close == 0 ? Float(quote["Close"]) : close;
    }

    private async Task<JsonObject> SnapshotAsync(BrokerSession session, IEnumerable<JsonObject> scrips, CancellationToken cancellationToken, string what)
    {
        var body = new JsonObject { ["ClientCode"] = Keys(session).ClientCode, ["Data"] = new JsonArray(scrips.Select(s => (JsonNode?)s).ToArray()) };
        var (status, answer) = await PostAsync(session, $"{Svc}/MarketSnapshot", body, cancellationToken);
        if (status >= 400) throw new BrokerException($"{DisplayName} could not give {what} just now. Try again in a moment.", 500);
        if (!Succeeded(answer)) throw new BrokerException($"{DisplayName} could not give {what}: {(Description(answer) is { Length: > 0 } d ? d : "no reason was given.")}", 500);
        return answer;
    }

    private async Task<List<JsonObject>> DepthRowsAsync(BrokerSession session, JsonObject scrip, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["ClientCode"] = Keys(session).ClientCode, ["Exchange"] = scrip["Exchange"]?.DeepClone(), ["ExchangeType"] = scrip["ExchangeType"]?.DeepClone(), ["ScripCode"] = scrip["ScripCode"]?.DeepClone(), ["ScripData"] = scrip["ScripData"]?.DeepClone() };
        var (status, answer) = await PostAsync(session, $"{Svc}/V2/MarketDepth", body, cancellationToken);
        if (status >= 400 || !Succeeded(answer)) throw new BrokerException($"{DisplayName} could not give the market depth: {(Description(answer) is { Length: > 0 } d ? d : "no reason was given.")}", 500);
        return (Body(answer)["MarketDepthData"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
    }

    private async Task<Quote> QuoteAsync(BrokerSession session, SymbolRecord record, string exchange, CancellationToken cancellationToken)
    {
        var scrip = Scrip(record, exchange);
        var answer = await SnapshotAsync(session, [scrip], cancellationToken, "the quote");
        if ((Body(answer)["Data"] as JsonArray)?.FirstOrDefault() is not JsonObject quote) throw new BrokerException($"{DisplayName} sent no quote for {record.Symbol} on {exchange}.", 500);

        double bid = 0, ask = 0;
        try
        {
            var depth = await DepthRowsAsync(session, scrip, cancellationToken);
            var buys = depth.Where(o => Float(o["BbBuySellFlag"]) == 66).Select(o => Float(o["Price"])).ToList();
            var sells = depth.Where(o => Float(o["BbBuySellFlag"]) == 83).Select(o => Float(o["Price"])).ToList();
            if (buys.Count > 0) bid = buys.Max();
            if (sells.Count > 0) ask = sells.Min();
        }
        catch (BrokerException)
        {
        }

        return new Quote(bid, ask, Float(quote["Open"]), Float(quote["High"]), Float(quote["Low"]), Float(quote["LastTradedPrice"]), PreviousClose(quote), Int(quote["Volume"]), Int(quote["OpenInterest"]));
    }

    private async Task<SymbolRecord> InstrumentAsync(string symbol, string exchange, CancellationToken cancellationToken) =>
        await symbols.FindAsync(Name, symbol, NormalizeExchange(symbol, exchange), cancellationToken) ?? throw new BrokerException($"{symbol} on {exchange} is not in the {DisplayName} master contract. Check the symbol, or download the master contract again.", 400);

    public async Task<JsonObject> GetQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var q = await QuoteAsync(session, await InstrumentAsync(symbol, exchange, cancellationToken), exchange, cancellationToken);
        return new JsonObject { ["ask"] = q.Ask, ["bid"] = q.Bid, ["high"] = q.High, ["low"] = q.Low, ["ltp"] = q.Ltp, ["open"] = q.Open, ["prev_close"] = q.PrevClose, ["volume"] = q.Volume };
    }

    public async Task<JsonObject> GetDepthAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var record = await InstrumentAsync(symbol, exchange, cancellationToken);
        var scrip = Scrip(record, exchange);
        var answer = await SnapshotAsync(session, [scrip], cancellationToken, "the market depth");
        if ((Body(answer)["Data"] as JsonArray)?.FirstOrDefault() is not JsonObject quote) throw new BrokerException($"{DisplayName} sent no data for {symbol} on {exchange}.", 500);

        var rows = await DepthRowsAsync(session, scrip, cancellationToken);
        var buys = rows.Where(o => Float(o["BbBuySellFlag"]) == 66).OrderByDescending(o => Float(o["Price"])).ToList();
        var sells = rows.Where(o => Float(o["BbBuySellFlag"]) == 83).OrderBy(o => Float(o["Price"])).ToList();
        JsonArray Levels(List<JsonObject> side)
        {
            var levels = new JsonArray(side.Take(5).Select(o => (JsonNode?)new JsonObject { ["price"] = Float(o["Price"]), ["quantity"] = Int(o["Quantity"]) }).ToArray());
            while (levels.Count < 5) levels.Add(new JsonObject { ["price"] = 0, ["quantity"] = 0 });
            return levels;
        }

        return new JsonObject
        {
            ["asks"] = Levels(sells), ["bids"] = Levels(buys), ["high"] = Float(quote["High"]), ["low"] = Float(quote["Low"]), ["ltp"] = Float(quote["LastTradedPrice"]), ["ltq"] = Int(quote["LastTradedQty"]), ["oi"] = Int(quote["OpenInterest"]),
            ["open"] = Float(quote["Open"]), ["prev_close"] = Float(quote["PClose"]), ["totalbuyqty"] = buys.Sum(o => Int(o["Quantity"])), ["totalsellqty"] = sells.Sum(o => Int(o["Quantity"])), ["volume"] = Int(quote["Volume"]),
        };
    }

    private const int BatchSize = 50;

    public async Task<JsonArray> GetMultiQuotesAsync(BrokerSession session, IReadOnlyList<SymbolRecord> instruments, CancellationToken cancellationToken)
    {
        var results = new JsonArray();
        for (var i = 0; i < instruments.Count; i += BatchSize)
        {
            foreach (var item in await BatchAsync(session, instruments.Skip(i).Take(BatchSize).ToList(), cancellationToken)) results.Add(item);
            if (i + BatchSize < instruments.Count) await Task.Delay(sessions.RetryBackoff(Name, TimeSpan.FromMilliseconds(500)), cancellationToken);
        }

        return results;
    }

    private async Task<List<JsonObject>> BatchAsync(BrokerSession session, List<SymbolRecord> batch, CancellationToken cancellationToken)
    {
        var answer = await SnapshotAsync(session, batch.Select(r => Scrip(r, r.Exchange)), cancellationToken, "the quotes");
        var byKey = new Dictionary<string, SymbolRecord>();
        foreach (var r in batch) byKey[r.Token == "0" ? $"scripdata:{r.BrokerSymbol}" : r.Token] = r;

        var results = new List<JsonObject>();
        foreach (var quote in (Body(answer)["Data"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var code = Str(quote["ScripCode"]) ?? "";
            var name = Truthy(quote["ScripData"]) ? Str(quote["ScripData"])! : Str(quote["Symbol"]) ?? "";
            var record = byKey.GetValueOrDefault(code);
            if (record is null && code == "0" && name.Length > 0) record = byKey.GetValueOrDefault($"scripdata:{name}");
            record ??= batch.FirstOrDefault(r => r.BrokerSymbol == name);
            if (record is null) continue;
            results.Add(new JsonObject
            {
                ["symbol"] = record.Symbol, ["exchange"] = record.Exchange, ["data"] = new JsonObject
                {
                    ["bid"] = 0, ["ask"] = 0, ["open"] = Float(quote["Open"]), ["high"] = Float(quote["High"]), ["low"] = Float(quote["Low"]), ["ltp"] = Float(quote["LastTradedPrice"]), ["prev_close"] = PreviousClose(quote),
                    ["volume"] = Int(quote["Volume"]), ["oi"] = Int(quote["OpenInterest"]),
                },
            });
        }

        return results;
    }
}
