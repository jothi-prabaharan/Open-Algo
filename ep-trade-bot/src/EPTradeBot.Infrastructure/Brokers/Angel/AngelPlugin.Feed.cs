using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;

namespace EPTradeBot.Infrastructure.Brokers.Angel;

/// <summary>The SmartAPI stream v2 (broker/angel/streaming). It needs the token, the feed token, the application key and the client code.</summary>
public sealed partial class AngelPlugin
{
    public IFeedCodec CreateFeedCodec() => new SmartApiFeedCodec();

    public FeedEndpoint? FeedEndpoint(BrokerSession session, string? overrideUrl)
    {
        var key = session.Key("ApiKey");
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(session.FeedToken) || string.IsNullOrWhiteSpace(session.ClientId)) return null;
        return new FeedEndpoint(new Uri(overrideUrl ?? "wss://smartapisocket.angelone.in/smart-stream"), new Dictionary<string, string>
        {
            ["Authorization"] = session.AuthToken, ["x-api-key"] = key, ["x-client-code"] = session.ClientId, ["x-feed-token"] = session.FeedToken,
        });
    }

    // SmartAPI wants a "ping" text every ten seconds and answers "pong".
    public string? FeedKeepAliveText => "ping";

    public TimeSpan FeedKeepAliveEvery => TimeSpan.FromSeconds(10);
}
