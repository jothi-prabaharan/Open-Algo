namespace EPTradeBot.Core.Scheduling;

public sealed record ControllerResult(bool Ok, string Message);

/// <summary>What the scheduler can ask of the thing that actually runs a strategy.</summary>
public interface IStrategyController
{
    Task<ControllerResult> StartAsync(Guid strategyId, CancellationToken cancellationToken);

    Task<ControllerResult> StopAsync(Guid strategyId, string reason, CancellationToken cancellationToken);
}
