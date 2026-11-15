using EPTradeBot.Core.Streaming.Feeds;
using System.Text.Json.Nodes;
using EPTradeBot.IntegrationTests;
using Xunit.Abstractions;
using static EPTradeBot.Contract.Tests.ContractRunner;

namespace EPTradeBot.Contract.Tests;

public abstract class AliceBlueFileHost(string file) : BrokerContractHost
{
    public override string BrokerName => "aliceblue";
    public override string FixtureFile => file;
    public override bool NumbersMayBeText => true;
    public override IReadOnlyDictionary<string, string> Keys { get; } = new Dictionary<string, string> { ["ApiKey"] = "ab-app-code", ["ApiSecret"] = "ab-secret" };
    public override IReadOnlyDictionary<(string, string), decimal> Prices { get; } = new Dictionary<(string, string), decimal>
    {
        [("NSE", "SBIN")] = 805m, [("NSE", "INFY")] = 1500m, [("NSE", "ODDTOKEN")] = 100m, [("BSE", "TATAMOTORS")] = 500m, [("NFO", "NIFTY25NOV26FUT")] = 24000m, [("BFO", "SENSEX26NOVFUT")] = 80000m, [("MCX", "GOLDM25NOV26FUT")] = 75000m, [("CDS", "USDINR25NOV26FUT")] = 83.5m,
    };
    public override IReadOnlyDictionary<string, string> HarnessReplies { get; } = new Dictionary<string, string> { ["GET /open-api/od/v1/positions"] = """{"status":"Ok","result":[]}""" };
    public override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["Brokers:RetryBackoffMs:aliceblue"] = "0", ["OpenAlgoApi:HistorySpacingMs"] = "0", ["Brokers:HistoryGapMs:aliceblue"] = "0" };
}

public sealed class AliceBlueHost() : AliceBlueFileHost("aliceblue.fixtures.json");

/// <summary>AliceBlue orders, books, positions and funds (tools/contract-fixtures/aliceblue.fixtures.json).</summary>
public sealed class AliceBlueContractTests(AliceBlueHost host, ITestOutputHelper output) : IClassFixture<AliceBlueHost>
{
    private const string Words = "OpenAlgo shows a failed read as an empty list or an empty balance. EP Trade bot says what AliceBlue did and what to do.";

    private static Deviation Says(int status, string message) => new(Words, f => Expect(f, status, Error(message)));

