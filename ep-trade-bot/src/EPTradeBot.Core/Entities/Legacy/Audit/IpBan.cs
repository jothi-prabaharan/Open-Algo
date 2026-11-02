// Generated from OpenAlgo table 'ip_bans' (database.traffic_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Audit;

/// <summary>Port of OpenAlgo table <c>ip_bans</c>.</summary>
public sealed class IpBan
{
    public int Id { get; set; }
    public string IpAddress { get; set; } = "";
    public string? BanReason { get; set; }
    public int? BanCount { get; set; } = 1;
    public DateTime? BannedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public bool? IsPermanent { get; set; } = false;
    public string? CreatedBy { get; set; } = "system";
}
