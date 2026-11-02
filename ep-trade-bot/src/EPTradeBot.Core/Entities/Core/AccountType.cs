namespace EPTradeBot.Core.Entities.Core;

/// <summary>
/// Where a broker account's orders go. Stored as the exact strings <c>LIVE</c> and
/// <c>PAPER</c>. In the Development environment only PAPER accounts may trade.
/// </summary>
public enum AccountType
{
    PAPER = 0,
    LIVE = 1,
}
