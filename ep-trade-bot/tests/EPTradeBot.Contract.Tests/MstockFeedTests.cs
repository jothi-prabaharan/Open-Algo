using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming;
using EPTradeBot.Core.Streaming.Feeds;
using EPTradeBot.Infrastructure.Brokers.Mstock;

namespace EPTradeBot.Contract.Tests;

/// <summary>mStock's stream against the Python adapter and client (tools/contract-fixtures/feed_mstock.fixtures.json).</summary>
public sealed class MstockFeedContractTests
{
    private const string File = "feed_mstock.fixtures.json";

    public static IEnumerable<object[]> Names() => FeedRunner.Names(File);

    private static JsonObject Stop(int mode, string token = "3045", int exchangeType = 1) => new()
    {
        ["action"] = 0,
        ["params"] = new JsonObject { ["mode"] = mode, ["tokenList"] = new JsonArray(new JsonObject { ["exchangeType"] = exchangeType, ["tokens"] = new JsonArray(token) }) },
    };

    /// <summary>
    /// OpenAlgo forgets a subscription's name before it looks the name up, so unsubscribing the last mode of a token sends the broker nothing and the broker keeps sending that token for the life of the connection.
    /// EP Trade bot sends the stop: the step number (from one) is the unsubscribe that ends the token, and the mode is the one it was subscribed in.
    /// </summary>
    private static readonly Dictionary<string, (int Step, int Mode)> LastModeStops = new()
    {
        ["unsubscribe_the_only_mode_of_a_token"] = (2, 2),
        ["unsubscribe_one_of_two_tokens"] = (2, 2),
        ["unsubscribe_every_mode_in_turn"] = (4, 1),
        ["unsubscribe_twice"] = (2, 2),
        ["unsubscribe_then_subscribe_again"] = (2, 2),
        ["unsubscribe_stops_the_ticks_of_that_scrip"] = (2, 2),
        ["a_connection_repeats_nothing_after_everything_was_dropped"] = (2, 2),
        ["a_text_message_does_not_change_the_subscriptions"] = (3, 2),
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void Matches_the_python_adapter(string name) => FeedRunner.Run(File, name, () => new MstockFeedCodec(),
        LastModeStops.TryGetValue(name, out var stop)
            ? scenario => scenario["steps"]!.AsArray()[stop.Step - 1]!["result"]!["sent"] = new JsonArray(Stop(stop.Mode))
            : null);
}

/// <summary>mStock depth against the Python client (tools/contract-fixtures/feed_snap_mstock.fixtures.json): the real plugin with a connection that hands back the frames the recording scripted.</summary>
public sealed class MstockSnapshotContractTests
{
    private const string File = "feed_snap_mstock.fixtures.json";

    public static IEnumerable<object[]> Names() => FeedRunner.Names(File);

    private sealed class Options(EPTradeBot.Core.Configuration.BrokerOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<EPTradeBot.Core.Configuration.BrokerOptions>
    {
        public EPTradeBot.Core.Configuration.BrokerOptions CurrentValue => value;

        public EPTradeBot.Core.Configuration.BrokerOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<EPTradeBot.Core.Configuration.BrokerOptions, string?> listener) => null;
    }

    private sealed class Master(IReadOnlyList<SymbolRecord> rows) : ISymbolMaster
    {
        public Task<SymbolRecord?> FindAsync(string broker, string symbol, string exchange, CancellationToken cancellationToken) => Task.FromResult(rows.FirstOrDefault(r => r.Symbol == symbol && r.Exchange == exchange));

        public Task<SymbolRecord?> FindByBrokerSymbolAsync(string broker, string brokerSymbol, string exchange, CancellationToken cancellationToken) => Task.FromResult<SymbolRecord?>(null);

        public Task<SymbolRecord?> FindByTokenAsync(string broker, string token, string exchange, CancellationToken cancellationToken) => Task.FromResult<SymbolRecord?>(null);
    }

