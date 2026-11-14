using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;

namespace EPTradeBot.Infrastructure.Brokers.IiflCapital;

/// <summary>
/// IIFL Capital's market-data stream (broker/iiflcapital/streaming): MQTT 3.1.1 over TLS to bridge.iiflcapital.com:8883. The user name is the preferred_username claim of the session token, the password
/// is OPENID~~token~, and the client id is new for every connection (a repeat makes the broker drop the older one).
/// </summary>
public sealed partial class IiflCapitalPlugin : IBrokerMqttFeed
{
    public IMqttFeedCodec CreateMqttCodec() => new IiflCapitalFeedCodec();

    public MqttEndpoint? MqttEndpoint(BrokerSession session)
    {
        var user = PreferredUsername(session.AuthToken);
        if (user is null) return null;
        var clientId = "openalgo" + DateTime.Now.ToString("ddMMyyHHmmssffffff", System.Globalization.CultureInfo.InvariantCulture) + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        return new MqttEndpoint("bridge.iiflcapital.com", 8883, true, clientId, user, $"OPENID~~{session.AuthToken}~");
    }

    private static string? PreferredUsername(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return null;
            var segment = parts[1].Replace('-', '+').Replace('_', '/');
            segment = segment.PadRight(segment.Length + ((4 - (segment.Length % 4)) % 4), '=');
            return JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(segment))) is JsonObject claims && claims["preferred_username"]?.GetValue<string>() is { Length: > 0 } name ? name : null;
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
