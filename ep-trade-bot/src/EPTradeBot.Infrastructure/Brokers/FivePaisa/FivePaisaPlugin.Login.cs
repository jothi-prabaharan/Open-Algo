using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.FivePaisa;

/// <summary>
/// 5paisa sign-in (broker/fivepaisa/api/auth_api.py): the client code (the e-mail address), the PIN and a one-time code go to TOTPLogin for a request token, which GetAccessToken exchanges, with the app secret and the
/// user id, for the access token. The app key, user id and client code are one setting (key:::user:::client).
/// </summary>
public sealed partial class FivePaisaPlugin : IBrokerLogin
{
    public LoginMethod LoginMethod => LoginMethod.Credentials;

    public IReadOnlyList<string> LoginFields { get; } = ["clientcode", "password", "totp"];

    public string? LoginUrl(IReadOnlyDictionary<string, string> keys, string state) => null;

    private async Task<(int Status, JsonObject Body)> LoginCallAsync(string path, JsonObject body, string appKey, CancellationToken cancellationToken)
    {
        var payload = new JsonObject { ["head"] = new JsonObject { ["Key"] = appKey }, ["body"] = body };
        using var request = new HttpRequestMessage(HttpMethod.Post, sessions.BaseUrl(Name, DefaultBaseUrl) + path) { Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await Client().SendAsync(request, cancellationToken);
        return ((int)response.StatusCode, await ReadAsync(response, cancellationToken) as JsonObject ?? []);
    }

    public async Task<BrokerLoginResult> LoginAsync(IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> input, CancellationToken cancellationToken)
    {
        var parts = (keys.TryGetValue("ApiKey", out var packed) ? packed : "").Split(":::");
        if (parts.Length != 3 || parts.Any(string.IsNullOrWhiteSpace) || !keys.TryGetValue("ApiSecret", out var secret) || string.IsNullOrWhiteSpace(secret))
            return BrokerLoginResult.Refused("The 5paisa app key and secret are not both set on this server, or the key is not written as app key:::user id:::client code. Correct them in the server's settings, then sign in again.", 409);
        foreach (var field in LoginFields)
        {
            if (!input.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
                return BrokerLoginResult.Refused("Enter the client code, PIN and one-time code to sign in to 5paisa.");
        }

        try
        {
            var first = await LoginCallAsync($"{Svc}/TOTPLogin", new JsonObject { ["Email_ID"] = input["clientcode"], ["TOTP"] = input["totp"], ["PIN"] = input["password"] }, parts[0].Trim(), cancellationToken);
            if (first.Status >= 400) return Unavailable(first.Status);
            var firstBody = first.Body["body"] as JsonObject;
            if (Str(firstBody?["RequestToken"]) is not { Length: > 0 } requestToken)
                return Declined(Str(firstBody?["Message"]), "Check the client code, PIN and one-time code, then try again.");

            var second = await LoginCallAsync($"{Svc}/GetAccessToken", new JsonObject { ["RequestToken"] = requestToken, ["EncryKey"] = secret.Trim(), ["UserId"] = parts[1].Trim() }, parts[0].Trim(), cancellationToken);
            if (second.Status >= 400) return Unavailable(second.Status);
            var secondBody = second.Body["body"] as JsonObject;
            if (Str(secondBody?["AccessToken"]) is { Length: > 0 } token) return BrokerLoginResult.Success(token, null);
            return Declined(Str(secondBody?["Message"]), "Check the app secret in the server's settings, then sign in again.");
        }
        catch (HttpRequestException)
        {
            return BrokerLoginResult.Refused($"Could not reach {DisplayName} to sign in. Check your internet connection and try again.", 502);
        }
    }

    private BrokerLoginResult Declined(string? message, string hint) =>
        BrokerLoginResult.Refused(string.IsNullOrEmpty(message) ? $"{DisplayName} did not accept the sign-in. {hint}" : $"{DisplayName} did not accept the sign-in: {message}");

    private BrokerLoginResult Unavailable(int status) =>
        BrokerLoginResult.Refused(status is 401 or 403 ? $"{DisplayName} did not accept the app key. Check it in the server's settings, then sign in again." : $"{DisplayName} could not sign you in right now. Try again in a minute.", status is 401 or 403 ? 400 : 502);
}
