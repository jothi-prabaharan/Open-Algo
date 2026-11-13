using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Shoonya;

/// <summary>
/// Shoonya sign-in (broker/shoonya/api/auth_api.py): the trader signs in on Shoonya's OAuth page (the client id is the part of the saved key after ":::"), which
/// sends back a code; the code is exchanged at GenAcsTok with the SHA-256 of the client id, the secret and the code. There is no separate feed token.
/// </summary>
public abstract partial class NorenPluginBase
{
    public LoginMethod LoginMethod => LoginMethod.Redirect;

    public IReadOnlyList<string> LoginFields { get; } = [];

    protected static string? ClientId(IReadOnlyDictionary<string, string> keys) =>
        keys.TryGetValue("ApiKey", out var key) && key.Split(":::", 2) is { Length: 2 } parts && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1].Trim() : null;

    public virtual string? LoginUrl(IReadOnlyDictionary<string, string> keys, string state) =>
        ClientId(keys) is { } id ? $"{Host}/OAuthlogin/authorize/oauth?client_id={Uri.EscapeDataString(id)}" : null;

    public virtual async Task<BrokerLoginResult> LoginAsync(IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> input, CancellationToken cancellationToken)
    {
        if (ClientId(keys) is not { } clientId)
            return BrokerLoginResult.Refused($"The {DisplayName} key must be saved as the user id, \":::\" and the client id. Fix it in the server's settings, then sign in again.", 409);
        if (!keys.TryGetValue("ApiSecret", out var secret) || string.IsNullOrWhiteSpace(secret))
            return BrokerLoginResult.Refused($"The {DisplayName} secret is not set on this server. Add it to the server's settings, then sign in again.", 409);
        if (!input.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            return BrokerLoginResult.Refused($"{DisplayName} did not send the sign-in back. Start the sign-in again.");

        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clientId + secret.Trim() + code))).ToLowerInvariant();
        using var request = new HttpRequestMessage(HttpMethod.Post, sessions.BaseUrl(Name, Host) + Api + "/GenAcsTok")
        {
            Content = new StringContent("jData=" + new JsonObject { ["code"] = code, ["checksum"] = checksum }.ToJsonString(), Encoding.UTF8, "text/plain"),
        };
        try
        {
            using var response = await http.CreateClient(Name).SendAsync(request, cancellationToken);
            var body = await ReadAsync(response, cancellationToken) as JsonObject;
            if (!response.IsSuccessStatusCode) return BrokerLoginResult.Refused($"{DisplayName} did not accept the sign-in. Start it again from the beginning.");
            if (Str(body?["stat"]) == "Ok" && Str(body?["access_token"]) is { Length: > 0 } token) return BrokerLoginResult.Success(token, null);
            return Str(body?["stat"]) == "Ok" || body?["access_token"] is not null || Str(body?["emsg"]) is not { Length: > 0 }
                ? BrokerLoginResult.Refused($"{DisplayName} did not accept the sign-in. Start it again from the beginning.")
                : BrokerLoginResult.Refused($"{DisplayName} did not accept the sign-in: {Str(body!["emsg"])}");
        }
        catch (HttpRequestException)
        {
            return BrokerLoginResult.Refused($"{DisplayName} did not answer. Check your connection, then try again.", 502);
        }
    }
}
