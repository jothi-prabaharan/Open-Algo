using System.Net;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;
using Microsoft.Extensions.Logging;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Upstox;

/// <summary>
/// Upstox's market-data socket (broker/upstox/streaming). The socket address is not fixed: Upstox hands out one for the login token, so each connection
/// asks for it first. A refusal at that step is the login having expired (it does every night) or the account being at its limit of concurrent feeds.
/// </summary>
public sealed partial class UpstoxPlugin : IBrokerFeedAuthorize
{
    public IFeedCodec CreateFeedCodec() => new UpstoxFeedCodec();

    // Whether the session can have a feed at all; the address itself comes from AuthorizeAsync.
    public FeedEndpoint? FeedEndpoint(BrokerSession session, string? overrideUrl) =>
        string.IsNullOrWhiteSpace(session.AuthToken) ? null : new FeedEndpoint(new Uri(overrideUrl ?? "wss://wsfeeder-api.upstox.com"), new Dictionary<string, string>());

    public async Task<FeedEndpoint?> AuthorizeAsync(BrokerSession session, string? overrideUrl, CancellationToken cancellationToken)
    {
        if (overrideUrl is not null) return FeedEndpoint(session, overrideUrl);
        try
        {
            using var request = Request(session, HttpMethod.Get, Api, "/v3/feed/market-data-feed/authorize", contentType: false);
            using var response = await SendAsync(request, cancellationToken);
            var reply = await ReadAsync(response, cancellationToken) as JsonObject;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                logger.LogWarning("Upstox did not accept the stored login for market data. The login expires every day; market data resumes after you log in to Upstox again.");
                return null;
            }

            if (!response.IsSuccessStatusCode || Str(reply?["data"]?["authorized_redirect_uri"]) is not { Length: > 0 } address)
            {
                logger.LogWarning("Upstox would not give a market-data address ({Status}). If the account is at its limit of concurrent feeds (2 on Standard, 5 on Plus), close another.", (int)response.StatusCode);
                return null;
            }

            return new FeedEndpoint(new Uri(address), new Dictionary<string, string>());
        }
        catch (BrokerException ex)
        {
            logger.LogWarning(ex, "Upstox did not answer the market-data address request.");
            return null;
        }
    }

    public string? FeedKeepAliveText => null;

    public TimeSpan FeedKeepAliveEvery => TimeSpan.Zero;
}
