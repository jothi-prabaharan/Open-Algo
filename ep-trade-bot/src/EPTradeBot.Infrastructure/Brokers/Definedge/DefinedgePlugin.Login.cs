using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Definedge;

/// <summary>
/// Definedge sign-in (broker/definedge api/auth_api.py): two steps. A first request, made with no code, sends a one-time code to the trader and answers an otp_token, which the server keeps; the code the trader types and a SHA-256 of the
/// token, the code and the API secret then go to /token, which answers the session key and the user token (also the feed token). The session is the session key, the user token and the API token joined by three colons.
/// </summary>
public sealed partial class DefinedgePlugin
{
    private const string SessionUrl = "https://signin.definedgesecurities.com/auth/realms/debroking/dsbpkc";
    private static readonly ConcurrentDictionary<string, string> OtpTokens = new();

    public LoginMethod LoginMethod => LoginMethod.Credentials;

    public IReadOnlyList<string> LoginFields { get; } = ["otp"];

    public string? LoginUrl(IReadOnlyDictionary<string, string> keys, string state) => null;

    public async Task<BrokerLoginResult> LoginAsync(IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> input, CancellationToken cancellationToken)
    {
        if (!keys.TryGetValue("ApiKey", out var apiToken) || string.IsNullOrWhiteSpace(apiToken) || !keys.TryGetValue("ApiSecret", out var secret) || string.IsNullOrWhiteSpace(secret))
            return BrokerLoginResult.Refused($"The {Display} API token and secret are not both set on this server. Add them to the server's settings, then sign in again.", 409);
        var code = input.GetValueOrDefault("otp")?.Trim() ?? "";
        try
        {
            return code.Length == 0 ? await SendCodeAsync(apiToken, secret, cancellationToken) : await VerifyAsync(apiToken, secret, code, input.GetValueOrDefault("otp_token"), cancellationToken);
        }
        catch (HttpRequestException)
        {
            return BrokerLoginResult.Refused($"Could not reach {Display} to sign in. Check your internet connection and try again.", 502);
        }
    }

    private async Task<BrokerLoginResult> SendCodeAsync(string apiToken, string secret, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{SessionUrl}/login/{apiToken}");
        request.Headers.TryAddWithoutValidation("api_secret", secret);
        using var response = await Client().SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode || await ReadAsync(response, cancellationToken) is not JsonObject answer || Str(answer["otp_token"]) is not { Length: > 0 } token)
            return BrokerLoginResult.Refused($"{Display} did not send a one-time code. Check the API token and secret on this server, then try again.", 502);
        OtpTokens[apiToken] = token;
        return BrokerLoginResult.Refused(Truthy(answer["message"]) ? Str(answer["message"])! : "OTP has been sent successfully", 202);
    }

    private async Task<BrokerLoginResult> VerifyAsync(string apiToken, string secret, string code, string? typedToken, CancellationToken cancellationToken)
    {
        var otpToken = typedToken is { Length: > 0 } ? typedToken : OtpTokens.GetValueOrDefault(apiToken);
        if (string.IsNullOrEmpty(otpToken)) return BrokerLoginResult.Refused($"No one-time code was asked for yet. Ask {Display} to send one, then enter it.");
        var ac = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(otpToken + code + secret))).ToLowerInvariant();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{SessionUrl}/token") { Content = new StringContent(new JsonObject { ["otp_token"] = otpToken, ["otp"] = code, ["ac"] = ac }.ToJsonString(), Encoding.UTF8, "application/json") };
        using var response = await Client().SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return BrokerLoginResult.Refused($"{Display} did not accept the one-time code. Check it, then try again.");
        if (await ReadAsync(response, cancellationToken) is not JsonObject answer) return BrokerLoginResult.Refused($"{Display} sent an answer that could not be read. Try again in a moment.", 502);
        if (Str(answer["stat"]) != "Ok") return BrokerLoginResult.Refused(Truthy(answer["emsg"]) ? $"{Display} did not accept the one-time code: {Str(answer["emsg"])}" : $"{Display} did not accept the one-time code. Check it, then try again.");
        if (Str(answer["api_session_key"]) is not { Length: > 0 } sessionKey) return BrokerLoginResult.Refused($"{Display} accepted the code but sent no session. Start the sign-in again.", 502);
        var userToken = Truthy(answer["susertoken"]) ? Str(answer["susertoken"]) : null;
        OtpTokens.TryRemove(apiToken, out _);
        return BrokerLoginResult.Success($"{sessionKey}:::{userToken ?? ""}:::{apiToken}", userToken);
    }
}
