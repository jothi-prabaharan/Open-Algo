using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Samco;

/// <summary>
/// Samco sign-in (broker/samco/api/auth_api.py): there is no redirect and nothing to type. The app's API key and secret from the server's settings are exchanged at POST /session/token for a session token that
/// lasts until 08:00 IST the next day. Samco's three common refusals carry an error code, and each gets the step that fixes it.
/// </summary>
public sealed partial class SamcoPlugin : IBrokerLogin
{
    private const string Dashboard = "https://tradeapi.samco.in/app/login";

    public LoginMethod LoginMethod => LoginMethod.Credentials;

    public IReadOnlyList<string> LoginFields { get; } = [];

    public string? LoginUrl(IReadOnlyDictionary<string, string> keys, string state) => null;

    private static string? Remedy(string? code) => code switch
    {
        "EOAUTH001" => $"Check that the API key in the server's settings is the one Samco emailed when the app was created, and that the app is Active at {Dashboard}.",
        "EOAUTH008" => "Copy the API secret again from the Samco dashboard (API Keys, then Reveal Secret), or generate a new one, and update it in the server's settings.",
        "EOAUTH009" => $"Register this server's address under Static IPs at {Dashboard}.",
        _ => null,
    };

    public async Task<BrokerLoginResult> LoginAsync(IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> input, CancellationToken cancellationToken)
    {
        var apiKey = keys.TryGetValue("ApiKey", out var k) ? k.Trim() : "";
        var secret = keys.TryGetValue("ApiSecret", out var s) ? s.Trim() : "";
        if (apiKey.Length == 0 || secret.Length == 0)
            return BrokerLoginResult.Refused("The Samco API key and secret are not both set on this server. Add them to the server's settings, then sign in again.", 409);

        using var request = new HttpRequestMessage(HttpMethod.Post, sessions.BaseUrl(Name, DefaultBaseUrl) + "/session/token")
        {
            Content = new StringContent(new JsonObject { ["apiKey"] = apiKey, ["apiSecret"] = secret }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        JsonObject body;
        try
        {
            using var response = await Client().SendAsync(request, cancellationToken);
            body = await ReadAsync(response, cancellationToken) as JsonObject ?? [];
        }
        catch (HttpRequestException)
        {
            return BrokerLoginResult.Refused("Could not reach Samco to sign in. Check your internet connection and try again.", 502);
        }

        if (Succeeded(body))
        {
            return Str(body["sessionToken"]) is { Length: > 0 } token
                ? BrokerLoginResult.Success(token, null)
                : BrokerLoginResult.Refused("Samco accepted the sign-in but sent no session token. Start the sign-in again.", 502);
        }

        var reason = Truthy(body["statusMessage"]) ? Str(body["statusMessage"]) : null;
        var remedy = Remedy(Str(body["errorCode"]));
        var said = reason is null ? "Samco did not accept the sign-in. Check the API key and secret in the server's settings, then sign in again." : $"Samco did not accept the sign-in: {reason}";
        return BrokerLoginResult.Refused(remedy is null ? said : $"{said}. {remedy}");
    }
}
