namespace EPTradeBot.Core.Entities.Telemetry;

/// <summary>A sample of one message-bus lane's health.</summary>
public sealed class BusLaneStatus
{
    public long Id { get; set; }
    public string LaneName { get; set; } = "";
    public string Status { get; set; } = "";
    public int QueueDepth { get; set; }
    public long MessagesPublished { get; set; }
    public long MessagesDropped { get; set; }
    public DateTime? LastMessageAtUtc { get; set; }
    public DateTime SampledAtUtc { get; set; } = DateTime.UtcNow;
}
