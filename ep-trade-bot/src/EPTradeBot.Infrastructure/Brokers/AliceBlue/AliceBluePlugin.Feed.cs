using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.AliceBlue;

/// <summary>
/// AliceBlue's stream (broker/aliceblue/streaming): a socket session is asked for over REST first (the previous one is ended, a new one made, both with the user id), then one address is connected and signed in by its first frame (see
/// <see cref="AliceBlueFeedCodec"/>). It needs the account's client id. The connection is kept alive with a heartbeat frame every thirty seconds.
/// </summary>
public sealed partial class AliceBluePlugin : IBrokerFeedAuthorize
{
    private const string FeedUrl = "wss://ws1.aliceblueonline.com/NorenWS/";

    public IFeedCodec CreateFeedCodec() => new AliceBlueFeedCodec();

    public FeedEndpoint? FeedEndpoint(BrokerSession session, string? overrideUrl) =>
        string.IsNullOrWhiteSpace(session.ClientId) || string.IsNullOrWhiteSpace(session.AuthToken) ? null : new FeedEndpoint(new Uri(overrideUrl ?? FeedUrl), new Dictionary<string, string>());

    /// <summary>Ends the last socket session and makes a new one; without one the stream would be refused. A refusal to end the old one changes nothing: there may not be one.</summary>
    public async Task<FeedEndpoint?> AuthorizeAsync(BrokerSession session, string? overrideUrl, CancellationToken cancellationToken)
    {
        var endpoint = FeedEndpoint(session, overrideUrl);
        if (endpoint is null || overrideUrl is not null) return endpoint;
        try
        {
            var body = new JsonObject { ["source"] = "API", ["userId"] = session.ClientId };
            await SendAsync(session, HttpMethod.Post, "/open-api/od/v1/profile/invalidateWsSess", body, read: false, cancellationToken);
            var (_, reply) = await SendAsync(session, HttpMethod.Post, "/open-api/od/v1/profile/createWsSess", body.DeepClone(), read: false, cancellationToken);
            return reply is JsonObject answer && Str(answer["status"]) == "Ok" ? endpoint : null;
        }
        catch (BrokerException)
        {
            return null;
        }
    }

    public string? FeedKeepAliveText => "{\"k\":\"\",\"t\":\"h\"}";

    public TimeSpan FeedKeepAliveEvery => TimeSpan.FromSeconds(30);
}
