using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Nubra;

/// <summary>
/// Nubra sign-in (broker/nubra/api/auth_api.py): by a code sent to the phone, in two steps. A first request, made with no code, asks Nubra to send it (/sendphoneotp with no device id; when Nubra answers VERIFY_TOTP a second call, carrying the token, with skip_totp set, sends the SMS)
/// and answers a temp token, which the server keeps. The code the trader types goes to /verifyphoneotp with the temp token and the device id for an auth token, and the MPIN to /verifypin for the session token. The temp token is single use: it is dropped when the code is tried.
/// The phone number is the API key and the MPIN the API secret.
/// </summary>
public sealed partial class NubraPlugin : IBrokerLogin
{
    private static readonly ConcurrentDictionary<string, string> TempTokens = new();

    public LoginMethod LoginMethod => LoginMethod.Credentials;

    public IReadOnlyList<string> LoginFields { get; } = ["otp"];

    public string? LoginUrl(IReadOnlyDictionary<string, string> keys, string state) => null;

    private static string Fallback(JsonNode? body, string fallback) => body is JsonObject o && Truthy(o["error"]) ? Str(o["error"])! : body is JsonObject m && Truthy(m["message"]) ? Str(m["message"])! : fallback;

    public async Task<BrokerLoginResult> LoginAsync(IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> input, CancellationToken cancellationToken)
    {
        if (!keys.TryGetValue("ApiKey", out var phone) || string.IsNullOrWhiteSpace(phone) || !keys.TryGetValue("ApiSecret", out var mpin) || string.IsNullOrWhiteSpace(mpin))
            return BrokerLoginResult.Refused($"The {Display} phone number (API key) and MPIN (API secret) are not both set on this server. Add them to the server's settings, then sign in again.", 409);
        var code = input.GetValueOrDefault("otp")?.Trim() ?? "";
        try
        {
            return code.Length == 0 ? await SendCodeAsync(phone, cancellationToken) : await VerifyAsync(phone, mpin, code, input.GetValueOrDefault("otp_token"), cancellationToken);
        }
        catch (HttpRequestException)
        {
            return BrokerLoginResult.Refused($"Could not reach {Display} to sign in. Check your internet connection and try again.", 502);
        }
    }

    private async Task<(int Status, JsonNode? Body)> PostAsync(string path, JsonObject body, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, sessions.BaseUrl(Name, DefaultBaseUrl) + path) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("Accept", headers.GetValueOrDefault("Accept", "*/*"));
        foreach (var (name, value) in headers.Where(h => h.Key != "Accept")) request.Headers.TryAddWithoutValidation(name, value);
        using var response = await Client().SendAsync(request, cancellationToken);
        return ((int)response.StatusCode, await ReadAsync(response, cancellationToken));
    }

    private async Task<(JsonObject? Data, string? Error)> SendPhoneCodeAsync(string phone, string? temp, bool skipTotp, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>();
        if (temp is not null) headers["x-temp-token"] = temp;
        var (status, body) = await PostAsync("/sendphoneotp", new JsonObject { ["phone"] = phone, ["flow"] = "", ["skip_totp"] = skipTotp }, headers, cancellationToken);
        if (body is JsonObject answer && Truthy(answer["temp_token"])) return (answer, null);
        return (null, body is JsonObject ? Fallback(body, $"sendphoneotp failed (HTTP {status})") : $"{Display} sent an answer that could not be read. Try again in a moment.");
    }

    private async Task<BrokerLoginResult> SendCodeAsync(string phone, CancellationToken cancellationToken)
    {
        var masked = phone.Length > 7 ? $"{phone[..5]}***{phone[^2..]}" : "***";
        var (data, error) = await SendPhoneCodeAsync(phone, null, false, cancellationToken);
        if (data is null) return BrokerLoginResult.Refused(error!, error!.Contains("could not be read", StringComparison.Ordinal) ? 502 : 400);
        var next = (Str(data["next"]) ?? "").ToUpperInvariant();
        if (next == "VERIFY_TOTP")
        {
            (data, error) = await SendPhoneCodeAsync(phone, Str(data!["temp_token"]), true, cancellationToken);
            if (data is null) return BrokerLoginResult.Refused(error!, error!.Contains("could not be read", StringComparison.Ordinal) ? 502 : 400);
        }
        else if (next != "VERIFY_MOBILE")
        {
            return BrokerLoginResult.Refused($"{Display} asked for a sign-in step this server does not know ({(Truthy(data?["next"]) ? Str(data!["next"]) : "none")}). Try again, and tell {Display} if it keeps happening.", 502);
        }

        TempTokens[phone] = Str(data!["temp_token"])!;
        return BrokerLoginResult.Refused($"A one-time code was sent to {masked}. Enter it to finish signing in.", 202);
    }

    private async Task<BrokerLoginResult> VerifyAsync(string phone, string mpin, string code, string? typedToken, CancellationToken cancellationToken)
    {
        // The temp token is single use, so it is taken out whether or not the code is right.
        var typed = typedToken is { Length: > 0 } ? typedToken : null;
        var stored = TempTokens.TryRemove(phone, out var kept) ? kept : null;
        var temp = typed ?? stored;
        if (temp is null) return BrokerLoginResult.Refused($"The sign-in expired before the code was entered. Start the {Display} sign-in again to be sent a new code.");
        if (!code.All(char.IsAsciiDigit)) return BrokerLoginResult.Refused("Enter the numeric code that was sent to your phone.");

        var (status, body) = await PostAsync("/verifyphoneotp", new JsonObject { ["phone"] = phone, ["otp"] = code }, new Dictionary<string, string> { ["x-temp-token"] = temp, ["x-device-id"] = DeviceId }, cancellationToken);
        if (body is not JsonObject first) return BrokerLoginResult.Refused($"{Display} sent an answer that could not be read. Try again in a moment.", 502);
        if (!Truthy(first["auth_token"])) return BrokerLoginResult.Refused(Fallback(first, $"verifyphoneotp failed (HTTP {status})"));

        var (pinStatus, pinBody) = await PostAsync("/verifypin", new JsonObject { ["pin"] = mpin }, new Dictionary<string, string> { ["Authorization"] = $"Bearer {Str(first["auth_token"])}", ["Accept"] = "application/json", ["x-device-id"] = DeviceId }, cancellationToken);
        if (pinBody is not JsonObject second) return BrokerLoginResult.Refused($"{Display} sent an answer that could not be read. Try again in a moment.", 502);
        if (pinStatus != 200) return BrokerLoginResult.Refused(Fallback(second, "PIN verification failed"));
        return Truthy(second["session_token"]) ? BrokerLoginResult.Success(Str(second["session_token"])!, null) : BrokerLoginResult.Refused($"{Display} accepted the PIN but sent no session. Start the sign-in again.", 502);
    }
}
