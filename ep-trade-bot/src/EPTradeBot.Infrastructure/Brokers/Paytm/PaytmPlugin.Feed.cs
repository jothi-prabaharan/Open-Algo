using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;

namespace EPTradeBot.Infrastructure.Brokers.Paytm;

/// <summary>Paytm Money's broadcast stream (broker/paytm/streaming): the public access token rides on the address.</summary>
public sealed partial class PaytmPlugin
{
    public IFeedCodec CreateFeedCodec() => new PaytmFeedCodec();

    public FeedEndpoint? FeedEndpoint(BrokerSession session, string? overrideUrl)
    {
        if (string.IsNullOrWhiteSpace(session.FeedToken)) return null;
        var root = overrideUrl ?? "wss://developer-ws.paytmmoney.com/broadcast/user/v1/data";
        return new FeedEndpoint(new Uri($"{root}?x_jwt_token={Uri.EscapeDataString(session.FeedToken)}"), new Dictionary<string, string>());
    }

    // The server pings every thirty seconds and the socket answers.
    public string? FeedKeepAliveText => null;

    public TimeSpan FeedKeepAliveEvery => TimeSpan.Zero;
}
