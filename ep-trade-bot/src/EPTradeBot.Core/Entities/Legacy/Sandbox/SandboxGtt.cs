// Generated from OpenAlgo table 'sandbox_gtt' (database.sandbox_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Sandbox;

/// <summary>Port of OpenAlgo table <c>sandbox_gtt</c>.</summary>
public sealed class SandboxGtt
{
    public int Id { get; set; }
    public string GttId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string? Strategy { get; set; }
    public string TriggerType { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Exchange { get; set; } = "";
    public decimal LastPrice { get; set; }
    public string GttStatus { get; set; } = "active";
    public decimal MarginBlocked { get; set; } = 0.0m;
    public DateTime? ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
