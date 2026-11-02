// Generated from OpenAlgo table 'scalping_tracked_symbol' (database.scalping_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>scalping_tracked_symbol</c>.</summary>
public sealed class ScalpingTrackedSymbol
{
    public int Id { get; set; }
    public string Symbol { get; set; } = "";
    public string Exchange { get; set; } = "";
    public string Product { get; set; } = "";
    public string Mode { get; set; } = "analyze";
    public DateTime? CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; } = DateTime.UtcNow;
}
