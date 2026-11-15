using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;

namespace EPTradeBot.Infrastructure.Brokers.Pocketful;

/// <summary>
/// Pocketful's stream (broker/pocketful/streaming): wss://trade.pocketful.in/ws/v1/feeds with the client id and the access token in the address, no sign-in frame, and a heartbeat {"a": "h"} every fifteen seconds (see <see cref="PocketfulFeedCodec"/>).
/// A session with no access token cannot connect and the feed does not start. The client id is the session's; a session without one asks Pocketful for it when the connection is made.
/// </summary>
public sealed partial class PocketfulPlugin : IBrokerFeed
{
    public IFeedCodec CreateFeedCodec() => new PocketfulFeedCodec();

    public FeedEndpoint? FeedEndpoint(BrokerSession session, string? overrideUrl) =>
        string.IsNullOrWhiteSpace(session.AuthToken) || string.IsNullOrWhiteSpace(session.ClientId) ? null
            : new FeedEndpoint(new Uri($"{overrideUrl ?? sessions.FeedUrl(Name) ?? FeedAddress}/ws/v1/feeds?login_id={session.ClientId}&access_token={session.AuthToken}"), new Dictionary<string, string>());

    public string? FeedKeepAliveText => """{"a": "h"}""";

    public TimeSpan FeedKeepAliveEvery => TimeSpan.FromSeconds(15);
}
