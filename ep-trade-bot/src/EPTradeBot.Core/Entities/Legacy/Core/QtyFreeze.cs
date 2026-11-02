// Generated from OpenAlgo table 'qty_freeze' (database.qty_freeze_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>qty_freeze</c>.</summary>
public sealed class QtyFreeze
{
    public int Id { get; set; }
    public string Exchange { get; set; } = "";
    public string Symbol { get; set; } = "";
    public int FreezeQty { get; set; }
}
