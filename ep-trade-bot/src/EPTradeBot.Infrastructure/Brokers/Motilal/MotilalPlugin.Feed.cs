using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;

namespace EPTradeBot.Infrastructure.Brokers.Motilal;

/// <summary>Motilal's market data stream: the broadcast feed at ws1feed.motilaloswal.com, signed in with the client code in a binary packet (the codec has the protocol).</summary>
public sealed partial class MotilalPlugin : IBrokerFeed
{
    public IFeedCodec CreateFeedCodec() => new MotilalFeedCodec();

    public FeedEndpoint? FeedEndpoint(BrokerSession session, string? overrideUrl) =>
        string.IsNullOrWhiteSpace(session.ClientId) || string.IsNullOrWhiteSpace(session.AuthToken) ? null : new FeedEndpoint(new Uri(overrideUrl ?? sessions.FeedUrl(Name) ?? "wss://ws1feed.motilaloswal.com/jwebsocket/jwebsocket"), new Dictionary<string, string>());

    public string? FeedKeepAliveText => null;

    public TimeSpan FeedKeepAliveEvery => TimeSpan.FromSeconds(30);
}
