using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming;
using EPTradeBot.Core.Streaming.Feeds;
using EPTradeBot.Infrastructure.Brokers.Tradejini;

namespace EPTradeBot.Contract.Tests;

/// <summary>What the recorded cases cannot show: the headers every call carries, the address of the stream and who may have one, the paths an order can fail on after it was sent, sign-in without a key, and the real stream connection against a local server.</summary>
public sealed class TradejiniUnitTests
{
    private sealed class Options(EPTradeBot.Core.Configuration.BrokerOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<EPTradeBot.Core.Configuration.BrokerOptions>
    {
        public EPTradeBot.Core.Configuration.BrokerOptions CurrentValue => value;

        public EPTradeBot.Core.Configuration.BrokerOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<EPTradeBot.Core.Configuration.BrokerOptions, string?> listener) => null;
    }

    private sealed class Master(bool known = true) : ISymbolMaster
    {
        public Task<SymbolRecord?> FindAsync(string broker, string symbol, string exchange, CancellationToken cancellationToken) =>
            Task.FromResult<SymbolRecord?>(known ? new(symbol, "EQT_" + symbol + "_EQ_" + exchange, exchange, exchange, "3045", null, null, null, 1, "EQ", 0.05) : null);

        public Task<SymbolRecord?> FindByBrokerSymbolAsync(string broker, string brokerSymbol, string exchange, CancellationToken cancellationToken) => Task.FromResult<SymbolRecord?>(null);

        public Task<SymbolRecord?> FindByTokenAsync(string broker, string token, string exchange, CancellationToken cancellationToken) => Task.FromResult<SymbolRecord?>(null);
    }

    private sealed class Capture(Func<HttpRequestMessage, HttpResponseMessage> answer) : IHttpClientFactory
    {
        public List<HttpRequestMessage> Seen { get; } = [];

        public List<string> Bodies { get; } = [];

        public HttpClient CreateClient(string name) => new(new Handler(this, answer));

