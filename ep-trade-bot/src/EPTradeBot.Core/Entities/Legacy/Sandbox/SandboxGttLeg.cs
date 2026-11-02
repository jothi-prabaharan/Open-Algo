// Generated from OpenAlgo table 'sandbox_gtt_legs' (database.sandbox_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Sandbox;

/// <summary>Port of OpenAlgo table <c>sandbox_gtt_legs</c>.</summary>
public sealed class SandboxGttLeg
{
    public int Id { get; set; }
    public string GttId { get; set; } = "";
    public int LegNumber { get; set; }
    public decimal TriggerPrice { get; set; }
    public string TriggerDirection { get; set; } = "below";
    public string Action { get; set; } = "";
    public int Quantity { get; set; }
    public decimal Price { get; set; }
    public string Pricetype { get; set; } = "LIMIT";
    public string Product { get; set; } = "";
    public string LegStatus { get; set; } = "pending";
    public string? TriggeredOrderId { get; set; }
    public decimal LegMargin { get; set; } = 0.0m;
    public DateTime? ClaimedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
