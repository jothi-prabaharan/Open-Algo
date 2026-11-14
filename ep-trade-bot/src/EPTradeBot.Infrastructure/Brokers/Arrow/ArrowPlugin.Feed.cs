using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;

namespace EPTradeBot.Infrastructure.Brokers.Arrow;

/// <summary>Arrow's market-data stream: the application id and the access token ride in the address, and the server expects the text PONG every few seconds (it sends no ticks to a client that stays silent).</summary>
public sealed partial class ArrowPlugin : IBrokerFeed
{
    public IFeedCodec CreateFeedCodec() => new ArrowFeedCodec();

    public FeedEndpoint? FeedEndpoint(BrokerSession session, string? overrideUrl) =>
        string.IsNullOrWhiteSpace(session.AuthToken) || string.IsNullOrWhiteSpace(session.Key("ApiKey"))
            ? null
            : new FeedEndpoint(new Uri($"{overrideUrl ?? "wss://ds.arrow.trade"}?appID={Uri.EscapeDataString(session.Key("ApiKey")!)}&token={Uri.EscapeDataString(session.AuthToken)}"), new Dictionary<string, string>());

    public string? FeedKeepAliveText => "PONG";

    public TimeSpan FeedKeepAliveEvery => TimeSpan.FromSeconds(3);
}
