using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;

namespace EPTradeBot.Infrastructure.Brokers.Fyers;

/// <summary>Fyers' HSM market-data socket (broker/fyers/streaming). The address is fixed; the sign-in is the first message, using a key inside the access token.</summary>
public sealed partial class FyersPlugin : IBrokerFeed
{
    public IFeedCodec CreateFeedCodec() => new FyersFeedCodec();

    public FeedEndpoint? FeedEndpoint(BrokerSession session, string? overrideUrl) =>
        FyersFeedCodec.ExtractHsmKey(session.AuthToken) is null ? null : new FeedEndpoint(new Uri(overrideUrl ?? "wss://socket.fyers.in/hsm/v1-5/prod"), new Dictionary<string, string>());

    // The socket answers the server's pings.
    public string? FeedKeepAliveText => null;

    public TimeSpan FeedKeepAliveEvery => TimeSpan.Zero;
}
