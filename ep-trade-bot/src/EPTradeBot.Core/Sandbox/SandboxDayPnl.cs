namespace EPTradeBot.Core.Sandbox;

/// <summary>One sandbox position as the day's P&amp;L sees it.</summary>
public sealed record SandboxPositionState(string Symbol, string Exchange, string Product, int Quantity, decimal AveragePrice, decimal TodayRealizedPnl, DateTime UpdatedAtUtc);

/// <summary>A position's part in the day's P&amp;L. <see cref="Total"/> is today's realized plus, while it is open, its unrealized.</summary>
public sealed record SandboxDayPnlRow(string Symbol, string Exchange, string Product, int Quantity, decimal Unrealized, decimal TodayRealized)
{
    public decimal Total => TodayRealized + Unrealized;
}

/// <summary>
/// The sandbox day P&amp;L of one position, ported from OpenAlgo's
/// sandbox/position_manager.get_open_positions. Pure: the session boundary and the mark price
/// come in as arguments. The risk check and /pnl/symbols both read it, so the two never disagree.
/// </summary>
public static class SandboxDayPnl
{
    /// <summary>
    /// Today's realized P&amp;L counts only for a position changed since the last session boundary
    /// (OpenAlgo's catch-up reset sets it to 0 for one changed before it).
    /// </summary>
    public static decimal TodayRealized(SandboxPositionState p, DateTime sessionStartUtc) =>
        p.UpdatedAtUtc >= sessionStartUtc ? p.TodayRealizedPnl : 0m;

    /// <summary>
    /// Open positions are marked to <paramref name="mark"/>, times the contract value (a crypto
    /// perpetual's 0.01 per unit; 1 elsewhere). A closed one has nothing unrealized.
    /// </summary>
    public static decimal Unrealized(SandboxPositionState p, decimal mark, decimal contractValue = 1m) =>
        p.Quantity == 0 ? 0m : (mark - p.AveragePrice) * p.Quantity * contractValue;

    /// <summary>
    /// Whether the day's book lists the position: one changed this session while it is open or
    /// has realized something today, and from an earlier session only an open NRML position
    /// (MIS and CNC were settled at the boundary).
    /// </summary>
    public static bool Listed(SandboxPositionState p, DateTime sessionStartUtc) =>
        p.UpdatedAtUtc >= sessionStartUtc
            ? p.Quantity != 0 || p.TodayRealizedPnl != 0
            : p.Product == "NRML" && p.Quantity != 0;

    public static SandboxDayPnlRow Row(SandboxPositionState p, decimal mark, decimal contractValue, DateTime sessionStartUtc) =>
        new(p.Symbol, p.Exchange, p.Product, p.Quantity, Unrealized(p, mark, contractValue), TodayRealized(p, sessionStartUtc));
}
