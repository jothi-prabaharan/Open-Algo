using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Contracts.V1;
using EPTradeBot.Core.Entities.Core;
using Microsoft.Extensions.Configuration;

namespace EPTradeBot.Infrastructure.OpenAlgoApi;

/// <summary>
/// /history and /intervals, ported from services/history_service.py and intervals_service.py.
/// Candles come from the account's broker, for sandbox accounts too, as in OpenAlgo's analyze mode.
/// </summary>
public sealed partial class OpenAlgoApiService
{
    /// <summary>OpenAlgo's SUPPORTED_INTERVALS: what history accepts, and the only intervals /intervals offers.</summary>
    public static readonly IReadOnlyList<string> SupportedIntervals =
        ["1s", "5s", "10s", "15s", "30s", "45s", "1m", "2m", "3m", "5m", "10m", "15m", "20m", "30m", "1h", "2h", "3h", "4h", "D", "W", "M", "Q", "Y"];

    private readonly SemaphoreSlim _historyGate = new(1, 1);
    private DateTimeOffset _nextHistorySlot = DateTimeOffset.MinValue;

    /// <summary>History calls are spaced 350 ms apart (about 3 a second), as OpenAlgo spaces them for every broker.</summary>
    private async Task HistorySlotAsync(CancellationToken ct)
    {
        var spacing = TimeSpan.FromMilliseconds(configuration.GetValue("OpenAlgoApi:HistorySpacingMs", 350));
        TimeSpan wait;
        await _historyGate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            var slot = now > _nextHistorySlot ? now : _nextHistorySlot;
            wait = slot - now;
            _nextHistorySlot = slot + spacing;
        }
        finally
        {
            _historyGate.Release();
        }

        if (wait > TimeSpan.Zero) await Task.Delay(wait, clock, ct);
    }

    public async Task<V1Result> HistoryAsync(BrokerAccount account, HistoryRequest r, CancellationToken ct)
    {
        if (r.Source == "db")
            return V1Result.Error(501, "Local history (Historify) is not available in EP Trade bot yet. Ask for source \"api\" to read candles from the broker.");
        await HistorySlotAsync(ct);

        if (!Broker<IBrokerHistory>(account, out var history, out var session, out var missing)) return missing!;
        var exchange = r.Exchange.ToUpperInvariant();
        if (await symbols.FindAsync(history!.Name, r.Symbol, exchange, ct) is not { } instrument)
            return V1Result.Error(400, $"Symbol '{r.Symbol}' not found for exchange '{r.Exchange}'. Please verify the symbol name and ensure master contracts are downloaded.");

        var start = DateOnly.ParseExact(r.StartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = DateOnly.ParseExact(r.EndDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return await Guarded(async () =>
        {
            var rows = new JsonArray();
            foreach (var candle in await history.GetHistoryAsync(session!, instrument, r.Interval, start, end, ct))
            {
                var row = (JsonObject)candle!.DeepClone();
                // OpenAlgo adds open interest as 0 when the broker sends none.
                if (!row.ContainsKey("oi")) row["oi"] = 0;
                rows.Add(row);
            }

            return new V1Result(200, new JsonObject { ["status"] = "success", ["data"] = rows });
        });
    }

    public V1Result Intervals(BrokerAccount account)
    {
        if (!Broker<IBrokerHistory>(account, out var history, out _, out var missing)) return missing!;
        // Only what history accepts: a resolution outside the supported list ("60m", which "1h" says better) is not offered.
        var offered = history!.HistoryTimeframes.Where(SupportedIntervals.Contains).ToList();

        static int Number(string interval) => int.TryParse(new string(interval.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0;
        JsonArray Group(Func<string, bool> pick, bool byNumber) =>
            new((byNumber ? offered.Where(pick).OrderBy(Number) : offered.Where(pick).Order(StringComparer.Ordinal)).Select(i => (JsonNode?)i).ToArray());

        return Success(new JsonObject
        {
            ["seconds"] = Group(i => i.EndsWith('s'), true),
            ["minutes"] = Group(i => i.EndsWith('m'), true),
            ["hours"] = Group(i => i.EndsWith('h'), true),
            ["days"] = Group(i => i == "D", false),
            ["weeks"] = Group(i => i == "W", false),
            ["months"] = Group(i => i == "M", false),
        });
    }
}
