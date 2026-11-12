using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.Legacy.Core;
using EPTradeBot.Core.Streaming;
using EPTradeBot.Infrastructure.Streaming;
using Microsoft.AspNetCore.Hosting;

namespace EPTradeBot.IntegrationTests;

/// <summary>
/// Broker market-data feeds end to end: a client of the WebSocket proxy subscribes, the feed host connects to the broker's stream (a fake one on a real
/// loopback port), the broker's frame is decoded and comes back to the client as a market_data message.
/// </summary>
public sealed class FeedHostTests
{
    private static byte[] KiteLtp(uint token, double price)
    {
        var packet = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(packet, token);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4), (int)Math.Round(price * 100));
        var frame = new byte[2 + 2 + packet.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, 1);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)packet.Length);
        packet.CopyTo(frame, 4);
        return frame;
    }

    private sealed class Proxy(WebSocket socket) : IAsyncDisposable
    {
        public Task SendAsync(object message) =>
            socket.SendAsync(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(message)), WebSocketMessageType.Text, true, CancellationToken.None);

        public async Task<JsonObject?> ReceiveTypeAsync(string type, TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;
            var buffer = new byte[64 * 1024];
            while (DateTime.UtcNow < until)
            {
                using var cts = new CancellationTokenSource(until - DateTime.UtcNow);
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

                if (JsonNode.Parse(text.ToArray()) is JsonObject m && m["type"]?.GetValue<string>() == type) return m;
            }

            return null;
        }

        public ValueTask DisposeAsync()
        {
            socket.Abort();
            socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record Setup(TestHost Host, FakeFeedServer Broker, Guid Account, string Key);

    private static async Task<Setup> ZerodhaAsync(FakeFeedServer broker, Action<IWebHostBuilder>? more = null)
    {
        var host = new TestHost("Production", b =>
        {
            b.UseSetting("Brokers:Enabled:0", "zerodha");
            b.UseSetting("Brokers:Keys:zerodha:ApiKey", "kite-key");
            b.UseSetting("Brokers:FeedUrls:zerodha", broker.Address);
            b.UseSetting("Streaming:FeedReconnectBaseMs", "20");
            more?.Invoke(b);
        });
        var account = await host.CreateAccountAsync(AccountType.LIVE, "kite", broker: "zerodha");
        (await host.Admin.PutAsJsonAsync($"/api/v1/accounts/{account}/broker-session", new { auth_token = "kite-key:access-1" })).EnsureSuccessStatusCode();
        var key = (await host.Admin.PostAsJsonAsync($"/api/v1/accounts/{account}/apikeys", new { label = "feed" })).Content.ReadFromJsonAsync<JsonObject>().Result!["apikey"]!.GetValue<string>();
        await using (var db = host.NewDb())
        {
            db.Set<SymToken>().Add(new SymToken { Broker = "zerodha", Symbol = "SBIN", Brsymbol = "SBIN", Exchange = "NSE", Brexchange = "NSE", Token = "779521::::3045", Lotsize = 1 });
            db.Set<SymToken>().Add(new SymToken { Broker = "zerodha", Symbol = "INFY", Brsymbol = "INFY", Exchange = "NSE", Brexchange = "NSE", Token = "408065::::1594", Lotsize = 1 });
            await db.SaveChangesAsync();
        }

        return new Setup(host, broker, account, key);
    }

    private static async Task<Proxy> ConnectAsync(Setup s)
    {
        var proxy = new Proxy(await s.Host.Server.CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None));
        await proxy.SendAsync(new { action = "authenticate", api_key = s.Key });
        Assert.NotNull(await proxy.ReceiveTypeAsync("auth", TimeSpan.FromSeconds(5)));
        return proxy;
    }

    private static object Subscribe(string symbol, string mode = "LTP") => new { action = "subscribe", symbols = new[] { new { symbol, exchange = "NSE" } }, mode };

    /// <summary>Sends the frame until the client has the tick, covering the bus's slow-joiner gap.</summary>
    private static async Task<JsonObject?> TickAsync(Setup s, Proxy proxy, Func<byte[]> frame, string symbol)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until)
        {
            await s.Broker.SendBinaryAsync(frame());
            var m = await proxy.ReceiveTypeAsync("market_data", TimeSpan.FromMilliseconds(300));
            if (m is not null && m["symbol"]?.GetValue<string>() == symbol) return m;
        }

        return null;
    }

    [PostgresFact]
    public async Task A_subscription_reaches_the_broker_and_its_ticks_reach_the_client()
    {
        await using var broker = new FakeFeedServer();
        var s = await ZerodhaAsync(broker);
        await using var _host = s.Host;
        await using var proxy = await ConnectAsync(s);

        await proxy.SendAsync(Subscribe("SBIN"));
        var answer = await proxy.ReceiveTypeAsync("subscribe", TimeSpan.FromSeconds(5));
        Assert.Equal("success", answer!["subscriptions"]![0]!["status"]!.GetValue<string>());

        Assert.True(await FakeFeedServer.UntilAsync(() => broker.Received.Count >= 2));
        Assert.Equal("""{"a":"subscribe","v":[779521]}""", broker.Received[0]);
        Assert.Equal("""{"a":"mode","v":["ltp",[779521]]}""", broker.Received[1]);

        // The stream is addressed with the api key and the access token apart, from the api_key:access_token the account keeps.
        var connected = broker.Requests.Single();
        Assert.Contains("api_key=kite-key", connected.Query);
        Assert.Contains("access_token=access-1", connected.Query);

        var tick = await TickAsync(s, proxy, () => KiteLtp(779521, 805.45), "SBIN");
        Assert.NotNull(tick);
        Assert.Equal(805.45, tick!["data"]!["ltp"]!.GetValue<double>());
        Assert.Equal("NSE", tick["exchange"]!.GetValue<string>());
    }

    [PostgresFact]
    public async Task A_dropped_connection_comes_back_and_repeats_the_subscriptions()
    {
        await using var broker = new FakeFeedServer();
        var s = await ZerodhaAsync(broker);
        await using var _host = s.Host;
        await using var proxy = await ConnectAsync(s);
        await proxy.SendAsync(Subscribe("SBIN"));
        Assert.True(await FakeFeedServer.UntilAsync(() => broker.Received.Count >= 2));

        broker.Drop();
        Assert.True(await FakeFeedServer.UntilAsync(() => broker.Connections >= 2), "The feed never reconnected.");
        Assert.True(await FakeFeedServer.UntilAsync(() => broker.Received.Count >= 4));
        Assert.Equal(broker.Received[0], broker.Received[2]);
        Assert.Equal(broker.Received[1], broker.Received[3]);
        Assert.NotNull(await TickAsync(s, proxy, () => KiteLtp(779521, 806.0), "SBIN"));
    }

    [PostgresFact]
    public async Task The_broker_is_told_to_stop_only_when_the_last_client_lets_go()
    {
        await using var broker = new FakeFeedServer();
        var s = await ZerodhaAsync(broker);
        await using var _host = s.Host;
        await using var first = await ConnectAsync(s);
        await using var second = await ConnectAsync(s);
        await first.SendAsync(Subscribe("SBIN"));
        await second.SendAsync(Subscribe("SBIN"));
        Assert.NotNull(await first.ReceiveTypeAsync("subscribe", TimeSpan.FromSeconds(5)));
        Assert.NotNull(await second.ReceiveTypeAsync("subscribe", TimeSpan.FromSeconds(5)));
        Assert.True(await FakeFeedServer.UntilAsync(() => broker.Received.Count >= 2));
        Assert.Equal(2, broker.Received.Count);

        await first.SendAsync(new { action = "unsubscribe", symbols = new[] { new { symbol = "SBIN", exchange = "NSE" } }, mode = "LTP" });
        Assert.NotNull(await first.ReceiveTypeAsync("unsubscribe", TimeSpan.FromSeconds(5)));
        await Task.Delay(200);
        Assert.DoesNotContain(broker.Received, m => m.Contains("unsubscribe"));

        await second.SendAsync(new { action = "unsubscribe_all" });
        Assert.NotNull(await second.ReceiveTypeAsync("unsubscribe", TimeSpan.FromSeconds(5)));
        Assert.True(await FakeFeedServer.UntilAsync(() => broker.Received.Any(m => m.Contains("unsubscribe"))));
        Assert.Contains("""{"a":"unsubscribe","v":[779521]}""", broker.Received);
        Assert.True(await FakeFeedServer.UntilAsync(() => s.Host.Get<FeedHost>().Connections == 0), "The connection stayed open with nothing subscribed.");
    }

    /// <summary>
    /// Two clients letting go together both reach "nothing held". The second holds a connection that is already closed, and by then a new subscription may have opened
    /// another. This replays that second call by name, since no ordering of public calls can be made to produce it every time.
    /// </summary>
    [PostgresFact]
    public async Task A_late_release_on_a_closed_connection_leaves_the_newer_connection_alone_and_a_connection_stops_only_once()
    {
        await using var broker = new FakeFeedServer();
        var s = await ZerodhaAsync(broker);
        await using var _host = s.Host;
        var feed = s.Host.Get<FeedHost>();
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var table = (System.Collections.IDictionary)typeof(FeedHost).GetField("connections", flags)!.GetValue(feed)!;

        Assert.True((await feed.SubscribeAsync(s.Account, "zerodha", "NSE", "SBIN", StreamMode.Ltp, default)).Ok);
        var old = table[s.Account]!;
        await feed.UnsubscribeAsync(s.Account, "NSE", "SBIN", StreamMode.Ltp, default); // the first client lets go: closed and removed
        Assert.Equal(0, feed.Connections);
        Assert.True((await feed.SubscribeAsync(s.Account, "zerodha", "NSE", "INFY", StreamMode.Ltp, default)).Ok);
        var newer = table[s.Account]!;
        Assert.NotSame(old, newer);

        // The second client's release arrives now, on the connection it looked up before.
        var letGo = (Task)typeof(FeedHost).GetMethod("LetGoAsync", flags)!.Invoke(feed, [s.Account, old, "NSE", "SBIN", StreamMode.Ltp])!;
        await letGo;

        Assert.Equal(1, feed.Connections);
        Assert.Same(newer, table[s.Account]);

        // And asking a closed connection to stop again is harmless, however many times and from wherever.
        var stop = old.GetType().GetMethod("StopAsync")!;
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => (Task)stop.Invoke(old, null)!));

        await feed.UnsubscribeAsync(s.Account, "NSE", "INFY", StreamMode.Ltp, default);
        Assert.Equal(0, feed.Connections);
    }

    [PostgresFact]
    public async Task A_client_that_disconnects_lets_go_of_what_it_held()
    {
        await using var broker = new FakeFeedServer();
        var s = await ZerodhaAsync(broker);
        await using var _host = s.Host;
        var proxy = await ConnectAsync(s);
        await proxy.SendAsync(Subscribe("SBIN"));
        Assert.NotNull(await proxy.ReceiveTypeAsync("subscribe", TimeSpan.FromSeconds(5)));
        Assert.True(await FakeFeedServer.UntilAsync(() => broker.Received.Count >= 2));
        await proxy.DisposeAsync();
        Assert.True(await FakeFeedServer.UntilAsync(() => s.Host.Get<FeedHost>().Connections == 0), "The feed outlived the only client that wanted it.");
    }

    [PostgresFact]
    public async Task A_token_the_broker_refuses_is_tried_once_more_then_the_feed_stops()
    {
        await using var broker = new FakeFeedServer(HttpStatusCode.Forbidden);
        var s = await ZerodhaAsync(broker);
        await using var _host = s.Host;
        await using var proxy = await ConnectAsync(s);
        await proxy.SendAsync(Subscribe("SBIN"));
        Assert.NotNull(await proxy.ReceiveTypeAsync("subscribe", TimeSpan.FromSeconds(5)));

        // The same dead token a second time ends it: two attempts, no more.
        Assert.True(await FakeFeedServer.UntilAsync(() => broker.Connections >= 2));
        await Task.Delay(500);
        Assert.Equal(2, broker.Connections);
    }

    [PostgresFact]
    public async Task A_stream_that_goes_silent_is_reconnected()
    {
        await using var broker = new FakeFeedServer();
        var s = await ZerodhaAsync(broker, b => b.UseSetting("Streaming:FeedStallSeconds", "1"));
        await using var _host = s.Host;
        await using var proxy = await ConnectAsync(s);
        await proxy.SendAsync(Subscribe("SBIN"));
        Assert.True(await FakeFeedServer.UntilAsync(() => broker.Connections >= 1));
        Assert.True(await FakeFeedServer.UntilAsync(() => broker.Connections >= 2, 8), "A silent stream was never reconnected.");
    }

    [PostgresFact]
    public async Task An_instrument_missing_from_the_master_contract_and_an_account_not_signed_in_are_refused_by_name()
    {
        await using var broker = new FakeFeedServer();
        var s = await ZerodhaAsync(broker);
        await using var _host = s.Host;
        await using var proxy = await ConnectAsync(s);
        await proxy.SendAsync(Subscribe("NOPE"));
        var answer = await proxy.ReceiveTypeAsync("subscribe", TimeSpan.FromSeconds(5));
        var entry = answer!["subscriptions"]![0]!;
        Assert.Equal("error", entry["status"]!.GetValue<string>());
        Assert.Contains("master contract", entry["message"]!.GetValue<string>());

        (await s.Host.Admin.DeleteAsync($"/api/v1/accounts/{s.Account}/broker-session")).EnsureSuccessStatusCode();
        await proxy.SendAsync(Subscribe("SBIN"));
        answer = await proxy.ReceiveTypeAsync("subscribe", TimeSpan.FromSeconds(5));
        Assert.Contains("not signed in", answer!["subscriptions"]![0]!["message"]!.GetValue<string>());
        Assert.Equal(0, broker.Connections);
    }

    [PostgresFact]
    public async Task An_account_is_told_when_its_feed_is_full()
    {
        await using var broker = new FakeFeedServer();
        var s = await ZerodhaAsync(broker, b => b.UseSetting("Streaming:FeedMaxInstruments", "1"));
        await using var _host = s.Host;
        await using var proxy = await ConnectAsync(s);
        await proxy.SendAsync(Subscribe("SBIN"));
        Assert.Equal("success", (await proxy.ReceiveTypeAsync("subscribe", TimeSpan.FromSeconds(5)))!["subscriptions"]![0]!["status"]!.GetValue<string>());
        await proxy.SendAsync(Subscribe("INFY"));
        var entry = (await proxy.ReceiveTypeAsync("subscribe", TimeSpan.FromSeconds(5)))!["subscriptions"]![0]!;
        Assert.Equal("error", entry["status"]!.GetValue<string>());
        Assert.Contains("allows 1 instruments", entry["message"]!.GetValue<string>());
        // The same instrument again is not another one.
        await proxy.SendAsync(Subscribe("SBIN"));
        Assert.Equal("success", (await proxy.ReceiveTypeAsync("subscribe", TimeSpan.FromSeconds(5)))!["subscriptions"]![0]!["status"]!.GetValue<string>());
    }

    [PostgresFact]
    public async Task Angel_One_connects_with_its_four_credentials_and_asks_in_its_own_words()
    {
        await using var broker = new FakeFeedServer();
        await using var host = new TestHost("Production", b =>
        {
            b.UseSetting("Brokers:Enabled:0", "angel");
            b.UseSetting("Brokers:Keys:angel:ApiKey", "angel-key");
            b.UseSetting("Brokers:FeedUrls:angel", broker.Address);
        });
        var account = await host.CreateAccountAsync(AccountType.LIVE, "angel", broker: "angel", clientId: "C123");
        (await host.Admin.PutAsJsonAsync($"/api/v1/accounts/{account}/broker-session", new { auth_token = "jwt-1", feed_token = "feed-1" })).EnsureSuccessStatusCode();
        var key = (await (await host.Admin.PostAsJsonAsync($"/api/v1/accounts/{account}/apikeys", new { label = "feed" })).Content.ReadFromJsonAsync<JsonObject>())!["apikey"]!.GetValue<string>();
        await using (var db = host.NewDb())
        {
            db.Set<SymToken>().Add(new SymToken { Broker = "angel", Symbol = "SBIN", Brsymbol = "SBIN-EQ", Exchange = "NSE", Brexchange = "NSE", Token = "3045", Lotsize = 1 });
            await db.SaveChangesAsync();
        }

        var s = new Setup(host, broker, account, key);
        await using var proxy = await ConnectAsync(s);
        await proxy.SendAsync(Subscribe("SBIN", "Quote"));
        Assert.True(await FakeFeedServer.UntilAsync(() => broker.Received.Count >= 1));

        var connected = broker.Requests.Single();
        Assert.Equal("jwt-1", connected.Headers["Authorization"]);
        Assert.Equal("angel-key", connected.Headers["x-api-key"]);
        Assert.Equal("C123", connected.Headers["x-client-code"]);
        Assert.Equal("feed-1", connected.Headers["x-feed-token"]);

        var message = JsonNode.Parse(broker.Received[0])!;
        Assert.Equal(1, message["action"]!.GetValue<int>());
        Assert.Equal(2, message["params"]!["mode"]!.GetValue<int>());
        Assert.Equal("3045", message["params"]!["tokenList"]![0]!["tokens"]![0]!.GetValue<string>());
    }

    [PostgresFact]
    public async Task An_account_with_no_feed_token_is_told_to_sign_in_again()
    {
        await using var broker = new FakeFeedServer();
        await using var host = new TestHost("Production", b =>
        {
            b.UseSetting("Brokers:Enabled:0", "angel");
            b.UseSetting("Brokers:Keys:angel:ApiKey", "angel-key");
            b.UseSetting("Brokers:FeedUrls:angel", broker.Address);
        });
        var account = await host.CreateAccountAsync(AccountType.LIVE, "angel", broker: "angel", clientId: "C123");
        (await host.Admin.PutAsJsonAsync($"/api/v1/accounts/{account}/broker-session", new { auth_token = "jwt-1" })).EnsureSuccessStatusCode();
        var key = (await (await host.Admin.PostAsJsonAsync($"/api/v1/accounts/{account}/apikeys", new { label = "feed" })).Content.ReadFromJsonAsync<JsonObject>())!["apikey"]!.GetValue<string>();
        await using (var db = host.NewDb())
        {
            db.Set<SymToken>().Add(new SymToken { Broker = "angel", Symbol = "SBIN", Brsymbol = "SBIN-EQ", Exchange = "NSE", Brexchange = "NSE", Token = "3045", Lotsize = 1 });
            await db.SaveChangesAsync();
        }

        await using var proxy = await ConnectAsync(new Setup(host, broker, account, key));
        await proxy.SendAsync(Subscribe("SBIN"));
        var answer = await proxy.ReceiveTypeAsync("subscribe", TimeSpan.FromSeconds(5));
        Assert.Contains("did not give this account a live price feed", answer!["subscriptions"]![0]!["message"]!.GetValue<string>());
        Assert.Equal(0, broker.Connections);
    }
}
