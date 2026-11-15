using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming;
using EPTradeBot.Core.Streaming.Feeds;
using EPTradeBot.Infrastructure.Brokers.Pocketful;

namespace EPTradeBot.Contract.Tests;

/// <summary>What the recorded cases cannot show: a session with no client id, the sign-in's setup refusals, the address and heartbeat of the feed, and the real feed connection against a local server.</summary>
public sealed class PocketfulUnitTests
{
    private sealed class Options(EPTradeBot.Core.Configuration.BrokerOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<EPTradeBot.Core.Configuration.BrokerOptions>
    {
        public EPTradeBot.Core.Configuration.BrokerOptions CurrentValue => value;

        public EPTradeBot.Core.Configuration.BrokerOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<EPTradeBot.Core.Configuration.BrokerOptions, string?> listener) => null;
    }

    private sealed class Master : ISymbolMaster
    {
        public Task<SymbolRecord?> FindAsync(string broker, string symbol, string exchange, CancellationToken cancellationToken) => Task.FromResult<SymbolRecord?>(new(symbol, symbol + "-EQ", exchange, exchange, "3045", null, null, null, 1, "EQ", 0.05));

        public Task<SymbolRecord?> FindByBrokerSymbolAsync(string broker, string brokerSymbol, string exchange, CancellationToken cancellationToken) => Task.FromResult<SymbolRecord?>(null);

        public Task<SymbolRecord?> FindByTokenAsync(string broker, string token, string exchange, CancellationToken cancellationToken) => Task.FromResult<SymbolRecord?>(null);
    }

    private sealed class Capture(Func<HttpRequestMessage, HttpResponseMessage> answer) : IHttpClientFactory
    {
        public List<(string Method, string Url)> Seen { get; } = [];

        public HttpClient CreateClient(string name) => new(new Handler(this, answer));

