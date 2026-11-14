using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.IiflCapital;

/// <summary>
/// IIFL Capital history (broker/iiflcapital/api/data.py): one POST /marketdata/historicaldata for the whole range, the interval spelt out ("5 minutes") and the dates as dd-Mon-yyyy. Candles are read from the
/// shapes OpenAlgo reads: a row of numbers (epoch seconds or milliseconds, or a date text taken as UTC), candles nested in a row, or pipe-separated text. A row that is an object carries its candles only inside
/// "candles", as OpenAlgo reads it; a short list row becomes a zero candle at time zero, as there.
/// </summary>
public sealed partial class IiflCapitalPlugin
{
    private static readonly Dictionary<string, string> Intervals = new()
    {
        ["1m"] = "1 minute", ["5m"] = "5 minutes", ["10m"] = "10 minutes", ["15m"] = "15 minutes", ["30m"] = "30 minutes", ["60m"] = "60 minutes", ["1h"] = "60 minutes", ["D"] = "1 day", ["W"] = "weekly", ["M"] = "monthly",
    };

    public IReadOnlyList<string> HistoryTimeframes { get; } = [.. Intervals.Keys];

    private sealed record Candle(long Timestamp, double Open, double High, double Low, double Close, long Volume, long Oi);

    private static long EpochOf(JsonNode? node)
    {
        var text = Str(node);
        if (text is not null && !(text.Length > 0 && text.All(char.IsAsciiDigit)) && node is JsonValue v && v.TryGetValue<string>(out _))
            return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? new DateTimeOffset(parsed, TimeSpan.Zero).ToUnixTimeSeconds() : 0;
        var seconds = Lng(node);
        return seconds > 1_000_000_000_000L ? seconds / 1000 : seconds;
    }

    private static Candle? CandleFromList(JsonNode? row) => row is JsonArray { Count: >= 6 } a
        ? new Candle(EpochOf(a[0]), Dbl(a[1]), Dbl(a[2]), Dbl(a[3]), Dbl(a[4]), Lng(a[5]), a.Count > 6 ? Lng(a[6]) : 0)
        : null;

    public async Task<JsonArray> GetHistoryAsync(BrokerSession session, SymbolRecord instrument, string interval, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        if (!Intervals.TryGetValue(interval, out var brokerInterval))
            throw new BrokerException($"Interval {interval} is not supported by {DisplayName}. Use one of: {string.Join(", ", Intervals.Keys)}.", 400);

        var payload = Instrument(instrument);
        payload["interval"] = brokerInterval;
        payload["fromDate"] = start.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture);
        payload["toDate"] = end.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture);

        var rows = MarketRows(await DataPostAsync(session, "/marketdata/historicaldata", payload, cancellationToken));
        var candles = new List<Candle>();
        foreach (var raw in rows)
        {
            var row = TryJson(raw);
            if (row is JsonObject obj && TryJson(First(obj, ["candles", "Candles"], new JsonArray())) is JsonArray nested)
            {
                foreach (var one in nested)
                {
                    if (CandleFromList(TryJson(one) is { } inner ? inner : one) is { } c) candles.Add(c);
                }

                continue;
            }

            if (CandleFromList(row) is { } sequence)
            {
                candles.Add(sequence);
                continue;
            }

            if (row is JsonValue sv && sv.TryGetValue<string>(out var text) && text.Contains('|', StringComparison.Ordinal))
            {
                foreach (var piece in text.Split(','))
                {
                    var parts = piece.Split('|');
                    if (parts.Length < 6) continue;
                    candles.Add(new Candle(Lng(JsonValue.Create(parts[0])), Dbl(JsonValue.Create(parts[1])), Dbl(JsonValue.Create(parts[2])), Dbl(JsonValue.Create(parts[3])), Dbl(JsonValue.Create(parts[4])), Lng(JsonValue.Create(parts[5])), parts.Length > 6 ? Lng(JsonValue.Create(parts[6])) : 0));
                }

                continue;
            }

            var flat = SafeDict(row);
            candles.Add(new Candle(EpochOf(First(flat, ["timestamp", "time", "dateTime", "datetime", "epoch", "candleTime"], 0)), Dbl(First(flat, ["open", "o"], 0)), Dbl(First(flat, ["high", "h"], 0)),
                Dbl(First(flat, ["low", "l"], 0)), Dbl(First(flat, ["close", "c"], 0)), Lng(First(flat, ["volume", "v"], 0)), Lng(First(flat, ["oi", "openInterest"], 0))));
        }

        var bars = new SortedDictionary<long, Candle>();
        foreach (var c in candles) bars.TryAdd(c.Timestamp, c);
        return new JsonArray(bars.Values.Select(c => (JsonNode?)new JsonObject
        {
            ["timestamp"] = c.Timestamp, ["open"] = c.Open, ["high"] = c.High, ["low"] = c.Low, ["close"] = c.Close, ["volume"] = c.Volume, ["oi"] = c.Oi,
        }).ToArray());
    }
}
