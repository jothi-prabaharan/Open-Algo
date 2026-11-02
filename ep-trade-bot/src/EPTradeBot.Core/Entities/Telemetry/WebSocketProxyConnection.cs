namespace EPTradeBot.Core.Entities.Telemetry;

/// <summary>A client connection to the market-data WebSocket proxy (port 8765).</summary>
public sealed class WebSocketProxyConnection
{
    public long Id { get; set; }
    public string ConnectionId { get; set; } = "";
    public string? ClientIp { get; set; }
    public string? UserAgent { get; set; }
    public DateTime ConnectedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? DisconnectedAtUtc { get; set; }
    public int SubscriptionCount { get; set; }
    public long MessagesSent { get; set; }
    public string? DisconnectReason { get; set; }
}
