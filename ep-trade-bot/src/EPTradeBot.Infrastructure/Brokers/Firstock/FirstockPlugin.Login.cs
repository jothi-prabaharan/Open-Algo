using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Firstock;

/// <summary>
/// Firstock sign-in (broker/firstock/api/auth_api.py): the user id, the password (sent as its SHA-256) and a one-time code go to /login with the vendor code (the broker key) and the API key (the secret); the session
/// key comes back as susertoken (or, older, jKey).
/// </summary>
public sealed partial class FirstockPlugin : IBrokerLogin
{
    public LoginMethod LoginMethod => LoginMethod.Credentials;

    public IReadOnlyList<string> LoginFields { get; } = ["clientcode", "password", "totp"];

    public string? LoginUrl(IReadOnlyDictionary<string, string> keys, string state) => null;

    public async Task<BrokerLoginResult> LoginAsync(IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> input, CancellationToken cancellationToken)
    {
        if (!keys.TryGetValue("ApiKey", out var vendor) || string.IsNullOrWhiteSpace(vendor) || !keys.TryGetValue("ApiSecret", out var apiKey) || string.IsNullOrWhiteSpace(apiKey))
            return BrokerLoginResult.Refused("The Firstock vendor code and API key are not both set on this server. Add them to the server's settings, then sign in again.", 409);
        if (!input.TryGetValue("clientcode", out var user) || string.IsNullOrWhiteSpace(user) || !input.TryGetValue("password", out var password) || string.IsNullOrEmpty(password))
            return BrokerLoginResult.Refused("Enter the user id and password to sign in to Firstock.");

        var body = new JsonObject
        {
            ["userId"] = user, ["password"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password))).ToLowerInvariant(), ["TOTP"] = input.TryGetValue("totp", out var totp) ? totp : "",
            ["vendorCode"] = vendor, ["apiKey"] = apiKey,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, sessions.BaseUrl(Name, DefaultBaseUrl) + "/login") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        JsonObject answer;
        int status;
        try
        {
            using var response = await Client().SendAsync(request, cancellationToken);
            status = (int)response.StatusCode;
            answer = await ReadAsync(response, cancellationToken) as JsonObject ?? [];
        }
        catch (HttpRequestException)
        {
            return BrokerLoginResult.Refused($"Could not reach {DisplayName} to sign in. Check your internet connection and try again.", 502);
        }

        if (status == 200 && Succeeded(answer))
        {
            var data = answer["data"] as JsonObject;
            var key = Str(data?["susertoken"]) is { Length: > 0 } fresh ? fresh : Str(data?["jKey"]);
            return !string.IsNullOrEmpty(key)
                ? BrokerLoginResult.Success(key, null)
                : BrokerLoginResult.Refused($"{DisplayName} accepted the sign-in but sent no session key. Start the sign-in again.", 502);
        }

        if (status == 401) return BrokerLoginResult.Refused($"{DisplayName} did not accept the user id, the password or the API key. Check them, then try again.");
        if (status >= 500) return BrokerLoginResult.Refused($"{DisplayName} could not sign you in right now. Try again in a minute.", 502);
        var detail = answer["error"] as JsonObject;
        var field = Str(detail?["field"]);
        var said = !string.IsNullOrEmpty(field) && Str(detail?["message"]) is { Length: > 0 } fieldMessage ? $"{field}: {fieldMessage}" : Str(answer["message"]) is { Length: > 0 } m ? m : Str(detail?["message"]);
        return BrokerLoginResult.Refused(string.IsNullOrEmpty(said) ? $"{DisplayName} did not accept the sign-in. Check the user id, password and one-time code, then try again." : $"{DisplayName} did not accept the sign-in: {said}");
    }
}
