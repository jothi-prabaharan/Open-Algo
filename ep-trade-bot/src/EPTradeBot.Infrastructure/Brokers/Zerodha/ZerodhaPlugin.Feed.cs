using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;

namespace EPTradeBot.Infrastructure.Brokers.Zerodha;

/// <summary>The Kite ticker (broker/zerodha/streaming). The account keeps api_key:access_token; the stream takes the two apart.</summary>
public sealed partial class ZerodhaPlugin
{
    public IFeedCodec CreateFeedCodec() => new KiteFeedCodec();

    public FeedEndpoint? FeedEndpoint(BrokerSession session, string? overrideUrl)
    {
        var key = session.Key("ApiKey");
        var parts = session.AuthToken.Split(':', 2);
        var accessToken = parts.Length == 2 ? parts[1] : session.AuthToken;
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(accessToken)) return null;
        var root = (overrideUrl ?? "wss://ws.kite.trade").TrimEnd('/');
        return new FeedEndpoint(new Uri($"{root}?api_key={Uri.EscapeDataString(key)}&access_token={Uri.EscapeDataString(accessToken)}"), new Dictionary<string, string>());
    }

    // Kite sends its own one-byte heartbeat every second.
    public string? FeedKeepAliveText => null;

    public TimeSpan FeedKeepAliveEvery => TimeSpan.Zero;
}
