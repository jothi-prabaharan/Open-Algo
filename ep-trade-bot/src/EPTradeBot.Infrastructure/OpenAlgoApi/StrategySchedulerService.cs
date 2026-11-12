using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Contracts.V1;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.Legacy.Core;
using EPTradeBot.Core.Time;
using EPTradeBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EPTradeBot.Infrastructure.OpenAlgoApi;

/// <summary>
/// Starts and squares off batch strategies on their own schedule: <c>{enabled, days, start_time, auto_stop_time, default_mode}</c>, all in IST. A visit fires what is due in the current minute, and a slot is
/// fired once a day; if the platform was down at the slot, a late visit within a minute still fires it (OpenAlgo's 60 second grace), and a longer miss is left alone, because an entry into a market that has
/// moved is worse than none. Starting is refused by the engine when the strategy already runs, so a double visit cannot open two runs.
/// </summary>
public sealed class StrategySchedulerService(OpenAlgoApiService api, IDbContextFactory<EpTradeDbContext> contexts, TimeProvider clock, ILogger<StrategySchedulerService> logger) : BackgroundService
{
    private readonly HashSet<(int Strategy, DateOnly Day, string Slot)> fired = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try { await VisitAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "The strategy scheduler pass failed; it will run again."); }
        }
    }

    private static readonly string[] Days = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    private static TimeOnly? Slot(JsonNode? node) =>
        node?.GetValueKind() == System.Text.Json.JsonValueKind.String && TimeOnly.TryParseExact(node.GetValue<string>(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    /// <summary>One pass: fires every start and stop slot that is due.</summary>
    public async Task VisitAsync(CancellationToken ct)
    {
        var now = Ist.FromUtc(clock.GetUtcNow().UtcDateTime);
        var today = DateOnly.FromDateTime(now);
        List<SmStrategy> strategies;
        await using (var db = await contexts.CreateDbContextAsync(ct))
            strategies = await db.Set<SmStrategy>().AsNoTracking().Where(s => s.Scheduler != null && s.StrategyKind == "batch").ToListAsync(ct);
        fired.RemoveWhere(f => f.Day != today);

        foreach (var strategy in strategies)
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(strategy.Scheduler!) is not JsonObject config || config["enabled"]?.GetValue<bool>() != true) continue;
            var days = (config["days"] as JsonArray)?.Select(d => d?.GetValue<string>()?.ToUpperInvariant()).ToHashSet() ?? [];
            if (!days.Contains(Days[(int)now.DayOfWeek])) continue;
            foreach (var (name, slot) in new[] { ("start", Slot(config["start_time"])), ("stop", Slot(config["auto_stop_time"])) })
            {
                if (slot is not { } at) continue;
                var late = now.TimeOfDay - at.ToTimeSpan();
                if (late < TimeSpan.Zero || late >= TimeSpan.FromSeconds(60) || !fired.Add((strategy.Id, today, name))) continue;
                try { await FireAsync(strategy, name, config["default_mode"]?.GetValue<string>() ?? "sandbox", ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "The scheduled {Slot} of strategy {StrategyId} failed.", name, strategy.Id); }
            }
        }
    }

    private async Task FireAsync(SmStrategy strategy, string slot, string mode, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var account = slot == "stop" && strategy.CurrentRunId is { } runId
            ? await db.Set<SmStrategyRun>().Where(r => r.Id == runId).Select(r => r.BrokerAccount).FirstOrDefaultAsync(ct) is { } id && Guid.TryParseExact(id, "N", out var guid)
                ? await db.Set<BrokerAccount>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == guid, ct) : null
            : await api.AccountForModeAsync(mode, ct);
        if (account is null)
        {
            db.Set<SmStrategyEvent>().Add(new SmStrategyEvent { StrategyId = strategy.Id, UserId = strategy.UserId, Kind = "run_stop_failed", Severity = "critical",
                Message = slot == "start" ? $"The scheduled start did not run: there is no enabled {(mode == "live" ? "live" : "sandbox")} account to trade on. Add or enable one." : "The scheduled stop did not run: the account this run trades on is not available." });
            await db.SaveChangesAsync(ct);
            return;
        }

        var result = slot == "start"
            ? await api.StartRunAsync(account, new StrategyStartRequest { StrategyId = strategy.Id, Mode = mode }, "scheduler", ct)
            : await api.ScheduledStopAsync(strategy.Id, ct);
        if (result.Status != 200) logger.LogWarning("The scheduled {Slot} of strategy {StrategyId} answered {Status}.", slot, strategy.Id, result.Status);
    }
}
