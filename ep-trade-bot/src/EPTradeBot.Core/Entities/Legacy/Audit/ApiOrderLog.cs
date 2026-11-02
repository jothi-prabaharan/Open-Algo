// Generated from OpenAlgo table 'order_logs' (database.apilog_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Audit;

/// <summary>Port of OpenAlgo table <c>order_logs</c>.</summary>
public sealed class ApiOrderLog
{
    public int Id { get; set; }
    public string ApiType { get; set; } = "";
    public string RequestData { get; set; } = "";
    public string ResponseData { get; set; } = "";
    public DateTime? CreatedAt { get; set; } = DateTime.UtcNow;
}