    /// <summary>
    /// The Python client's connection: the first read is for an answer to the sign-in (a frame of the scenario's, or nothing), and then up to three more reads. EP Trade bot does not wait for an answer to the sign-in, so it is skipped here;
    /// a frame that reads as a quote is the answer, a text frame is passed over and a failed read ends the wait.
    /// </summary>
    private sealed class Script(JsonObject step) : IMstockSockets
    {
        public List<JsonNode?> Sent { get; } = [];

        public List<string> Connections { get; } = [];

        public Task<byte[]?> SnapshotAsync(Uri address, string login, string subscribe, Func<byte[], bool> accept, CancellationToken cancellationToken)
        {
            Connections.Add(address.OriginalString);
            Sent.Add(JsonValue.Create(login));
            Sent.Add(JsonNode.Parse(subscribe));
            foreach (var item in (step["frames"] as JsonArray ?? []).OfType<JsonObject>().Take(3))
            {
                if (item["error"] is not null) break;
                if (item["hex"] is not JsonValue hex) continue;
                var frame = Convert.FromHexString(hex.GetValue<string>());
                if (frame.Length > 0 && accept(frame)) return Task.FromResult<byte[]?>(frame);
            }

            return Task.FromResult<byte[]?>(null);
        }
    }

    private static string Reason(string python) => python switch
    {
        var p when p.Contains("Failed to fetch depth data", StringComparison.Ordinal) => "did not send the market depth for",
        var p when p.Contains("not supported for depth", StringComparison.Ordinal) => "does not give market depth for the",
        var p when p.Contains("NaN", StringComparison.Ordinal) => "that could not be read",
        _ => python,
    };

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Matches_the_python_client(string name)
    {
        var fixtures = BrokerContractHost.Load(File);
        var rows = fixtures["symbols"]!.AsArray().OfType<JsonObject>().Select(s => new SymbolRecord(s["symbol"]!.GetValue<string>(), s["brsymbol"]!.GetValue<string>(), s["exchange"]!.GetValue<string>(), s["brexchange"]!.GetValue<string>(),
            s["token"]!.GetValue<string>(), s["name"]?.GetValue<string>(), null, null, 1, s["instrumenttype"]?.GetValue<string>(), 0.05)).ToList();
        var session = new BrokerSession(Guid.NewGuid(), "mstock", fixtures["access_token"]!.GetValue<string>(), null, null, new Dictionary<string, string> { ["ApiKey"] = "mst-api-key", ["ApiSecret"] = "mst-api-secret" });
        var sessions = new EPTradeBot.Infrastructure.Brokers.BrokerSessionFactory(null!, new Options(new EPTradeBot.Core.Configuration.BrokerOptions()));
        var scenario = fixtures["scenarios"]!.AsArray().OfType<JsonObject>().Single(s => s["name"]!.GetValue<string>() == name);
        var index = 0;
        foreach (var entry in scenario["steps"]!.AsArray().OfType<JsonObject>())
        {
            var step = entry["step"]!.AsObject();
            var inner = step["depth"]!.AsObject();
            var result = entry["result"]!.AsObject();
            var label = $"{name} step {++index}";
            var sockets = new Script(inner);
            var plugin = new MstockPlugin(null!, sessions, new Master(rows), TimeProvider.System, sockets);
            JsonNode? actual = null;
            string? error = null;
            try
            {
                actual = await plugin.GetDepthAsync(session, inner["symbol"]!.GetValue<string>(), inner["exchange"]!.GetValue<string>(), CancellationToken.None);
            }
            catch (BrokerException ex)
            {
                error = ex.Message;
            }

            if (result["error"] is JsonValue expectedError)
            {
                Assert.True(error is not null, $"{label}: expected an error ({expectedError.GetValue<string>()}), got an answer {actual?.ToJsonString()}");
                Assert.Contains(Reason(expectedError.GetValue<string>()), error!, StringComparison.Ordinal);
            }
            else
            {
                Assert.True(error is null, $"{label}: unexpected error: {error}");
                var diffs = JsonCompare.Differences(result["result"], actual);
                Assert.True(diffs.Count == 0, $"{label}: the answer differs:\n{string.Join("\n", diffs)}");
            }

            var expectedSent = result["sent"]!.AsArray();
            Assert.True(expectedSent.Count == sockets.Sent.Count, $"{label}: expected {expectedSent.Count} messages, got {sockets.Sent.Count}");
            for (var i = 0; i < expectedSent.Count; i++)
            {
                var diffs = JsonCompare.Differences(expectedSent[i], sockets.Sent[i]);
                Assert.True(diffs.Count == 0, $"{label}: message {i} differs:\n{string.Join("\n", diffs)}");
            }

            Assert.Equal(result["connections"]!.AsArray().Count, sockets.Connections.Count);
            foreach (var (expected, got) in result["connections"]!.AsArray().Zip(sockets.Connections))
                Assert.Equal(expected!.GetValue<string>(), got);
        }
    }
}

/// <summary>What the recordings cannot say: the address and the sign-in, a session that cannot sign in, and the real depth connection against a local server.</summary>
public sealed class MstockFeedEndpointTests
{
    private static BrokerSession Session(string token = "mst-session-jwt", string? secret = "mst-api-secret", string? key = "mst-api-key")
    {
        var keys = new Dictionary<string, string>();
        if (secret is not null) keys["ApiSecret"] = secret;
        if (key is not null) keys["ApiKey"] = key;
        return new(Guid.NewGuid(), "mstock", token, null, null, keys);
    }

