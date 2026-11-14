using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;

namespace EPTradeBot.Infrastructure.Brokers.HdfcSky;

/// <summary>
/// HDFC Sky's stream (broker/hdfcsky/streaming): the access token and the application key ride in the address and the token again in Authorization, with the browser User-Agent the gateway insists on.
/// The server expects {"heart_beat": true} every ten seconds.
/// </summary>
public sealed partial class HdfcSkyPlugin : IBrokerFeed
{
    public IFeedCodec CreateFeedCodec() => new HdfcSkyFeedCodec();

    public FeedEndpoint? FeedEndpoint(BrokerSession session, string? overrideUrl) =>
        string.IsNullOrWhiteSpace(session.AuthToken) || string.IsNullOrWhiteSpace(session.Key("ApiKey"))
            ? null
            : new FeedEndpoint(
                new Uri($"{overrideUrl ?? "wss://developer.hdfcsky.com/wsapi/v1/session"}?token={Uri.EscapeDataString(session.AuthToken)}&api_key={Uri.EscapeDataString(session.Key("ApiKey")!)}"),
                new Dictionary<string, string> { ["Authorization"] = session.AuthToken, ["User-Agent"] = UserAgent });

    public string? FeedKeepAliveText => "{\"heart_beat\": true}";

    public TimeSpan FeedKeepAliveEvery => TimeSpan.FromSeconds(10);
}
