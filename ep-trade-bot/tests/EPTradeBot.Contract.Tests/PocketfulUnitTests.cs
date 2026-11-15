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
}
