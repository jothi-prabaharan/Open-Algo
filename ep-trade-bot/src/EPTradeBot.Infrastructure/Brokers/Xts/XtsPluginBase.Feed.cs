using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Xts;

/// <summary>The XTS market-data feed (broker/wisdom/streaming and its copies): a Socket.IO connection for ticks and HTTP calls to subscribe, held to the codec in Core.</summary>
public abstract partial class XtsPluginBase : IBrokerFeedHttp
{
    /// <summary>Where the Socket.IO endpoint lives under the address; Jainam's is under the binary market-data path.</summary>
    protected virtual string SocketPath => "/apimarketdata/socket.io";

    public virtual IFeedCodec CreateFeedCodec() => new XtsFeedCodec(Name);

    /// <summary>
    /// The socket is opened with the market-data token and the user id that token was issued to (read from the token, which is a JWT, and failing that the
    /// account's client id). A session without a market-data token has no feed.
    /// </summary>
    public FeedEndpoint? FeedEndpoint(BrokerSession session, string? overrideUrl)
    {
        if (string.IsNullOrEmpty(session.FeedToken)) return null;
        var user = UserOf(session.FeedToken) ?? session.ClientId;
        if (string.IsNullOrEmpty(user)) return null;
        var address = (overrideUrl ?? sessions.BaseUrl(Name, DefaultBaseUrl)).TrimEnd('/');
        address = address.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss://" + address["https://".Length..] : address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "ws://" + address["http://".Length..] : address;
        var query = $"token={Uri.EscapeDataString(session.FeedToken)}&userID={Uri.EscapeDataString(user)}&publishFormat=JSON&broadcastMode=FULL&EIO=4&transport=websocket";
        return new FeedEndpoint(new Uri($"{address}{SocketPath}/?{query}"), new Dictionary<string, string>());
    }

    // The server pings and the codec answers; nothing is sent on a timer.
    public string? FeedKeepAliveText => null;

    public TimeSpan FeedKeepAliveEvery => TimeSpan.Zero;

    private static string? UserOf(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            return Str((JsonNode.Parse(Convert.FromBase64String(payload)) as JsonObject)?["userID"]) is { Length: > 0 } id ? id : null;
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Makes one subscription call. The initial quotes the broker answers with, as JSON text.</summary>
    public async Task<IReadOnlyList<string>> SendFeedCallAsync(BrokerSession session, FeedHttpCall call, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod(call.Method), sessions.BaseUrl(Name, DefaultBaseUrl).TrimEnd('/') + MarketDataPath + call.Path);
        request.Headers.TryAddWithoutValidation("authorization", session.FeedToken);
        request.Content = new StringContent(call.Body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject;
        var quotes = new List<string>();
        if (Str(reply?["type"]) != "success") return quotes;
        foreach (var raw in ((reply!["result"] as JsonObject)?["listQuotes"] as JsonArray) ?? [])
        {
            if (raw is JsonValue v && v.TryGetValue<string>(out var text)) quotes.Add(text);
            else if (raw is JsonObject o) quotes.Add(o.ToJsonString());
        }

        return quotes;
    }
}
