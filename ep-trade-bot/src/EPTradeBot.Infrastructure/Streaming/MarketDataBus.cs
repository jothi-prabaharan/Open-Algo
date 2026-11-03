using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Configuration;
using EPTradeBot.Core.Streaming;
using EPTradeBot.Core.Trading;
using EPTradeBot.Infrastructure.Trading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetMQ;
using NetMQ.Sockets;

namespace EPTradeBot.Infrastructure.Streaming;

/// <summary>One tick from the bus.</summary>
public sealed record MarketTick(MarketTopic Topic, JsonObject Data);

/// <summary>Something that wants every tick (the WebSocket proxy, the sandbox matcher). It must not block.</summary>
public interface IMarketTickSink
{
    void OnTick(MarketTick tick);
}

/// <summary>
/// The market-data bus. Its SUB socket is the single binder of the fixed bus port;
/// every publisher (broker feed adapters, tests) connects to it. There is no port
/// scan and no fallback: if the port is taken, the service stops the host with a
/// clear message rather than sliding to another port where no publisher would
/// find it. Each tick updates the quote book (prices for risk checks and sandbox
/// fills) and is handed to every sink.
/// </summary>
public sealed class MarketDataBus(
    IOptions<StreamingOptions> options,
    QuoteBook quotes,
    IEnumerable<IMarketTickSink> sinks,
    ILogger<MarketDataBus> logger) : BackgroundService
{
    private readonly TaskCompletionSource bound = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long received;

    /// <summary>Completes once the SUB socket is bound, so a publisher connecting after it loses nothing to the slow-joiner gap.</summary>
    public Task Bound => bound.Task;

    public long Received => Interlocked.Read(ref received);

    public string Address => $"tcp://{options.Value.BusHost}:{options.Value.BusPort}";

    private SubscriberSocket? socket;

    /// <summary>Binds before the host finishes starting, so a taken port stops startup instead of failing later in the background.</summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        var s = new SubscriberSocket();
        try
        {
            s.Bind(Address);
        }
        catch (NetMQ.NetMQException ex)
        {
            s.Dispose();
            var error = new InvalidOperationException(
                $"The market-data bus could not bind {Address}: the port is in use. Another EP Trade bot (or OpenAlgo) instance is probably running. " +
                "Give each instance its own Streaming:BusPort. The bus never moves to another port by itself.", ex);
            bound.TrySetException(error);
            throw error;
        }

        s.SubscribeToAnyTopic();
        socket = s;
        bound.TrySetResult();
        logger.LogInformation("Market-data bus listening on {Address}.", Address);
        return base.StartAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => Run(stoppingToken), stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public override void Dispose()
    {
        socket?.Dispose();
        base.Dispose();
    }

    private void Run(CancellationToken stoppingToken)
    {
        var socket = this.socket!;
        var message = new NetMQMessage();
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!socket.TryReceiveMultipartMessage(TimeSpan.FromMilliseconds(200), ref message)) continue;
            if (message.FrameCount < 2) continue;
            try
            {
                Handle(message[0].ConvertToString(Encoding.UTF8), message[1].ConvertToString(Encoding.UTF8));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                logger.LogWarning(ex, "Ignored a malformed bus message.");
            }
        }
    }

    private void Handle(string topic, string payload)
    {
        // Private account topics share the bus but are not market data.
        if (topic.StartsWith("CACHE_INVALIDATE", StringComparison.Ordinal) || topic.EndsWith("_orders", StringComparison.Ordinal)
            || topic.EndsWith("_positions", StringComparison.Ordinal) || topic.EndsWith("_margins", StringComparison.Ordinal)) return;
        if (MarketTopics.Parse(topic) is not { } parsed) return;
        if (JsonNode.Parse(payload) is not JsonObject data) return;
        Interlocked.Increment(ref received);

        var ltp = Price(data["ltp"]);
        var bid = Price(data["bid"]) ?? Price(data["depth"]?["buy"]?[0]?["price"]);
        var ask = Price(data["ask"]) ?? Price(data["depth"]?["sell"]?[0]?["price"]);
        if (ltp is not null || bid is not null || ask is not null)
        {
            var previous = quotes.Get(parsed.Exchange, parsed.Symbol);
            quotes.Set(parsed.Exchange, parsed.Symbol, new Quote(ltp ?? previous?.Ltp, bid ?? previous?.Bid, ask ?? previous?.Ask));
        }

        var tick = new MarketTick(parsed, data);
        foreach (var sink in sinks)
        {
            try
            {
                sink.OnTick(tick);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogError(ex, "A tick consumer failed; the bus keeps running.");
            }
        }
    }

    private static decimal? Price(JsonNode? node) => BookFormatting.Number(node) is { } d && d > 0 ? (decimal)d : null;
}

/// <summary>
/// Publishes onto the bus. Connects (never binds) to the bus port, so any number of
/// publishers in any number of processes share the one fixed port.
/// </summary>
public sealed class MarketDataPublisher(IOptions<StreamingOptions> options) : IDisposable
{
    private readonly Lock gate = new();
    private PublisherSocket? socket;

    public void Publish(string topic, JsonObject data)
    {
        lock (gate)
        {
            if (socket is null)
            {
                socket = new PublisherSocket();
                socket.Options.SendHighWatermark = 10_000;
                socket.Connect($"tcp://{options.Value.BusHost}:{options.Value.BusPort}");
            }

            socket.SendMoreFrame(topic).SendFrame(data.ToJsonString());
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            socket?.Dispose();
            socket = null;
        }
    }
}

/// <summary>Fills open sandbox orders as prices arrive. Ticks for one instrument are coalesced, so a burst costs one match.</summary>
public sealed class SandboxTickMatcher(Sandbox.SandboxEngine sandbox, ILogger<SandboxTickMatcher> logger) : BackgroundService, IMarketTickSink
{
    private readonly Channel<(string Exchange, string Symbol)> queue = Channel.CreateUnbounded<(string, string)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string, string), byte> pending = new();

    public void OnTick(MarketTick tick)
    {
        var key = (tick.Topic.Exchange, tick.Topic.Symbol);
        if (pending.TryAdd(key, 0)) queue.Writer.TryWrite(key);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var key in queue.Reader.ReadAllAsync(stoppingToken))
        {
            pending.TryRemove(key, out _);
            try
            {
                await sandbox.MatchAsync(key.Exchange, key.Symbol, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Sandbox matching for {Exchange}:{Symbol} failed.", key.Exchange, key.Symbol);
            }
        }
    }
}
