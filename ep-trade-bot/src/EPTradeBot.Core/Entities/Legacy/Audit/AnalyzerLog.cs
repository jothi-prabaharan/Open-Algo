// Generated from OpenAlgo table 'analyzer_logs' (database.analyzer_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Audit;

/// <summary>Port of OpenAlgo table <c>analyzer_logs</c>.</summary>
public sealed class AnalyzerLog
{
    public int Id { get; set; }
    public string ApiType { get; set; } = "";
    public string RequestData { get; set; } = "";
    public string ResponseData { get; set; } = "";
    public DateTime? CreatedAt { get; set; } = DateTime.UtcNow;
}
