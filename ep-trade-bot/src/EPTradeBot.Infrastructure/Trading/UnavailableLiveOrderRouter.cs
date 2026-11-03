using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Trading;

namespace EPTradeBot.Infrastructure.Trading;

/// <summary>
/// Stands in until the broker plugins of Task Group 3. It fails closed: no order is
/// sent, exposure is unknown (so risk checks refuse), and nothing is reported as done.
/// </summary>
public sealed class UnavailableLiveOrderRouter : ILiveOrderRouter
{
    public Task<DispatchResult> PlaceAsync(BrokerAccount account, OrderIntent intent, CancellationToken cancellationToken = default) =>
        Task.FromResult(new DispatchResult(DispatchStatus.Failed,
            $"Orders for {account.DisplayName} cannot reach {account.Broker} yet: live broker connections are not set up in this build. Nothing was sent.",
            Code: "live_router_unavailable"));

    public Task<AccountExposure?> GetExposureAsync(BrokerAccount account, OrderIntent intent, CancellationToken cancellationToken = default) =>
        Task.FromResult<AccountExposure?>(null);

    public Task<IReadOnlyList<string>> CancelAllAsync(BrokerAccount account, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException($"Live broker connections are not set up in this build, so open orders at {account.Broker} could not be cancelled.");

    public Task<IReadOnlyList<OpenPosition>> GetOpenPositionsAsync(BrokerAccount account, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException($"Live broker connections are not set up in this build, so positions at {account.Broker} could not be read.");
}
