using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;

namespace EPTradeBot.Infrastructure.Brokers.Tradejini;

/// <summary>
/// Tradejini's stream (broker/tradejini/streaming): one address that carries the token as the API key, a colon and the access token (the token alone when it already holds a colon) and the client version; there is no sign-in frame.
/// A session with no access token, or no API key to put before it, has no feed.
/// </summary>
public sealed partial class TradejiniPlugin : IBrokerFeed
{
    private const string FeedAddress = "wss://api.tradejini.com/v2.1/stream";

    public IFeedCodec CreateFeedCodec() => new TradejiniFeedCodec();

    public FeedEndpoint? FeedEndpoint(BrokerSession session, string? overrideUrl)
    {
        if (string.IsNullOrWhiteSpace(session.AuthToken)) return null;
        string token;
        if (session.AuthToken.Contains(':', StringComparison.Ordinal)) token = session.AuthToken;
        else if (ApiKey(session) is { } key) token = $"{key}:{session.AuthToken}";
        else return null;
        return new FeedEndpoint(new Uri($"{overrideUrl ?? FeedAddress}?token={token}&version=3.1"), new Dictionary<string, string>());
    }

    public string? FeedKeepAliveText => null;

    public TimeSpan FeedKeepAliveEvery => TimeSpan.Zero;
}
