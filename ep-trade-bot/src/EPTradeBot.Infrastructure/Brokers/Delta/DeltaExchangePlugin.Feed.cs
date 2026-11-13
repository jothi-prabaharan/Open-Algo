using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;

namespace EPTradeBot.Infrastructure.Brokers.Delta;

/// <summary>
/// Delta Exchange's public market-data socket (broker/deltaexchange/streaming). It needs no sign-in, so a session is not required for the stream; the
/// account channels (orders, positions, margins) are on a different, signed socket and are not part of this feed.
/// </summary>
public sealed partial class DeltaExchangePlugin : IBrokerFeed
{
    public IFeedCodec CreateFeedCodec() => new DeltaFeedCodec();

    public FeedEndpoint? FeedEndpoint(BrokerSession session, string? overrideUrl) =>
        new(new Uri(overrideUrl ?? "wss://public-socket.india.delta.exchange"), new Dictionary<string, string>());

    // The socket answers protocol pings; the host sends them every twenty seconds.
    public string? FeedKeepAliveText => null;

    public TimeSpan FeedKeepAliveEvery => TimeSpan.Zero;
}
