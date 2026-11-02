// Generated from OpenAlgo table 'chartink_strategies' (database.chartink_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>chartink_strategies</c>.</summary>
public sealed class ChartinkStrategy
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string WebhookId { get; set; } = "";
    public string UserId { get; set; } = "";
    public bool? IsActive { get; set; } = true;
    public bool? IsIntraday { get; set; } = true;
    public string? StartTime { get; set; }
    public string? EndTime { get; set; }
    public string? SquareoffTime { get; set; }
    public DateTime? CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
