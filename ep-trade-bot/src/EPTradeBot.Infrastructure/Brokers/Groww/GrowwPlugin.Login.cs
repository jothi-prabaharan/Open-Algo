using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Groww;

/// <summary>
/// Groww sign-in (broker/groww api/auth_api.py): nothing is typed in and there is no page. The API key is the bearer token of one call to /v1/token/api/access with the SHA-256 of the secret and the time (seconds); the answer carries the
/// access token, and one Groww marks inactive, or already past its expiry, is refused here rather than failing on the first order.
/// </summary>
public sealed partial class GrowwPlugin : IBrokerLogin
{
    public LoginMethod LoginMethod => LoginMethod.Credentials;

    public IReadOnlyList<string> LoginFields { get; } = [];

    public string? LoginUrl(IReadOnlyDictionary<string, string> keys, string state) => null;

    /// <summary>A sign-in failure with Groww's own reason, and advice that depends on why: a throttle and a fault on Groww's side are not problems with the key.</summary>
    private static string LoginError(JsonNode? body, int? status)
    {
        string? reason = null;
        if (body is JsonObject o) reason = o["error"] is JsonObject error && Truthy(error["message"]) ? Str(error["message"]) : Truthy(o["message"]) ? Str(o["message"]) : null;
        var message = $"{Display} did not issue an access token" + (string.IsNullOrEmpty(reason) ? "" : $": {reason}");
        if (status == 429) return $"{message}. {Display} is limiting login requests (30 a minute, 150 a day). Wait a minute, then log in again.";
        if (status >= 500) return $"{message}. {Display}'s login service is not responding right now; the API key is not the problem. Try again in a few minutes.";
        return $"{message}. Check the API key and secret, and that the key is approved for today on {Display}'s API Keys page.";
    }

    public async Task<BrokerLoginResult> LoginAsync(IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> input, CancellationToken cancellationToken)
    {
        if (!keys.TryGetValue("ApiKey", out var apiKey) || string.IsNullOrWhiteSpace(apiKey) || !keys.TryGetValue("ApiSecret", out var apiSecret) || string.IsNullOrWhiteSpace(apiSecret))
            return BrokerLoginResult.Refused($"The {Display} API key and secret are not both set on this server. Add them to the server's settings, then sign in again.", 409);
        var timestamp = Now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var checksum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(apiSecret + timestamp)));
        var body = new JsonObject { ["key_type"] = "approval", ["checksum"] = checksum, ["timestamp"] = timestamp };
        try
        {
            // The sign-in is paced and sent with the API key as the bearer token, not the session's.
            var (status, answer, _) = await SendAsync(new BrokerSession(Guid.Empty, Name, apiKey, null, null, new Dictionary<string, string>()), HttpMethod.Post, "/v1/token/api/access", "auth", null, body, cancellationToken);
            if (status != 200) return BrokerLoginResult.Refused(LoginError(answer, status));
            if (answer is not JsonObject data || !Truthy(data["token"])) return BrokerLoginResult.Refused(LoginError(answer, null));
            if (data["isActive"] is JsonValue active && active.TryGetValue<bool>(out var isActive) && !isActive)
                return BrokerLoginResult.Refused($"{Display} issued an access token that is not active. Approve the API key for today on {Display}'s API Keys page, then log in again.");
            if (Truthy(data["expiry"]) && Str(data["expiry"]) is { } expiry && DateTimeOffset.TryParse(expiry, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var expiresAt) && expiresAt <= Now)
                return BrokerLoginResult.Refused($"{Display} issued an access token that expired at {expiry}. Log in again to get a new one.");
            return BrokerLoginResult.Success(Str(data["token"])!, null);
        }
        catch (BrokerException)
        {
            return BrokerLoginResult.Refused($"Could not reach {Display} to log in. Check your connection and try again.", 502);
        }
    }
}
