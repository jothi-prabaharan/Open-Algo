using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Mstock;

/// <summary>
/// mStock sign-in (broker/mstock/api/auth_api.py authenticate_with_totp): the client code, the password and the one-time code from the authenticator go to /connect/login; the refresh token it answers (or its jwtToken when it gives no
/// refresh token) is verified at /session/verifytotp with the API key in X-PrivateKey and the same code, and that answer carries the jwtToken that signs every call and the feedToken the stream uses. OpenAlgo's two-step sign-in by a code
/// sent to the phone (send_otp, verify_otp) is not reached from its sign-in page and is not ported.
/// </summary>
public sealed partial class MstockPlugin : IBrokerLogin
{
    public LoginMethod LoginMethod => LoginMethod.Credentials;

    public IReadOnlyList<string> LoginFields { get; } = ["password", "totp"];

    public string? LoginUrl(IReadOnlyDictionary<string, string> keys, string state) => null;

    /// <summary>mStock's success for a sign-in: status true or the text true, no other spelling.</summary>
    private static bool SignedIn(JsonObject answer) => answer["status"] is JsonValue v && (v.TryGetValue<bool>(out var flag) ? flag : v.TryGetValue<string>(out var text) && text == "true") && answer.ContainsKey("data");

    private async Task<(int Status, JsonNode? Body)> LoginCallAsync(string path, JsonObject body, string? privateKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, sessions.BaseUrl(Name, DefaultBaseUrl) + path) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("X-Mirae-Version", "1");
        if (privateKey is not null) request.Headers.TryAddWithoutValidation("X-PrivateKey", privateKey);
        using var response = await Client().SendAsync(request, cancellationToken);
        return ((int)response.StatusCode, await ReadAsync(response, cancellationToken));
    }

    public async Task<BrokerLoginResult> LoginAsync(IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> input, CancellationToken cancellationToken)
    {
        if (!keys.TryGetValue("ApiKey", out var clientCode) || string.IsNullOrWhiteSpace(clientCode) || !keys.TryGetValue("ApiSecret", out var apiKey) || string.IsNullOrWhiteSpace(apiKey))
            return BrokerLoginResult.Refused($"The {Display} client code and API key are not both set on this server. Add them to the server's settings, then sign in again.", 409);
        if (!input.TryGetValue("password", out var password) || string.IsNullOrEmpty(password)) return BrokerLoginResult.Refused($"Enter the password to sign in to {Display}.");
        if (!input.TryGetValue("totp", out var totp) || string.IsNullOrEmpty(totp)) return BrokerLoginResult.Refused($"Enter the one-time code from your authenticator to sign in to {Display}.");

        try
        {
            var (loginStatus, login) = await LoginCallAsync("/connect/login", new JsonObject { ["clientcode"] = clientCode, ["password"] = password, ["totp"] = totp, ["state"] = "" }, null, cancellationToken);
            if (loginStatus >= 400 || login is not JsonObject first) return Refusal("sign-in", login, loginStatus);
            if (!SignedIn(first)) return Refusal("sign-in", first, 200);
            var data = first["data"] as JsonObject;
            var refresh = data is not null && Truthy(data["refreshToken"]) ? Str(data["refreshToken"]) : data is not null && Truthy(data["jwtToken"]) ? Str(data["jwtToken"]) : null;
            if (string.IsNullOrEmpty(refresh)) return BrokerLoginResult.Refused($"{Display} signed you in but sent no token to finish with. Start the sign-in again.", 502);

            var (verifyStatus, verify) = await LoginCallAsync("/session/verifytotp", new JsonObject { ["refreshToken"] = refresh, ["totp"] = totp }, apiKey, cancellationToken);
            if (verifyStatus >= 400 || verify is not JsonObject second) return Refusal("one-time code", verify, verifyStatus);
            if (!SignedIn(second)) return Refusal("one-time code", second, 200);
            var final = second["data"] as JsonObject;
            var jwt = final is not null && Truthy(final["jwtToken"]) ? Str(final["jwtToken"]) : null;
            if (string.IsNullOrEmpty(jwt)) return BrokerLoginResult.Refused($"{Display} accepted the one-time code but sent no session. Start the sign-in again.", 502);
            return BrokerLoginResult.Success(jwt, final is not null && Truthy(final["feedToken"]) ? Str(final["feedToken"]) : null);
        }
        catch (HttpRequestException)
        {
            return BrokerLoginResult.Refused($"Could not reach {Display} to sign in. Check your internet connection and try again.", 502);
        }
    }

    private static BrokerLoginResult Refusal(string step, JsonNode? answer, int status)
    {
        if (answer is not JsonObject) return BrokerLoginResult.Refused($"{Display} sent an answer that could not be read. Try again in a moment.", 502);
        var why = Message(answer);
        return BrokerLoginResult.Refused(why is not null ? $"{Display} did not accept the {step}: {why}" : $"{Display} did not accept the {step}. Check your password and the one-time code, then try again.");
    }
}
