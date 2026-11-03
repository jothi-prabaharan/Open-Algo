using EPTradeBot.Core.Entities.Core;

namespace EPTradeBot.Core.Trading;

/// <summary>Latest quotes by exchange and symbol. TG3 feeds it from the market-data bus.</summary>
public interface IQuoteSource
{
    Quote? Get(string exchange, string symbol);
}

/// <summary>Exchange freeze quantity for a symbol; 0 when none is known.</summary>
public interface IFreezeQuantityLookup
{
    Task<int> GetAsync(string exchange, string symbol, CancellationToken cancellationToken = default);
}

/// <summary>The single entry point for every order. Applies the environment guard and the account risk rules, routes, and audits.</summary>
public interface IOrderDispatcher
{
    Task<DispatchResult> DispatchAsync(OrderIntent intent, CancellationToken cancellationToken = default);
}

/// <summary>Sends an order for a LIVE account to its broker. Broker plugins arrive in TG3.</summary>
public interface ILiveOrderRouter
{
    Task<DispatchResult> PlaceAsync(BrokerAccount account, OrderIntent intent, CancellationToken cancellationToken = default);
    Task<AccountExposure?> GetExposureAsync(BrokerAccount account, OrderIntent intent, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> CancelAllAsync(BrokerAccount account, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OpenPosition>> GetOpenPositionsAsync(BrokerAccount account, CancellationToken cancellationToken = default);
}

/// <summary>One open position, signed quantity.</summary>
public sealed record OpenPosition(string Symbol, string Exchange, string Product, int NetQuantity);
