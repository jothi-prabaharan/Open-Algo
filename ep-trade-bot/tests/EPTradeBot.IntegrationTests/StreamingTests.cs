using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Infrastructure.Security;
using EPTradeBot.Infrastructure.Streaming;
using EPTradeBot.Infrastructure.Trading;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using NetMQ;
using NetMQ.Sockets;

namespace EPTradeBot.IntegrationTests;

/// <summary>Gate 3 WebSocket health: connect, authenticate, subscribe, receive a tick through the bus, unsubscribe.</summary>
public sealed class StreamingTests
{
    private sealed class WsClient(WebSocket socket) : IAsyncDisposable
    {
        public Task SendAsync(object message) =>
            socket.SendAsync(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(message)), WebSocketMessageType.Text, true, CancellationToken.None);

        public async Task<JsonObject?> ReceiveAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            var buffer = new byte[64 * 1024];
            using var text = new MemoryStream();
            try
            {
                WebSocketReceiveResult r;
                do
                {
                    r = await socket.ReceiveAsync(buffer, cts.Token);
                    text.Write(buffer, 0, r.Count);
                }
                while (!r.EndOfMessage);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            return JsonNode.Parse(text.ToArray()) as JsonObject;
        }

        /// <summary>Next message of a type, skipping others.</summary>
        public async Task<JsonObject?> ReceiveTypeAsync(string type, TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < until)
            {
                var m = await ReceiveAsync(until - DateTime.UtcNow);
                if (m is null) return null;
                if (m["type"]?.GetValue<string>() == type) return m;
            }

