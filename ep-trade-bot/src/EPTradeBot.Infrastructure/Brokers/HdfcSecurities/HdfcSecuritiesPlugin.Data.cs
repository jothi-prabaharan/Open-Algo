using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;

namespace EPTradeBot.Infrastructure.Brokers.HdfcSecurities;

/// <summary>
/// HDFC Securities market data (broker/hdfcsecurities/api/data.py). /fetch-ltp is the only REST market-data call and gives the last price and the previous close; open, high, low, volume,
/// open interest, the best bid and ask and the depth book exist only on the live feed. OpenAlgo opens a one-off feed connection to fill them. EP Trade bot's REST path does not: those
/// fields are zero here, and the running feed (the WebSocket stream) is where a trader reads them. HDFC Securities has no historical candle call at all.
/// </summary>
public sealed partial class HdfcSecuritiesPlugin
{
    private async Task<(double Ltp, double PrevClose)> PriceAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var record = await symbols.FindAsync(Name, symbol, exchange, cancellationToken) ?? throw new BrokerException($"Could not find instrument for {exchange}:{symbol}", 400);
        var prices = await FetchLtpAsync(session, [(record.Exchange, record.Token)], cancellationToken);
        return prices.GetValueOrDefault((record.Exchange, record.Token), (0.0, 0.0));
    }

    public async Task<JsonObject> GetQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var (ltp, prev) = await PriceAsync(session, symbol, exchange, cancellationToken);
        return Quote(ltp, prev, 0);
    }

    private static JsonObject Quote(double ltp, double prev, long oi) => new()
    {
        ["ask"] = 0.0, ["bid"] = 0.0, ["high"] = 0.0, ["low"] = 0.0, ["ltp"] = ltp, ["open"] = 0.0, ["prev_close"] = prev, ["volume"] = 0, ["oi"] = oi,
    };

    private static JsonArray EmptyBook() => new(Enumerable.Range(0, 5).Select(_ => (JsonNode?)new JsonObject { ["price"] = 0.0, ["quantity"] = 0 }).ToArray());

    public async Task<JsonObject> GetDepthAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var (ltp, prev) = await PriceAsync(session, symbol, exchange, cancellationToken);
        return new JsonObject
        {
            ["asks"] = EmptyBook(), ["bids"] = EmptyBook(), ["high"] = 0.0, ["low"] = 0.0, ["ltp"] = ltp, ["ltq"] = 0, ["oi"] = 0, ["open"] = 0.0, ["prev_close"] = prev,
            ["totalbuyqty"] = 0, ["totalsellqty"] = 0, ["volume"] = 0,
        };
    }

    public async Task<JsonArray> GetMultiQuotesAsync(BrokerSession session, IReadOnlyList<SymbolRecord> instruments, CancellationToken cancellationToken)
    {
        var prices = new Dictionary<(string Exchange, string Token), (double Ltp, double PrevClose)>();
        for (var start = 0; start < instruments.Count; start += LtpBatch)
        {
            var batch = instruments.Skip(start).Take(LtpBatch).Select(r => (r.Exchange, r.Token)).ToList();
            foreach (var (key, price) in await FetchLtpAsync(session, batch, cancellationToken)) prices[key] = price;
            if (start + LtpBatch < instruments.Count) await Task.Delay(sessions.RetryBackoff(Name, TimeSpan.FromMilliseconds(150)), cancellationToken);
        }

        var results = new JsonArray();
        foreach (var r in instruments)
        {
            results.Add(prices.TryGetValue((r.Exchange, r.Token), out var p)
                ? new JsonObject { ["symbol"] = r.Symbol, ["exchange"] = r.Exchange, ["data"] = Quote(p.Ltp, p.PrevClose, 0) }
                : new JsonObject { ["symbol"] = r.Symbol, ["exchange"] = r.Exchange, ["error"] = "No quote data available" });
        }

        return results;
    }
}
