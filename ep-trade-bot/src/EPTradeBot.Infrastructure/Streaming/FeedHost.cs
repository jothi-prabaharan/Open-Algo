using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Configuration;
using EPTradeBot.Core.Streaming;
using EPTradeBot.Core.Streaming.Feeds;
using EPTradeBot.Infrastructure.Brokers;
using EPTradeBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EPTradeBot.Infrastructure.Streaming;

/// <summary>
/// Broker market-data feeds, on demand. The WebSocket proxy tells it what each client wants; it keeps one broker connection per account, subscribes an
/// instrument while any client of that account holds it, and publishes every decoded tick onto the bus (which connects, never binds). A feed that
/// drops reconnects with a growing wait and repeats its subscriptions; a token the broker refuses is re-read from the account once, and a feed whose
/// token has not changed stops and says the account needs to sign in again.
/// </summary>
public sealed class FeedHost(
    IBrokerRegistry registry,
    ISymbolMaster symbols,
    IDbContextFactory<EpTradeDbContext> contexts,
    BrokerSessionFactory sessions,
    MarketDataPublisher publisher,
    IOptions<StreamingOptions> options,
    IOptions<BrokerOptions> brokerOptions,
    TimeProvider clock,
    ILogger<FeedHost> logger) : BackgroundService, IFeedSubscriptions
{
    // The nested connection reads these through the host.
    private readonly IDbContextFactory<EpTradeDbContext> contexts = contexts;
    private readonly BrokerSessionFactory sessions = sessions;
    private readonly MarketDataPublisher publisher = publisher;
    private readonly IOptions<StreamingOptions> options = options;
    private readonly IOptions<BrokerOptions> brokerOptions = brokerOptions;
    private readonly TimeProvider clock = clock;
    private readonly ILogger<FeedHost> logger = logger;

    private readonly object gate = new();
    private readonly Dictionary<Guid, Connection> connections = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        List<Connection> all;
        lock (gate)
        {
            all = [.. connections.Values];
            connections.Clear();
        }

        foreach (var c in all) await c.StopAsync();
        await base.StopAsync(cancellationToken);
    }

    /// <summary>How many accounts have a feed connection (running or waiting to reconnect).</summary>
    public int Connections
    {
        get
        {
            lock (gate) return connections.Count;
        }
    }

    public async Task<FeedAnswer> SubscribeAsync(Guid accountId, string broker, string exchange, string symbol, StreamMode mode, CancellationToken cancellationToken)
    {
        if (!options.Value.FeedsEnabled) return FeedAnswer.Taken;
        if (registry.Get<IBrokerFeed>(broker) is not { } plugin)
            return FeedAnswer.Refused($"Live prices from {broker} are not available yet. The subscription is kept, but no ticks will arrive.");
        if (await symbols.FindAsync(plugin.Name, symbol, exchange, cancellationToken) is not { } instrument)
            return FeedAnswer.Refused($"{symbol} on {exchange} is not in the {plugin.DisplayName} master contract. Check the symbol, or download the master contract again.");

        // A feed that could never start is refused here, where the client can be told, not later in a log.
        bool known;
        lock (gate) known = connections.ContainsKey(accountId);
        if (!known)
        {
            await using var db = await contexts.CreateDbContextAsync(cancellationToken);
            var account = await db.BrokerAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId, cancellationToken);
            var session = account is null ? null : sessions.Create(account);
            if (session is null) return FeedAnswer.Refused($"{plugin.DisplayName} is not signed in, so there are no live prices. Sign in to {plugin.DisplayName}, then subscribe again.");
            if (plugin.FeedEndpoint(session, null) is null)
                return FeedAnswer.Refused($"{plugin.DisplayName} did not give this account a live price feed. Sign in to {plugin.DisplayName} again, then subscribe again.");
        }

        // A hold taken on a connection that was just closed is taken again on a new one.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Connection connection;
            lock (gate)
            {
                if (!connections.TryGetValue(accountId, out connection!))
                {
                    connection = new Connection(this, accountId, plugin);
                    connections[accountId] = connection;
                }
            }

            var refusal = connection.Hold(new FeedRequest(exchange, symbol, instrument.Token, instrument.BrokerExchange, mode, instrument.BrokerSymbol, instrument.Name));
            if (refusal is not null) return FeedAnswer.Refused(refusal);
            bool current;
            lock (gate) current = connections.TryGetValue(accountId, out var now) && ReferenceEquals(now, connection);
            if (!current) continue;
            connection.Start();
            return FeedAnswer.Taken;
        }

        return FeedAnswer.Refused("The live price feed was busy closing. Subscribe again.");
    }

    public async Task UnsubscribeAsync(Guid accountId, string exchange, string symbol, StreamMode mode, CancellationToken cancellationToken)
    {
        Connection? connection;
        lock (gate) connections.TryGetValue(accountId, out connection);
        if (connection is null) return;
        await LetGoAsync(accountId, connection, exchange, symbol, mode);
    }

    /// <summary>Gives up one hold on the connection the caller looked up, and closes that connection if it is now empty.</summary>
    private async Task LetGoAsync(Guid accountId, Connection connection, string exchange, string symbol, StreamMode mode)
    {
        if (connection.Release(exchange, symbol, mode) != 0) return;
        // Removed only if it is still this connection: two clients letting go together both reach zero, and the second must not take out a newer
        // connection that a subscription opened in between (it would be left running with nothing able to stop it).
        bool removed;
        lock (gate) removed = ((ICollection<KeyValuePair<Guid, Connection>>)connections).Remove(new KeyValuePair<Guid, Connection>(accountId, connection));
        if (removed) await connection.StopAsync();
    }

    /// <summary>One account's connection to its broker.</summary>
    private sealed class Connection(FeedHost host, Guid accountId, IBrokerFeed plugin)
    {
        private readonly IFeedCodec codec = plugin.CreateFeedCodec();
        private readonly object sync = new();
        private readonly Dictionary<(string Exchange, string Symbol, StreamMode Mode), (FeedRequest Request, int Holders)> held = [];
        private readonly CancellationTokenSource stop = new();
        private System.Threading.Channels.Channel<string> outbox = System.Threading.Channels.Channel.CreateUnbounded<string>();
        private Task? run;
        private Task? senderTask;
        private bool connected;
        private BrokerSession? currentSession;
        private readonly SemaphoreSlim httpTurn = new(1, 1);
        private Task httpWork = Task.CompletedTask;

        private long Now => host.clock.GetUtcNow().ToUnixTimeMilliseconds();

        /// <summary>Takes one client's hold on an instrument. Null when taken; otherwise why not.</summary>
        public string? Hold(FeedRequest request)
        {
            IReadOnlyList<string> messages = [];
            lock (sync)
            {
                var key = (request.Exchange, request.Symbol, request.Mode);
                if (held.TryGetValue(key, out var existing))
                {
                    held[key] = (existing.Request, existing.Holders + 1);
                    return null;
                }

                if (held.Count >= host.options.Value.FeedMaxInstruments)
                    return $"{plugin.DisplayName} allows {host.options.Value.FeedMaxInstruments} instruments on one live feed. Unsubscribe some before adding more.";
                held[key] = (request, 1);
                messages = codec.Subscribe([request], Now);
                if (connected) foreach (var m in messages) outbox.Writer.TryWrite(m);
            }

            PumpHttp();
            return null;
        }

        /// <summary>Gives up one hold. The number of instruments still held on the connection.</summary>
        public int Release(string exchange, string symbol, StreamMode mode)
        {
            int count;
            lock (sync)
            {
                var key = (exchange, symbol, mode);
                if (!held.TryGetValue(key, out var existing)) return held.Count;
                if (existing.Holders > 1)
                {
                    held[key] = (existing.Request, existing.Holders - 1);
                    return held.Count;
                }

                held.Remove(key);
                var messages = codec.Unsubscribe(existing.Request.Target, mode, Now);
                if (connected) foreach (var m in messages) outbox.Writer.TryWrite(m);
                count = held.Count;
            }

            PumpHttp();
            return count;
        }

        /// <summary>Makes the HTTP calls the codec owes, one batch after another, and reads the quotes the broker answers with.</summary>
        private void PumpHttp()
        {
            if (codec is not IFeedHttpCodec http || plugin is not IBrokerFeedHttp sender) return;
            IReadOnlyList<FeedHttpCall> calls;
            BrokerSession? session;
            lock (sync)
            {
                calls = connected ? http.TakeHttp() : [];
                session = currentSession;
            }

            if (calls.Count == 0 || session is null) return;
            // The calls are not cut short by the connection stopping: the unsubscribe that empties a connection is the last thing it sends, and stopping waits for it.
            var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var token = limit.Token;
            var work = Task.Run(async () =>
            {
                try
                {
                    await httpTurn.WaitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    limit.Dispose();
                    return;
                }

                try
                {
                    foreach (var call in calls)
                    {
                        try
                        {
                            foreach (var quote in await sender.SendFeedCallAsync(session, call, token))
                            {
                                IReadOnlyList<FeedPublish> ticks;
                                lock (sync) ticks = codec.DecodeText(quote, Now);
                                foreach (var tick in ticks) host.publisher.Publish(tick.Topic, tick.Data);
                            }
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            host.logger.LogWarning(ex, "A {Broker} subscription request failed and was skipped.", plugin.Name);
                        }
                    }
                }
                finally
                {
                    httpTurn.Release();
                    limit.Dispose();
                }
            });
            lock (sync) httpWork = Task.WhenAll(httpWork, work);
        }

        public void Start()
        {
            lock (sync) run ??= Task.Run(() => RunAsync(stop.Token));
        }

        private Task? stopping;

        /// <summary>Stops the connection. Safe to call more than once and from several places at once: they all wait for the one stop.</summary>
        public Task StopAsync()
        {
            lock (sync) return stopping ??= StopCoreAsync();
        }

        private async Task StopCoreAsync()
        {
            // What was queued for the broker (the unsubscribe that emptied the connection) goes out first, for a moment, before the socket is closed.
            Task? sending;
            lock (sync)
            {
                outbox.Writer.TryComplete();
                sending = senderTask;
            }

            if (sending is not null) await Task.WhenAny(sending, Task.Delay(TimeSpan.FromSeconds(1)));
            Task pending;
            lock (sync) pending = httpWork;
            await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(5)));
            await stop.CancelAsync();
            Task? task;
            lock (sync) task = run;
            if (task is not null)
            {
                try
                {
                    await task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
                {
                    host.logger.LogWarning("The {Broker} feed of {AccountId} did not stop in time.", plugin.Name, accountId);
                }
            }

            stop.Dispose();
        }

        private async Task RunAsync(CancellationToken ct)
        {
            var o = host.options.Value;
            var attempts = 0;
            string? lastRefusedToken = null;
            while (!ct.IsCancellationRequested)
            {
                BrokerSession? session;
                await using (var db = await host.contexts.CreateDbContextAsync(ct))
                {
                    var account = await db.BrokerAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId, ct);
                    session = account is null ? null : host.sessions.Create(account);
                }

                var feedUrl = host.brokerOptions.Value.FeedUrls.GetValueOrDefault(plugin.Name);
                var endpoint = session is null ? null
                    : plugin is IBrokerFeedAuthorize authorize ? await authorize.AuthorizeAsync(session, feedUrl, ct)
                    : plugin.FeedEndpoint(session, feedUrl);
                if (session is null || endpoint is null)
                {
                    host.logger.LogWarning("The {Broker} feed of {AccountId} cannot start: the account is not signed in to {Broker}. Sign in, then subscribe again.", plugin.Name, accountId, plugin.Name);
                    return;
                }

                codec.Begin(session);
                lock (sync) currentSession = session;
                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                foreach (var (name, value) in endpoint.Headers) socket.Options.SetRequestHeader(name, value);
                try
                {
                    using var connecting = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    connecting.CancelAfter(TimeSpan.FromSeconds(o.FeedConnectSeconds));
                    await socket.ConnectAsync(endpoint.Url, connecting.Token);
                    attempts = 0;
                    lastRefusedToken = null;
                    await ServeAsync(socket, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or HttpRequestException or IOException)
                {
                    if (Refused(ex))
                    {
                        // The broker refused the token. A token that has changed since is worth one more try; one that has not will be refused again.
                        if (lastRefusedToken == session.AuthToken)
                        {
                            host.logger.LogError("{Broker} refused the feed token of {AccountId} again. The account needs to sign in to {Broker} again.", plugin.Name, accountId, plugin.Name);
                            return;
                        }

                        lastRefusedToken = session.AuthToken;
                    }
                    else
                    {
                        host.logger.LogWarning(ex, "The {Broker} feed of {AccountId} dropped.", plugin.Name, accountId);
                    }
                }
                finally
                {
                    lock (sync) connected = false;
                }

                attempts++;
                if (attempts > 50)
                {
                    host.logger.LogError("The {Broker} feed of {AccountId} gave up after {Attempts} attempts.", plugin.Name, accountId, attempts);
                    return;
                }

                var wait = Math.Min(o.FeedReconnectBaseMs * Math.Pow(1.5, attempts - 1), 60_000);
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(wait), ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>One connection's life: repeat the subscriptions, then read frames, send queued messages and keep-alives until it drops or stalls.</summary>
        private async Task ServeAsync(ClientWebSocket socket, CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var o = host.options.Value;
            IReadOnlyList<string> resubscribe;
            lock (sync)
            {
                outbox = System.Threading.Channels.Channel.CreateUnbounded<string>();
                resubscribe = codec.Resubscribe(Now);
                connected = true;
            }

            foreach (var m in resubscribe) await SendAsync(socket, m, codec, linked.Token);
            var queue = outbox;
            var sender = Task.Run(async () =>
            {
                try
                {
                    await foreach (var m in queue.Reader.ReadAllAsync(linked.Token)) await SendAsync(socket, m, codec, linked.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
            }, linked.Token);
            lock (sync) senderTask = sender;
            var keepAlive = plugin.FeedKeepAliveText is { } text && plugin.FeedKeepAliveEvery > TimeSpan.Zero
                ? Task.Run(async () =>
                {
                    try
                    {
                        using var timer = new PeriodicTimer(plugin.FeedKeepAliveEvery);
                        while (await timer.WaitForNextTickAsync(linked.Token)) await SendAsync(socket, text, codec, linked.Token);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
                }, linked.Token)
                : Task.CompletedTask;

            try
            {
                var buffer = new byte[64 * 1024];
                while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    using var heard = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    heard.CancelAfter(TimeSpan.FromSeconds(o.FeedStallSeconds));
                    using var frame = new MemoryStream();
                    ValueWebSocketReceiveResult received;
                    try
                    {
                        do
                        {
                            received = await socket.ReceiveAsync(buffer.AsMemory(), heard.Token);
                            if (received.MessageType == WebSocketMessageType.Close) return;
                            frame.Write(buffer, 0, received.Count);
                        }
                        while (!received.EndOfMessage);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        host.logger.LogWarning("The {Broker} feed of {AccountId} was silent for {Seconds}s and is being reconnected.", plugin.Name, accountId, o.FeedStallSeconds);
                        return;
                    }

                    Handle(received.MessageType, frame.ToArray());
                }
            }
            finally
            {
                lock (sync) connected = false;
                await linked.CancelAsync();
                try
                {
                    await Task.WhenAll(sender, keepAlive);
                }
                catch (OperationCanceledException) { }

                // A connection ended on purpose is closed properly, so the broker is not left to find out by timeout.
                if (socket.State == WebSocketState.Open)
                {
                    using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    try
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closing", closing.Token);
                    }
                    catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
                }
            }
        }

        private void Handle(WebSocketMessageType type, byte[] frame)
        {
            try
            {
                var ticks = type == WebSocketMessageType.Binary ? codec.DecodeBinary(frame, Now) : codec.DecodeText(Encoding.UTF8.GetString(frame), Now);
                foreach (var tick in ticks) host.publisher.Publish(tick.Topic, tick.Data);
                // What reading the frame obliges the codec to send (subscribing once a sign-in is accepted) goes out behind it.
                lock (sync)
                {
                    if (connected) foreach (var m in codec.TakeOutgoing()) outbox.Writer.TryWrite(m);
                }

                PumpHttp();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                host.logger.LogWarning(ex, "A {Broker} frame could not be read and was skipped.", plugin.Name);
            }
        }

        /// <summary>The broker answered the upgrade with 401 or 403: the token is no good.</summary>
        private static bool Refused(Exception ex) =>
            ex.Message.Contains("'401'", StringComparison.Ordinal) || ex.Message.Contains("'403'", StringComparison.Ordinal) || ex.Message.Contains("status code 401", StringComparison.Ordinal) || ex.Message.Contains("status code 403", StringComparison.Ordinal);

        private static Task SendAsync(ClientWebSocket socket, string text, IFeedCodec codec, CancellationToken ct) =>
            socket.State == WebSocketState.Open
                ? socket.SendAsync((codec.MessagesAreHex ? Convert.FromHexString(text) : Encoding.UTF8.GetBytes(text)).AsMemory(), codec.SendsBinary || codec.MessagesAreHex ? WebSocketMessageType.Binary : WebSocketMessageType.Text, true, ct).AsTask()
                : Task.CompletedTask;
    }
}
