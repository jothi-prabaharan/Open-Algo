// Generated from OpenAlgo table 'scalping_sl_state' (database.scalping_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>scalping_sl_state</c>.</summary>
public sealed class ScalpingSlState
{
    public int Id { get; set; }
    public string Symbol { get; set; } = "";
    public string Exchange { get; set; } = "";
    public string Product { get; set; } = "";
    public string Side { get; set; } = "BUY";
    public string Mode { get; set; } = "analyze";
    public double EntryPrice { get; set; } = 0.0;
    public int Quantity { get; set; } = 0;
    public double? InitialSl { get; set; }
    public bool TrailingEnabled { get; set; } = false;
    public double? TrailingStep { get; set; }
    public double? HighestPrice { get; set; }
    public double? LowestPrice { get; set; }
    public double? CurrentSl { get; set; }
    public double? Target { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; } = DateTime.UtcNow;
}
