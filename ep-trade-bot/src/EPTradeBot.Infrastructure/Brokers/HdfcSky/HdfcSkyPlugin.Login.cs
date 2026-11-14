using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.HdfcSky;

/// <summary>
/// HDFC Sky sign-in (broker/hdfcsky/api/auth_api.py): the trader signs in on HDFC Sky' page, which returns a request token. It is exchanged at /oapi/v1/access-token
/// with the api_key and the token in the query and the secret in the body; the access token comes back as accessToken, access_token, or the same inside a data envelope.
/// </summary>
public sealed partial class HdfcSkyPlugin : IBrokerLogin
{
    public LoginMethod LoginMethod => LoginMethod.Redirect;

    public IReadOnlyList<string> LoginFields { get; } = [];

    public string? LoginUrl(IReadOnlyDictionary<string, string> keys, string state) =>
        keys.TryGetValue("ApiKey", out var key) && !string.IsNullOrWhiteSpace(key) ? $"https://developer.hdfcsky.com/oapi/v1/login?api_key={Uri.EscapeDataString(key)}" : null;

    public async Task<BrokerLoginResult> LoginAsync(IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> input, CancellationToken cancellationToken)
    {
        if (!keys.TryGetValue("ApiKey", out var apiKey) || string.IsNullOrWhiteSpace(apiKey) || !keys.TryGetValue("ApiSecret", out var secret) || string.IsNullOrWhiteSpace(secret))
            return BrokerLoginResult.Refused("The HDFC Sky API key and secret are not set on this server. Add them to the server's settings, then sign in again.", 409);
        var token = input.TryGetValue("request_token", out var t) && !string.IsNullOrWhiteSpace(t) ? t : input.TryGetValue("code", out var c) ? c : null;
        if (string.IsNullOrWhiteSpace(token)) return BrokerLoginResult.Refused("HDFC Sky did not send the sign-in back. Start the sign-in again.");

        var session = new BrokerSession(Guid.Empty, Name, "", null, null, new Dictionary<string, string>(keys));
        (int Status, JsonNode? Body) reply;
        try
        {
            reply = await CallAsync(session, HttpMethod.Post, "/oapi/v1/access-token", new Dictionary<string, string> { ["request_token"] = token }, new JsonObject { ["apiSecret"] = secret }, false, cancellationToken);
        }
        catch (BrokerException)
        {
            return BrokerLoginResult.Refused("Could not reach HDFC Sky to sign in. Check your internet connection and try again.", 502);
        }

        var body = reply.Body as JsonObject;
        string? access = null;
        if (body is not null)
        {
            access = Str(body["accessToken"]) is { Length: > 0 } a ? a : Str(body["access_token"]);
            if (string.IsNullOrEmpty(access) && body["data"] is JsonObject data) access = Str(data["accessToken"]) is { Length: > 0 } d ? d : Str(data["access_token"]);
        }

        if (!string.IsNullOrEmpty(access)) return BrokerLoginResult.Success(access, null);
        var why = body is null ? null : Str(body["message"]) is { Length: > 0 } m ? m : Str(body["error"]);
        return BrokerLoginResult.Refused(why is { Length: > 0 } ? $"HDFC Sky did not accept the sign-in: {why}" : "HDFC Sky did not accept the sign-in. Start it again from the beginning.");
    }
}
