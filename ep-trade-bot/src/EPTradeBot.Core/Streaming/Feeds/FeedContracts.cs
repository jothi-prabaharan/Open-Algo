using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Streaming.Feeds;

/// <summary>An instrument, with the broker's own token and exchange for it (the master contract's).</summary>
public sealed record FeedTarget(string Exchange, string Symbol, string Token, string BrokerExchange, string? BrokerSymbol = null, string? Name = null);

/// <summary>An instrument a client wants ticks for, at one depth.</summary>
public sealed record FeedRequest(string Exchange, string Symbol, string Token, string BrokerExchange, StreamMode Mode, string? BrokerSymbol = null, string? Name = null)
{
    public FeedTarget Target => new(Exchange, Symbol, Token, BrokerExchange, BrokerSymbol, Name);
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

    /// <summary>True when each message is hex text for the bytes to send (a protocol whose requests are raw bytes, not text).</summary>
    bool MessagesAreHex => false;

    /// <summary>Called with the session of each connection attempt, before anything is sent: a protocol that signs in with something in the session (a key inside the token) reads it here.</summary>
    void Begin(EPTradeBot.Core.Brokers.BrokerSession session) { }

    /// <summary>Messages the codec owes the broker because of what it just read (subscribing once a sign-in is accepted). Taken after every frame.</summary>
    IReadOnlyList<string> TakeOutgoing() => [];
}

/// <summary>A request a broker wants made over HTTP beside its socket (XTS subscribes and unsubscribes that way): the verb, the path under the broker's market-data address and the JSON body.</summary>
public sealed record FeedHttpCall(string Method, string Path, JsonObject Body);

/// <summary>A codec whose subscriptions are HTTP calls rather than socket messages. The connection sends what it hands over and gives the quotes in the replies back to <see cref="IFeedCodec.DecodeText"/>.</summary>
public interface IFeedHttpCodec : IFeedCodec
{
    /// <summary>The calls owed since the last time, in order.</summary>
    IReadOnlyList<FeedHttpCall> TakeHttp();
}

/// <summary>One MQTT command: subscribe or unsubscribe a list of topics.</summary>
public sealed record MqttCommand(bool Subscribe, IReadOnlyList<string> Topics);

/// <summary>
/// A broker's market-data protocol over MQTT: the topics to subscribe and unsubscribe, and how to read a message. Like <see cref="IFeedCodec"/> it holds no socket; the connection (reconnecting, pacing)
/// is the feed host's.
/// </summary>
public interface IMqttFeedCodec
{
    string Broker { get; }

    IReadOnlyList<MqttCommand> Subscribe(IReadOnlyList<FeedRequest> requests, long nowMs);

    IReadOnlyList<MqttCommand> Unsubscribe(FeedTarget target, StreamMode? mode, long nowMs);

    /// <summary>The commands that restore every subscription after a reconnect.</summary>
    IReadOnlyList<MqttCommand> Resubscribe(long nowMs);

    IReadOnlyList<FeedPublish> DecodeMessage(string topic, ReadOnlySpan<byte> payload, long nowMs);

    void Reset();
}

/// <summary>Where an MQTT feed connects and as whom.</summary>
public sealed record MqttEndpoint(string Host, int Port, bool Tls, string ClientId, string UserName, string Password);

/// <summary>A broker plugin whose market data comes over MQTT rather than a WebSocket (IIFL Capital).</summary>
public interface IBrokerMqttFeed : EPTradeBot.Core.Brokers.IBrokerPlugin
{
    IMqttFeedCodec CreateMqttCodec();

    /// <summary>The broker's MQTT endpoint for this session, or null when the session cannot connect.</summary>
    MqttEndpoint? MqttEndpoint(EPTradeBot.Core.Brokers.BrokerSession session);
}
