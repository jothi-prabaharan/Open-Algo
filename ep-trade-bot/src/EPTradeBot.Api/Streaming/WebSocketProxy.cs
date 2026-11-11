using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Streaming;
using EPTradeBot.Infrastructure.Security;
using EPTradeBot.Infrastructure.Streaming;

namespace EPTradeBot.Api.Streaming;

/// <summary>
/// OpenAlgo's WebSocket proxy protocol (websocket_proxy/server.py), on port 8765 and
/// at /ws: authenticate with an /api/v1 key, subscribe and unsubscribe by symbol and
/// mode (LTP, Quote, Depth), receive market_data messages for every tick on the bus,
/// and optionally subscribe_orders for the account's order updates. Like OpenAlgo
/// today, every tick is forwarded (no server-side throttling); a slow client's
/// outbound queue is bounded and drops its oldest messages, so it never holds up the bus.
/// </summary>
public sealed class WebSocketProxy(ApiKeyService keys, IFeedSubscriptions feeds, ILogger<WebSocketProxy> logger) : IMarketTickSink, IOrderUpdateSink
{
    private readonly ConcurrentDictionary<Guid, Client> clients = new();

    public int ClientCount => clients.Count;

    public async Task RunAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var client = new Client(Guid.NewGuid(), socket);
        clients[client.Id] = client;
        var writer = Task.Run(() => client.PumpAsync(cancellationToken), cancellationToken);
        try
        {
            var buffer = new byte[16 * 1024];
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var text = new MemoryStream();
                WebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (received.MessageType == WebSocketMessageType.Close) return;
                    text.Write(buffer, 0, received.Count);
                    if (text.Length > 1024 * 1024) return;
                }
                while (!received.EndOfMessage);

                JsonObject? message;
                try
                {
                    message = JsonNode.Parse(text.ToArray()) as JsonObject;
                }
                catch (JsonException)
                {
                    message = null;
                }

                if (message is null)
                {
                    client.Send(Error("INVALID_JSON", "Invalid JSON message"));
                    continue;
                }

