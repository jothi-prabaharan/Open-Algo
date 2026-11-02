// Generated from OpenAlgo table 'master_contract_status' (database.master_contract_status_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>master_contract_status</c>.</summary>
public sealed class MasterContractStatus
{
    public string Broker { get; set; } = "";
    public string? Status { get; set; } = "pending";
    public string? Message { get; set; }
    public DateTime? LastUpdated { get; set; } = DateTime.UtcNow;
    public string? TotalSymbols { get; set; } = "0";
    public bool? IsReady { get; set; } = false;
    public DateTime? LastDownloadTime { get; set; }
    public DateOnly? DownloadDate { get; set; }
    public string? ExchangeStats { get; set; }
    public int? DownloadDurationSeconds { get; set; }
}
