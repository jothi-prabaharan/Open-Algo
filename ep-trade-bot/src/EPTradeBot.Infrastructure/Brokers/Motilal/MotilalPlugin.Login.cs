using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Motilal;

/// <summary>
/// Motilal sign-in (broker/motilal/api/auth_api.py): the client code, the password (hashed with SHA-256 together with the app key), the date of birth and the one-time code from the authenticator go to authdirectapi, which answers the AuthToken;
/// with an API secret set, getaccesstoken then adds the access token (a failure there never fails the sign-in). The SMS and email code path is not ported: a token still waiting for its code is refused.
/// </summary>
public sealed partial class MotilalPlugin : IBrokerLogin
{
    public LoginMethod LoginMethod => LoginMethod.Credentials;

    public IReadOnlyList<string> LoginFields { get; } = ["clientcode", "password", "totp", "dob"];

    public string? LoginUrl(IReadOnlyDictionary<string, string> keys, string state) => null;

    private static string Reason(JsonObject body, string fallback)
    {
        var message = Truthy(body["message"]) ? Str(body["message"])! : fallback;
        return Truthy(body["errorcode"]) ? $"{message} (errorcode: {Str(body["errorcode"])})" : message;
    }

    public async Task<BrokerLoginResult> LoginAsync(IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> input, CancellationToken cancellationToken)
    {
        if (!keys.TryGetValue("ApiKey", out var apiKey) || string.IsNullOrWhiteSpace(apiKey))
            return BrokerLoginResult.Refused($"The {Display} API key is not set on this server. Add it to the server's settings, then sign in again.", 409);
        if (!input.TryGetValue("clientcode", out var user) || string.IsNullOrWhiteSpace(user)) return BrokerLoginResult.Refused($"Enter your {Display} client code to sign in.");
        if (!input.TryGetValue("password", out var password) || string.IsNullOrEmpty(password)) return BrokerLoginResult.Refused($"Enter your password to sign in to {Display}.");
        if (!input.TryGetValue("dob", out var dob) || string.IsNullOrWhiteSpace(dob)) return BrokerLoginResult.Refused($"Enter your date of birth (DD/MM/YYYY) to sign in to {Display}.");
        if (!input.TryGetValue("totp", out var totp) || string.IsNullOrWhiteSpace(totp))
            return BrokerLoginResult.Refused($"Enter the one-time code from your authenticator to sign in to {Display}. Signing in with a code sent by SMS or email is not available here.");

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password + apiKey))).ToLowerInvariant();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, sessions.BaseUrl(Name, DefaultBaseUrl) + Endpoints["authdirectapi"]);
            AddHeaders(request, keys, user, null);
            request.Content = new StringContent(new JsonObject { ["userid"] = user, ["password"] = hash, ["2FA"] = dob, ["totp"] = totp }.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await Client().SendAsync(request, cancellationToken);
            if ((int)response.StatusCode != 200) return BrokerLoginResult.Refused($"{Display} did not accept the sign-in (HTTP {(int)response.StatusCode}). Check your details, then try again.");
            if (await ReadAsync(response, cancellationToken) is not JsonObject answer) return BrokerLoginResult.Refused($"{Display} sent an answer that could not be read. Try again in a moment.", 502);
            if (Str(answer["status"]) != "SUCCESS" || !Truthy(answer["AuthToken"])) return BrokerLoginResult.Refused(Reason(answer, "Authentication failed. Please try again."));
            var verified = (Truthy(answer["isAuthTokenVerified"]) ? Str(answer["isAuthTokenVerified"])! : "").Trim().ToUpperInvariant();
            if (verified is "FALSE" or "0" or "NO" or "N")
                return BrokerLoginResult.Refused($"{Display} wants a code sent to you by SMS or email, which is not available here. Turn on an authenticator app with {Display} and sign in with its code.");
            var token = Str(answer["AuthToken"])!;
            var access = await AccessTokenAsync(keys, user, token, cancellationToken);
            return BrokerLoginResult.Success(access is null ? token : token + Separator + access, null);
        }
        catch (HttpRequestException)
        {
            return BrokerLoginResult.Refused($"Could not reach {Display} to sign in. Check your internet connection and try again.", 502);
        }
    }

    /// <summary>getaccesstoken: needs the API secret and gives the access token every later call carries. Never fails the sign-in.</summary>
    private async Task<string?> AccessTokenAsync(IReadOnlyDictionary<string, string> keys, string user, string token, CancellationToken cancellationToken)
    {
        if (!keys.TryGetValue("ApiSecret", out var secret) || string.IsNullOrEmpty(secret)) return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, sessions.BaseUrl(Name, DefaultBaseUrl) + Endpoints["getaccesstoken"]);
            AddHeaders(request, keys, user, token);
            using var response = await Client().SendAsync(request, cancellationToken);
            if ((int)response.StatusCode != 200) return null;
            return await ReadAsync(response, cancellationToken) is JsonObject o && Str(o["status"]) == "SUCCESS" && Truthy(o["accesstoken"]) ? Str(o["accesstoken"]) : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }
}
