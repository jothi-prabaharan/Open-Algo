using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.IndMoney;

/// <summary>
/// IndMoney sign-in (broker/indmoney/api/auth_api.py), two ways as OpenAlgo has them.
/// With the MPIN and a live one-time code, POST /generate/token takes the Client ID in x-api-key and answers the day's token (data.token, else data.access_token, else the same two at the top); it is called once and never again, because
/// IndMoney allows one request a minute and locks the account after five wrong codes. With no MPIN and code, a token pasted into the API secret is used after GET /user/profile accepts it: a 401 or 403 refuses it, and any other
/// answer (or none) is not proof against it, so it is used.
/// </summary>
public sealed partial class IndMoneyPlugin : IBrokerLogin
{
    public LoginMethod LoginMethod => LoginMethod.Credentials;

    /// <summary>The MPIN and the code. Both blank signs in with the token saved as the API secret.</summary>
    public IReadOnlyList<string> LoginFields { get; } = ["mpin", "totp"];

    public string? LoginUrl(IReadOnlyDictionary<string, string> keys, string state) => null;

    private static string Unreadable => $"{Display} sent an answer that could not be read. Try again in a moment.";

    public async Task<BrokerLoginResult> LoginAsync(IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> input, CancellationToken cancellationToken)
    {
        var mpin = input.TryGetValue("mpin", out var m) ? m.Trim() : "";
        var code = input.TryGetValue("totp", out var t) ? t.Trim() : "";
        var secret = keys.TryGetValue("ApiSecret", out var s) ? s.Trim() : "";
        if (mpin.Length > 0 && code.Length > 0) return await MintAsync(keys, mpin, code, cancellationToken);
        if (secret.Length > 0) return await UsePastedAsync(secret, cancellationToken);
        return BrokerLoginResult.Refused($"Enter the MPIN and the one-time code from your authenticator to sign in to {Display}.");
    }

    private async Task<BrokerLoginResult> MintAsync(IReadOnlyDictionary<string, string> keys, string mpin, string code, CancellationToken cancellationToken)
    {
        var clientId = keys.TryGetValue("ApiKey", out var k) ? k.Trim() : "";
        if (clientId.Length == 0) return BrokerLoginResult.Refused($"The {Display} Client ID is not set on this server. Add the Client ID shown on the {Display} access tokens page, then sign in again.", 409);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, sessions.BaseUrl(Name, DefaultBaseUrl) + "/generate/token")
            {
                Content = new StringContent(new JsonObject { ["mpin"] = mpin, ["totp"] = code }.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("x-api-key", clientId);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var response = await Client().SendAsync(request, cancellationToken);
            var status = (int)response.StatusCode;
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonNode? parsed = null;
            try
            {
                parsed = JsonNode.Parse(text);
            }
            catch (System.Text.Json.JsonException)
            {
            }

            if (parsed is not JsonObject answer) return BrokerLoginResult.Refused(Unreadable, 502);
            if (status is 200 or 201)
            {
                var data = answer["data"] as JsonObject;
                var token = Pick(data?["token"], data?["access_token"], answer["token"], answer["access_token"]);
                return token is null ? BrokerLoginResult.Refused($"{Display} accepted the sign-in but sent no token. Start the sign-in again.", 502) : BrokerLoginResult.Success(token, null);
            }

            return BrokerLoginResult.Refused(Declined(status, Truthy(answer["message"]) ? Str(answer["message"]) : Truthy(answer["error"]) ? Str(answer["error"]) : null));
        }
        catch (HttpRequestException)
        {
            return BrokerLoginResult.Refused($"Could not reach {Display} to sign in. Check your internet connection and try again.", 502);
        }
    }

    private static string? Pick(params JsonNode?[] candidates) => candidates.Where(Truthy).Select(Str).FirstOrDefault(x => !string.IsNullOrEmpty(x));

    /// <summary>What a refusal says, with the hint that keeps the trader out of a lockout.</summary>
    internal static string Declined(int status, string? why)
    {
        switch (status)
        {
            case 401 or 403:
                return $"{why ?? $"{Display} did not accept the Client ID, the MPIN or the one-time code."} Check the Client ID saved on this server and your MPIN, then wait for a fresh code and never send one you already used. " +
                       "Five wrong codes in 15 minutes lock token requests for 15 minutes. If codes keep failing, check that this server's clock is right.";
            case 429:
                return $"{why ?? $"{Display} is limiting token requests."} {Display} allows one token request a minute. Wait a minute, then try again.";
            case >= 500:
                return why ?? $"{Display} is not available right now. Try again shortly.";
            default:
                return why ?? $"{Display} did not give a token. Check the details and try again.";
        }
    }

    private async Task<BrokerLoginResult> UsePastedAsync(string token, CancellationToken cancellationToken)
    {
        var session = new BrokerSession(Guid.Empty, Name, token, null, null, new Dictionary<string, string>());
        try
        {
            var (status, body, _) = await SendAsync(session, HttpMethod.Get, "/user/profile", null, null, cancellationToken);
            if (status is 401 or 403)
            {
                var why = body is JsonObject o ? (Truthy(o["message"]) ? Str(o["message"]) : Truthy(o["error_type"]) ? Str(o["error_type"]) : null) : null;
                return BrokerLoginResult.Refused($"{Display} did not accept the access token saved as the API secret ({why ?? "it was refused"}). {Display} tokens last 24 hours. Save a fresh token from the {Display} access tokens page, or clear the API secret and sign in with your MPIN and a one-time code.");
            }
        }
        catch (BrokerException)
        {
            // An answer that never came says nothing against the token, so it is used and the first real call will say if it is dead.
        }

        return BrokerLoginResult.Success(token, null);
    }
}
