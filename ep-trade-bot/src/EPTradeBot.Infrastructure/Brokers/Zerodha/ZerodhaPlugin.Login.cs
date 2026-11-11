using System.Security.Cryptography;
using System.Text;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Zerodha;

/// <summary>Zerodha sign-in (broker/zerodha/api/auth_api.py): Kite sends the trader back with a request token, exchanged for the access token.</summary>
public sealed partial class ZerodhaPlugin
{
    public LoginMethod LoginMethod => LoginMethod.Redirect;

    public IReadOnlyList<string> LoginFields { get; } = [];

    public string? LoginUrl(IReadOnlyDictionary<string, string> keys, string state) =>
        keys.TryGetValue("ApiKey", out var key) && !string.IsNullOrWhiteSpace(key)
            ? $"https://kite.zerodha.com/connect/login?v=3&api_key={Uri.EscapeDataString(key)}&redirect_params={Uri.EscapeDataString("state=" + state)}"
            : null;

    public async Task<BrokerLoginResult> LoginAsync(IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> input, CancellationToken cancellationToken)
    {
        if (!keys.TryGetValue("ApiKey", out var apiKey) || string.IsNullOrWhiteSpace(apiKey) || !keys.TryGetValue("ApiSecret", out var secret) || string.IsNullOrWhiteSpace(secret))
            return BrokerLoginResult.Refused("The Zerodha API key and secret are not set on this server. Add them to the server's settings, then sign in again.", 409);
        if (!input.TryGetValue("request_token", out var requestToken) || string.IsNullOrWhiteSpace(requestToken))
            return BrokerLoginResult.Refused("Zerodha did not send the sign-in back. Start the sign-in again.");

        var checksum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey + requestToken + secret)));
        using var request = Request(new BrokerSession(Guid.Empty, BrokerName, "", null, null, keys), HttpMethod.Post, "/session/token");
        request.Headers.Remove("Authorization");
        request.Content = new FormUrlEncodedContent([new("api_key", apiKey), new("request_token", requestToken), new("checksum", checksum)]);
        using var response = await SendAsync(request, cancellationToken);
        var body = await ReadJsonAsync(response, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var message = Str(body?["message"]);
            return BrokerLoginResult.Refused(message is { Length: > 0 } ? $"Zerodha did not accept the sign-in: {message}" : "Zerodha did not accept the sign-in. Start it again from the beginning.");
        }

        return Str(body?["data"]?["access_token"]) is { Length: > 0 } token
            // Kite's Authorization header takes api_key:access_token, so that pair is what the account keeps.
            ? BrokerLoginResult.Success($"{apiKey}:{token}", null)
            : BrokerLoginResult.Refused("Zerodha accepted the sign-in but sent no access token. Start the sign-in again.", 502);
    }
}
