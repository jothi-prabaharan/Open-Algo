namespace EPTradeBot.Core.Streaming;

/// <summary>
/// The broker market-data subscriptions behind the WebSocket proxy. The proxy tells it what each client wants; it holds one broker connection
/// per account and subscribes an instrument at the broker while any client of that account holds it.
/// </summary>
public interface IFeedSubscriptions
{
    /// <summary>Asks for ticks. The answer says whether the broker feed took the request; a refusal is worded for a trader.</summary>
    Task<FeedAnswer> SubscribeAsync(Guid accountId, string broker, string exchange, string symbol, StreamMode mode, CancellationToken cancellationToken);

    /// <summary>Gives up one client's hold on an instrument; the broker is told only when no client of the account holds it any more.</summary>
    Task UnsubscribeAsync(Guid accountId, string exchange, string symbol, StreamMode mode, CancellationToken cancellationToken);
}

/// <summary>Whether a feed request was taken, and if not, why.</summary>
public sealed record FeedAnswer(bool Ok, string Message = "")
{
    public static readonly FeedAnswer Taken = new(true);

    public static FeedAnswer Refused(string message) => new(false, message);
}
