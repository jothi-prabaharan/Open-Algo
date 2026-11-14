using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;

namespace EPTradeBot.Infrastructure.Brokers.FivePaisa;

/// <summary>
/// 5paisa's stream (broker/fivepaisa/streaming): the feed is sharded, and the address is the host named by the RedirectServer claim in the access token (A, B or C, else the default), with the token and the client code
/// in its query (Value1=token|client code). The connection is kept alive with a ping every ten seconds, which the feed host does for every WebSocket.
/// </summary>
public sealed partial class FivePaisaPlugin : IBrokerFeed
{
    private static readonly Dictionary<string, string> FeedHosts = new()
    {
        ["A"] = "wss://aopenfeed.5paisa.com/feeds/api/chat", ["B"] = "wss://bopenfeed.5paisa.com/feeds/api/chat", ["C"] = "wss://openfeed.5paisa.com/feeds/api/chat",
    };

    private const string DefaultFeedHost = "wss://openfeed.5paisa.com/Feeds/api/chat";

    public IFeedCodec CreateFeedCodec() => new FivePaisaFeedCodec();

    private static string RedirectServer(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return "default";
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            return JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload))) is JsonObject claims && claims["RedirectServer"] is JsonValue v && v.TryGetValue<string>(out var server) ? server : "default";
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or ArgumentException)
        {
            return "default";
        }
    }

    public FeedEndpoint? FeedEndpoint(BrokerSession session, string? overrideUrl)
    {
        var parts = (session.Key("ApiKey") ?? "").Split(":::");
        var client = parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2].Trim() : session.ClientId;
        if (string.IsNullOrWhiteSpace(session.AuthToken) || string.IsNullOrWhiteSpace(client)) return null;
        var host = overrideUrl ?? FeedHosts.GetValueOrDefault(RedirectServer(session.AuthToken), DefaultFeedHost);
        return new FeedEndpoint(new Uri($"{host}?Value1={session.AuthToken}|{client}"), new Dictionary<string, string>());
    }

    public string? FeedKeepAliveText => null;

    public TimeSpan FeedKeepAliveEvery => TimeSpan.FromSeconds(10);
}
