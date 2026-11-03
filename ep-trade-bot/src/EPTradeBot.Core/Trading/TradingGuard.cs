using EPTradeBot.Core.Entities.Core;

namespace EPTradeBot.Core.Trading;

/// <summary>
/// Environment policy. Only the Production environment may trade a LIVE account;
/// every other environment (Development, Staging, anything unnamed) is PAPER only.
/// The rule is written as "Production allows" rather than "Development forbids", so
/// a misspelt or missing environment name fails closed.
/// </summary>
public static class TradingGuard
{
    public const string ProductionEnvironment = "Production";

    public static bool LiveTradingAllowed(string environmentName) =>
        string.Equals(environmentName, ProductionEnvironment, StringComparison.Ordinal);

    public static bool IsLocked(BrokerAccount account, string environmentName) =>
        account.AccountType == AccountType.LIVE && !LiveTradingAllowed(environmentName);

    /// <summary>Null when the account may receive orders in this environment.</summary>
    public static Refusal? Check(BrokerAccount? account, string environmentName)
    {
        if (account is null)
            return new("account_not_found", "This account does not exist. Pick an account from the dashboard.");
        if (!account.IsEnabled)
            return new("account_disabled", $"The account {account.DisplayName} is turned off. Turn it on before placing orders.");
        if (IsLocked(account, environmentName))
            return new("live_locked",
                $"The account {account.DisplayName} is a Live account, and this is the {environmentName} environment, which trades Paper accounts only. " +
                "Use a Paper account here, or place the order from the Production instance.");
        return null;
    }
}

/// <summary>Resolves the running environment and applies <see cref="TradingGuard"/>.</summary>
public interface ITradingGuardService
{
    string EnvironmentName { get; }
    bool LiveTradingAllowed { get; }
    Refusal? Check(BrokerAccount? account);
}
