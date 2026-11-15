using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;

namespace EPTradeBot.Infrastructure.Brokers.Mstock;

/// <summary>
/// mStock's stream (broker/mstock/streaming): one address that carries the account's key (the secret when there is one, else the key) and the access token, signed in by the text LOGIN:&lt;access token&gt; the codec sends on every connection
/// (see <see cref="MstockFeedCodec"/>). The connection is kept alive by the socket's own ping every twenty seconds. A session with no access token, or with no key, cannot be signed in and the feed does not start.
/// </summary>
public sealed partial class MstockPlugin : IBrokerFeed
{
    private const string FeedAddress = "wss://ws.mstock.trade";

    public IFeedCodec CreateFeedCodec() => new MstockFeedCodec();

    public FeedEndpoint? FeedEndpoint(BrokerSession session, string? overrideUrl)
    {
        var key = session.Key("ApiSecret") is { Length: > 0 } secret ? secret : session.Key("ApiKey");
        if (string.IsNullOrWhiteSpace(session.AuthToken) || string.IsNullOrWhiteSpace(key)) return null;
        var address = overrideUrl ?? FeedAddress;
        return new FeedEndpoint(new Uri($"{address}?API_KEY={key}&ACCESS_TOKEN={session.AuthToken}"), new Dictionary<string, string>());
    }

    public string? FeedKeepAliveText => null;

    public TimeSpan FeedKeepAliveEvery => TimeSpan.Zero;
}
