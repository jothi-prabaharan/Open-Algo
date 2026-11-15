using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;

namespace EPTradeBot.Infrastructure.Brokers.Definedge;

/// <summary>
/// Definedge's stream (broker/definedge/streaming): one address, signed in by the first frame the connection sends (see <see cref="DefinedgeFeedCodec"/>). It needs the account's Definedge user id, the account's client id here, and the
/// session token; without either the connection cannot be signed in and the feed does not start. The connection is kept alive with a heartbeat frame every thirty seconds.
/// </summary>
public sealed partial class DefinedgePlugin : IBrokerFeed
{
    private const string FeedUrl = "wss://trade.definedgesecurities.com/NorenWSTRTP/";

    public IFeedCodec CreateFeedCodec() => new DefinedgeFeedCodec();

    public FeedEndpoint? FeedEndpoint(BrokerSession session, string? overrideUrl)
    {
        var parts = session.AuthToken.Split(":::");
        var token = !string.IsNullOrEmpty(session.FeedToken) ? session.FeedToken : parts.Length > 1 ? parts[1] : "";
        if (string.IsNullOrWhiteSpace(session.ClientId) || string.IsNullOrWhiteSpace(token)) return null;
        return new FeedEndpoint(new Uri(overrideUrl ?? FeedUrl), new Dictionary<string, string>());
    }

    public string? FeedKeepAliveText => "{\"t\":\"h\"}";

    public TimeSpan FeedKeepAliveEvery => TimeSpan.FromSeconds(30);
}
