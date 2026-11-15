using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;

namespace EPTradeBot.Infrastructure.Brokers.IndMoney;

/// <summary>
/// IndMoney's stream (broker/indmoney/streaming): wss://ws-prices.indstocks.com/api/v1/ws/prices with the access token as the Authorization header (see <see cref="IndMoneyFeedCodec"/>). The connection is kept alive by the socket's own ping
/// every thirty seconds. A session with no access token cannot connect and the feed does not start.
/// </summary>
public sealed partial class IndMoneyPlugin : IBrokerFeed
{
    private const string FeedAddress = "wss://ws-prices.indstocks.com/api/v1/ws/prices";

    public IFeedCodec CreateFeedCodec() => new IndMoneyFeedCodec();

    public FeedEndpoint? FeedEndpoint(BrokerSession session, string? overrideUrl) =>
        string.IsNullOrWhiteSpace(session.AuthToken) ? null : new FeedEndpoint(new Uri(overrideUrl ?? sessions.FeedUrl(Name) ?? FeedAddress), new Dictionary<string, string> { ["Authorization"] = session.AuthToken });

    public string? FeedKeepAliveText => null;

    public TimeSpan FeedKeepAliveEvery => TimeSpan.FromSeconds(30);
}
