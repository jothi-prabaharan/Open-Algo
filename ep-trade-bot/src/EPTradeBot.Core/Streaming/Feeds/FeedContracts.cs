using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>An instrument, with the broker's own token and exchange for it (the master contract's).</summary>
public sealed record FeedTarget(string Exchange, string Symbol, string Token, string BrokerExchange, string? BrokerSymbol = null);

/// <summary>An instrument a client wants ticks for, at one depth.</summary>
public sealed record FeedRequest(string Exchange, string Symbol, string Token, string BrokerExchange, StreamMode Mode, string? BrokerSymbol = null)
{
    public FeedTarget Target => new(Exchange, Symbol, Token, BrokerExchange, BrokerSymbol);
}

/// <summary>One tick ready for the bus: the topic (EXCHANGE_SYMBOL_MODE) and the OpenAlgo-shaped data.</summary>
public sealed record FeedPublish(string Topic, JsonObject Data);

/// <summary>
/// A broker's market-data protocol with no sockets in it: what to send to subscribe, and how to read what comes back. The connection
/// (reconnecting, pacing, FD hygiene) is the feed host's. A codec keeps the subscriptions it was asked for, so it can repeat them after a reconnect.
/// </summary>
public interface IFeedCodec
{
    string Broker { get; }

    /// <summary>The messages that start these subscriptions. Text frames, in the order they are sent.</summary>
    IReadOnlyList<string> Subscribe(IReadOnlyList<FeedRequest> requests, long nowMs);

    /// <summary>The messages that end the subscription to this instrument. A null mode ends every mode.</summary>
    IReadOnlyList<string> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs);

    /// <summary>The messages that restore every subscription after a reconnect.</summary>
    IReadOnlyList<string> Resubscribe(long nowMs);

    /// <summary>What a binary frame carries, ready for the bus. A frame that carries no data (a heartbeat) gives an empty list.</summary>
    IReadOnlyList<FeedPublish> DecodeBinary(ReadOnlySpan<byte> frame, long nowMs);

    /// <summary>What a text frame carries, ready for the bus.</summary>
    IReadOnlyList<FeedPublish> DecodeText(string frame, long nowMs);

    /// <summary>Forgets every subscription (a new sign-in, or the host stopping).</summary>
    void Reset();

    /// <summary>True when the messages from <see cref="Subscribe"/> and the others are to go as binary frames rather than text (Upstox sends JSON that way).</summary>
    bool SendsBinary => false;
}
