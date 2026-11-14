using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Kotak;

/// <summary>
/// Kotak's stream (broker/kotak/streaming): the market-data host depends on the account's data centre (the fifth part of the session), which Kotak's config service names; any failure of that lookup, an unknown data centre or the
/// legacy HSM host (which this codec cannot read) gives the default SFeed host. The connection is kept alive with a ping every thirty seconds, which the feed host does for every WebSocket.
/// </summary>
public sealed partial class KotakPlugin : IBrokerFeedAuthorize
{
    private const string ConfigUrl = "https://lapi.kotaksecurities.com/5config/config?appVersion=1.0.0&platform=api&environment=prod";
    private const string DefaultFeedUrl = "wss://sfeed.kotaksecurities.com/apifeed";

    public IFeedCodec CreateFeedCodec() => new KotakFeedCodec();

    private static string WebSocketScheme(string url)
    {
        url = url.Trim();
        if (url.StartsWith("https://", StringComparison.Ordinal)) return "wss://" + url["https://".Length..];
        if (url.StartsWith("http://", StringComparison.Ordinal)) return "ws://" + url["http://".Length..];
        return url.StartsWith("wss://", StringComparison.Ordinal) || url.StartsWith("ws://", StringComparison.Ordinal) ? url : "wss://" + url;
    }

    // Whether the session can have a feed at all; the address itself comes from AuthorizeAsync.
    public FeedEndpoint? FeedEndpoint(BrokerSession session, string? overrideUrl) =>
        session.AuthToken.Split(":::").Length < 4 ? null : new FeedEndpoint(new Uri(overrideUrl ?? DefaultFeedUrl), new Dictionary<string, string>());

    public async Task<FeedEndpoint?> AuthorizeAsync(BrokerSession session, string? overrideUrl, CancellationToken cancellationToken)
    {
        var parts = session.AuthToken.Split(":::");
        if (parts.Length < 4) return null;
        if (overrideUrl is not null) return FeedEndpoint(session, overrideUrl);
        var dataCenter = parts.Length > 4 ? parts[4] : "";
        var address = DefaultFeedUrl;
        if (dataCenter.Length > 0)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var response = await Client().GetAsync(ConfigUrl, timeout.Token);
                response.EnsureSuccessStatusCode();
                var configs = (await ReadAsync(response, cancellationToken) as JsonObject)?["data"] is JsonObject data ? data["configs"] as JsonObject : null;
                if (configs is not null && Truthy(configs[$"{dataCenter}_broadcast_source"]) && Str(configs[$"{dataCenter}_broadcast_source"]) is { } source
                    && source != "hs" && Truthy(configs[$"{dataCenter}_{source}_broadcast_endpoint"]) && Str(configs[$"{dataCenter}_{source}_broadcast_endpoint"]) is { } market)
                {
                    address = WebSocketScheme(market);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
            {
                address = DefaultFeedUrl;
            }
        }

        return new FeedEndpoint(new Uri(address), new Dictionary<string, string>());
    }

    public string? FeedKeepAliveText => null;

    public TimeSpan FeedKeepAliveEvery => TimeSpan.FromSeconds(30);
}
