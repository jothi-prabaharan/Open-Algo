using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using static EPTradeBot.Infrastructure.Brokers.JsonRead;

namespace EPTradeBot.Infrastructure.Brokers.Pocketful;

/// <summary>
/// Pocketful sign-in (broker/pocketful/api/auth_api.py): Pocketful's page sends the trader back with a code, which is exchanged at /oauth2/token (a form with the code and the redirect address, Basic key:secret). The token is only a sign-in once
/// the client id has been read with it from /api/v1/user/trading_info; a token with no client id is refused, as OpenAlgo refuses it. The client id is not kept with the session: it is read again when a call needs it.
/// </summary>
public sealed partial class PocketfulPlugin : IBrokerLogin
{
    public LoginMethod LoginMethod => LoginMethod.Redirect;

    public IReadOnlyList<string> LoginFields { get; } = [];

    public string? LoginUrl(IReadOnlyDictionary<string, string> keys, string state) =>
        keys.TryGetValue("ApiKey", out var key) && !string.IsNullOrWhiteSpace(key) && keys.TryGetValue("RedirectUrl", out var redirect) && !string.IsNullOrWhiteSpace(redirect)
            ? $"{DefaultBaseUrl}/oauth2/auth?client_id={Uri.EscapeDataString(key)}&redirect_uri={Uri.EscapeDataString(redirect)}&response_type=code&scope={Uri.EscapeDataString("orders holdings")}&state={Uri.EscapeDataString(state)}"
            : null;

    public async Task<BrokerLoginResult> LoginAsync(IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> input, CancellationToken cancellationToken)
    {
        if (!keys.TryGetValue("ApiKey", out var apiKey) || string.IsNullOrWhiteSpace(apiKey) || !keys.TryGetValue("ApiSecret", out var secret) || string.IsNullOrWhiteSpace(secret) || !keys.TryGetValue("RedirectUrl", out var redirect) || string.IsNullOrWhiteSpace(redirect))
            return BrokerLoginResult.Refused($"The {Display} API key, secret and redirect address are not all set on this server. Add them to the server's settings, then sign in again.", 409);
        if (!input.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code)) return BrokerLoginResult.Refused($"{Display} did not send the sign-in back. Start the sign-in again.");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, sessions.BaseUrl(Name, DefaultBaseUrl) + "/oauth2/token")
            {
                Content = new FormUrlEncodedContent([new("grant_type", "authorization_code"), new("code", code), new("redirect_uri", redirect)]),
            };
            request.Headers.TryAddWithoutValidation("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{apiKey}:{secret}")));
            request.Headers.TryAddWithoutValidation("Accept", "*/*");
            using var response = await Client().SendAsync(request, cancellationToken);
            var body = await ReadAsync(response, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var why = Reason(body);
                return BrokerLoginResult.Refused(why is not null ? $"{Display} did not accept the sign-in: {why}" : $"{Display} did not accept the sign-in. Start it again from the beginning.");
            }

            if (body is not JsonObject answer) return BrokerLoginResult.Refused($"{Display} sent an answer that could not be read. Try again in a moment.", 502);
            if (!Truthy(answer["access_token"])) return BrokerLoginResult.Refused($"{Display} accepted the sign-in but sent no access token. Start the sign-in again.", 502);
            var token = Str(answer["access_token"])!;

            // The sign-in is only complete once the client id is known.
            var session = new BrokerSession(Guid.Empty, Name, token, null, null, keys);
            var (status, info, _) = await SendAsync(session, HttpMethod.Get, "/api/v1/user/trading_info", null, null, cancellationToken);
            if (status >= 400) return BrokerLoginResult.Refused($"{Display} signed you in but would not say which client you are ({Reason(info) ?? "no reason was given"}). Start the sign-in again.", 502);
            if (!Succeeded(info)) return BrokerLoginResult.Refused($"{Display} signed you in but would not say which client you are ({Reason(info) ?? "no reason was given"}). Start the sign-in again.", 502);
            if ((info as JsonObject)?["data"] is not JsonObject data || !Truthy(data["client_id"])) return BrokerLoginResult.Refused($"{Display} signed you in but sent no client id. Start the sign-in again.", 502);
            return BrokerLoginResult.Success(token, null);
        }
        catch (HttpRequestException)
        {
            return BrokerLoginResult.Refused($"Could not reach {Display} to sign in. Check your internet connection and try again.", 502);
        }
        catch (BrokerException ex)
        {
            return BrokerLoginResult.Refused(ex.Message, ex.HttpStatus);
        }
    }
}
