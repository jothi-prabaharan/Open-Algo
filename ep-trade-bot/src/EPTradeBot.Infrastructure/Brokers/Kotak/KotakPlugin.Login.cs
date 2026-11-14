using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Kotak;

/// <summary>
/// Kotak Neo sign-in (broker/kotak api/auth_api.py): the mobile number, the client code (the broker key), the access token (the secret) and a one-time code go to /login/1.0/tradeApiLogin, which answers with a view token and
/// session id; the MPIN goes to /login/1.0/tradeApiValidate with those and answers with the trading token, the session id, the account's base address and its data centre. The session is those four and the access token joined by
/// three colons. A session with no base address would fail on its first call, so it is refused here.
/// </summary>
public sealed partial class KotakPlugin : IBrokerLogin
{
    private const string LoginBase = "https://mis.kotaksecurities.com/login/1.0";

    public LoginMethod LoginMethod => LoginMethod.Credentials;

    public IReadOnlyList<string> LoginFields { get; } = ["mobilenumber", "totp", "mpin"];

    public string? LoginUrl(IReadOnlyDictionary<string, string> keys, string state) => null;

    /// <summary>+91 and a ten-digit number: Kotak wants the country code, and the trader may type it with or without it.</summary>
    private static string Mobile(string typed)
    {
        var number = typed.Trim().Replace("+91", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        if (number.StartsWith("91", StringComparison.Ordinal) && number.Length == 12) number = number[2..];
        return "+91" + number;
    }

    private async Task<(JsonObject? Data, BrokerLoginResult? Refusal)> LoginStepAsync(string url, string access, JsonObject body, Dictionary<string, string> extra, string what, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("Authorization", access);
        request.Headers.TryAddWithoutValidation("neo-fin-key", "neotradeapi");
        foreach (var (name, value) in extra) request.Headers.TryAddWithoutValidation(name, value);
        JsonNode? answer;
        try
        {
            using var response = await Client().SendAsync(request, cancellationToken);
            answer = await ReadAsync(response, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return (null, BrokerLoginResult.Refused($"Could not reach {Display} to sign in. Check your internet connection and try again.", 502));
        }

        if (answer is not JsonObject reply) return (null, BrokerLoginResult.Refused($"{Display} sent an answer that could not be read. Try again in a moment.", 502));
        if (reply["data"] is not null && reply["data"] is not JsonObject) return (null, BrokerLoginResult.Refused($"{Display} sent an answer that could not be read. Try again in a moment.", 502));
        var data = reply["data"] as JsonObject;
        if (data is null || Str(data["status"]) != "success")
        {
            var said = Truthy(reply["errMsg"]) ? Str(reply["errMsg"]) : Truthy(reply["message"]) ? Str(reply["message"]) : null;
            return (null, BrokerLoginResult.Refused(said is { Length: > 0 } ? $"{Display} did not accept the {what}: {said}" : $"{Display} did not accept the {what}. Check it, then try again."));
        }

        return (data, null);
    }

    public async Task<BrokerLoginResult> LoginAsync(IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> input, CancellationToken cancellationToken)
    {
        if (!keys.TryGetValue("ApiKey", out var ucc) || string.IsNullOrWhiteSpace(ucc) || !keys.TryGetValue("ApiSecret", out var access) || string.IsNullOrWhiteSpace(access))
            return BrokerLoginResult.Refused($"The {Display} client code and access token are not both set on this server. Add them to the server's settings, then sign in again.", 409);
        if (!input.TryGetValue("mobilenumber", out var mobile) || string.IsNullOrWhiteSpace(mobile) || !input.TryGetValue("totp", out var totp) || string.IsNullOrWhiteSpace(totp) || !input.TryGetValue("mpin", out var mpin) || string.IsNullOrWhiteSpace(mpin))
            return BrokerLoginResult.Refused($"Enter the mobile number, the one-time code and the MPIN to sign in to {Display}.");

        var (view, refusal) = await LoginStepAsync($"{LoginBase}/tradeApiLogin", access, new JsonObject { ["mobileNumber"] = Mobile(mobile), ["ucc"] = ucc, ["totp"] = totp }, [], "one-time code", cancellationToken);
        if (refusal is not null) return refusal;
        if (Str(view!["token"]) is not { Length: > 0 } viewToken || Str(view["sid"]) is not { Length: > 0 } viewSid)
            return BrokerLoginResult.Refused($"{Display} accepted the one-time code but sent no session. Start the sign-in again.", 502);

        var (trade, second) = await LoginStepAsync($"{LoginBase}/tradeApiValidate", access, new JsonObject { ["mpin"] = mpin }, new Dictionary<string, string> { ["sid"] = viewSid, ["Auth"] = viewToken }, "MPIN", cancellationToken);
        if (second is not null) return second;
        if (Str(trade!["token"]) is not { Length: > 0 } token || Str(trade["sid"]) is not { Length: > 0 } sid)
            return BrokerLoginResult.Refused($"{Display} accepted the MPIN but sent no session. Start the sign-in again.", 502);
        var baseUrl = Str(trade["baseUrl"]) ?? "";
        if (baseUrl.Length == 0) return BrokerLoginResult.Refused($"{Display} accepted the sign-in but did not send the address of your account, so no call could be made. Start the sign-in again.", 502);
        var dataCenter = Str(trade["dataCenter"]) ?? "";
        return BrokerLoginResult.Success($"{token}:::{sid}:::{baseUrl}:::{access}:::{dataCenter}", null);
    }
}
