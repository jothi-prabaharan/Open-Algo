using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;

namespace EPTradeBot.Infrastructure.Brokers.Rupeezy;

/// <summary>Rupeezy's Vortex price feed. The access token rides in the address; the socket answers the protocol pings the client sends.</summary>
public sealed partial class RupeezyPlugin : IBrokerFeed
{
    public IFeedCodec CreateFeedCodec() => new VortexFeedCodec();

    public FeedEndpoint? FeedEndpoint(BrokerSession session, string? overrideUrl) =>
        string.IsNullOrWhiteSpace(session.AuthToken)
            ? null
            : new FeedEndpoint(new Uri($"{overrideUrl ?? "wss://wire.rupeezy.in/ws"}?auth_token={Uri.EscapeDataString(session.AuthToken)}"), new Dictionary<string, string>());

    public string? FeedKeepAliveText => null;

    public TimeSpan FeedKeepAliveEvery => TimeSpan.Zero;
}
