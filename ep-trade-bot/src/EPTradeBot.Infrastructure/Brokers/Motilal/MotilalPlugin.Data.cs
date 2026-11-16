using EPTradeBot.Core.Brokers;

namespace EPTradeBot.Infrastructure.Brokers.Motilal;

public sealed partial class MotilalPlugin
{
    /// <summary>The last price of an instrument, for a market order's protection. Quotes are not read yet, so there is none.</summary>
    private Task<double> LastPriceAsync(BrokerSession session, SymbolRecord info, CancellationToken cancellationToken) => Task.FromResult(0.0);
}
