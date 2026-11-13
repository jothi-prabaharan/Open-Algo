using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Xts;

/// <summary>
/// XTS sign-in (broker/wisdom/api/auth_api.py): there is no redirect and nothing to type. The interactive key and secret sign in at
/// /interactive/user/session, then the market-data key and secret sign in at /apimarketdata/auth/login for the feed token. If either is
/// refused the sign-in fails, as it does in OpenAlgo.
/// </summary>
public abstract partial class XtsPluginBase
{
    public LoginMethod LoginMethod => LoginMethod.Credentials;

    public IReadOnlyList<string> LoginFields { get; } = [];

    public string? LoginUrl(IReadOnlyDictionary<string, string> keys, string state) => null;

    public async Task<BrokerLoginResult> LoginAsync(IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> input, CancellationToken cancellationToken)
    {
        string Key(string name) => keys.TryGetValue(name, out var v) ? v.Trim() : "";
        if (Key("ApiKey").Length == 0 || Key("ApiSecret").Length == 0 || Key("ApiKeyMarket").Length == 0 || Key("ApiSecretMarket").Length == 0)
            return BrokerLoginResult.Refused($"The {DisplayName} interactive and market data keys and secrets are not all set on this server. Add them to the server's settings, then sign in again.", 409);

        var (token, refusal) = await TokenAsync("/interactive/user/session", SessionBody(Key("ApiKey"), Key("ApiSecret")), cancellationToken);
        if (refusal is not null) return BrokerLoginResult.Refused($"{DisplayName} did not accept the sign-in: {refusal}");
        if (token is null) return BrokerLoginResult.Refused($"{DisplayName} accepted the sign-in but sent no access token. Start the sign-in again.", 502);

        var (feed, feedRefusal) = await TokenAsync(MarketDataPath + "/auth/login", KeyBody(Key("ApiKeyMarket"), Key("ApiSecretMarket")), cancellationToken);
        if (feedRefusal is not null) return BrokerLoginResult.Refused($"{DisplayName} accepted the sign-in but refused the market data login: {feedRefusal}");
        return BrokerLoginResult.Success(token, feed);
    }

    private static JsonObject KeyBody(string appKey, string secretKey) => new() { ["appKey"] = appKey, ["secretKey"] = secretKey, ["source"] = "WebAPI" };

    /// <summary>What the interactive sign-in sends. Jainam names no source and sends an access token instead.</summary>
    protected virtual JsonObject SessionBody(string appKey, string secretKey) => KeyBody(appKey, secretKey);

    /// <summary>One XTS session request. The token, or the reason it was refused; both null when the answer carried no token.</summary>
    private async Task<(string? Token, string? Refusal)> TokenAsync(string path, JsonObject payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, sessions.BaseUrl(Name, DefaultBaseUrl) + path)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var response = await SendAsync(request, cancellationToken);
        var body = await ReadAsync(response, cancellationToken) as JsonObject;
        if (!response.IsSuccessStatusCode || Str(body?["type"]) != "success")
            return (null, Str(body?["message"]) ?? Str(body?["description"]) ?? "no reason was given.");
        return (Str(body?["result"]?["token"]) is { Length: > 0 } t ? t : null, null);
    }
}
