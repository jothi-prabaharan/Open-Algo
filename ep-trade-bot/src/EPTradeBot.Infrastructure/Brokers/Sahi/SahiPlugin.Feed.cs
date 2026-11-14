using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;

namespace EPTradeBot.Infrastructure.Brokers.Sahi;

/// <summary>Sahi's market-data stream: one socket on direct.sahi.com, signed in with the Bearer token. The server pings every 30 seconds and the socket answers.</summary>
public sealed partial class SahiPlugin : IBrokerFeed
{
    public IFeedCodec CreateFeedCodec() => new SahiFeedCodec();

    public FeedEndpoint? FeedEndpoint(BrokerSession session, string? overrideUrl) =>
        string.IsNullOrWhiteSpace(session.AuthToken)
            ? null
            : new FeedEndpoint(new Uri(overrideUrl ?? "wss://direct.sahi.com/v1/stream/marketdata"), new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {session.AuthToken}",
                ["User-Agent"] = "ep-trade-bot/1.0",
            });

    public string? FeedKeepAliveText => null;

    public TimeSpan FeedKeepAliveEvery => TimeSpan.Zero;
}