        private sealed class Handler(Capture owner, Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Seen.Add((request.Method.Method, request.RequestUri!.PathAndQuery));
                return Task.FromResult(answer(request));
            }
        }
    }

    private static HttpResponseMessage Json(string body, int status = 200) => new((System.Net.HttpStatusCode)status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static PocketfulPlugin Plugin(IHttpClientFactory http, EPTradeBot.Core.Configuration.BrokerOptions? options = null) =>
        new(http, new EPTradeBot.Infrastructure.Brokers.BrokerSessionFactory(null!, new Options(options ?? new EPTradeBot.Core.Configuration.BrokerOptions())), new Master(), TimeProvider.System);

    private static BrokerSession Session(string? clientId = null, string token = "tok") => new(Guid.NewGuid(), "pocketful", token, null, clientId, new Dictionary<string, string>());

    private static readonly BrokerOrder Order = new("SBIN", "NSE", "BUY", 1, "MARKET", "MIS", 0m, 0m, 0, "s");

    [Fact]
    public async Task A_session_with_no_client_id_asks_for_it_first_and_uses_it_in_the_call()
    {
        var http = new Capture(r => r.RequestUri!.AbsolutePath.EndsWith("trading_info", StringComparison.Ordinal) ? Json("""{"status":"success","data":{"client_id":"PF9"}}""") : Json("""{"status":"success","data":{"oms_order_id":"1"}}"""));
        var result = await Plugin(http).CancelAsync(Session(), "77", CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Equal([("GET", "/api/v1/user/trading_info"), ("DELETE", "/api/v1/orders/77?client_id=PF9")], http.Seen);
    }

    [Fact]
    public async Task A_session_with_a_client_id_does_not_ask_for_it()
    {
        var http = new Capture(_ => Json("""{"status":"success","data":{"oms_order_id":"1"}}"""));
        await Plugin(http).CancelAsync(Session("PF1"), "77", CancellationToken.None);
        Assert.Equal([("DELETE", "/api/v1/orders/77?client_id=PF1")], http.Seen);
    }

    [Theory]
    [InlineData("""{"status":"error","message":"Session expired"}""", "Session expired")]
    [InlineData("""{"status":"success","data":{}}""", "no reason was given")]
    public async Task An_order_is_not_sent_when_the_client_id_cannot_be_had(string answer, string reason)
    {
        var http = new Capture(_ => Json(answer));
        var result = await Plugin(http).PlaceAsync(Session(), Order, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains(reason, result.Message, StringComparison.Ordinal);
        Assert.Equal([("GET", "/api/v1/user/trading_info")], http.Seen);
    }

    [Fact]
    public async Task A_call_that_cannot_reach_pocketful_says_so()
    {
        var plugin = Plugin(new Failing());
        var error = await Assert.ThrowsAsync<BrokerException>(() => plugin.GetFundsAsync(Session("PF1"), CancellationToken.None));
        Assert.Equal(502, error.HttpStatus);
        var placed = await plugin.PlaceAsync(Session("PF1"), Order, CancellationToken.None);
        Assert.True(placed.MayHaveBeenSent);
    }

    private sealed class Failing : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Thrower());

        private sealed class Thrower : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new HttpRequestException("down");
        }
    }

    // ---- sign-in ---------------------------------------------------------------------------------------------

    private static readonly IReadOnlyDictionary<string, string> AllKeys = new Dictionary<string, string> { ["ApiKey"] = "key", ["ApiSecret"] = "secret", ["RedirectUrl"] = "http://127.0.0.1:5000/pocketful/callback" };

    [Theory]
    [InlineData("ApiKey")]
    [InlineData("ApiSecret")]
    [InlineData("RedirectUrl")]
    public async Task A_sign_in_with_a_key_missing_is_refused_before_anything_is_sent(string missing)
    {
        var http = new Capture(_ => Json("{}"));
        var keys = AllKeys.Where(k => k.Key != missing).ToDictionary(k => k.Key, k => k.Value);
        var result = await Plugin(http).LoginAsync(keys, new Dictionary<string, string> { ["code"] = "c" }, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal(409, result.HttpStatus);
        Assert.Empty(http.Seen);
    }

    [Fact]
    public async Task A_sign_in_that_came_back_with_no_code_is_refused()
    {
        var http = new Capture(_ => Json("{}"));
        var result = await Plugin(http).LoginAsync(AllKeys, new Dictionary<string, string>(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Empty(http.Seen);
    }

    [Fact]
    public void The_sign_in_page_carries_the_key_the_redirect_the_scope_and_the_state()
    {
        var url = Plugin(new Capture(_ => Json("{}"))).LoginUrl(AllKeys, "st 1");
        Assert.Equal("https://trade.pocketful.in/oauth2/auth?client_id=key&redirect_uri=http%3A%2F%2F127.0.0.1%3A5000%2Fpocketful%2Fcallback&response_type=code&scope=orders%20holdings&state=st%201", url);
        Assert.Null(Plugin(new Capture(_ => Json("{}"))).LoginUrl(new Dictionary<string, string> { ["ApiKey"] = "key" }, "s"));
    }

    // ---- feed ------------------------------------------------------------------------------------------------

    [Fact]
    public void The_feed_address_carries_the_client_id_and_the_token_and_is_not_given_without_either()
    {
        var plugin = Plugin(new Capture(_ => Json("{}")));
        Assert.Equal("wss://trade.pocketful.in/ws/v1/feeds?login_id=PF1&access_token=tok", plugin.FeedEndpoint(Session("PF1"), null)!.Url.AbsoluteUri);
        Assert.Equal("wss://other/ws/v1/feeds?login_id=PF1&access_token=tok", plugin.FeedEndpoint(Session("PF1"), "wss://other")!.Url.AbsoluteUri);
        Assert.Null(plugin.FeedEndpoint(Session(), null));
        Assert.Null(plugin.FeedEndpoint(Session("PF1", " "), null));
        Assert.Equal("""{"a": "h"}""", plugin.FeedKeepAliveText);
        Assert.Equal(TimeSpan.FromSeconds(15), plugin.FeedKeepAliveEvery);
    }

    [Fact]
    public void The_codec_is_a_new_one_for_each_connection_and_names_its_broker()
    {
        var plugin = Plugin(new Capture(_ => Json("{}")));
        Assert.NotSame(plugin.CreateFeedCodec(), plugin.CreateFeedCodec());
        Assert.Equal("pocketful", new PocketfulFeedCodec().Broker);
    }

    [Fact]
    public void Unsubscribing_with_no_mode_ends_every_mode_the_symbol_holds()
    {
        var codec = new PocketfulFeedCodec();
        codec.Subscribe([new FeedRequest("NSE", "SBIN", "3045", "NSE", StreamMode.Ltp), new FeedRequest("NSE", "SBIN", "3045", "NSE", StreamMode.Depth), new FeedRequest("NSE", "INFY", "1594", "NSE", StreamMode.Quote)], 0);
        var frames = codec.Unsubscribe(new FeedTarget("NSE", "SBIN", "3045", "NSE"), null, 0);
        Assert.Equal(["""{"a":"unsubscribe","v":[[1,3045]],"m":"compact_marketdata"}""", """{"a":"unsubscribe","v":[[1,3045]],"m":"full_snapquote"}"""], frames);
        Assert.Empty(codec.Unsubscribe(new FeedTarget("NSE", "SBIN", "3045", "NSE"), null, 0));
        Assert.Single(codec.Resubscribe(0));
    }

    [Fact]
    public void Reset_forgets_every_subscription()
    {
        var codec = new PocketfulFeedCodec();
        codec.Subscribe([new FeedRequest("NSE", "SBIN", "3045", "NSE", StreamMode.Quote)], 0);
        codec.Reset();
        Assert.Empty(codec.Resubscribe(0));
    }

    // ---- the real connection ----------------------------------------------------------------------------------

    private static (System.Net.HttpListener Listener, int Port) Listen()
    {
        int port;
        using (var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }

        var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        return (listener, port);
    }

    private static byte[] DetailedPacket(uint token)
    {
        var packet = new byte[102];
        packet[0] = 1;
        packet[1] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(2), token);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(6), 80545);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(74), 80000);
        return packet;
    }

    [Fact]
    public async Task A_quote_is_read_from_a_real_socket_and_the_subscription_is_ended()
    {
        var (listener, port) = Listen();
        using var _ = listener;
        var received = new List<string>();
        string? query = null;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            query = context.Request.Url!.Query;
            var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            var buffer = new byte[8192];
            var first = await socket.ReceiveAsync(buffer, stop.Token);
            lock (received) received.Add(System.Text.Encoding.UTF8.GetString(buffer, 0, first.Count));
            await socket.SendAsync(System.Text.Encoding.UTF8.GetBytes("welcome"), System.Net.WebSockets.WebSocketMessageType.Text, true, stop.Token);
            await socket.SendAsync(DetailedPacket(3045), System.Net.WebSockets.WebSocketMessageType.Binary, true, stop.Token);
            var second = await socket.ReceiveAsync(buffer, stop.Token);
            lock (received) received.Add(System.Text.Encoding.UTF8.GetString(buffer, 0, second.Count));
        });

        var options = new EPTradeBot.Core.Configuration.BrokerOptions();
        options.FeedUrls["pocketful"] = $"ws://127.0.0.1:{port}";
        var quote = await Plugin(new Capture(_ => Json("{}")), options).GetQuoteAsync(Session("PF1"), "SBIN", "NSE", CancellationToken.None);
        await server.WaitAsync(stop.Token);
        Assert.Equal(805.45, (double)quote["ltp"]!, 6);
        Assert.Equal("?login_id=PF1&access_token=tok", query);
        Assert.Equal("""{"a":"subscribe","v":[[1,3045]],"m":"marketdata"}""", received[0]);
        Assert.Equal("""{"a":"unsubscribe","v":[[1,3045]],"m":"marketdata"}""", received[1]);
    }

    [Fact]
    public async Task A_connection_that_cannot_be_made_is_an_error_that_says_what_to_do()
    {
        var (listener, port) = Listen();
        listener.Stop();
        listener.Close();
        var options = new EPTradeBot.Core.Configuration.BrokerOptions();
        options.FeedUrls["pocketful"] = $"ws://127.0.0.1:{port}";
        var error = await Assert.ThrowsAsync<BrokerException>(() => Plugin(new Capture(_ => Json("{}")), options).GetQuoteAsync(Session("PF1"), "SBIN", "NSE", CancellationToken.None));
        Assert.Contains("did not open its market data connection", error.Message, StringComparison.Ordinal);
    }

    // ---- packets: every field at its own place ----------------------------------------------------------------

    private static byte[] Numbered(int length, int mode)
    {
        // Each 4-byte word after the header holds its own number, so a field read from the wrong place shows.
        var bytes = new byte[length];
        bytes[0] = (byte)mode;
        bytes[1] = 3;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(2), 77);
        for (var at = 6; at + 4 <= length; at += 4) System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at), (uint)(1000 + at));
        return bytes;
    }

    [Fact]
    public void A_detailed_packet_is_read_field_by_field()
    {
        var r = PocketfulPackets.DecodeDetailed(Numbered(102, 1));
        var expected = new Dictionary<string, long>
        {
            ["last_traded_price"] = 1006, ["last_traded_time"] = 1010, ["last_traded_quantity"] = 1014, ["trade_volume"] = 1018, ["best_bid_price"] = 1022, ["best_bid_quantity"] = 1026, ["best_ask_price"] = 1030, ["best_ask_quantity"] = 1034,
            ["average_trade_price"] = 1054, ["exchange_timestamp"] = 1058, ["open_price"] = 1062, ["high_price"] = 1066, ["low_price"] = 1070, ["close_price"] = 1074, ["yearly_high_price"] = 1078, ["yearly_low_price"] = 1082,
            ["lowDPR"] = 1086, ["highDPR"] = 1090, ["currentOpenInterest"] = 1094, ["initialOpenInterest"] = 1098, ["instrument_token"] = 77, ["exchange_code"] = 3, ["mode"] = 1,
        };
        foreach (var (key, value) in expected) Assert.Equal(value, (long)r[key]!);
        Assert.Equal((1038UL << 32) | 1042UL, (ulong)r["total_buy_quantity"]!);
        Assert.Equal((1046UL << 32) | 1050UL, (ulong)r["total_sell_quantity"]!);
        Assert.Empty(PocketfulPackets.DecodeDetailed(Numbered(101, 1)));
    }

    [Fact]
    public void A_compact_packet_is_read_field_by_field()
    {
        var r = PocketfulPackets.DecodeCompact(Numbered(42, 2));
        var expected = new Dictionary<string, long> { ["last_traded_price"] = 1006, ["change"] = 1010, ["last_traded_time"] = 1014, ["lowDPR"] = 1018, ["highDPR"] = 1022, ["currentOpenInterest"] = 1026, ["initialOpenInterest"] = 1030, ["bidPrice"] = 1034, ["askPrice"] = 1038, ["mode"] = 2 };
        foreach (var (key, value) in expected) Assert.Equal(value, (long)r[key]!);
        Assert.Empty(PocketfulPackets.DecodeCompact(Numbered(41, 2)));
    }

    [Fact]
    public void A_snapquote_packet_is_read_field_by_field_and_its_ladders_are_five_deep()
    {
        var r = PocketfulPackets.DecodeSnapquote(Numbered(166, 4));
        foreach (var (key, start) in new[] { ("buyers", 6), ("bidPrices", 26), ("bidQtys", 46), ("sellers", 66), ("askPrices", 86), ("askQtys", 106) })
        {
            var values = ((JsonArray)r[key]!).Select(v => (long)v!).ToArray();
            Assert.Equal(Enumerable.Range(0, 5).Select(i => (long)(1000 + start + 4 * i)).ToArray(), values);
        }

        foreach (var (key, value) in new Dictionary<string, long> { ["averageTradePrice"] = 1126, ["open"] = 1130, ["high"] = 1134, ["low"] = 1138, ["close"] = 1142, ["volume"] = 1162 }) Assert.Equal(value, (long)r[key]!);
        Assert.Equal((1146UL << 32) | 1150UL, (ulong)r["totalBuyQty"]!);
        Assert.Equal((1154UL << 32) | 1158UL, (ulong)r["totalSellQty"]!);
        Assert.Empty(PocketfulPackets.DecodeSnapquote(Numbered(165, 4)));
    }

    [Fact]
    public void A_text_packet_keeps_the_names_it_came_with_and_borrows_the_other_ones_only_when_it_has_none()
    {
        var both = PocketfulPackets.DecodeCompact("""{"instrument_token":1,"instrumentToken":2,"exchange_code":3,"exchangeCode":4}""");
        Assert.Equal(1, (int)both["instrument_token"]!);
        Assert.Equal(3, (int)both["exchange_code"]!);
        var other = PocketfulPackets.DecodeSnapquote("""{"instrumentToken":2,"exchangeCode":4}""");
        Assert.Equal(2, (int)other["instrument_token"]!);
        Assert.Equal(4, (int)other["exchange_code"]!);
        Assert.Empty(PocketfulPackets.DecodeCompact("""[{"instrument_token":1}]"""));
        Assert.Equal(1, (int)PocketfulPackets.DecodeSnapquote("""[{"instrument_token":1}]""")["instrument_token"]!);
    }

    // ---- waits, exchange codes and what a failed call says -----------------------------------------------------

    private sealed class Capturing : IPocketfulSockets
    {
        public List<TimeSpan> Waits { get; } = [];

        public List<bool> Finished { get; } = [];

        public byte[][] Frames { get; set; } = [];

        public Task<bool> RunAsync(Uri address, IReadOnlyList<string> subscribes, IReadOnlyList<string> unsubscribes, Func<PocketfulFrame, bool> onFrame, TimeSpan wait, CancellationToken cancellationToken)
        {
            Waits.Add(wait);
            foreach (var frame in Frames) Finished.Add(onFrame(PocketfulFrame.Of(frame)));
            return Task.FromResult(true);
        }
    }

    private sealed class ManyMaster : ISymbolMaster
    {
        public Task<SymbolRecord?> FindAsync(string broker, string symbol, string exchange, CancellationToken cancellationToken) => Task.FromResult<SymbolRecord?>(new(symbol, symbol, exchange, exchange, "3045", null, null, null, 1, "EQ", 0.05));

        public Task<SymbolRecord?> FindByBrokerSymbolAsync(string broker, string brokerSymbol, string exchange, CancellationToken cancellationToken) => Task.FromResult<SymbolRecord?>(null);

        public Task<SymbolRecord?> FindByTokenAsync(string broker, string token, string exchange, CancellationToken cancellationToken) => Task.FromResult<SymbolRecord?>(null);
    }

    private static PocketfulPlugin WithSockets(IPocketfulSockets sockets) =>
        new(new Capture(_ => Json("{}")), new EPTradeBot.Infrastructure.Brokers.BrokerSessionFactory(null!, new Options(new EPTradeBot.Core.Configuration.BrokerOptions())), new ManyMaster(), TimeProvider.System, sockets);

    private static SymbolRecord Record(int i) => new($"S{i}", $"S{i}", "NSE", "NSE", (1000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), null, null, null, 1, "EQ", 0.05);

    [Fact]
    public async Task A_quote_waits_ten_seconds_depth_fifteen_and_several_quotes_half_a_second_each_between_three_and_fifteen()
    {
        var sockets = new Capturing();
        var plugin = WithSockets(sockets);
        await Assert.ThrowsAsync<BrokerException>(() => plugin.GetQuoteAsync(Session("PF1"), "SBIN", "NSE", CancellationToken.None));
        await Assert.ThrowsAsync<BrokerException>(() => plugin.GetDepthAsync(Session("PF1"), "SBIN", "NSE", CancellationToken.None));
        foreach (var n in new[] { 1, 6, 7, 40 }) await plugin.GetMultiQuotesAsync(Session("PF1"), Enumerable.Range(0, n).Select(Record).ToList(), CancellationToken.None);
        Assert.Equal([10.0, 15.0, 3.0, 3.0, 3.5, 15.0], sockets.Waits.Select(w => w.TotalSeconds));
    }

    [Fact]
    public async Task Several_quotes_are_read_fifty_at_a_time()
    {
        var sockets = new Capturing();
        await WithSockets(sockets).GetMultiQuotesAsync(Session("PF1"), Enumerable.Range(0, 100).Select(Record).ToList(), CancellationToken.None);
        Assert.Equal(2, sockets.Waits.Count);
        sockets.Waits.Clear();
        await WithSockets(sockets).GetMultiQuotesAsync(Session("PF1"), Enumerable.Range(0, 101).Select(Record).ToList(), CancellationToken.None);
        Assert.Equal(3, sockets.Waits.Count);
    }

    [Fact]
    public async Task The_read_of_several_quotes_is_finished_the_moment_every_instrument_has_come()
    {
        var sockets = new Capturing { Frames = [DetailedPacket(1000), DetailedPacket(1001)] };
        var results = await WithSockets(sockets).GetMultiQuotesAsync(Session("PF1"), [Record(0), Record(1)], CancellationToken.None);
        Assert.Equal([false, true], sockets.Finished);
        Assert.All(results.OfType<JsonObject>(), r => Assert.NotNull(r["data"]));
    }

    [Theory]
    [InlineData("NSE", 1)]
    [InlineData("NFO", 2)]
    [InlineData("CDS", 3)]
    [InlineData("MCX", 4)]
    [InlineData("BSE", 6)]
    [InlineData("BFO", 7)]
    [InlineData("NSE_INDEX", 1)]
    [InlineData("BSE_INDEX", 6)]
    [InlineData("SOMETHING", 1)]
    public void An_exchange_goes_to_its_code_on_the_feed(string exchange, int code)
    {
        var frames = new PocketfulFeedCodec().Subscribe([new FeedRequest("NSE", "SBIN", "3045", exchange, StreamMode.Quote)], 0);
        Assert.Equal($"{{\"a\":\"subscribe\",\"v\":[[{code},3045]],\"m\":\"marketdata\"}}", Assert.Single(frames));
    }

    [Fact]
    public async Task A_change_or_a_cancel_that_fails_after_it_was_sent_says_it_may_have_been_made()
    {
        var plugin = Plugin(new Failing());
        var change = await plugin.ModifyAsync(Session("PF1"), new BrokerOrderChange("1", "SBIN", "NSE", "BUY", 1, "LIMIT", "MIS", 1m, 0m, 0), CancellationToken.None);
        var cancel = await plugin.CancelAsync(Session("PF1"), "1", CancellationToken.None);
        var placed = await plugin.PlaceAsync(Session("PF1"), Order, CancellationToken.None);
        foreach (var result in new[] { change, cancel, placed })
        {
            Assert.False(result.Ok);
            Assert.Equal(502, result.HttpStatus);
            Assert.True(result.MayHaveBeenSent);
        }
    }

    [Fact]
    public async Task A_sign_in_with_a_blank_key_or_code_is_refused_and_one_that_cannot_reach_pocketful_says_so()
    {
        var http = new Capture(_ => Json("{}"));
        foreach (var blank in new[] { "ApiKey", "ApiSecret", "RedirectUrl" })
        {
            var keys = AllKeys.ToDictionary(k => k.Key, k => k.Key == blank ? " " : k.Value);
            Assert.Equal(409, (await Plugin(http).LoginAsync(keys, new Dictionary<string, string> { ["code"] = "c" }, CancellationToken.None)).HttpStatus);
        }

        Assert.False((await Plugin(http).LoginAsync(AllKeys, new Dictionary<string, string> { ["code"] = " " }, CancellationToken.None)).Ok);
        Assert.Empty(http.Seen);
        var down = await Plugin(new Failing()).LoginAsync(AllKeys, new Dictionary<string, string> { ["code"] = "c" }, CancellationToken.None);
        Assert.Equal(502, down.HttpStatus);
        Assert.Contains("Could not reach", down.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unsubscribing_with_no_mode_ends_all_three_modes_in_order()
    {
        var codec = new PocketfulFeedCodec();
        codec.Subscribe([new FeedRequest("NSE", "SBIN", "3045", "NSE", StreamMode.Quote), new FeedRequest("NSE", "SBIN", "3045", "NSE", StreamMode.Ltp), new FeedRequest("NSE", "SBIN", "3045", "NSE", StreamMode.Depth)], 0);
        var frames = codec.Unsubscribe(new FeedTarget("NSE", "SBIN", "3045", "NSE"), null, 0);
        Assert.Equal(["compact_marketdata", "marketdata", "full_snapquote"], frames.Select(f => (string)JsonNode.Parse(f)!["m"]!));
    }
}