        private sealed class Handler(Capture owner, Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Seen.Add(request);
                owner.Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
                return answer(request);
            }
        }
    }

    private static HttpResponseMessage Json(string body, int status = 200) => new((System.Net.HttpStatusCode)status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static TradejiniPlugin Plugin(IHttpClientFactory? http = null, bool known = true, ITradejiniSockets? sockets = null, EPTradeBot.Core.Configuration.BrokerOptions? options = null) =>
        new(http!, new EPTradeBot.Infrastructure.Brokers.BrokerSessionFactory(null!, new Options(options ?? new EPTradeBot.Core.Configuration.BrokerOptions())), new Master(known), TimeProvider.System, sockets);

    private static BrokerSession Session(string token = "tj-access", string? key = "tj-key", string? secret = null)
    {
        var keys = new Dictionary<string, string>();
        if (key is not null) keys["ApiKey"] = key;
        if (secret is not null) keys["ApiSecret"] = secret;
        return new(Guid.NewGuid(), "tradejini", token, null, null, keys);
    }

    private static BrokerOrder Order() => new("SBIN", "NSE", "BUY", 1, "MARKET", "MIS", 0m, 0m, 0, "s");

    // ---- calls ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_call_carries_the_api_key_and_the_access_token_as_one_bearer_token()
    {
        var http = new Capture(_ => Json("""{"s":"ok","d":{"orderId":"1"}}"""));
        await Plugin(http).PlaceAsync(Session(), Order(), CancellationToken.None);
        Assert.Equal("Bearer tj-key:tj-access", http.Seen.Single().Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task The_secret_stands_in_for_a_missing_key_and_a_placeholder_is_not_a_key()
    {
        var http = new Capture(_ => Json("""{"s":"ok","d":{"orderId":"1"}}"""));
        await Plugin(http).PlaceAsync(Session(key: "YOUR_API_KEY", secret: "tj-secret"), Order(), CancellationToken.None);
        await Plugin(http).PlaceAsync(Session(key: " ", secret: "tj-secret2"), Order(), CancellationToken.None);
        Assert.Equal("Bearer tj-secret:tj-access", http.Seen[0].Headers.Authorization!.ToString());
        Assert.Equal("Bearer tj-secret2:tj-access", http.Seen[1].Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task A_session_with_no_api_key_is_refused_before_anything_is_sent()
    {
        var http = new Capture(_ => Json("{}"));
        var error = await Assert.ThrowsAsync<BrokerException>(() => Plugin(http).PlaceAsync(Session(key: null), Order(), CancellationToken.None));
        Assert.Equal(409, error.HttpStatus);
        Assert.Empty(http.Seen);
    }

    [Fact]
    public async Task An_order_whose_call_fails_after_it_was_sent_says_it_may_have_been_placed()
    {
        var result = await Plugin(new Capture(_ => throw new HttpRequestException("reset"))).PlaceAsync(Session(), Order(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal(502, result.HttpStatus);
        Assert.True(result.MayHaveBeenSent);
    }

    [Fact]
    public async Task An_order_sends_the_limit_price_as_a_float_and_a_market_protection_on_a_market_order()
    {
        var http = new Capture(_ => Json("""{"s":"ok","d":{"orderId":"1"}}"""));
        await Plugin(http).PlaceAsync(Session(), Order() with { Price = 800m }, CancellationToken.None);
        var form = http.Bodies.Single();
        Assert.Contains("limitPrice=800.0", form, StringComparison.Ordinal);
        Assert.Contains("mktProt=2", form, StringComparison.Ordinal);
        Assert.DoesNotContain("discQty", form, StringComparison.Ordinal);
    }

    // ---- sign-in -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_sign_in_with_no_key_is_refused_with_409()
    {
        var result = await Plugin(new Capture(_ => throw new InvalidOperationException())).LoginAsync(new Dictionary<string, string>(), new Dictionary<string, string> { ["password"] = "p", ["totp"] = "1" }, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal(409, result.HttpStatus);
    }

    [Fact]
    public async Task A_sign_in_that_cannot_reach_tradejini_says_to_check_the_connection()
    {
        var result = await Plugin(new Capture(_ => throw new HttpRequestException("down"))).LoginAsync(new Dictionary<string, string> { ["ApiKey"] = "k" }, new Dictionary<string, string> { ["password"] = "p", ["totp"] = "1" }, CancellationToken.None);
        Assert.Equal(502, result.HttpStatus);
        Assert.Contains("Check your internet connection", result.Message, StringComparison.Ordinal);
    }

    // ---- the stream's address ------------------------------------------------------------------------------------

    [Fact]
    public void The_stream_address_carries_the_token_as_key_colon_token()
    {
        var endpoint = Plugin().FeedEndpoint(Session(), null)!;
        Assert.Equal("wss://api.tradejini.com/v2.1/stream?token=tj-key:tj-access&version=3.1", endpoint.Url.ToString());
    }

    [Fact]
    public void A_token_that_already_holds_a_colon_is_sent_as_it_is() =>
        Assert.Equal("wss://api.tradejini.com/v2.1/stream?token=a:b&version=3.1", Plugin().FeedEndpoint(Session(token: "a:b", key: null), null)!.Url.ToString());

    [Fact]
    public void A_session_with_no_key_and_a_plain_token_has_no_feed() => Assert.Null(Plugin().FeedEndpoint(Session(key: null), null));

    [Fact]
    public void A_session_with_no_token_has_no_feed() => Assert.Null(Plugin().FeedEndpoint(Session(token: " "), null));

    [Fact]
    public void A_configured_address_replaces_the_host() =>
        Assert.StartsWith("wss://feed.example/stream?token=", Plugin().FeedEndpoint(Session(), "wss://feed.example/stream")!.Url.ToString(), StringComparison.Ordinal);

    [Fact]
    public void The_stream_needs_no_keep_alive_text()
    {
        var plugin = Plugin();
        Assert.Null(plugin.FeedKeepAliveText);
        Assert.Equal(TimeSpan.Zero, plugin.FeedKeepAliveEvery);
    }

    [Fact]
    public void The_codec_names_its_broker_and_is_a_new_one_for_each_connection()
    {
        Assert.Equal("tradejini", new TradejiniFeedCodec().Broker);
        Assert.NotSame(Plugin().CreateFeedCodec(), Plugin().CreateFeedCodec());
    }

    [Fact]
    public void Reset_forgets_every_subscription()
    {
        var codec = new TradejiniFeedCodec();
        codec.Subscribe([new FeedRequest("NSE", "SBIN", "3045", "NSE", StreamMode.Quote)], 0);
        codec.Reset();
        Assert.Empty(codec.Resubscribe(0));
    }

    [Fact]
    public void A_mode_the_stream_does_not_have_is_left_out() =>
        Assert.Empty(new TradejiniFeedCodec().Subscribe([new FeedRequest("NSE", "SBIN", "3045", "NSE", (StreamMode)4)], 0));

    [Fact]
    public void A_text_frame_publishes_nothing() => Assert.Empty(new TradejiniFeedCodec().DecodeText("{}", 0));

    [Fact]
    public void Unsubscribing_every_mode_sends_a_request_for_each_feed_it_touched()
    {
        var codec = new TradejiniFeedCodec();
        codec.Subscribe([new FeedRequest("NSE", "SBIN", "3045", "NSE", StreamMode.Quote), new FeedRequest("NSE", "SBIN", "3045", "NSE", StreamMode.Depth)], 0);
        var frames = codec.Unsubscribe(new FeedTarget("NSE", "SBIN", "3045", "NSE"), null, 0);
        Assert.Equal(2, frames.Count);
        Assert.Contains("\"type\":\"L1\"", frames[0], StringComparison.Ordinal);
        Assert.Contains("\"type\":\"L5\"", frames[1], StringComparison.Ordinal);
        Assert.All(frames, f => Assert.Contains("\"action\":\"unsub\"", f, StringComparison.Ordinal));
    }

    // ---- quotes against a real local stream ---------------------------------------------------------------------

    private static byte[] QuoteFrame()
    {
        var fields = new List<byte> { 10 };
        void I(byte key, int value) { fields.Add(key); fields.AddRange(BitConverter.GetBytes(value)); }
        fields.Add(26); fields.Add(1);
        I(27, 3045); I(29, 80545); I(30, 80100); I(31, 80950); I(32, 79900); I(33, 80000);
        fields.Add(40); fields.AddRange(BitConverter.GetBytes(1234u));
        I(49, 80540); I(52, 80550);
        var packet = BitConverter.GetBytes((short)(fields.Count + 2)).Concat(fields).ToArray();
        return BitConverter.GetBytes(packet.Length).Concat(new byte[] { 1, 0 }).Concat(packet).ToArray();
    }

    [Fact]
    public async Task A_quote_is_read_from_a_real_stream()
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
        string? query = null, asked = null;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            query = context.Request.Url!.Query;
            var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            var buffer = new byte[8192];
            var result = await socket.ReceiveAsync(buffer, stop.Token);
            asked = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
            await socket.SendAsync(new byte[] { 9, 9 }, System.Net.WebSockets.WebSocketMessageType.Binary, true, stop.Token);
            await socket.SendAsync(QuoteFrame(), System.Net.WebSockets.WebSocketMessageType.Binary, true, stop.Token);
            try
            {
                await socket.ReceiveAsync(buffer, stop.Token);
            }
            catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or OperationCanceledException)
            {
            }
        });
        var options = new EPTradeBot.Core.Configuration.BrokerOptions();
        options.FeedUrls["tradejini"] = $"ws://127.0.0.1:{port}/stream";
        var quote = await Plugin(options: options).GetQuoteAsync(Session(), "SBIN", "NSE", stop.Token);

        Assert.Equal(805.45, (double)quote["ltp"]!);
        Assert.Equal(805.4, (double)quote["bid"]!);
        Assert.Equal(1234L, (long)quote["volume"]!);
        Assert.Equal("?token=tj-key:tj-access&version=3.1", query);
        Assert.Equal("""{"type":"L1","action":"sub","tokens":[{"t":"3045_NSE"}]}""", asked!.Trim());
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
    public async Task A_quote_from_a_stream_that_is_not_there_says_market_data_could_not_be_reached()
    {
        var options = new EPTradeBot.Core.Configuration.BrokerOptions();
        options.FeedUrls["tradejini"] = "ws://127.0.0.1:1/stream";
        var error = await Assert.ThrowsAsync<BrokerException>(() => Plugin(options: options).GetQuoteAsync(Session(), "SBIN", "NSE", CancellationToken.None));
        Assert.Contains("market data could not be reached", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_quote_for_a_symbol_the_master_lacks_is_refused_before_a_connection_is_made()
    {
        var error = await Assert.ThrowsAsync<BrokerException>(() => Plugin(known: false, sockets: new Refusing()).GetQuoteAsync(Session(), "SBIN", "NSE", CancellationToken.None));
        Assert.Equal(400, error.HttpStatus);
    }

    private sealed class Refusing : ITradejiniSockets
    {
        public Task<ITradejiniStream?> OpenAsync(Uri address, CancellationToken cancellationToken) => throw new InvalidOperationException("nothing should be connected");
    }
}
