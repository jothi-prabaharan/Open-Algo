using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.IiflCapital;

/// <summary>
/// IIFL Capital sign-in (broker/iiflcapital/api/auth_api.py): the trader signs in on IIFL's page, which returns to the callback address with an authCode and a clientId. They are exchanged at /getusersession
/// with a checksum, the SHA-256 of clientId + authCode + app secret; the user session is the access token.
/// </summary>
public sealed partial class IiflCapitalPlugin : IBrokerLogin
{
    public LoginMethod LoginMethod => LoginMethod.Redirect;

    public IReadOnlyList<string> LoginFields { get; } = [];

    public string? LoginUrl(IReadOnlyDictionary<string, string> keys, string state)
    {
        if (!keys.TryGetValue("ApiKey", out var key) || string.IsNullOrWhiteSpace(key) || !keys.TryGetValue("RedirectUrl", out var redirect) || string.IsNullOrWhiteSpace(redirect)) return null;
        var back = $"{redirect.Trim()}?state={Uri.EscapeDataString(state).Replace("%20", "+", StringComparison.Ordinal)}";
        return $"https://markets.iiflcapital.com/?v=1&appkey={Uri.EscapeDataString(key.Trim())}&redirecturl={back}&redirectUrl={back}";
    }

    private static string First(IReadOnlyDictionary<string, string> input, params string[] names) => names.Select(n => input.TryGetValue(n, out var v) ? v : null).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    public async Task<BrokerLoginResult> LoginAsync(IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> input, CancellationToken cancellationToken)
    {
        if (!keys.TryGetValue("ApiSecret", out var secret) || string.IsNullOrWhiteSpace(secret))
            return BrokerLoginResult.Refused("The IIFL Capital app secret is not set on this server. Add it to the server's settings, then sign in again.", 409);
        var authCode = First(input, "authCode", "authcode", "auth_code", "code");
        var clientId = First(input, "clientId", "clientid", "client_id");
        if (authCode.Length == 0 || clientId.Length == 0) return BrokerLoginResult.Refused("IIFL Capital did not send the sign-in back with everything it needs. Start the sign-in again.");

        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{clientId}{authCode}{secret.Trim()}"))).ToLowerInvariant();
        var session = new BrokerSession(Guid.Empty, Name, "", null, null, new Dictionary<string, string>(keys));
        (int Status, JsonNode? Body, string Text, TimeSpan? RetryAfter) reply;
        try
        {
            reply = await SendAsync(session, HttpMethod.Post, "/getusersession", new JsonObject { ["checkSum"] = checksum }, cancellationToken);
        }
        catch (BrokerException)
        {
            return BrokerLoginResult.Refused("Could not reach IIFL Capital to sign in. Check your internet connection and try again.", 502);
        }

        var body = reply.Body as JsonObject ?? [];
        if (reply.Status != 200)
            return BrokerLoginResult.Refused(Truthy(body["message"]) ? $"IIFL Capital did not accept the sign-in: {Str(body["message"])}" : Truthy(body["error"]) ? $"IIFL Capital did not accept the sign-in: {Str(body["error"])}" : "IIFL Capital did not accept the sign-in. Start it again from the beginning.");
        if ((Str(body["status"]) ?? "").Equals("ok", StringComparison.OrdinalIgnoreCase) && Str(body["userSession"]) is { Length: > 0 } token) return BrokerLoginResult.Success(token, null);
        return BrokerLoginResult.Refused(Truthy(body["message"]) ? $"IIFL Capital did not accept the sign-in: {Str(body["message"])}" : "IIFL Capital did not accept the sign-in. Start it again from the beginning.");
    }
}