            return null;
        }

        public ValueTask DisposeAsync()
        {
            // A receive cancelled by a test timeout aborts the in-memory test socket, so there is nothing left to close gracefully.
            socket.Abort();
            socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static async Task<WsClient> ConnectAsync(TestHost host) =>
        new(await host.Server.CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None));

    /// <summary>Publishes until the bus has seen it, covering the PUB/SUB slow-joiner gap.</summary>
    private static async Task PublishUntilReceivedAsync(TestHost host, string topic, JsonObject data)
    {
        var bus = host.Get<MarketDataBus>();
        await bus.Bound.WaitAsync(TimeSpan.FromSeconds(10));
        var publisher = host.Get<MarketDataPublisher>();
        var before = bus.Received;
        for (var i = 0; i < 100 && bus.Received == before; i++)
        {
            publisher.Publish(topic, data);
            await Task.Delay(50);
        }

        Assert.True(bus.Received > before, "The bus never received the published tick.");
    }

    [PostgresFact]
    public async Task Client_authenticates_subscribes_receives_a_tick_through_the_bus_and_unsubscribes()
    {
        var log = Environment.GetEnvironmentVariable("EPTRADE_TEST_LOG");
        await using var host = new TestHost("Development", b =>
        {
            if (log is not null) b.ConfigureLogging(l => l.AddProvider(new FileLoggerProvider(log)).SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug));
        });
        var account = await host.CreateAccountAsync(AccountType.PAPER, "ws-paper", 100_000, 1000, 10_000_000);
        var key = (await host.Get<ApiKeyService>().IssueAsync(account, "ws")).Key;
        await using var ws = await ConnectAsync(host);

        await ws.SendAsync(new { action = "subscribe", symbols = new[] { new { symbol = "SBIN", exchange = "NSE" } }, mode = "LTP" });
        Assert.Equal("NOT_AUTHENTICATED", (await ws.ReceiveAsync(TimeSpan.FromSeconds(5)))!["code"]!.GetValue<string>());

        await ws.SendAsync(new { action = "authenticate", api_key = "wrong" });
        Assert.Equal("AUTHENTICATION_ERROR", (await ws.ReceiveAsync(TimeSpan.FromSeconds(5)))!["code"]!.GetValue<string>());

        await ws.SendAsync(new { action = "authenticate", api_key = key });
        var auth = (await ws.ReceiveAsync(TimeSpan.FromSeconds(5)))!;
        Assert.Equal("auth", auth["type"]!.GetValue<string>());
        Assert.Equal("success", auth["status"]!.GetValue<string>());
        Assert.Equal(account.ToString(), auth["user_id"]!.GetValue<string>());

        await ws.SendAsync(new { action = "subscribe", symbols = new[] { new { symbol = "SBIN", exchange = "NSE" } }, mode = "ltp", request_id = "r1" });
        var sub = (await ws.ReceiveAsync(TimeSpan.FromSeconds(5)))!;
        Assert.Equal("subscribe", sub["type"]!.GetValue<string>());
        Assert.Equal("r1", sub["request_id"]!.GetValue<string>());
        Assert.Equal("LTP", sub["subscriptions"]![0]!["mode"]!.GetValue<string>());

        // A Quote tick reaches an LTP subscriber (a higher mode carries the lower), tagged with the client's mode.
        await PublishUntilReceivedAsync(host, "NSE_SBIN_QUOTE", new JsonObject { ["ltp"] = 512.5, ["bid"] = 512.4, ["ask"] = 512.6 });
        var tick = (await ws.ReceiveTypeAsync("market_data", TimeSpan.FromSeconds(5)))!;
        Assert.Equal("SBIN", tick["symbol"]!.GetValue<string>());
        Assert.Equal("NSE", tick["exchange"]!.GetValue<string>());
        Assert.Equal(1, tick["mode"]!.GetValue<int>());
        Assert.Equal(512.5, tick["data"]!["ltp"]!.GetValue<double>());

        // The same tick priced the quote book that risk checks and sandbox fills read.
        var quote = host.Get<QuoteBook>().Get("NSE", "SBIN")!;
        Assert.Equal(512.5m, quote.Ltp);
        Assert.Equal(512.6m, quote.Ask);

        await ws.SendAsync(new { action = "unsubscribe", symbols = new[] { new { symbol = "SBIN", exchange = "NSE" } }, mode = "LTP" });
        var unsub = (await ws.ReceiveTypeAsync("unsubscribe", TimeSpan.FromSeconds(5)))!;
        Assert.Equal("success", unsub["status"]!.GetValue<string>());

        await PublishUntilReceivedAsync(host, "NSE_SBIN_LTP", new JsonObject { ["ltp"] = 513 });
        await ws.SendAsync(new { action = "ping", timestamp = 1 });
        var next = (await ws.ReceiveAsync(TimeSpan.FromSeconds(5)))!;
        Assert.Equal("pong", next["type"]!.GetValue<string>());
        Assert.Null(await ws.ReceiveAsync(TimeSpan.FromMilliseconds(300)));
    }

    [PostgresFact]
    public async Task A_tick_fills_a_resting_sandbox_order_and_the_order_update_reaches_websocket_and_signalr_subscribers()
    {
        await using var host = new TestHost("Development");
        var account = await host.CreateAccountAsync(AccountType.PAPER, "fill-paper", 100_000, 1000, 10_000_000);
        var key = (await host.Get<ApiKeyService>().IssueAsync(account, "ws")).Key;
        host.SetQuote("NSE", "INFY", 1510);

        await using var ws = await ConnectAsync(host);
        await ws.SendAsync(new { action = "authenticate", api_key = key });
        await ws.ReceiveTypeAsync("auth", TimeSpan.FromSeconds(5));
        await ws.SendAsync(new { action = "subscribe_orders" });
        await ws.ReceiveTypeAsync("subscribe_orders", TimeSpan.FromSeconds(5));

        var hub = new HubConnectionBuilder()
            .WithUrl($"http://localhost{EPTradeBot.Api.Streaming.OrdersHub.Path}?access_token={TestHost.AdminToken}", o => o.HttpMessageHandlerFactory = _ => host.Server.CreateHandler())
            .Build();
        var updates = new System.Collections.Concurrent.ConcurrentQueue<JsonObject>();
        hub.On<JsonObject>("order_update", u => updates.Enqueue(u));
        await hub.StartAsync();

        using var client = host.Anonymous();
        var placed = await client.PostAsJsonAsync("/api/v1/placeorder", new { apikey = key, strategy = "s", symbol = "INFY", exchange = "NSE", action = "BUY", quantity = 2, pricetype = "LIMIT", price = 1500, product = "MIS" });
        var orderId = (await placed.Content.ReadFromJsonAsync<JsonObject>())!["orderid"]!.GetValue<string>();

        var wsUpdate = (await ws.ReceiveTypeAsync("order_update", TimeSpan.FromSeconds(5)))!;
        Assert.Equal(orderId, wsUpdate["orderid"]!.GetValue<string>());
        Assert.Equal("accepted", wsUpdate["status"]!.GetValue<string>());
        for (var i = 0; i < 50 && updates.IsEmpty; i++) await Task.Delay(100);
        Assert.Contains(updates, u => u["order_id"]?.GetValue<string>() == orderId);

        await PublishUntilReceivedAsync(host, "NSE_INFY_LTP", new JsonObject { ["ltp"] = 1499.5 });
        string? status = null;
        for (var i = 0; i < 50 && status != "complete"; i++)
        {
            await Task.Delay(100);
            var book = await (await client.PostAsJsonAsync("/api/v1/orderstatus", new { apikey = key, strategy = "s", orderid = orderId })).Content.ReadFromJsonAsync<JsonObject>();
            status = book!["data"]?["order_status"]?.GetValue<string>();
        }

        Assert.Equal("complete", status);
        await hub.DisposeAsync();
    }

    [PostgresFact]
    public async Task Order_hub_refuses_a_connection_without_the_admin_token()
    {
        await using var host = new TestHost("Development");
        var hub = new HubConnectionBuilder()
            .WithUrl($"http://localhost{EPTradeBot.Api.Streaming.OrdersHub.Path}?access_token=wrong", o => o.HttpMessageHandlerFactory = _ => host.Server.CreateHandler())
            .Build();
        var closed = new TaskCompletionSource();
        hub.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        try
        {
            await hub.StartAsync();
        }
        catch (Exception)
        {
            closed.TrySetResult();
        }

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(HubConnectionState.Connected, hub.State);
        await hub.DisposeAsync();
    }

    [PostgresFact]
    public void The_bus_never_moves_to_another_port_when_its_port_is_taken()
    {
        var port = TestHost.FreePort();
        using var squatter = new SubscriberSocket();
        squatter.Bind($"tcp://127.0.0.1:{port}");

        var failure = Record.Exception(() => new TestHost("Development", b => b.UseSetting("Streaming:BusPort", port.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        Assert.NotNull(failure);
        Assert.Contains("could not bind", failure!.ToString());
    }
}