    private static MstockPlugin Plugin() => new(null!, new EPTradeBot.Infrastructure.Brokers.BrokerSessionFactory(null!, null!), null!, TimeProvider.System);

    [Fact]
    public void The_address_carries_the_secret_and_the_token() =>
        Assert.Equal("wss://ws.mstock.trade/?API_KEY=mst-api-secret&ACCESS_TOKEN=mst-session-jwt", Plugin().FeedEndpoint(Session(), null)!.Url.ToString());

    [Fact]
    public void The_address_falls_back_to_the_key_when_there_is_no_secret() =>
        Assert.Equal("wss://ws.mstock.trade/?API_KEY=mst-api-key&ACCESS_TOKEN=mst-session-jwt", Plugin().FeedEndpoint(Session(secret: null), null)!.Url.ToString());

    [Fact]
    public void An_empty_secret_falls_back_to_the_key() =>
        Assert.Equal("wss://ws.mstock.trade/?API_KEY=mst-api-key&ACCESS_TOKEN=mst-session-jwt", Plugin().FeedEndpoint(Session(secret: ""), null)!.Url.ToString());

    [Fact]
    public void A_configured_address_replaces_the_host() =>
        Assert.StartsWith("wss://feed.example/?API_KEY=", Plugin().FeedEndpoint(Session(), "wss://feed.example/")!.Url.ToString(), StringComparison.Ordinal);

    [Fact]
    public void A_session_with_no_token_cannot_start_a_feed() => Assert.Null(Plugin().FeedEndpoint(Session(token: " "), null));

    [Fact]
    public void A_session_with_no_key_cannot_start_a_feed() => Assert.Null(Plugin().FeedEndpoint(Session(secret: null, key: null), null));

    [Fact]
    public void The_connection_is_signed_in_with_the_token_and_nothing_is_kept_alive_by_text()
    {
        var plugin = Plugin();
        Assert.Null(plugin.FeedKeepAliveText);
        var codec = plugin.CreateFeedCodec();
        codec.Begin(Session());
        Assert.Equal(["LOGIN:mst-session-jwt"], codec.Resubscribe(0));
    }

