using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.AliceBlue;

/// <summary>
/// AliceBlue sign-in (broker/aliceblue/api/auth_api.py): the trader signs in on AliceBlue's page, opened with the app code, and AliceBlue sends back an auth code. The code is exchanged at the vendor's getUserDetails with a SHA-256 checksum
/// of the user id (the account's client id), the code and the API secret, and the answer carries the user session. There is no separate feed token.
/// </summary>
public sealed partial class AliceBluePlugin : IBrokerLogin
{
    private const string SignInPage = "https://ant.aliceblueonline.com/";

    public LoginMethod LoginMethod => LoginMethod.Redirect;

    public IReadOnlyList<string> LoginFields { get; } = [];

    public string? LoginUrl(IReadOnlyDictionary<string, string> keys, string state) =>
        keys.TryGetValue("ApiKey", out var key) && !string.IsNullOrWhiteSpace(key) ? $"{SignInPage}?appcode={Uri.EscapeDataString(key)}" : null;

    public async Task<BrokerLoginResult> LoginAsync(IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> input, CancellationToken cancellationToken)
    {
        if (!keys.TryGetValue("ApiSecret", out var secret) || string.IsNullOrWhiteSpace(secret))
            return BrokerLoginResult.Refused($"The {Display} API secret is not set on this server. Add it to the server's settings, then sign in again.", 409);
        var code = input.TryGetValue("code", out var c) && !string.IsNullOrWhiteSpace(c) ? c : input.TryGetValue("request_token", out var t) ? t : null;
        if (string.IsNullOrWhiteSpace(code)) return BrokerLoginResult.Refused($"{Display} did not send the sign-in back. Start the sign-in again.");
        if (!input.TryGetValue("client_id", out var userId) || string.IsNullOrWhiteSpace(userId))
            return BrokerLoginResult.Refused($"This account has no {Display} client id. Add it to the account, then sign in again.", 409);

        var checksum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{userId}{code}{secret}")));
        (int Status, JsonNode? Body) reply;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, sessions.BaseUrl(Name, "https://ant.aliceblueonline.com") + "/open-api/od/v1/vendor/getUserDetails")
            {
                Content = new StringContent(new JsonObject { ["checkSum"] = checksum }.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var response = await Client().SendAsync(request, cancellationToken);
            reply = ((int)response.StatusCode, await ReadAsync(response, cancellationToken));
        }
        catch (HttpRequestException)
        {
            return BrokerLoginResult.Refused($"Could not reach {Display} to sign in. Check your internet connection and try again.", 502);
        }

        if (reply.Body is not JsonObject answer) return BrokerLoginResult.Refused($"{Display} sent an answer that could not be read. Try again in a moment.", 502);
        if (Str(answer["stat"]) == "Ok" && Truthy(answer["userSession"])) return BrokerLoginResult.Success(Str(answer["userSession"])!, null);
        if (Str(answer["stat"]) == "Not_ok") return BrokerLoginResult.Refused(Truthy(answer["emsg"]) ? $"{Display} did not accept the sign-in: {Str(answer["emsg"])}" : $"{Display} did not accept the sign-in. Start it again from the beginning.");
        if (Truthy(answer["emsg"])) return BrokerLoginResult.Refused($"{Display} did not accept the sign-in: {Str(answer["emsg"])}");
        return BrokerLoginResult.Refused($"{Display} did not accept the sign-in. Start it again from the beginning.");
    }
}
