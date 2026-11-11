using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Scheduling;
using EPTradeBot.Core.Time;
using EPTradeBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;

namespace EPTradeBot.Infrastructure.Scheduling;

/// <summary>
/// Keeps Quartz's triggers equal to the strategies in the database: a start and a stop trigger per enabled, scheduled strategy, in IST on its days, and
/// the two system jobs (the 00:01 trading-day check and the per-minute enforcer). The database is the source of truth, so Quartz holds its jobs in
/// memory and they are rebuilt at every start.
/// </summary>
public sealed class ScheduleRegistry(ISchedulerFactory factory, IDbContextFactory<EpTradeDbContext> contexts, ILogger<ScheduleRegistry> logger) : IHostedService
{
    public const string StrategyKey = "strategy";
    public const string Group = "strategies";
    public const string SystemGroup = "system";

    public static JobKey StartJobKey(Guid id) => new($"start:{id:N}", Group);

    public static JobKey StopJobKey(Guid id) => new($"stop:{id:N}", Group);

    public static readonly JobKey DailyCheckKey = new("trading-day-check", SystemGroup);
    public static readonly JobKey EnforcerKey = new("market-hours-enforcer", SystemGroup);

    /// <summary>Rebuilds the triggers of one strategy from its row (or removes them when the row is gone, disabled or unscheduled).</summary>
    public async Task SyncAsync(Guid id, CancellationToken ct)
    {
        var scheduler = await factory.GetScheduler(ct);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var s = await db.HostedStrategies.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        await ApplyAsync(scheduler, id, s, ct);
    }

    /// <summary>Starts the system jobs and puts every strategy's triggers in place.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        var scheduler = await factory.GetScheduler(ct);
        await AddSystemJobAsync<TradingDayCheckJob>(scheduler, DailyCheckKey, "0 1 0 * * ?", ct);
        await AddSystemJobAsync<MarketHoursEnforcerJob>(scheduler, EnforcerKey, "0 * * * * ?", ct);

        await using var db = await contexts.CreateDbContextAsync(ct);
        foreach (var s in await db.HostedStrategies.AsNoTracking().ToListAsync(ct))
            await ApplyAsync(scheduler, s.Id, s, ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task ApplyAsync(IScheduler scheduler, Guid id, HostedStrategy? s, CancellationToken ct)
    {
        await scheduler.DeleteJob(StartJobKey(id), ct);
        await scheduler.DeleteJob(StopJobKey(id), ct);
        if (s is null || !s.IsEnabled || !s.IsScheduled) return;

        if (s.ScheduleStartIst is { } start) await AddAsync<ScheduledStartJob>(scheduler, StartJobKey(id), id, start, s.ScheduleDays, ct);
        if (s.ScheduleStopIst is { } stop) await AddAsync<ScheduledStopJob>(scheduler, StopJobKey(id), id, stop, s.ScheduleDays, ct);
        logger.LogInformation("Strategy {StrategyId} scheduled: start {Start}, stop {Stop}, days {Days}.", id, s.ScheduleStartIst, s.ScheduleStopIst, s.ScheduleDays);
    }

    private static async Task AddAsync<TJob>(IScheduler scheduler, JobKey key, Guid id, TimeOnly time, TradingDays days, CancellationToken ct)
        where TJob : IJob
    {
        var job = JobBuilder.Create<TJob>().WithIdentity(key).UsingJobData(StrategyKey, id.ToString()).Build();
        var trigger = TriggerBuilder.Create().WithIdentity(key.Name, key.Group).ForJob(job)
            .WithCronSchedule(ScheduleRules.Cron(time, days), x => x.InTimeZone(Ist.Zone).WithMisfireInstruction(CronTriggerMisfireInstruction.DoNothing))
            .Build();
        await scheduler.ScheduleJob(job, trigger, default, ct);
    }

    private static async Task AddSystemJobAsync<TJob>(IScheduler scheduler, JobKey key, string cron, CancellationToken ct)
        where TJob : IJob
    {
        if (await scheduler.Exists(key, ct)) return;
        var job = JobBuilder.Create<TJob>().WithIdentity(key).Build();
        var trigger = TriggerBuilder.Create().WithIdentity(key.Name, key.Group).ForJob(job)
            .WithCronSchedule(cron, x => x.InTimeZone(Ist.Zone).WithMisfireInstruction(CronTriggerMisfireInstruction.DoNothing))
            .Build();
        await scheduler.ScheduleJob(job, trigger, default, ct);
    }
}
