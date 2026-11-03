using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Trading;

namespace EPTradeBot.Core.Tests.Trading;

public sealed class TradingGuardTests
{
    private static BrokerAccount Account(AccountType type, bool enabled = true) =>
        new() { DisplayName = "Desk", Broker = "zerodha", AccountType = type, IsEnabled = enabled };

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("production")]
    [InlineData("")]
    [InlineData("Prod")]
    public void Live_accounts_are_locked_everywhere_except_Production(string environment)
    {
        var refusal = TradingGuard.Check(Account(AccountType.LIVE), environment);
        Assert.Equal("live_locked", refusal?.Code);
    }

    [Fact]
    public void Live_accounts_trade_in_Production()
    {
        Assert.Null(TradingGuard.Check(Account(AccountType.LIVE), "Production"));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void Paper_accounts_trade_in_every_environment(string environment)
    {
        Assert.Null(TradingGuard.Check(Account(AccountType.PAPER), environment));
    }

    [Fact]
    public void Disabled_and_missing_accounts_are_refused()
    {
        Assert.Equal("account_disabled", TradingGuard.Check(Account(AccountType.PAPER, enabled: false), "Production")?.Code);
        Assert.Equal("account_not_found", TradingGuard.Check(null, "Production")?.Code);
    }
}
