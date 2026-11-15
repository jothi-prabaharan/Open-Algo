using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Groww;

/// <summary>
/// Groww's stream (broker/groww/streaming): NATS over wss://socket-api.groww.in. Each connection starts with a call for a socket token: a key is made for it, its public half goes to Groww, and the token that comes back (and the id of the subscription) are what the
/// socket is opened with; the seed of the key signs the server's nonce in the CONNECT (see <see cref="GrowwFeedCodec"/>). A login token Groww refuses ends the feed (the account has to sign in again). A PING goes every ten seconds.
/// </summary>
public sealed partial class GrowwPlugin : IBrokerFeedAuthorize
{
    private const string FeedAddress = "wss://socket-api.groww.in";
    private const string SocketTokenPath = "/v1/api/apex/v1/socket/token/create/";

    public IFeedCodec CreateFeedCodec() => new GrowwFeedCodec();

    /// <summary>Whether the session can have a feed at all; the headers it needs come from <see cref="AuthorizeAsync"/>.</summary>
    public FeedEndpoint? FeedEndpoint(BrokerSession session, string? overrideUrl) =>
        string.IsNullOrWhiteSpace(session.AuthToken) ? null : new FeedEndpoint(new Uri(overrideUrl ?? sessions.FeedUrl(Name) ?? FeedAddress), new Dictionary<string, string>(), "nats");

    public async Task<FeedEndpoint?> AuthorizeAsync(BrokerSession session, string? overrideUrl, CancellationToken cancellationToken)
    {
        if (FeedEndpoint(session, overrideUrl) is not { } endpoint) return null;
        var seed = RandomNumberGenerator.GetBytes(32);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, sessions.BaseUrl(Name, DefaultBaseUrl) + SocketTokenPath)
            {
                Content = new StringContent(new JsonObject { ["socketKey"] = GrowwNkeys.EncodePublicKey(seed) }.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("x-request-id", Guid.NewGuid().ToString());
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {session.AuthToken}");
            request.Headers.TryAddWithoutValidation("x-client-id", "growwapi");
            request.Headers.TryAddWithoutValidation("x-client-platform", "growwapi-python-client");
            request.Headers.TryAddWithoutValidation("x-client-platform-version", "0.0.8");
            request.Headers.TryAddWithoutValidation("x-api-version", "1.0");
            using var response = await Client().SendAsync(request, cancellationToken);
            // A refused login token ends the feed: nothing the socket can do will make it work until the account signs in again.
            if (!response.IsSuccessStatusCode) return null;
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
            if (body is null || !Truthy(body["token"]) || !Truthy(body["subscriptionId"])) return null;
            GrowwSocketCredentials.Put(session.AuthToken, Str(body["token"])!, GrowwNkeys.EncodeSeed(seed));
            return endpoint with
            {
                Headers = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {Str(body["token"])}", ["X-Subscription-Id"] = Str(body["subscriptionId"])!, ["User-Agent"] = "Python/3.10 nats.py/2.10.18", ["X-Client-Id"] = "nats-py", ["X-API-Version"] = "1.0",
                },
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    public string? FeedKeepAliveText => "PING\r\n";

    public TimeSpan FeedKeepAliveEvery => TimeSpan.FromSeconds(10);
}
