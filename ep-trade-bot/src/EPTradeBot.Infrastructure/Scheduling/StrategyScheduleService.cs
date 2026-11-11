using EPTradeBot.Core.Calendar;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Scheduling;
using EPTradeBot.Core.Time;
using EPTradeBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EPTradeBot.Infrastructure.Scheduling;

/// <summary>
/// What the scheduled jobs do when they fire: read the strategy, ask <see cref="ScheduleRules"/>, and start or stop it through the controller.
/// Port of scheduled_start_strategy, scheduled_stop_strategy, the 00:01 trading-day check and the per-minute enforcer in OpenAlgo.
/// </summary>
public sealed class StrategyScheduleService(
    IDbContextFactory<EpTradeDbContext> contexts,
    IMarketSessions sessions,
    IStrategyController controller,
    TimeProvider clock,
    ILogger<StrategyScheduleService> logger)
{
    public static StrategySchedule Snapshot(HostedStrategy s) =>
        new(s.Exchange, s.ScheduleStartIst, s.ScheduleStopIst, s.ScheduleDays, s.IsScheduled, s.IsEnabled, s.IsRunning, s.ManuallyStopped, s.PausedReason);

    /// <summary>A scheduled start firing. Returns what was decided, for the log and for tests.</summary>
    public async Task<string> RunScheduledStartAsync(Guid id, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var today = Ist.TradingDay(now);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var s = await db.HostedStrategies.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (s is null || !s.IsEnabled || !s.IsScheduled) return "skipped";

        var verdict = ScheduleRules.ScheduledStart(Snapshot(s), today, await sessions.SessionsAsync(today, ct));
        if (!verdict.Allowed)
        {
            // A calendar refusal is a pause the enforcer lifts when the exchange trades; a trader's stop or an off day is not.
            s.PausedReason = verdict.Reason is ScheduleRules.Weekend or ScheduleRules.Holiday ? verdict.Reason : s.PausedReason;
            s.LastError = verdict.Message;
            s.UpdatedAtUtc = now;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Scheduled start of strategy {StrategyId} refused: {Reason}.", id, verdict.Reason);
            return verdict.Reason!;
        }

        return await StartAsync(db, s, now, ct);
    }

    /// <summary>A scheduled stop firing: stops the strategy and clears any pause, so the next start is judged afresh.</summary>
    public async Task<string> RunScheduledStopAsync(Guid id, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var s = await db.HostedStrategies.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (s is null || !s.IsScheduled) return "skipped";
        s.PausedReason = null;
        s.UpdatedAtUtc = now;
        if (!s.IsRunning)
        {
            await db.SaveChangesAsync(ct);
            return "not_running";
        }

        var result = await controller.StopAsync(id, "scheduled_stop", ct);
        MarkStopped(s, result, now);
        await db.SaveChangesAsync(ct);
        return result.Ok ? "stopped" : "stop_failed";
    }

    /// <summary>The 00:01 IST check over every scheduled strategy.</summary>
    public async Task<int> RunDailyCheckAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var today = Ist.TradingDay(now);
        var todays = await sessions.SessionsAsync(today, ct);
        return await ApplyAsync(now, s => ScheduleRules.DailyCheck(Snapshot(s), today, todays), ct);
    }

    /// <summary>The per-minute enforcer over every scheduled strategy.</summary>
    public async Task<int> RunEnforcerAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var todays = await sessions.SessionsAsync(Ist.TradingDay(now), ct);
        return await ApplyAsync(now, s => ScheduleRules.Enforce(Snapshot(s), now, todays), ct);
    }

    /// <summary>The trader starts a strategy by hand: it is no longer "stopped by hand", and any pause ends.</summary>
    public async Task<ControllerResult> StartManuallyAsync(Guid id, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var s = await db.HostedStrategies.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return new ControllerResult(false, "No such strategy.");
        s.ManuallyStopped = false;
        s.PausedReason = null;
        await StartAsync(db, s, now, ct);
        return s.IsRunning ? new ControllerResult(true, "Started.") : new ControllerResult(false, s.LastError ?? "The strategy could not be started.");
    }

    /// <summary>The trader stops a strategy by hand: the schedule will not bring it back until they start it.</summary>
    public async Task<ControllerResult> StopManuallyAsync(Guid id, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var s = await db.HostedStrategies.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return new ControllerResult(false, "No such strategy.");
        s.ManuallyStopped = true;
        s.PausedReason = null;
        s.UpdatedAtUtc = now;
        var result = s.IsRunning ? await controller.StopAsync(id, "manual_stop", ct) : new ControllerResult(true, "Already stopped.");
        if (s.IsRunning) MarkStopped(s, result, now);
        await db.SaveChangesAsync(ct);
        return result;
    }

    private async Task<int> ApplyAsync(DateTime now, Func<HostedStrategy, EnforcerAction> decide, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var all = await db.HostedStrategies.Where(x => x.IsScheduled && x.IsEnabled).ToListAsync(ct);
        var acted = 0;
        foreach (var s in all)
        {
            try
            {
                var action = decide(s);
                if (action.Kind == EnforcerKind.None) continue;
                if (action.Kind == EnforcerKind.Stop)
                {
                    var result = await controller.StopAsync(s.Id, action.Reason ?? "market_closed", ct);
                    MarkStopped(s, result, now);
                    if (result.Ok) s.PausedReason = action.Reason;
                }
                else
                {
                    await StartAsync(db, s, now, ct);
                }

                s.UpdatedAtUtc = now;
                acted++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One strategy failing never stops the others being looked at.
                logger.LogError(ex, "The schedule check failed for strategy {StrategyId}.", s.Id);
            }
        }

        await db.SaveChangesAsync(ct);
        return acted;
    }

    private async Task<string> StartAsync(EpTradeDbContext db, HostedStrategy s, DateTime now, CancellationToken ct)
    {
        s.UpdatedAtUtc = now;
        if (s.IsRunning)
        {
            s.PausedReason = null;
            await db.SaveChangesAsync(ct);
            return "already_running";
        }

        var result = await controller.StartAsync(s.Id, ct);
        if (result.Ok)
        {
            s.IsRunning = true;
            s.LastStartedAtUtc = now;
            s.PausedReason = null;
            s.LastError = null;
        }
        else
        {
            s.LastError = Trim(result.Message);
        }

        await db.SaveChangesAsync(ct);
        return result.Ok ? "started" : "start_failed";
    }

    private static void MarkStopped(HostedStrategy s, ControllerResult result, DateTime now)
    {
        if (result.Ok)
        {
            s.IsRunning = false;
            s.ProcessId = null;
            s.LastStoppedAtUtc = now;
            s.LastError = null;
        }
        else
        {
            s.LastError = Trim(result.Message);
        }
    }

    private static string Trim(string message) => message.Length <= 500 ? message : message[..500];
}
