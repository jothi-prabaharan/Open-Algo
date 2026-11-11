using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Angel;

/// <summary>Angel One sign-in (broker/angel/api/auth_api.py): client code, PIN and a one-time code, for a token and a feed token.</summary>
public sealed partial class AngelPlugin
{
    public LoginMethod LoginMethod => LoginMethod.Credentials;

    public IReadOnlyList<string> LoginFields { get; } = ["clientcode", "password", "totp"];

    public string? LoginUrl(IReadOnlyDictionary<string, string> keys, string state) => null;

    public async Task<BrokerLoginResult> LoginAsync(IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> input, CancellationToken cancellationToken)
    {
        if (!keys.TryGetValue("ApiKey", out var apiKey) || string.IsNullOrWhiteSpace(apiKey))
            return BrokerLoginResult.Refused("The Angel One API key is not set on this server. Add it to the server's settings, then sign in again.", 409);
        foreach (var field in LoginFields)
        {
            if (!input.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
                return BrokerLoginResult.Refused("Enter the client code, PIN and one-time code to sign in to Angel One.");
        }

        var body = new JsonObject { ["clientcode"] = input["clientcode"], ["password"] = input["password"], ["totp"] = input["totp"] };
        // The same headers as every SmartAPI call, but there is no token yet.
        using var request = Request(new BrokerSession(Guid.Empty, Name, "", null, null, keys), HttpMethod.Post, "/rest/auth/angelbroking/user/v1/loginByPassword", body);
        request.Headers.Remove("Authorization");
        using var response = await SendAsync(request, cancellationToken);
        var reply = await ReadAsync(response, cancellationToken) as JsonObject;
        if (Str(reply?["data"]?["jwtToken"]) is { Length: > 0 } token) return BrokerLoginResult.Success(token, Str(reply?["data"]?["feedToken"]));

        var message = Str(reply?["message"]);
        return BrokerLoginResult.Refused(message is { Length: > 0 }
            ? $"Angel One did not accept the sign-in: {message}"
            : "Angel One did not accept the sign-in. Check the client code, PIN and one-time code, then try again.");
    }
}
