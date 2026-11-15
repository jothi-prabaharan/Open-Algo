using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;

namespace EPTradeBot.Infrastructure.Brokers.Nubra;

/// <summary>
/// Nubra's stream (broker/nubra/streaming): wss://api.nubra.io/apibatch/ws with the session token as a bearer token and x-device-id OPENALGO in the headers; every command carries the token (see <see cref="NubraFeedCodec"/>). Nubra
/// is kept alive by the socket's own ping every twenty seconds, which the connection layer sends, so there is no text heartbeat. A session with no token cannot connect and the feed does not start.
/// </summary>
public sealed partial class NubraPlugin : IBrokerFeed
{
    public IFeedCodec CreateFeedCodec() => new NubraFeedCodec();

    public FeedEndpoint? FeedEndpoint(BrokerSession session, string? overrideUrl) =>
        string.IsNullOrWhiteSpace(session.AuthToken) ? null
            : new FeedEndpoint(new Uri(overrideUrl ?? sessions.FeedUrl(Name) ?? FeedAddress), new Dictionary<string, string> { ["Authorization"] = $"Bearer {session.AuthToken}", ["x-device-id"] = DeviceId });

    public string? FeedKeepAliveText => null;

    public TimeSpan FeedKeepAliveEvery => TimeSpan.FromSeconds(20);
}
