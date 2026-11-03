using EPTradeBot.Core.Entities.Audit;
using EPTradeBot.Core.Entities.Core;

namespace EPTradeBot.Core.Trading;

/// <summary>What the account holds right now, as the risk rules need it. Built by the caller; no I/O here.</summary>
public sealed record AccountExposure
{
    /// <summary>Realised plus marked-to-market P&amp;L for the current IST trading day, INR.</summary>
    public decimal DayPnl { get; init; }

    /// <summary>Sum of |net quantity| x mark price over open positions, INR.</summary>
    public decimal GrossOpenValue { get; init; }

    /// <summary>Signed net quantity already held in the order's symbol, exchange and product.</summary>
    public int NetQuantityInInstrument { get; init; }

    /// <summary>Mark price of the instrument (LTP), if known.</summary>
    public decimal? MarkPrice { get; init; }

    /// <summary>Exchange freeze quantity; 0 when no limit is known (never read as 1).</summary>
    public int FreezeQuantity { get; init; }
}

/// <summary>
/// Per-account limits, checked before every order. Pure: the caller supplies the
/// settings, the exposure and the intent. Two principles shape it:
/// an order that only reduces what is held is never refused by a loss or exposure
/// limit (refusing an exit is how a position ends up with nothing able to close it),
/// and a check that cannot be computed refuses rather than passes.
/// </summary>
public static class AccountRiskRules
{
    public static Refusal? Check(BrokerAccount account, AccountRiskSetting? settings, AccountExposure exposure, OrderIntent intent)
    {
        if (account.KillSwitchEngaged && intent.Source != OrderSource.KillSwitch)
            return new("kill_switch_engaged",
                $"The kill switch is on for {account.DisplayName}, so new orders are blocked. Turn it off on the dashboard to trade again.");

        if (intent.Quantity <= 0)
            return new("quantity_invalid", "The quantity must be a whole number above zero.");

        if (exposure.FreezeQuantity > 0 && intent.Quantity > exposure.FreezeQuantity)
            return new("above_freeze_quantity",
                $"{intent.Quantity} is above the exchange freeze limit of {exposure.FreezeQuantity} for {intent.Symbol}. Send it as a split order.");

        var current = exposure.NetQuantityInInstrument;
        var after = current + intent.Sign * intent.Quantity;
        var reducesOnly = current != 0 && Math.Sign(current) != intent.Sign && intent.Quantity <= Math.Abs(current);
        if (reducesOnly || intent.Source == OrderSource.KillSwitch) return null;

        if (settings is null || !settings.IsEnabled)
        {
            return account.AccountType == AccountType.LIVE
                ? new("risk_limits_missing", $"Set the risk limits for {account.DisplayName} before trading it live.")
                : null;
        }

        if (intent.Quantity > settings.MaxOrderQuantity)
            return new("above_max_order_quantity",
                $"{intent.Quantity} is above this account's maximum of {settings.MaxOrderQuantity} per order. Lower the quantity or raise the limit on the account card.");

        if (exposure.DayPnl <= -settings.MaxDailyLoss)
            return new("max_daily_loss_reached",
                $"Today's loss of {Inr(-exposure.DayPnl)} has reached this account's limit of {Inr(settings.MaxDailyLoss)}. New positions are blocked for the rest of the day; exits are still allowed.");

        var price = intent.PriceType is "LIMIT" or "SL" && intent.Price > 0 ? intent.Price : exposure.MarkPrice;
        if (price is not { } p || p <= 0)
            return new("price_unavailable",
                $"There is no live price for {intent.Symbol} yet, so the position limit cannot be checked. Try again once the price is showing.");

        var currentValue = Math.Abs(current) * p;
        var projected = exposure.GrossOpenValue - currentValue + Math.Abs(after) * p;
        if (projected > settings.MaxPositionValue)
            return new("above_max_position_value",
                $"This order would take open positions to {Inr(projected)}, above this account's limit of {Inr(settings.MaxPositionValue)}.");

        return null;
    }

    private static string Inr(decimal amount) => "INR " + amount.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("en-IN"));
}

/// <summary>Loads what <see cref="AccountRiskRules"/> needs and applies it.</summary>
public interface IAccountRiskManager
{
    Task<Refusal?> CheckAsync(BrokerAccount account, OrderIntent intent, CancellationToken cancellationToken = default);
}
