namespace EPTradeBot.Core.Brokers;

/// <summary>How a trader signs in to a broker.</summary>
public enum LoginMethod
{
    /// <summary>Through the broker's own page, which sends the trader back with a request token (Zerodha, Paytm Money).</summary>
    Redirect,

    /// <summary>With details typed into EP Trade bot: client code, PIN and a one-time code (Angel One).</summary>
    Credentials,
}

/// <summary>The outcome of a sign-in. <see cref="Message"/> is written for the trader; <see cref="HttpStatus"/> is the status to answer with on a refusal.</summary>
public sealed record BrokerLoginResult(bool Ok, string AuthToken, string? FeedToken, string Message, int HttpStatus = 400)
{
    public static BrokerLoginResult Success(string authToken, string? feedToken) => new(true, authToken, feedToken, "", 200);
    public static BrokerLoginResult Refused(string message, int status = 400) => new(false, "", null, message, status);
}

/// <summary>Signing in to a broker and getting back the tokens its calls and its feed need. The keys are the broker's application keys from configuration.</summary>
public interface IBrokerLogin : IBrokerPlugin
{
    LoginMethod LoginMethod { get; }

    /// <summary>For <see cref="LoginMethod.Credentials"/>: the details the trader types, by the names <see cref="LoginAsync"/> reads them under.</summary>
    IReadOnlyList<string> LoginFields { get; }

    /// <summary>For <see cref="LoginMethod.Redirect"/>: the broker's sign-in page, which sends the trader back carrying <paramref name="state"/>. Null when the keys are not set.</summary>
    string? LoginUrl(IReadOnlyDictionary<string, string> keys, string state);

    /// <summary>Exchanges what the trader returned with (a <c>request_token</c>, or the typed details) for tokens.</summary>
    Task<BrokerLoginResult> LoginAsync(IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> input, CancellationToken cancellationToken);
}
