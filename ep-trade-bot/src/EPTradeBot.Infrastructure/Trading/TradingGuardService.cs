using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Trading;
using Microsoft.Extensions.Hosting;

namespace EPTradeBot.Infrastructure.Trading;

public sealed class TradingGuardService(IHostEnvironment environment) : ITradingGuardService
{
    public string EnvironmentName => environment.EnvironmentName;

    public bool LiveTradingAllowed => TradingGuard.LiveTradingAllowed(environment.EnvironmentName);

    public Refusal? Check(BrokerAccount? account) => TradingGuard.Check(account, environment.EnvironmentName);
}
