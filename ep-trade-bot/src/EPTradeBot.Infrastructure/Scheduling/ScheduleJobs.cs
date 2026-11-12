using Quartz;

namespace EPTradeBot.Infrastructure.Scheduling;

/// <summary>The Quartz jobs. Each only calls the schedule service; the decision lives in <see cref="Core.Scheduling.ScheduleRules"/>.</summary>
[DisallowConcurrentExecution]
public sealed class ScheduledStartJob(StrategyScheduleService service) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) =>
        await service.RunScheduledStartAsync(Guid.Parse(context.MergedJobDataMap.GetString(ScheduleRegistry.StrategyKey)!), cancellationToken);
}

[DisallowConcurrentExecution]
public sealed class ScheduledStopJob(StrategyScheduleService service) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) =>
        await service.RunScheduledStopAsync(Guid.Parse(context.MergedJobDataMap.GetString(ScheduleRegistry.StrategyKey)!), cancellationToken);
}

[DisallowConcurrentExecution]
public sealed class TradingDayCheckJob(StrategyScheduleService service) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) => await service.RunDailyCheckAsync(cancellationToken);
}

[DisallowConcurrentExecution]
public sealed class MarketHoursEnforcerJob(StrategyScheduleService service) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) => await service.RunEnforcerAsync(cancellationToken);
}

/// <summary>Runs a scheduled workflow, on the days it chose and only when its exchange trades that day.</summary>
[DisallowConcurrentExecution]
public sealed class FlowScheduleJob(Flow.FlowScheduleRunner runner) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) =>
        await runner.RunAsync(Guid.Parse(context.MergedJobDataMap.GetString(ScheduleRegistry.StrategyKey)!), cancellationToken);
}
