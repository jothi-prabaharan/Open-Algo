using EPTradeBot.Core.Brokers;

namespace EPTradeBot.Infrastructure.Brokers.Delta;

/// <summary>
/// Delta Exchange sign-in (broker/deltaexchange/api/auth_api.py): there is no redirect and nothing to type. The API key and secret from the
/// server's settings are proven by one signed read of the account profile, and the key itself is the session token.
/// </summary>
public sealed partial class DeltaExchangePlugin
{
    public LoginMethod LoginMethod => LoginMethod.Credentials;

    public IReadOnlyList<string> LoginFields { get; } = [];

    public string? LoginUrl(IReadOnlyDictionary<string, string> keys, string state) => null;

    public async Task<BrokerLoginResult> LoginAsync(IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> input, CancellationToken cancellationToken)
    {
        var apiKey = keys.TryGetValue("ApiKey", out var k) ? k.Trim() : "";
        var secret = keys.TryGetValue("ApiSecret", out var s) ? s.Trim() : "";
        if (apiKey.Length == 0 || secret.Length == 0)
            return BrokerLoginResult.Refused("The Delta Exchange API key and secret are not both set on this server. Add them to the server's settings, then sign in again.", 409);

        var session = new BrokerSession(Guid.Empty, Name, apiKey, null, null, new Dictionary<string, string>(keys, StringComparer.OrdinalIgnoreCase) { ["ApiSecret"] = secret });
        using var request = Signed(session, HttpMethod.Get, "/v2/profile", "", null);
        using var response = await SendAsync(request, cancellationToken);
        var body = await JsonRead.ReadAsync(response, cancellationToken) as System.Text.Json.Nodes.JsonObject;

        switch ((int)response.StatusCode)
        {
            case 200 when JsonRead.Truthy(body?["success"]):
                return BrokerLoginResult.Success(apiKey, null);
            case 200:
                return BrokerLoginResult.Refused("Delta Exchange did not accept the API key. Check the key and secret in the server's settings, then sign in again.");
            case 401:
                return BrokerLoginResult.Refused("Delta Exchange did not accept the API key or secret. Check both in the server's settings, then sign in again.");
            case 403:
                return BrokerLoginResult.Refused("Delta Exchange blocked this server. Add this server's address to the API key's allowed addresses in your Delta Exchange account, then sign in again.");
            default:
                return BrokerLoginResult.Refused("Delta Exchange could not confirm the API key right now. Try again in a minute.", 502);
        }
    }
}
