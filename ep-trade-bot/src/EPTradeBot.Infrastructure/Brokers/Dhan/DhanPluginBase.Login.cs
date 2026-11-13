using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Dhan;

/// <summary>
/// Dhan sign-in (broker/dhan/api/auth_api.py and the Dhan branch of blueprints/brlogin.py). Three steps: Dhan issues a consent for this sign-in,
/// the trader approves it on Dhan's page which returns a token id, and the token id is exchanged for an access token. A ready access token
/// (over 100 characters, pasted from Dhan's web) is taken the same way. Either token is then proven with a read of the fund limits, and a token
/// that is not for the configured client id is refused rather than stored.
/// </summary>
public abstract partial class DhanPluginBase
{
    private const string AuthHost = "https://auth.dhan.co";

    /// <summary>The Dhan key is saved as <c>client_id:::api_key</c> or as the key alone.</summary>
    private static (string? ClientId, string Key) SplitKey(string apiKey) =>
        apiKey.Split(":::", 2) is { Length: 2 } parts ? (parts[0].Trim() is { Length: > 0 } c ? c : null, parts[1].Trim()) : (null, apiKey.Trim());

    private static string? Present(IReadOnlyDictionary<string, string> map, string name) => map.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    private HttpRequestMessage AuthRequest(string pathAndQuery, string appKey, string appSecret, bool json = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, sessions.BaseUrl(Name + "_auth", AuthHost) + pathAndQuery);
        request.Headers.TryAddWithoutValidation("app_id", appKey);
        request.Headers.TryAddWithoutValidation("app_secret", appSecret);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        if (json)
        {
            // Dhan's token exchange is a POST with no body that still declares JSON, as OpenAlgo sends it.
            request.Content = new StringContent("");
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        return request;
    }

    protected async Task<LoginStart> ConsentAsync(IReadOnlyDictionary<string, string> keys, string? accountClientId, CancellationToken cancellationToken)
    {
        if (Present(keys, "ApiKey") is not { } rawKey || Present(keys, "ApiSecret") is not { } secret)
            return LoginStart.Refused("The Dhan API key and secret are not set on this server. Add them to the server's settings, then sign in again.");
        var (keyClient, appKey) = SplitKey(rawKey);
        var clientId = keyClient ?? accountClientId;
        if (string.IsNullOrWhiteSpace(clientId))
            return LoginStart.Refused("Dhan needs your client id to start a sign-in. Set the client id on the account, then try again.", 400);

        using var request = AuthRequest($"/app/generate-consent?client_id={Uri.EscapeDataString(clientId)}", appKey, secret);
        using var response = await SendAsync(request, cancellationToken);
        var body = await ReadAsync(response, cancellationToken) as JsonObject;
        if (!response.IsSuccessStatusCode || Str(body?["status"]) != "success" || Str(body?["consentAppId"]) is not { Length: > 0 } consent)
            return LoginStart.Refused("Dhan did not start the sign-in. Check the Dhan API key, secret and client id, then try again.", 502);

        return LoginStart.At($"{sessions.BaseUrl(Name + "_auth", AuthHost)}/login/consentApp-login?consentAppId={Uri.EscapeDataString(consent)}");
    }

    protected async Task<BrokerLoginResult> SignInAsync(IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> input, CancellationToken cancellationToken)
    {
        if (Present(keys, "ApiKey") is not { } rawKey || Present(keys, "ApiSecret") is not { } secret)
            return BrokerLoginResult.Refused("The Dhan API key and secret are not set on this server. Add them to the server's settings, then sign in again.", 409);
        if (!input.TryGetValue("request_token", out var given) || string.IsNullOrWhiteSpace(given))
            return BrokerLoginResult.Refused("Dhan did not send the sign-in back. Start the sign-in again.");

        var (keyClient, appKey) = SplitKey(rawKey);
        var configured = keyClient ?? Present(input, "client_id");
        string token;
        string? reported = null;
        var direct = given.Length > 100;
        if (direct)
        {
            token = given.Trim();
        }
        else
        {
            using var request = AuthRequest($"/app/consumeApp-consent?tokenId={Uri.EscapeDataString(given)}", appKey, secret, json: true);
            using var response = await SendAsync(request, cancellationToken);
            var body = await ReadAsync(response, cancellationToken) as JsonObject;
            if (!response.IsSuccessStatusCode) return BrokerLoginResult.Refused("Dhan did not accept the sign-in. Start it again from the beginning.");
            if (Str(body?["accessToken"]) is not { Length: > 0 } issued) return BrokerLoginResult.Refused("Dhan accepted the sign-in but sent no access token. Start the sign-in again.", 502);
            token = issued;
            reported = Str(body?["dhanClientId"]);
            if (Mismatch(configured, reported) is { } early) return early;
        }

        // Prove the token with the fund limits, which also name the account a pasted token belongs to.
        var checkSession = new BrokerSession(Guid.Empty, Name, token, null, null, keys);
        using var check = Request(checkSession, HttpMethod.Get, "/v2/fundlimit");
        using var funds = await SendAsync(check, cancellationToken);
        var fundBody = await ReadAsync(funds, cancellationToken) as JsonObject;
        var failedStatus = direct ? 401 : 400;
        if (Str(fundBody?["errorType"]) == "Invalid_Authentication" || Str(fundBody?["status"]) == "error" || fundBody is null)
            return BrokerLoginResult.Refused("Dhan did not accept that access token. Sign in again to get a fresh one.", failedStatus);
        if (direct && Mismatch(configured, Str(fundBody["dhanClientId"])) is { } late) return late;

        return BrokerLoginResult.Success(token, null);
    }

    /// <summary>A token for another account, or one whose account Dhan did not name, is never stored when a client id is configured.</summary>
    private static BrokerLoginResult? Mismatch(string? configured, string? reported)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;
        if (string.IsNullOrWhiteSpace(reported))
            return BrokerLoginResult.Refused("Dhan did not confirm which account this sign-in is for, so it was not stored. Sign in again.", 409);
        return reported.Trim() == configured.Trim()
            ? null
            : BrokerLoginResult.Refused("This Dhan sign-in is for a different account than the client id saved for Dhan. Sign in with that account, or change the client id in the server's settings.", 409);
    }
}
