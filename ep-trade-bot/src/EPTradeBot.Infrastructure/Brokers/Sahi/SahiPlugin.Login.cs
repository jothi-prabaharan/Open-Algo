using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Sahi;

/// <summary>
/// Sahi sign-in (broker/sahi/api/auth_api.py). There is no redirect and no code: the API key id (pk_...) and secret (sahi_sk_...) are exchanged at Sahi's auth host
/// for a Bearer token that lasts until the next 06:00 India time. The trader approves the key once each trading day in the Sahi API Dashboard.
/// </summary>
public sealed partial class SahiPlugin : IBrokerLogin
{
    private const string AuthHost = "https://apis.sahi.com";

    public LoginMethod LoginMethod => LoginMethod.Credentials;

    public IReadOnlyList<string> LoginFields { get; } = [];

    public string? LoginUrl(IReadOnlyDictionary<string, string> keys, string state) => null;

    private static string LoginRefusal(int status, JsonObject body)
    {
        var code = Str(body["code"]);
        var reason = Str(body["message"]);
        if (status == 403 || code == "Forbidden")
            return "Sahi did not issue an access token because the API key is not approved for today. Open Sahi web, go to Profile, API Dashboard, and click Approve on the key, then log in again.";
        if (code == "Locked") return "Sahi reports that your account is frozen or locked. Contact Sahi support to unlock it.";
        if (status == 401)
            return "Sahi did not accept the API key and secret. Check the key id (it starts with pk_) and the secret (it starts with sahi_sk_) in the server's settings, and that the key has not been deleted in the Sahi API Dashboard.";
        if (status == 429) return "Sahi is limiting login attempts. Wait a minute, then log in again.";
        if (status >= 500) return "Sahi's login service is not responding right now. Try again in a few minutes.";
        var message = "Sahi did not issue an access token";
        if (!string.IsNullOrEmpty(reason)) message += $": {reason}";
        return message + ". Check the API key and secret in the server's settings.";
    }

    public async Task<BrokerLoginResult> LoginAsync(IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> input, CancellationToken cancellationToken)
    {
        var keyId = keys.TryGetValue("ApiKey", out var k) ? k.Trim() : "";
        var secret = keys.TryGetValue("ApiSecret", out var s) ? s.Trim() : "";
        if (keyId.Length == 0 || secret.Length == 0)
            return BrokerLoginResult.Refused("Sahi needs both the API key id (pk_...) and the API secret (sahi_sk_...). Add them to the server's settings, then sign in again.", 409);

        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, AuthHost + "/api/v1/apikeys/token")
            {
                Content = JsonContent.Create(new JsonObject { ["key_id"] = keyId, ["secret"] = secret }),
            };
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", "ep-trade-bot/1.0");
            HttpResponseMessage response;
            try
            {
                response = await Client().SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException)
            {
                return BrokerLoginResult.Refused("Could not reach Sahi to sign in. Check your internet connection and try again.", 502);
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                if (status == 429 && attempt < MaxRetries)
                {
                    await Task.Delay(sessions.RetryBackoff(Name, RetryAfter(response, attempt)), cancellationToken);
                    continue;
                }

                var body = await JsonRead.ReadAsync(response, cancellationToken) as JsonObject ?? [];
                if (status == 200 && Str(body["access_token"]) is { Length: > 0 } token) return BrokerLoginResult.Success(token, null);
                return BrokerLoginResult.Refused(LoginRefusal(status, body));
            }
        }
    }
}