    [Fact]
    public async Task Depth_asks_with_the_key_when_the_secret_is_empty()
    {
        var capture = new Capturing();
        var plugin = new MstockPlugin(null!, new EPTradeBot.Infrastructure.Brokers.BrokerSessionFactory(null!, new OptionsOf(new EPTradeBot.Core.Configuration.BrokerOptions())), new Master(), TimeProvider.System, capture);
        await Assert.ThrowsAsync<BrokerException>(() => plugin.GetDepthAsync(Session(secret: ""), "SBIN", "NSE", CancellationToken.None));
        Assert.Equal("wss://ws.mstock.trade?API_KEY=mst-api-key&ACCESS_TOKEN=mst-session-jwt", capture.Address);
    }

    private sealed class Capturing : IMstockSockets
    {
        public string? Address { get; private set; }

        public Task<byte[]?> SnapshotAsync(Uri address, string login, string subscribe, Func<byte[], bool> accept, CancellationToken cancellationToken)
        {
            Address = address.OriginalString;
            return Task.FromResult<byte[]?>(null);
        }
    }

    [Fact]
    public void The_codec_names_its_broker() => Assert.Equal("mstock", new MstockFeedCodec().Broker);

    [Fact]
    public void The_codec_is_a_new_one_for_each_connection() => Assert.NotSame(Plugin().CreateFeedCodec(), Plugin().CreateFeedCodec());

    [Fact]
    public void A_new_sign_in_replaces_the_token_of_the_login()
    {
        var codec = new MstockFeedCodec();
        codec.Begin(Session("first"));
        codec.Begin(Session("second"));
        Assert.Equal(["LOGIN:second"], codec.Resubscribe(0));
    }

    [Fact]
    public void Reset_forgets_every_subscription()
    {
        var codec = new MstockFeedCodec();
        codec.Begin(Session());
        codec.Subscribe([new FeedRequest("NSE", "SBIN", "3045", "NSE", StreamMode.Quote)], 0);
        codec.Reset();
        Assert.Equal(["LOGIN:mst-session-jwt"], codec.Resubscribe(0));
    }

    [Fact]
    public void A_mode_the_stream_does_not_have_is_left_out()
    {
        var codec = new MstockFeedCodec();
        Assert.Empty(codec.Subscribe([new FeedRequest("NSE", "SBIN", "3045", "NSE", (StreamMode)4)], 0));
    }

