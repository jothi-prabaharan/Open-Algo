using EPTradeBot.Core.Calendar;
using EPTradeBot.Core.Scheduling;
using EPTradeBot.Core.Time;
using EPTradeBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EPTradeBot.Infrastructure.Flow;

/// <summary>A scheduled workflow firing: runs it unless today is a day it did not choose or its exchange is closed (the rules a scheduled strategy follows).</summary>
public sealed class FlowScheduleRunner(IDbContextFactory<EpTradeDbContext> contexts, IMarketSessions sessions, FlowService flows, TimeProvider clock, ILogger<FlowScheduleRunner> logger)
{
    public async Task<string> RunAsync(Guid flowId, CancellationToken ct)
    {
        var today = Ist.TradingDay(clock.GetUtcNow().UtcDateTime);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var flow = await db.FlowDefinitions.AsNoTracking().SingleOrDefaultAsync(f => f.Id == flowId, ct);
        if (flow is null || !flow.IsActive || !flow.IsScheduled) return "skipped";

        var verdict = ScheduleRules.ScheduledStart(
            new StrategySchedule(flow.Exchange, flow.ScheduleTimeIst, null, flow.ScheduleDays, true, true, false, false, null), today, await sessions.SessionsAsync(today, ct));
        if (!verdict.Allowed)
        {
            logger.LogInformation("Scheduled run of workflow {FlowId} skipped: {Reason}.", flowId, verdict.Reason);
            return verdict.Reason!;
        }

        var result = await flows.RunAsync(flowId, "schedule", null, ct);
        return result.AlreadyRunning ? "already_running" : result.Started ? (result.Succeeded ? "completed" : "failed") : "not_started";
    }
}
