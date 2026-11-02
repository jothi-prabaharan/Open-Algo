// Generated from OpenAlgo table 'chart_preferences' (database.chart_prefs_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>chart_preferences</c>.</summary>
public sealed class ChartPreference
{
    public string UserId { get; set; } = "";
    public string Key { get; set; } = "";
    public string? Value { get; set; }
    public DateTime? UpdatedAt { get; set; } = DateTime.UtcNow;
}