    [Fact]
    public void Unsubscribing_every_mode_stops_the_token_once_in_the_mode_it_was_held()
    {
        var codec = new MstockFeedCodec();
        codec.Subscribe([new FeedRequest("NSE", "SBIN", "3045", "NSE", StreamMode.Ltp), new FeedRequest("NSE", "SBIN", "3045", "NSE", StreamMode.Depth)], 0);
        var frames = codec.Unsubscribe(new FeedTarget("NSE", "SBIN", "3045", "NSE"), null, 0);
        var only = Assert.Single(frames);
        Assert.Contains("\"action\":0", only, StringComparison.Ordinal);
        Assert.Contains("\"mode\":3", only, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Depth_is_read_from_a_real_socket()
    {
        using var listener = new System.Net.HttpListener();
        int port;
        using (var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }

        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var received = new List<string>();
        string? query = null;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            query = context.Request.Url!.Query;
            var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            var buffer = new byte[8192];
            while (received.Count < 2)
            {
                var result = await socket.ReceiveAsync(buffer, stop.Token);
                lock (received) received.Add(System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count));
            }

            await socket.SendAsync(System.Text.Encoding.UTF8.GetBytes("welcome"), System.Net.WebSockets.WebSocketMessageType.Text, true, stop.Token);
            await socket.SendAsync(new byte[] { 1, 2, 3 }, System.Net.WebSockets.WebSocketMessageType.Binary, true, stop.Token);
            var packet = new byte[51];
            packet[0] = 1;
            System.Text.Encoding.ASCII.GetBytes("3045").CopyTo(packet, 2);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(43), 80545);
            await socket.SendAsync(packet, System.Net.WebSockets.WebSocketMessageType.Binary, true, stop.Token);
            try
            {
                await socket.ReceiveAsync(buffer, stop.Token);
            }
            catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or OperationCanceledException)
            {
            }
        });
        var options = new EPTradeBot.Core.Configuration.BrokerOptions();
        options.FeedUrls["mstock"] = $"ws://127.0.0.1:{port}/";
        var master = new Master();
        var plugin = new MstockPlugin(null!, new EPTradeBot.Infrastructure.Brokers.BrokerSessionFactory(null!, new OptionsOf(options)), master, TimeProvider.System);

        var depth = await plugin.GetDepthAsync(Session(), "SBIN", "NSE", stop.Token);

        Assert.Equal(805.45, (double)depth["ltp"]!);
        Assert.Equal(5, depth["bids"]!.AsArray().Count);
        lock (received)
        {
            Assert.Equal("LOGIN:mst-session-jwt", received[0]);
            Assert.Equal(3, (int)JsonNode.Parse(received[1])!["params"]!["mode"]!);
        }

        Assert.Equal("?API_KEY=mst-api-secret&ACCESS_TOKEN=mst-session-jwt", query);
        stop.Cancel();
        listener.Stop();
        try
        {
            await server;
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.Net.HttpListenerException or System.Net.WebSockets.WebSocketException or ObjectDisposedException)
        {
        }
    }

    [Fact]
    public async Task Depth_from_a_server_that_is_not_there_is_an_error_for_a_trader()
    {
        var options = new EPTradeBot.Core.Configuration.BrokerOptions();
        options.FeedUrls["mstock"] = "ws://127.0.0.1:1/";
        var plugin = new MstockPlugin(null!, new EPTradeBot.Infrastructure.Brokers.BrokerSessionFactory(null!, new OptionsOf(options)), new Master(), TimeProvider.System);
        var error = await Assert.ThrowsAsync<BrokerException>(() => plugin.GetDepthAsync(Session(), "SBIN", "NSE", CancellationToken.None));
        Assert.Contains("did not send the market depth for SBIN on NSE", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Depth_for_a_symbol_the_master_does_not_know_is_refused()
    {
        var plugin = new MstockPlugin(null!, new EPTradeBot.Infrastructure.Brokers.BrokerSessionFactory(null!, new OptionsOf(new EPTradeBot.Core.Configuration.BrokerOptions())), new Master(known: false), TimeProvider.System, new Refusing());
        var error = await Assert.ThrowsAsync<BrokerException>(() => plugin.GetDepthAsync(Session(), "SBIN", "NSE", CancellationToken.None));
        Assert.Contains("is not in the mStock master contract", error.Message, StringComparison.Ordinal);
    }

    private sealed class Refusing : IMstockSockets
    {
        public Task<byte[]?> SnapshotAsync(Uri address, string login, string subscribe, Func<byte[], bool> accept, CancellationToken cancellationToken) => throw new InvalidOperationException("nothing should be connected");
    }

    private sealed class OptionsOf(EPTradeBot.Core.Configuration.BrokerOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<EPTradeBot.Core.Configuration.BrokerOptions>
    {
        public EPTradeBot.Core.Configuration.BrokerOptions CurrentValue => value;

        public EPTradeBot.Core.Configuration.BrokerOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<EPTradeBot.Core.Configuration.BrokerOptions, string?> listener) => null;
    }

    private sealed class Master(bool known = true) : ISymbolMaster
    {
        public Task<SymbolRecord?> FindAsync(string broker, string symbol, string exchange, CancellationToken cancellationToken) =>
            Task.FromResult<SymbolRecord?>(known ? new(symbol, symbol + "-EQ", exchange, exchange, "3045", null, null, null, 1, "EQ", 0.05) : null);

        public Task<SymbolRecord?> FindByBrokerSymbolAsync(string broker, string brokerSymbol, string exchange, CancellationToken cancellationToken) => Task.FromResult<SymbolRecord?>(null);

        public Task<SymbolRecord?> FindByTokenAsync(string broker, string token, string exchange, CancellationToken cancellationToken) => Task.FromResult<SymbolRecord?>(null);
    }
}
