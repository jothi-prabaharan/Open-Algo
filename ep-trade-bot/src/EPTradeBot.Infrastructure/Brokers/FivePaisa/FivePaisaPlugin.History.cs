using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.FivePaisa;

/// <summary>
/// 5paisa history (broker/fivepaisa/api/data.py): GET /V2/historical/{exchange}/{type}/{token}/{interval}, a hundred days a call for daily candles and thirty for the rest. A chunk that fails or answers with an error is
/// left out, so a range where every chunk fails is empty rather than an error. Candles outside the chunk's dates are dropped (5paisa answers a range with no sessions with its latest candle), an empty candle always, and, for a
/// share, a daily candle with no volume or no range. Daily candles are stamped midnight UTC of their date, the rest with their Indian wall-clock time.
/// </summary>
public sealed partial class FivePaisaPlugin
{
    private static readonly Dictionary<string, string> Intervals = new() { ["1m"] = "1m", ["5m"] = "5m", ["10m"] = "10m", ["15m"] = "15m", ["30m"] = "30m", ["1h"] = "60m", ["D"] = "1d" };

    public IReadOnlyList<string> HistoryTimeframes { get; } = ["1m", "5m", "10m", "15m", "30m", "1h", "D"];

    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    public async Task<JsonArray> GetHistoryAsync(BrokerSession session, SymbolRecord instrument, string interval, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        if (!Intervals.TryGetValue(interval, out var code)) throw new BrokerException($"Interval {interval} is not supported by {DisplayName}. Use one of: {string.Join(", ", HistoryTimeframes)}.", 400);
        var daily = interval == "D";
        var index = instrument.Exchange.EndsWith("_INDEX", StringComparison.Ordinal);
        var chunkDays = daily ? 100 : 30;
        var bars = new SortedDictionary<long, JsonObject>();
        for (var from = start; from <= end;)
        {
            var to = from.AddDays(chunkDays - 1) < end ? from.AddDays(chunkDays - 1) : end;
            var path = $"/V2/historical/{MapExchange(instrument.Exchange)}/{MapExchangeType(instrument.Exchange)}/{instrument.Token}/{code}?from={from:yyyy-MM-dd}&end={to:yyyy-MM-dd}";
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, sessions.BaseUrl(Name, DefaultBaseUrl) + path);
                request.Headers.TryAddWithoutValidation("Authorization", $"bearer {session.AuthToken}");
                using var response = await Client().SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode && await ReadAsync(response, cancellationToken) is JsonObject body && Str(body["status"]) == "success")
                {
                    foreach (var candle in (((body["data"] as JsonObject)?["candles"]) as JsonArray)?.OfType<JsonArray>() ?? []) Add(bars, candle, from, to, daily, index);
                }
            }
            catch (HttpRequestException)
            {
            }

            from = to.AddDays(1);
        }

        return new JsonArray(bars.Values.Select(v => (JsonNode?)v).ToArray());
    }

    private static void Add(SortedDictionary<long, JsonObject> bars, JsonArray candle, DateOnly from, DateOnly to, bool daily, bool index)
    {
        if (candle.Count < 6) return;
        if (!DateTime.TryParseExact(Str(candle[0]), "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment)) return;
        var day = DateOnly.FromDateTime(moment);
        if (day < from || day > to) return;
        double open = Float(candle[1]), high = Float(candle[2]), low = Float(candle[3]), close = Float(candle[4]);
        long volume = Int(candle[5]);
        var empty = open == 0 && high == 0 && low == 0 && close == 0;
        if (index || !daily ? empty : volume == 0 || empty || high == low) return;
        var epoch = daily ? new DateTimeOffset(moment.Date, TimeSpan.Zero).ToUnixTimeSeconds() : new DateTimeOffset(moment, Ist).ToUnixTimeSeconds();
        bars.TryAdd(epoch, new JsonObject { ["timestamp"] = epoch, ["open"] = open, ["high"] = high, ["low"] = low, ["close"] = close, ["volume"] = volume, ["oi"] = 0 });
    }
}
