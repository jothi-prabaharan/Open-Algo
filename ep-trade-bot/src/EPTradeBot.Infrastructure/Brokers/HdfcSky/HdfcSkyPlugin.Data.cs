using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using Microsoft.Extensions.Logging;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.HdfcSky;

/// <summary>
/// HDFC Sky market data (broker/hdfcsky/api/data.py). REST offers /fetch-ltp (last price and previous close) and the chart service's candles; there is no REST quote or depth. A quote is the last
/// price and previous close with open, high, low and volume from the latest of the last seven daily candles. OpenAlgo opens a one-off feed connection for the book, last quantity, totals and
/// open interest; EP Trade bot's REST path does not, so those are zero here and the live stream carries them.
/// </summary>
public sealed partial class HdfcSkyPlugin
{
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);
    private static readonly HashSet<string> NfoIndexUnderlyings = ["NIFTY", "BANKNIFTY", "FINNIFTY", "MIDCPNIFTY", "NIFTYNXT50"];
    private static readonly HashSet<string> BfoIndexUnderlyings = ["SENSEX", "SENSEX50", "BANKEX", "FOCIT"];

    private DateTime IstNow() => clock.GetUtcNow().ToOffset(Ist).DateTime;

    private static bool IsIndex(string exchange) => exchange is "NSE_INDEX" or "BSE_INDEX";

    /// <summary>The chart service's series for an instrument: the suffix on a cash broker symbol, INDICES or IDX for an index, and the exchange's own code for a derivative.</summary>
    private static string SeriesType(SymbolRecord r)
    {
        var exchange = r.Exchange;
        var instrument = r.InstrumentType;
        if (exchange is "NSE" or "BSE" || IsIndex(exchange))
        {
            if (IsIndex(exchange)) return exchange == "NSE_INDEX" ? "INDICES" : "IDX";
            var brokerSymbol = r.BrokerSymbol ?? "";
            return brokerSymbol.Contains('-') ? brokerSymbol[(brokerSymbol.LastIndexOf('-') + 1)..] : "EQ";
        }

        switch (exchange)
        {
            case "NFO":
                var nfoIndex = r.Name is { } n1 && NfoIndexUnderlyings.Contains(n1);
                return instrument == "FUT" ? (nfoIndex ? "FUTIDX" : "FUTSTK") : (nfoIndex ? "OPTIDX" : "OPTSTK");
            case "BFO":
                var bfoIndex = r.Name is { } n2 && BfoIndexUnderlyings.Contains(n2);
                return instrument == "FUT" ? (bfoIndex ? "IF" : "SF") : (bfoIndex ? "IO" : "SO");
            case "MCX":
                return instrument == "FUT" ? "FUTCOM" : "OPTFUT";
            case "CDS":
                return instrument == "FUT" ? "FUTCUR" : "OPTCUR";
            default:
                return "EQ";
        }
    }

    /// <summary>The chart service's own symbol vocabulary: series-free for cash, the broker symbol for derivatives, and for an index the compact name and the upper-cased broker name (either may be the one it answers to).</summary>
    private static List<string> ChartSymbols(SymbolRecord r)
    {
        if (IsIndex(r.Exchange))
        {
            var candidates = new List<string> { r.Symbol };
            var brokerForm = (r.BrokerSymbol ?? "").ToUpperInvariant();
            if (brokerForm.Length > 0 && brokerForm != r.Symbol) candidates.Add(brokerForm);
            return candidates;
        }

        return r.Exchange is "NSE" or "BSE" ? [r.Symbol] : [r.BrokerSymbol ?? ""];
    }

    private sealed class ChartRefusal(string message) : Exception(message);

    /// <summary>The raw candle rows for one instrument and date range: [open, high, low, close, volume, oi, "DD-MM-YYYY[ HH:MM]", cumulative volume], in no particular order.</summary>
    private async Task<List<JsonArray>> FetchCandlesAsync(BrokerSession session, SymbolRecord r, string chartType, string start, string end, string? symbol, CancellationToken cancellationToken)
    {
        var query = new Dictionary<string, string>
        {
            ["symbol"] = symbol ?? ChartSymbols(r)[0], ["exchange"] = RestExchange(r.Exchange), ["chartType"] = chartType, ["seriesType"] = SeriesType(r), ["start"] = start, ["end"] = end,
        };
        (int Status, JsonNode? Body) reply = (0, null);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            reply = await CallAsync(session, HttpMethod.Get, "/oapi/charts-api/charts/v1/fetch-candle", query, null, false, cancellationToken);
            // The chart service rate-limits per key: back off and try again rather than fail a long download.
            if (reply.Status == 429 && attempt < 3)
            {
                await Task.Delay(sessions.RetryBackoff(Name, TimeSpan.FromSeconds(0.5 * (1 << attempt))), cancellationToken);
                continue;
            }

            break;
        }

        var payload = reply.Body as JsonObject ?? [];
        var meta = payload["meta"] as JsonObject ?? [];
        if (reply.Status != 200)
        {
            var detail = Str(meta["displayMessage"]) is { Length: > 0 } d ? d : Str(payload["error"]);
            throw new ChartRefusal($"Chart data request failed (HTTP {reply.Status}{(Truthy(JsonValue.Create(detail)) ? ": " + detail : "")})");
        }

        var err = (Str(meta["err_code"]) ?? "").ToLowerInvariant();
        if (err.Length > 0 && err is not ("success" or "ok" or "0")) throw new ChartRefusal(Str(meta["displayMessage"]) is { Length: > 0 } m ? m : $"Chart data request failed ({err})");
        return ((payload["data"] as JsonObject)?["results"] as JsonArray ?? []).OfType<JsonArray>().ToList();
    }

    /// <summary>Open, high, low and volume for the latest of the last seven daily candles; zeros when there is none or the call fails, so a quote never fails for its candle.</summary>
    private async Task<(double Open, double High, double Low, long Volume)> SessionFiguresAsync(BrokerSession session, SymbolRecord r, CancellationToken cancellationToken)
    {
        try
        {
            var today = IstNow();
            var candles = await FetchCandlesAsync(session, r, "DAY", today.AddDays(-7).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), null, cancellationToken);
            if (candles.Count == 0) return (0, 0, 0, 0);
            JsonArray? last = null;
            string? lastKey = null;
            foreach (var candle in candles)
            {
                if (candle.Count < 7) throw new ChartRefusal("short candle");
                var key = Str(candle[6]) ?? "";
                if (last is null || string.CompareOrdinal(key, lastKey) > 0)
                {
                    last = candle;
                    lastKey = key;
                }
            }

            return (Flt(last![0]), Flt(last[1]), Flt(last[2]), Whole(last[4]));
        }
        catch (Exception ex) when (ex is ChartRefusal or BrokerException)
        {
            logger.LogDebug(ex, "{Broker} has no day candle for {Exchange}:{Symbol}; the day figures are zero.", DisplayName, r.Exchange, r.Symbol);
            return (0, 0, 0, 0);
        }
    }

    private async Task<(SymbolRecord Record, double Ltp, double PrevClose, double Open, double High, double Low, long Volume)> QuoteFiguresAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var record = await symbols.FindAsync(Name, symbol, exchange, cancellationToken) ?? throw new BrokerException($"Could not find instrument for {exchange}:{symbol}", 400);
        var code = LtpExchange(record.Exchange);
        var (ltp, prev) = (await FetchLtpAsync(session, [(code, record.Token)], cancellationToken)).GetValueOrDefault((code, record.Token));
        var (open, high, low, volume) = await SessionFiguresAsync(session, record, cancellationToken);
        return (record, ltp, prev, open, high, low, volume);
    }

    public async Task<JsonObject> GetQuoteAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var f = await QuoteFiguresAsync(session, symbol, exchange, cancellationToken);
        return new JsonObject { ["ask"] = 0.0, ["bid"] = 0.0, ["high"] = f.High, ["low"] = f.Low, ["ltp"] = f.Ltp, ["open"] = f.Open, ["prev_close"] = f.PrevClose, ["volume"] = f.Volume, ["oi"] = 0 };
    }

    private static JsonArray EmptyBook() => new(Enumerable.Range(0, 5).Select(_ => (JsonNode?)new JsonObject { ["price"] = 0.0, ["quantity"] = 0 }).ToArray());

    public async Task<JsonObject> GetDepthAsync(BrokerSession session, string symbol, string exchange, CancellationToken cancellationToken)
    {
        var f = await QuoteFiguresAsync(session, symbol, exchange, cancellationToken);
        return new JsonObject
        {
            ["asks"] = EmptyBook(), ["bids"] = EmptyBook(), ["high"] = f.High, ["low"] = f.Low, ["ltp"] = f.Ltp, ["ltq"] = 0, ["oi"] = 0, ["open"] = f.Open, ["prev_close"] = f.PrevClose,
            ["totalbuyqty"] = 0, ["totalsellqty"] = 0, ["volume"] = f.Volume,
        };
    }

    public async Task<JsonArray> GetMultiQuotesAsync(BrokerSession session, IReadOnlyList<SymbolRecord> instruments, CancellationToken cancellationToken)
    {
        var prices = new Dictionary<(string Exchange, string Token), (double Ltp, double PrevClose)>();
        var wanted = instruments.Select(r => (LtpExchange(r.Exchange), r.Token)).ToList();
        for (var start = 0; start < wanted.Count; start += LtpBatch)
        {
            foreach (var (key, price) in await FetchLtpAsync(session, wanted.Skip(start).Take(LtpBatch).ToList(), cancellationToken)) prices[key] = price;
            if (start + LtpBatch < wanted.Count) await Task.Delay(sessions.RetryBackoff(Name, TimeSpan.FromMilliseconds(150)), cancellationToken);
        }

        var results = new JsonArray();
        foreach (var r in instruments)
        {
            results.Add(prices.TryGetValue((LtpExchange(r.Exchange), r.Token), out var p)
                ? new JsonObject
                {
                    ["symbol"] = r.Symbol, ["exchange"] = r.Exchange,
                    ["data"] = new JsonObject { ["ask"] = 0.0, ["bid"] = 0.0, ["high"] = 0.0, ["low"] = 0.0, ["ltp"] = p.Ltp, ["open"] = 0.0, ["prev_close"] = p.PrevClose, ["volume"] = 0, ["oi"] = 0 },
                }
                : new JsonObject { ["symbol"] = r.Symbol, ["exchange"] = r.Exchange, ["error"] = "No quote data available" });
        }

        return results;
    }
}