    private const string Refusal = "OpenAlgo answers a refused order with a generic sentence that carries the status and a web address. EP Trade bot passes AliceBlue's own words on.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["placeorder_refused"] = new(Refusal, f => Expect(f, 400, Error("Failed to place the order."))),
        ["placeorder_refused_with_prose"] = new(Refusal, f => Expect(f, 400, Error("Margin shortfall"))),
        ["placeorder_refused_without_a_message"] = new(Refusal, f => Expect(f, 400, Error("Failed to place order"))),
        ["placeorder_refused_with_status_200"] = new("OpenAlgo answers success with no order number when AliceBlue refuses an order. EP Trade bot reports the refusal.", f => Expect(f, 400, Error("Rejected by RMS"))),
        ["placeorder_item_error_without_an_order_id"] = new("OpenAlgo answers success with no order number when AliceBlue refuses the order inside an accepted answer. EP Trade bot reports the refusal.",
            f => Expect(f, 400, Error("Quantity must be a multiple of the lot size"))),
        ["placeorder_ok_with_no_result"] = new("OpenAlgo answers success with no order number. EP Trade bot says the order may have been placed and to check the order book.",
            f => Expect(f, 502, Error("AliceBlue accepted the order but sent no order number, so it cannot be followed. Check the order book on AliceBlue before trying again."))),
        ["placeorder_ok_without_an_order_number"] = new("Same.", f => Expect(f, 502, Error("AliceBlue accepted the order but sent no order number, so it cannot be followed. Check the order book on AliceBlue before trying again."))),
        ["placeorder_not_json"] = new("OpenAlgo answers a failed call with a generic sentence. EP Trade bot says the order may have been placed and to check the order book, because the call failed after it was sent.",
            f => Expect(f, 502, Error("AliceBlue could not confirm the order, so it may have been placed. Check the order book on AliceBlue before trying again."))),
        ["placeorder_unknown_symbol"] = new("OpenAlgo fails with an internal error. The platform names the symbol and says what to do, and sends nothing.",
            f => { Expect(f, 400, Error("NOPE on NSE is not in the AliceBlue master contract. Check the symbol, or download the master contract again.")); f["sent"]!.AsArray().Clear(); }),
        ["modifyorder_refused"] = new(Refusal, f => Expect(f, 400, Error("Order is not open"))),
        ["modifyorder_refused_with_status_200"] = new("OpenAlgo answers success when AliceBlue refuses a change. EP Trade bot reports the refusal.", f => Expect(f, 400, Error("Too late"))),
        ["modifyorder_refused_without_a_message"] = new(Refusal, f => Expect(f, 400, Error("Failed to modify order"))),
        ["cancelorder_refused"] = new(Refusal, f => Expect(f, 404, Error("Order not found to cancel"))),
        ["cancelorder_refused_with_status_200"] = new("OpenAlgo answers success when AliceBlue refuses a cancel. EP Trade bot reports the refusal.", f => Expect(f, 400, Error("Already complete"))),
        ["cancelorder_refused_without_a_message"] = new(Refusal, f => Expect(f, 400, Error("Failed to cancel order"))),
        ["cancelallorder_book_failed"] = new("OpenAlgo cancels nothing and reports success when the order book cannot be read. EP Trade bot reports the failure.", f => Expect(f, 500, Error("Failed to cancel all orders due to internal error"))),
        ["orderbook_none_found_is_empty"] = Says(500, "AliceBlue could not send the order book: Failed to retrieve the order book"),
        ["orderbook_failed"] = Says(500, "AliceBlue could not send the order book: Failed to retrieve the order book."),
        ["orderbook_http_error"] = Says(500, "AliceBlue could not send the order book: no reason was given."),
        ["orderbook_not_json"] = Says(500, "AliceBlue could not send the order book: no reason was given."),
        ["tradebook_failed"] = Says(500, "AliceBlue could not send the trade book: Failed to retrieve the trade book."),
        ["positionbook"] = new("OpenAlgo leaves the symbol empty for a position that is not in the master contract. EP Trade bot shows AliceBlue's own name for it.", f =>
        {
            foreach (var row in f["expected"]!["body"]!["data"]!.AsArray().OfType<JsonObject>().Where(r => r["symbol"] is null)) row["symbol"] = "NOSUCH-EQ";
        }),
        ["positionbook_failed_to_retrieve_is_empty"] = Says(500, "AliceBlue could not send the positions: Failed to retrieve the position book."),
        ["positionbook_failed"] = Says(500, "AliceBlue could not send the positions: Failed to retrieve the position book."),
        ["positionbook_http_error"] = Says(500, "AliceBlue could not send the positions: no reason was given."),
        ["holdings_failed_to_retrieve_is_empty"] = Says(500, "AliceBlue could not send the holdings: Failed to retrieve holdings"),
        ["holdings_failed"] = Says(500, "AliceBlue could not send the holdings: Failed to retrieve holdings."),
        ["funds_text_that_is_not_a_number"] = new(Words + " The positions are not read once a figure is unreadable.", f =>
        {
            Expect(f, 500, Error("AliceBlue could not send the funds: a figure was not a number."));
            var sent = f["sent"]!.AsArray();
            foreach (var call in sent.OfType<JsonObject>().Where(c => c["path"]!.GetValue<string>().EndsWith("/positions", StringComparison.Ordinal)).ToList()) sent.Remove(call);
        }),
        ["funds_failed"] = Says(500, "AliceBlue could not send the funds: Failed to retrieve RMS limits."),
        ["funds_http_error"] = Says(500, "AliceBlue could not send the funds: no reason was given."),
        ["closeposition_book_failed"] = new("OpenAlgo answers that every position was squared off when the positions cannot be read. EP Trade bot says nothing was closed and to try again.", f => Expect(f, 500, Error("Your positions could not be read from aliceblue, so nothing was closed. Try again in a moment."))),
        ["closeposition"] = new("OpenAlgo sends an order with no symbol for a position it does not know and still reports every position squared off, and leaves the disclosed quantity empty on the orders it sends. EP Trade bot names the position it could not close and sends a disclosed quantity of zero.",
            f =>
            {
                Expect(f, 500, Error("1 position(s) could not be squared off and are still open: NOSUCH-EQ (NSE): NOSUCH-EQ on NSE is not in the AliceBlue master contract. Check the symbol, or download the master contract again."));
                foreach (var call in f["sent"]!.AsArray().OfType<JsonObject>().Where(c => c["json"] is JsonArray)) call["json"]![0]!["disclosedQuantity"] = "0";
            }),
        ["placesmartorder_book_failed_is_refused"] = new("OpenAlgo answers a position it could not read with a 500; EP Trade bot answers 503 and says to try again.",
            f => Expect(f, 503, Error("Your open position in SBIN could not be read from aliceblue, so no order was placed. Try again in a moment."))),
        ["placesmartorder_already_matched"] = new("OpenAlgo answers an already-matched smart order with a 500 whose message reads as a failure. EP Trade bot answers success and says the position already matches.", f => Expect(f, 200, new JsonObject { ["status"] = "success", ["message"] = "Positions Already Matched. No Action needed." })),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("aliceblue.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class AliceBlueHistoryHost() : AliceBlueFileHost("history_aliceblue.fixtures.json");

/// <summary>AliceBlue history (tools/contract-fixtures/history_aliceblue.fixtures.json), with the clock stopped at the recording.</summary>
public sealed class AliceBlueHistoryContractTests(AliceBlueHistoryHost host, ITestOutputHelper output) : IClassFixture<AliceBlueHistoryHost>
{
    private const string NoCode = "OpenAlgo answers an empty list when AliceBlue cannot be reached or answers with something that is not data, which a trader reads as no candles. EP Trade bot says to try again in a moment.";

    private static Deviation Down() => new(NoCode, f => Expect(f, 500, Error("AliceBlue could not return candles for NSE:SBIN right now. Try again in a moment.")));

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["history_http_error_is_no_candles"] = Down(),
        ["history_not_json_is_no_candles"] = Down(),
        ["history_an_interval_aliceblue_lacks"] = new("OpenAlgo answers an empty list for an interval AliceBlue does not offer. EP Trade bot answers 400 and names the intervals AliceBlue offers, so a trader is never handed an empty series that looks like no data.",
            f => { Expect(f, 400, Error("Interval 2m is not supported by AliceBlue. Use one of: 1m, 3m, 5m, 10m, 15m, 30m, 1h, D.")); f["sent"]!.AsArray().Clear(); }),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("history_aliceblue.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => RunAsync(host, name, Deviations, output);
}

public sealed class AliceBlueLoginHost() : AliceBlueFileHost("login_aliceblue.fixtures.json");

/// <summary>AliceBlue sign-in (tools/contract-fixtures/login_aliceblue.fixtures.json).</summary>
public sealed class AliceBlueLoginContractTests(AliceBlueLoginHost host, ITestOutputHelper output) : IClassFixture<AliceBlueLoginHost>
{
    private const string Again = "AliceBlue did not accept the sign-in. Start it again from the beginning.";

    public static readonly Dictionary<string, Deviation> Deviations = new()
    {
        ["login_refused_not_ok"] = LoginDeviations.Refused("AliceBlue did not accept the sign-in: Invalid auth code"),
        ["login_refused_not_ok_lowercase_without_a_message"] = LoginDeviations.Refused(Again),
        ["login_refused_not_ok_exact_spelling"] = LoginDeviations.Refused("AliceBlue did not accept the sign-in: Auth code expired"),
        ["login_refused_not_ok_without_a_message"] = LoginDeviations.Refused(Again),
        ["login_refused_with_a_message_only"] = LoginDeviations.Refused("AliceBlue did not accept the sign-in: Checksum mismatch"),
        ["login_ok_without_a_session"] = LoginDeviations.Refused(Again),
        ["login_ok_with_an_empty_session"] = LoginDeviations.Refused(Again),
        ["login_refused_with_a_status"] = LoginDeviations.Refused("AliceBlue did not accept the sign-in: Unauthorised"),
        ["login_answer_that_is_not_json"] = LoginDeviations.Refused("AliceBlue sent an answer that could not be read. Try again in a moment.", 502),
        ["login_answer_that_is_not_an_object"] = LoginDeviations.Refused("AliceBlue sent an answer that could not be read. Try again in a moment.", 502),
    };

    public static IEnumerable<object[]> Names() => ContractRunner.Names("login_aliceblue.fixtures.json");

    [PostgresTheory]
    [MemberData(nameof(Names))]
    public Task Matches_the_python_implementation(string name) => ContractRunner.RunAsync(host, name, Deviations, output);
}

/// <summary>
/// AliceBlue quotes, depth and several quotes against the Python client (tools/contract-fixtures/feed_snap_aliceblue.fixtures.json): the real plugin and the real market-data socket class, with a transport that answers each subscribe with the
/// frames the recording scripted. The frames sent, the sockets asked for and the answer must match what OpenAlgo's own client did; an error has the same cause here and is worded for a trader.
/// </summary>
public sealed class AliceBlueSnapshotContractTests
{
    private const string File = "feed_snap_aliceblue.fixtures.json";

    public static IEnumerable<object[]> Names() => FeedRunner.Names(File);

    private sealed class Options(EPTradeBot.Core.Configuration.BrokerOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<EPTradeBot.Core.Configuration.BrokerOptions>
    {
        public EPTradeBot.Core.Configuration.BrokerOptions CurrentValue => value;

        public EPTradeBot.Core.Configuration.BrokerOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<EPTradeBot.Core.Configuration.BrokerOptions, string?> listener) => null;
    }

    private sealed class Master(IReadOnlyList<EPTradeBot.Core.Brokers.SymbolRecord> rows) : EPTradeBot.Core.Brokers.ISymbolMaster
    {
        public Task<EPTradeBot.Core.Brokers.SymbolRecord?> FindAsync(string broker, string symbol, string exchange, CancellationToken cancellationToken) =>
            Task.FromResult(rows.FirstOrDefault(r => r.Symbol == symbol && r.Exchange == exchange));

        public Task<EPTradeBot.Core.Brokers.SymbolRecord?> FindByBrokerSymbolAsync(string broker, string brokerSymbol, string exchange, CancellationToken cancellationToken) => Task.FromResult<EPTradeBot.Core.Brokers.SymbolRecord?>(null);

        public Task<EPTradeBot.Core.Brokers.SymbolRecord?> FindByTokenAsync(string broker, string token, string exchange, CancellationToken cancellationToken) => Task.FromResult<EPTradeBot.Core.Brokers.SymbolRecord?>(null);
    }

    /// <summary>The Python driver's sockets: one is kept, a forced request makes a new one (and counts), and each subscribe is answered with the next scripted frames.</summary>
    private sealed class Script(JsonObject step) : EPTradeBot.Infrastructure.Brokers.AliceBlue.IAliceBlueSockets
    {
        private readonly Queue<JsonArray> frames = new(step["attempts"]!.AsArray().Select(a => a!.AsArray()));
        private readonly bool firstDown = step["first_socket_down"]?.GetValue<bool>() ?? false;
        private readonly bool none = step["no_socket"]?.GetValue<bool>() ?? false;
        private AliceBlueMarketSocket? held;

        public List<JsonNode> Sent { get; } = [];

        public int Forced { get; private set; }

        private AliceBlueMarketSocket Make(bool connected)
        {
            AliceBlueMarketSocket? socket = null;
            socket = new AliceBlueMarketSocket(text =>
            {
                var frame = JsonNode.Parse(text)!;
                Sent.Add(frame);
                if ((string?)frame["t"] is not ("t" or "d")) return;
                foreach (var item in frames.Count > 0 ? frames.Dequeue() : []) socket!.OnMessage(item is JsonValue v && v.TryGetValue<string>(out var raw) ? raw : item!.ToJsonString());
            }) { Connected = connected };
            held = socket;
            return socket;
        }

        public Task<AliceBlueMarketSocket?> GetAsync(EPTradeBot.Core.Brokers.BrokerSession session, bool forceNew, CancellationToken cancellationToken)
        {
            if (forceNew)
            {
                Forced++;
                return Task.FromResult(none ? null : Make(true));
            }

            if (none) return Task.FromResult<AliceBlueMarketSocket?>(null);
            return Task.FromResult<AliceBlueMarketSocket?>(held ?? Make(!firstDown));
        }
    }

    private static string Reason(string python) => python switch
    {
        var p when p.Contains("No quote data received", StringComparison.Ordinal) => "AliceBlue did not send a quote for",
        var p when p.Contains("No market depth received", StringComparison.Ordinal) => "AliceBlue did not send the market depth for",
        var p when p.Contains("WebSocket connection unavailable", StringComparison.Ordinal) => "AliceBlue market data could not be reached",
        _ => python,
    };

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Matches_the_python_client(string name)
    {
        var fixtures = BrokerContractHost.Load(File);
        var rows = fixtures["symbols"]!.AsArray().OfType<JsonObject>().Select(s => new EPTradeBot.Core.Brokers.SymbolRecord(s["symbol"]!.GetValue<string>(), s["brsymbol"]!.GetValue<string>(), s["exchange"]!.GetValue<string>(), s["brexchange"]!.GetValue<string>(),
            s["token"]!.GetValue<string>(), s["name"]?.GetValue<string>(), null, null, 1, s["instrumenttype"]?.GetValue<string>(), 0.05)).ToList();
        var options = new EPTradeBot.Core.Configuration.BrokerOptions();
        options.RetryBackoffMs["aliceblue"] = 0;
        var session = new EPTradeBot.Core.Brokers.BrokerSession(Guid.NewGuid(), "aliceblue", fixtures["access_token"]!.GetValue<string>(), null, fixtures["client_id"]!.GetValue<string>(), new Dictionary<string, string>());
        var sessions = new EPTradeBot.Infrastructure.Brokers.BrokerSessionFactory(null!, new Options(options));
        var scenario = fixtures["scenarios"]!.AsArray().OfType<JsonObject>().Single(s => s["name"]!.GetValue<string>() == name);
        // The sockets outlive a step (a second quote in a row reuses the first's socket), so one script serves the scenario.
        Script? sockets = null;
        var index = 0;
        foreach (var entry in scenario["steps"]!.AsArray().OfType<JsonObject>())
        {
            var step = entry["step"]!.AsObject();
            var kind = step.First().Key;
            var inner = step[kind]!.AsObject();
            var result = entry["result"]!.AsObject();
            var label = $"{name} step {++index} ({kind})";
            sockets = new Script(inner);
            var plugin = new EPTradeBot.Infrastructure.Brokers.AliceBlue.AliceBluePlugin(null!, sessions, new Master(rows), TimeProvider.System, sockets);
            JsonNode? actual = null;
            string? error = null;
            try
            {
                actual = kind switch
                {
                    "quote" => await plugin.GetQuoteAsync(session, inner["symbol"]!.GetValue<string>(), inner["exchange"]!.GetValue<string>(), CancellationToken.None),
                    "depth" => await plugin.GetDepthAsync(session, inner["symbol"]!.GetValue<string>(), inner["exchange"]!.GetValue<string>(), CancellationToken.None),
                    _ => await plugin.GetMultiQuotesAsync(session, inner["symbols"]!.AsArray().OfType<JsonObject>().Select(s => rows.FirstOrDefault(r => r.Symbol == s["symbol"]!.GetValue<string>() && r.Exchange == s["exchange"]!.GetValue<string>())).OfType<EPTradeBot.Core.Brokers.SymbolRecord>().ToList(), CancellationToken.None),
                };
            }
            catch (EPTradeBot.Core.Brokers.BrokerException ex)
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
                var expected = result["result"]!.DeepClone();
                // A symbol the master does not know is refused by the platform before this plugin is asked.
                if (kind == "multiquotes") expected = new JsonArray(expected.AsArray().Where(i => (string?)i!["error"] != "Could not resolve token").Select(i => i!.DeepClone()).ToArray());
                var diffs = JsonCompare.Differences(expected, actual);
                Assert.True(diffs.Count == 0, $"{label}: the answer differs:\n{string.Join("\n", diffs)}");
            }

            var expectedSent = result["sent"]!.AsArray();
            Assert.True(expectedSent.Count == sockets.Sent.Count, $"{label}: expected {expectedSent.Count} frames, got {sockets.Sent.Count}: {string.Join(" | ", sockets.Sent.Select(f => f.ToJsonString()))}");
            for (var i = 0; i < expectedSent.Count; i++)
            {
                var diffs = JsonCompare.Differences(expectedSent[i], sockets.Sent[i]);
                Assert.True(diffs.Count == 0, $"{label}: frame {i} differs:\n{string.Join("\n", diffs)}");
            }

            Assert.Equal(result["forced"]!.GetValue<int>(), sockets.Forced);
        }
    }
}

/// <summary>The real socket pool against a local market-data server: the session is made over REST, the connect frame is sent and answered, a subscribe is answered with a tick, and the socket is given back with an unsubscribe.</summary>
public sealed class AliceBlueSocketPoolTests
{
    private sealed class Options(EPTradeBot.Core.Configuration.BrokerOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<EPTradeBot.Core.Configuration.BrokerOptions>
    {
        public EPTradeBot.Core.Configuration.BrokerOptions CurrentValue => value;

        public EPTradeBot.Core.Configuration.BrokerOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<EPTradeBot.Core.Configuration.BrokerOptions, string?> listener) => null;
    }

    private sealed class Rest : IHttpClientFactory
    {
        public List<string> Paths { get; } = [];

        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler(Rest owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Paths.Add(request.RequestUri!.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"status":"Ok"}""", System.Text.Encoding.UTF8, "application/json") });
            }
        }
    }

    private sealed class Master : EPTradeBot.Core.Brokers.ISymbolMaster
    {
        public Task<EPTradeBot.Core.Brokers.SymbolRecord?> FindAsync(string broker, string symbol, string exchange, CancellationToken cancellationToken) =>
            Task.FromResult<EPTradeBot.Core.Brokers.SymbolRecord?>(new(symbol, symbol + "-EQ", exchange, exchange, "3045", null, null, null, 1, "EQ", 0.05));

        public Task<EPTradeBot.Core.Brokers.SymbolRecord?> FindByBrokerSymbolAsync(string broker, string brokerSymbol, string exchange, CancellationToken cancellationToken) => Task.FromResult<EPTradeBot.Core.Brokers.SymbolRecord?>(null);

        public Task<EPTradeBot.Core.Brokers.SymbolRecord?> FindByTokenAsync(string broker, string token, string exchange, CancellationToken cancellationToken) => Task.FromResult<EPTradeBot.Core.Brokers.SymbolRecord?>(null);
    }

    /// <summary>A server that answers the connect frame, answers a tick subscribe with a quote and records everything it was sent.</summary>
    private static async Task ServeAsync(System.Net.HttpListener listener, List<string> received, CancellationToken stop)
    {
        var context = await listener.GetContextAsync();
        var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
        var buffer = new byte[8192];
        while (!stop.IsCancellationRequested && socket.State == System.Net.WebSockets.WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, stop);
            if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
            lock (received) received.Add(text);
            var frame = JsonNode.Parse(text)!;
            string? reply = (string?)frame["t"] switch
            {
                "c" => """{"t":"ck","s":"OK"}""",
                "t" => """{"t":"tk","e":"NSE","tk":"3045","lp":"805.45","o":"801","h":"809.5","l":"799","c":"800","v":"1000","oi":"5","bp1":"805.4","sp1":"805.5"}""",
                _ => null,
            };
            if (reply is not null) await socket.SendAsync(System.Text.Encoding.UTF8.GetBytes(reply), System.Net.WebSockets.WebSocketMessageType.Text, true, stop);
        }
    }

    [Fact]
    public async Task A_quote_is_read_from_a_real_socket()
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
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = ServeAsync(listener, received, stop.Token);
        var options = new EPTradeBot.Core.Configuration.BrokerOptions();
        options.FeedUrls["aliceblue"] = $"ws://127.0.0.1:{port}/";
        var rest = new Rest();
        var plugin = new EPTradeBot.Infrastructure.Brokers.AliceBlue.AliceBluePlugin(rest, new EPTradeBot.Infrastructure.Brokers.BrokerSessionFactory(null!, new Options(options)), new Master(), TimeProvider.System);
        var session = new EPTradeBot.Core.Brokers.BrokerSession(Guid.NewGuid(), "aliceblue", "ab-session-pool", null, "AB1234", new Dictionary<string, string>());

        var quote = await plugin.GetQuoteAsync(session, "SBIN", "NSE", stop.Token);

        Assert.Equal(805.45, (double)quote["ltp"]!);
        Assert.Equal(805.4, (double)quote["bid"]!);
        Assert.Equal(["/open-api/od/v1/profile/invalidateWsSess", "/open-api/od/v1/profile/createWsSess"], rest.Paths.ToArray());
        await Task.Delay(300);
        string[] frames;
        lock (received) frames = received.ToArray();
        var connect = JsonNode.Parse(frames[0])!;
        Assert.Equal("c", (string?)connect["t"]);
        Assert.Equal("AB1234_API", (string?)connect["uid"]);
        Assert.Equal("t,u", string.Join(',', frames.Skip(1).Select(f => (string?)JsonNode.Parse(f)!["t"])));
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
    public async Task A_second_read_uses_the_same_socket_and_asks_for_no_new_session()
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
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = ServeAsync(listener, received, stop.Token);
        var options = new EPTradeBot.Core.Configuration.BrokerOptions();
        options.FeedUrls["aliceblue"] = $"ws://127.0.0.1:{port}/";
        var rest = new Rest();
        var plugin = new EPTradeBot.Infrastructure.Brokers.AliceBlue.AliceBluePlugin(rest, new EPTradeBot.Infrastructure.Brokers.BrokerSessionFactory(null!, new Options(options)), new Master(), TimeProvider.System);
        var session = new EPTradeBot.Core.Brokers.BrokerSession(Guid.NewGuid(), "aliceblue", "ab-session-pool-2", null, "AB1234", new Dictionary<string, string>());

        await plugin.GetQuoteAsync(session, "SBIN", "NSE", stop.Token);
        await plugin.GetQuoteAsync(session, "SBIN", "NSE", stop.Token);

        Assert.Equal(2, rest.Paths.Count);
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
    public async Task No_session_from_the_broker_means_no_socket()
    {
        var rest = new Rest();
        var options = new EPTradeBot.Core.Configuration.BrokerOptions();
        options.RetryBackoffMs["aliceblue"] = 0;
        var plugin = new EPTradeBot.Infrastructure.Brokers.AliceBlue.AliceBluePlugin(new Rest(), new EPTradeBot.Infrastructure.Brokers.BrokerSessionFactory(null!, new Options(options)), new Master(), TimeProvider.System, new Nothing());
        var session = new EPTradeBot.Core.Brokers.BrokerSession(Guid.NewGuid(), "aliceblue", "x", null, "AB1234", new Dictionary<string, string>());
        var error = await Assert.ThrowsAsync<EPTradeBot.Core.Brokers.BrokerException>(() => plugin.GetDepthAsync(session, "SBIN", "NSE", CancellationToken.None));
        Assert.Contains("could not be reached", error.Message, StringComparison.Ordinal);
        _ = rest;
    }

    private sealed class Nothing : EPTradeBot.Infrastructure.Brokers.AliceBlue.IAliceBlueSockets
    {
        public Task<AliceBlueMarketSocket?> GetAsync(EPTradeBot.Core.Brokers.BrokerSession session, bool forceNew, CancellationToken cancellationToken) => Task.FromResult<AliceBlueMarketSocket?>(null);
    }
}