                await HandleAsync(client, message, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            logger.LogDebug(ex, "WebSocket client {Client} disconnected.", client.Id);
        }
        finally
        {
            clients.TryRemove(client.Id, out _);
            // The client's holds on broker feeds go with it.
            foreach (var key in client.Subscriptions.Keys.ToList())
            {
                if (client.Account is { AccountType: AccountType.LIVE } held) await feeds.UnsubscribeAsync(held.Id, key.Exchange, key.Symbol, key.Mode, CancellationToken.None);
            }

            client.Complete();
            await writer.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task HandleAsync(Client client, JsonObject message, CancellationToken cancellationToken)
    {
        var action = Text(message["action"]) ?? Text(message["type"]);
        var requestId = message["request_id"]?.DeepClone();
        switch (action)
        {
            case "authenticate" or "auth":
                var key = Text(message["api_key"]) ?? Text(message["apikey"]);
                if (string.IsNullOrEmpty(key))
                {
                    client.Send(Error("AUTHENTICATION_ERROR", "API key is required"));
                    return;
                }

                var account = await keys.ResolveAsync(key, cancellationToken);
                if (account is null)
                {
                    client.Send(Error("AUTHENTICATION_ERROR", "Invalid API key"));
                    return;
                }

                client.Account = account;
                client.Send(new JsonObject
                {
                    ["type"] = "auth", ["status"] = "success", ["message"] = "Authentication successful",
                    ["broker"] = account.Broker, ["user_id"] = account.Id.ToString(),
                    ["supported_features"] = new JsonObject { ["ltp"] = true, ["quote"] = true, ["depth"] = true },
                });
                return;
            case "ping":
                var pong = new JsonObject { ["type"] = "pong", ["status"] = "success", ["server_timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                if (message["timestamp"] is { } ts) pong["client_timestamp"] = ts.DeepClone();
                if (message["_pingId"] is { } pid) pong["_pingId"] = pid.DeepClone();
                client.Send(pong);
                return;
        }

        if (client.Account is not { } acc)
        {
            client.Send(Error("NOT_AUTHENTICATED", "You must authenticate first", requestId));
            return;
        }

        switch (action)
        {
            case "subscribe":
                await SubscribeAsync(client, acc, message, requestId, cancellationToken);
                return;
            case "unsubscribe" or "unsubscribe_all":
                await UnsubscribeAsync(client, acc, message, action == "unsubscribe_all", requestId);
                return;
            case "subscribe_orders":
                client.Orders = true;
                client.Send(new JsonObject { ["type"] = "subscribe_orders", ["status"] = "success", ["message"] = "Subscribed to order updates" });
                return;
            case "unsubscribe_orders":
                client.Orders = false;
                client.Send(new JsonObject { ["type"] = "unsubscribe_orders", ["status"] = "success", ["message"] = "Unsubscribed from order updates" });
                return;
            case "get_broker_info":
                client.Send(new JsonObject { ["type"] = "broker_info", ["status"] = "success", ["broker"] = acc.Broker, ["adapter_status"] = "connected", ["user_id"] = acc.Id.ToString() });
                return;
            default:
                client.Send(Error("INVALID_ACTION", $"Invalid action: {action}"));
                return;
        }
    }

    private async Task SubscribeAsync(Client client, BrokerAccount account, JsonObject message, JsonNode? requestId, CancellationToken cancellationToken)
    {
        if (ModeOf(message["mode"] ?? "Quote") is not { } mode)
        {
            client.Send(Error("INVALID_MODE", "Invalid mode; expected 'LTP', 'Quote', or 'Depth' (case-insensitive), or 1, 2, 3", requestId));
            return;
        }

        var symbols = Symbols(message);
        if (symbols.Count == 0)
        {
            client.Send(Error("INVALID_PARAMETERS", "At least one symbol must be specified", requestId));
            return;
        }

        var depth = BookDepth(message);
        var results = new JsonArray();
        foreach (var (symbol, exchange) in symbols)
        {
            // A live account's subscription is also asked of its broker; a sandbox account listens to the prices live accounts bring in.
            var answer = FeedAnswer.Taken;
            if (account.AccountType == AccountType.LIVE && !client.Subscriptions.ContainsKey((exchange, symbol, mode)))
                answer = await feeds.SubscribeAsync(account.Id, account.Broker, exchange, symbol, mode, cancellationToken);
            if (answer.Ok) client.Subscriptions[(exchange, symbol, mode)] = 0;
            results.Add(answer.Ok
                ? new JsonObject { ["symbol"] = symbol, ["exchange"] = exchange, ["status"] = "success", ["mode"] = MarketTopics.Label(mode), ["depth"] = depth, ["broker"] = account.Broker }
                : new JsonObject { ["symbol"] = symbol, ["exchange"] = exchange, ["status"] = "error", ["message"] = answer.Message, ["mode"] = MarketTopics.Label(mode), ["broker"] = account.Broker });
        }

        var response = new JsonObject
        {
            ["type"] = "subscribe", ["status"] = "success", ["subscriptions"] = results,
            ["message"] = "Subscription processing complete", ["broker"] = account.Broker,
        };
        if (requestId is not null) response["request_id"] = requestId.DeepClone();
        client.Send(response);
    }

    private async Task UnsubscribeAsync(Client client, BrokerAccount account, JsonObject message, bool all, JsonNode? requestId)
    {
        var done = new JsonArray();
        var failed = new JsonArray();
        if (all)
        {
            foreach (var key in client.Subscriptions.Keys.ToList())
            {
                if (client.Subscriptions.TryRemove(key, out _) && account.AccountType == AccountType.LIVE) await feeds.UnsubscribeAsync(account.Id, key.Exchange, key.Symbol, key.Mode, CancellationToken.None);
                done.Add(new JsonObject { ["symbol"] = key.Symbol, ["exchange"] = key.Exchange, ["mode"] = MarketTopics.Label(key.Mode), ["status"] = "success", ["broker"] = account.Broker });
            }
        }
        else
        {
            var mode = ModeOf(message["mode"] ?? 2);
            foreach (var (symbol, exchange) in Symbols(message))
            {
                var keys = client.Subscriptions.Keys.Where(k => k.Symbol == symbol && k.Exchange == exchange && (mode is null || k.Mode == mode)).ToList();
                if (keys.Count == 0)
                {
                    failed.Add(new JsonObject { ["symbol"] = symbol, ["exchange"] = exchange, ["status"] = "error", ["message"] = "Not subscribed", ["broker"] = account.Broker });
                    continue;
                }

                foreach (var key in keys)
                {
                    if (client.Subscriptions.TryRemove(key, out _) && account.AccountType == AccountType.LIVE) await feeds.UnsubscribeAsync(account.Id, key.Exchange, key.Symbol, key.Mode, CancellationToken.None);
                    done.Add(new JsonObject { ["symbol"] = symbol, ["exchange"] = exchange, ["mode"] = MarketTopics.Label(key.Mode), ["status"] = "success", ["broker"] = account.Broker });
                }
            }
        }

        var response = new JsonObject
        {
            ["type"] = "unsubscribe",
            ["status"] = failed.Count == 0 ? "success" : done.Count > 0 ? "partial" : "error",
            ["message"] = "Unsubscription processing complete", ["successful"] = done, ["failed"] = failed, ["broker"] = account.Broker,
        };
        if (requestId is not null) response["request_id"] = requestId.DeepClone();
        client.Send(response);
    }

    /// <summary>Delivers a tick to every client subscribed to it at its mode or a lower one (Depth carries Quote carries LTP).</summary>
    public void OnTick(MarketTick tick)
    {
        foreach (var client in clients.Values)
        {
            if (client.Account is null) continue;
            for (var m = (int)tick.Topic.Mode; m >= 1; m--)
            {
                if (!client.Subscriptions.ContainsKey((tick.Topic.Exchange, tick.Topic.Symbol, (StreamMode)m))) continue;
                client.Send(new JsonObject
                {
                    ["type"] = "market_data", ["symbol"] = tick.Topic.Symbol, ["exchange"] = tick.Topic.Exchange,
                    ["mode"] = m, ["data"] = tick.Data.DeepClone(),
                });
                break;
            }
        }
    }

    public Task OnOrderAsync(OrderUpdate update)
    {
        foreach (var client in clients.Values)
        {
            if (!client.Orders || client.Account?.Id != update.AccountId) continue;
            client.Send(new JsonObject
            {
                ["type"] = "order_update", ["user_id"] = update.AccountId.ToString(), ["mode"] = update.Mode, ["status"] = update.Status,
                ["orderid"] = update.OrderId, ["symbol"] = update.Symbol, ["exchange"] = update.Exchange, ["action"] = update.Action,
                ["quantity"] = update.Quantity, ["pricetype"] = update.PriceType, ["product"] = update.Product, ["message"] = update.Message,
                ["timestamp"] = update.TimestampIst.ToString("yyyy-MM-dd HH:mm:ss"),
            });
        }

        return Task.CompletedTask;
    }

    private static List<(string Symbol, string Exchange)> Symbols(JsonObject message)
    {
        var list = new List<(string, string)>();
        if (message["symbols"] is JsonArray array)
        {
            foreach (var item in array.OfType<JsonObject>())
                if (Text(item["symbol"]) is { Length: > 0 } s && Text(item["exchange"]) is { Length: > 0 } e) list.Add((s, e));
        }

        if (list.Count == 0 && Text(message["symbol"]) is { Length: > 0 } one && Text(message["exchange"]) is { Length: > 0 } ex) list.Add((one, ex));
        return list;
    }

    private static int BookDepth(JsonObject message) =>
        (message["depth"] ?? message["depth_level"]) is JsonValue v && v.TryGetValue<int>(out var d) ? d : 5;

    private static StreamMode? ModeOf(JsonNode? node) => node is not JsonValue v ? null
        : v.TryGetValue<JsonElement>(out var e)
            ? e.ValueKind switch
            {
                JsonValueKind.Number when e.TryGetInt32(out var i) => MarketTopics.ParseMode(i),
                JsonValueKind.String => MarketTopics.ParseMode(e.GetString()),
                _ => null,
            }
            : v.TryGetValue<int>(out var n) ? MarketTopics.ParseMode(n) : v.TryGetValue<string>(out var s) ? MarketTopics.ParseMode(s) : null;

    private static JsonObject Error(string code, string message, JsonNode? requestId = null)
    {
        var error = new JsonObject { ["status"] = "error", ["code"] = code, ["message"] = message };
        if (requestId is not null) error["request_id"] = requestId.DeepClone();
        return error;
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<JsonElement>(out var e) ? e.ValueKind == JsonValueKind.String ? e.GetString() : null
        : node is JsonValue s && s.TryGetValue<string>(out var str) ? str : null;

    private sealed class Client(Guid id, WebSocket socket)
    {
        private readonly System.Threading.Channels.Channel<string> outbox = System.Threading.Channels.Channel.CreateBounded<string>(
            new System.Threading.Channels.BoundedChannelOptions(5_000) { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest, SingleReader = true });

        public Guid Id { get; } = id;
        public BrokerAccount? Account { get; set; }
        public volatile bool Orders;
        public ConcurrentDictionary<(string Exchange, string Symbol, StreamMode Mode), byte> Subscriptions { get; } = new();

        public void Send(JsonObject message) => outbox.Writer.TryWrite(message.ToJsonString());

        public void Complete() => outbox.Writer.TryComplete();

        public async Task PumpAsync(CancellationToken cancellationToken)
        {
            await foreach (var text in outbox.Reader.ReadAllAsync(cancellationToken))
            {
                if (socket.State != WebSocketState.Open) return;
                await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancellationToken);
            }
        }
    }
}
