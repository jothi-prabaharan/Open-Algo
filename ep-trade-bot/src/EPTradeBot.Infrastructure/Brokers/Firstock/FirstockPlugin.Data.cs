using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Firstock;

/// <summary>
/// Firstock market data (broker/firstock/api/data.py): /getQuote for a quote and for the depth (the same call), /getMultiQuotes for several (fifty a call, a second apart). An index is asked for under the cash exchange. A call that
/// answers with "rate limit" in plain text is tried again once after a second.
/// </summary>
public sealed partial class FirstockPlugin
{
    private sealed record Quote(double Bid, double Ask, double Open, double High, double Low, double Ltp, double PrevClose, long Volume, long Oi);

    private static double F(JsonNode? node) => Num(node);

    private static long W(JsonNode? node) => (long)Math.Truncate(Num(node));

    private static Quote ToQuote(JsonObject item) =>
        new(F(item["bestBuyPrice1"]), F(item["bestSellPrice1"]), F(item["dayOpenPrice"]), F(item["dayHighPrice"]), F(item["dayLowPrice"]), F(item["lastTradedPrice"]), F(item["dayClosePrice"]), W(item["volume"]), W(item["openInterest"]));

    private static JsonObject QuoteObject(Quote q) => new()
    {
        ["bid"] = q.Bid, ["ask"] = q.Ask, ["open"] = q.Open, ["high"] = q.High, ["low"] = q.Low, ["ltp"] = q.Ltp, ["prev_close"] = q.PrevClose, ["volume"] = q.Volume, ["oi"] = q.Oi,
    };

    private async Task<JsonObject> DataPostAsync(BrokerSession session, string path, JsonObject body, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var (status, answer) = await PostAsync(session, path, (JsonObject)body.DeepClone(), cancellationToken, accept: true);
            if (answer.Count == 0 && status is 429 && attempt == 0)
            {
                await Task.Delay(sessions.RetryBackoff(Name, TimeSpan.FromSeconds(1)), cancellationToken);
                continue;
            }

            if (answer.Count == 0) throw new BrokerException($"{DisplayName} sent an answer that could not be read. Try again in a moment.", 500);
            return answer;
        }
    }

    private async Task<JsonObject> QuoteDataAsync(BrokerSession session, SymbolRecord record, string exchange, CancellationToken cancellationToken)
    {
        var answer = await DataPostAsync(session, "/getQuote", new JsonObject { ["exchange"] = MapExchange(exchange), ["tradingSymbol"] = record.BrokerSymbol }, cancellationToken);
        if (!Succeeded(answer)) throw new BrokerException($"{DisplayName} could not give the quote: {Reason(answer, "no reason was given.")}", 500);
        return answer["data"] as JsonObject ?? [];
    }

    private async Task<Quote> QuoteAsync(BrokerSession session, SymbolRecord record, string exchange, CancellationToken cancellationToken) => ToQuote(await QuoteDataAsync(session, record, exchange, cancellationToken));

    private async Task<SymbolRecord> InstrumentAsync(string symbol, string exchange, CancellationToken cancellationToken) =>
        await symbols.FindAsync(Name, symbol, exchange, cancellationToken) ?? throw new BrokerException($"{symbol} on {exchange} is not in the {DisplayName} master contract. Check the symbol, or download the master contract again.", 400);

    public async Task<JsonObject> GetQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken) =>
        QuoteObject(await QuoteAsync(session, await InstrumentAsync(symbol, exchange, cancellationToken), exchange, cancellationToken));

    public async Task<JsonObject> GetDepthAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var data = await QuoteDataAsync(session, await InstrumentAsync(symbol, exchange, cancellationToken), exchange, cancellationToken);
        var bids = new JsonArray();
        var asks = new JsonArray();
        for (var i = 1; i <= 5; i++)
        {
            bids.Add(new JsonObject { ["price"] = F(data[$"bestBuyPrice{i}"]), ["quantity"] = W(data[$"bestBuyQuantity{i}"]) });
            asks.Add(new JsonObject { ["price"] = F(data[$"bestSellPrice{i}"]), ["quantity"] = W(data[$"bestSellQuantity{i}"]) });
        }

        return new JsonObject
        {
            ["asks"] = asks, ["bids"] = bids, ["high"] = F(data["dayHighPrice"]), ["low"] = F(data["dayLowPrice"]), ["ltp"] = F(data["lastTradedPrice"]), ["ltq"] = W(data["lastTradedQuantity"]), ["oi"] = W(data["openInterest"]),
            ["open"] = F(data["dayOpenPrice"]), ["prev_close"] = F(data["dayClosePrice"]), ["totalbuyqty"] = W(data["totalBuyQuantity"]), ["totalsellqty"] = W(data["totalSellQuantity"]), ["volume"] = W(data["volume"]),
        };
    }

    private const int BatchSize = 50;

    public async Task<JsonArray> GetMultiQuotesAsync(BrokerSession session, IReadOnlyList<SymbolRecord> instruments, CancellationToken cancellationToken)
    {
        var results = new JsonArray();
        for (var i = 0; i < instruments.Count; i += BatchSize)
        {
            foreach (var item in await BatchAsync(session, instruments.Skip(i).Take(BatchSize).ToList(), cancellationToken)) results.Add(item);
            if (i + BatchSize < instruments.Count) await Task.Delay(sessions.RetryBackoff(Name, TimeSpan.FromSeconds(1)), cancellationToken);
        }

        return results;
    }

    private async Task<List<JsonObject>> BatchAsync(BrokerSession session, List<SymbolRecord> batch, CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, SymbolRecord>();
        var data = new JsonArray();
        foreach (var r in batch)
        {
            data.Add(new JsonObject { ["exchange"] = MapExchange(r.Exchange), ["tradingSymbol"] = r.BrokerSymbol });
            map[$"{MapExchange(r.Exchange)}:{r.BrokerSymbol}"] = r;
        }

        var answer = await DataPostAsync(session, "/getMultiQuotes", new JsonObject { ["data"] = data }, cancellationToken);
        if (!Succeeded(answer)) throw new BrokerException($"{DisplayName} could not give the quotes: {Reason(answer, "no reason was given.")}", 500);

        var results = new List<JsonObject>();
        foreach (var item in (answer["data"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var exchange = Str(item["exchange"]) ?? "";
            var name = Str(item["tradingSymbol"]) ?? "";
            var known = map.GetValueOrDefault($"{exchange}:{name}");
            results.Add(new JsonObject { ["symbol"] = known?.Symbol ?? name, ["exchange"] = known?.Exchange ?? exchange, ["data"] = QuoteObject(ToQuote(item)) });
        }

        return results;
    }
}
