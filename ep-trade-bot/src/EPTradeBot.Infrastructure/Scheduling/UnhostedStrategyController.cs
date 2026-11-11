using EPTradeBot.Core.Scheduling;

namespace EPTradeBot.Infrastructure.Scheduling;

/// <summary>
/// The controller used until a process host is registered: it starts and stops nothing, and says so, so the schedule never records a strategy as
/// running when no process was started.
/// </summary>
public sealed class UnhostedStrategyController : IStrategyController
{
    private const string Message = "Strategy hosting is not available on this install yet, so nothing was started or stopped.";

    public Task<ControllerResult> StartAsync(Guid strategyId, CancellationToken cancellationToken) => Task.FromResult(new ControllerResult(false, Message));

    public Task<ControllerResult> StopAsync(Guid strategyId, string reason, CancellationToken cancellationToken) => Task.FromResult(new ControllerResult(false, Message));
}
