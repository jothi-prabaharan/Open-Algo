using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Tradejini;

/// <summary>
/// Tradejini sign-in (broker/tradejini/api/auth_api.py authenticate_broker): the password (the CubePlus PIN), the one-time code and its kind (totp, or otp sent by SMS or email; anything else is read as totp) go to the individual-token
/// service with the app API key as the bearer token. The access token it answers is the session; every later call sends it as the API key, a colon and the token. Tradejini answers every refusal that comes before the credentials are read
/// (a source address that is not whitelisted, a wrong API key) with the same bare 401, so a 401 is answered with the four things to check. OpenAlgo's public-app sign-in through Tradejini's authorize page is not reached from its sign-in page and is not ported.
/// </summary>
public sealed partial class TradejiniPlugin : IBrokerLogin
{
    public LoginMethod LoginMethod => LoginMethod.Credentials;

    public IReadOnlyList<string> LoginFields { get; } = ["password", "totp", "twofatype"];

    public string? LoginUrl(IReadOnlyDictionary<string, string> keys, string state) => null;

    private const string Unauthorized = "Tradejini did not accept the sign-in. Check, in order: the password is your CubePlus login PIN, not your account password; this server is the static IP whitelisted for your app in the Tradejini developer portal; the API key saved here is your app's API key; and the one-time code is current. Tradejini gives the same answer for all four.";

    private const string Unreadable = "Tradejini sent an answer that could not be read. Try again in a moment.";

    public async Task<BrokerLoginResult> LoginAsync(IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> input, CancellationToken cancellationToken)
    {
        if (!input.TryGetValue("password", out var password) || string.IsNullOrEmpty(password) || !input.TryGetValue("totp", out var code) || string.IsNullOrEmpty(code))
            return BrokerLoginResult.Refused($"Enter the password and the one-time code to sign in to {Display}.");
        var kind = (input.GetValueOrDefault("twofatype") ?? "totp").ToLowerInvariant();
        if (kind is not ("otp" or "totp")) kind = "totp";
        var key = ApiKey(new BrokerSession(Guid.Empty, Name, "", null, null, keys));
        if (key is null) return BrokerLoginResult.Refused(KeyMissing, 409);

        using var request = new HttpRequestMessage(HttpMethod.Post, sessions.BaseUrl(Name, DefaultBaseUrl) + "/api-gw/oauth/individual-token-v2")
        {
            Content = new FormUrlEncodedContent([new("password", password), new("twoFa", code), new("twoFaTyp", kind)]),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        try
        {
            using var response = await Client().SendAsync(request, cancellationToken);
            var status = (int)response.StatusCode;
            var body = await ReadAsync(response, cancellationToken);
            if (body is not JsonObject answer) return BrokerLoginResult.Refused(Unreadable, 502);
            if (status != 200)
            {
                if (status == 401) return BrokerLoginResult.Refused(Unauthorized);
                var why = Truthy(answer["msg"]) ? Str(answer["msg"]) : Truthy(answer["message"]) ? Str(answer["message"]) : null;
                return BrokerLoginResult.Refused(why is not null ? $"{Display} did not accept the sign-in: {why}" : $"{Display} did not accept the sign-in. Check your password and the one-time code, then try again.");
            }

            if (answer["access_token"] is not { } token || Str(token) is not { Length: > 0 } access) return BrokerLoginResult.Refused($"{Display} accepted the sign-in but sent no session. Start the sign-in again.", 502);
            var type = Truthy(answer["token_type"]) ? Str(answer["token_type"])!.ToLowerInvariant() : "";
            if (type.Length > 0 && type != "bearer") return BrokerLoginResult.Refused($"{Display} signed you in with a session of a kind this server cannot use ({Str(answer["token_type"])}). Try again, and tell {Display} if it keeps happening.", 502);
            return BrokerLoginResult.Success(access, null);
        }
        catch (HttpRequestException)
        {
            return BrokerLoginResult.Refused($"Could not reach {Display} to sign in. Check your internet connection and try again.", 502);
        }
    }
}
