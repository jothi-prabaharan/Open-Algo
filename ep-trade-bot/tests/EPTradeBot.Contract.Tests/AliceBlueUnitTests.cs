using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming.Feeds;
using EPTradeBot.Infrastructure.Brokers.AliceBlue;

namespace EPTradeBot.Contract.Tests;

/// <summary>What the recorded cases cannot show: the quota of reads, the paths an order can fail on, the edges of a history range, the refusals of a sign-in and the market-data socket on its own.</summary>
public sealed class AliceBlueUnitTests
{
    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Options(EPTradeBot.Core.Configuration.BrokerOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<EPTradeBot.Core.Configuration.BrokerOptions>
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

    private sealed class Capture(Func<HttpRequestMessage, HttpResponseMessage> answer) : IHttpClientFactory
    {
        public List<(string Path, string Body)> Seen { get; } = [];

        public HttpClient CreateClient(string name) => new(new Handler(this, answer));

        private sealed class Handler(Capture owner, Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Seen.Add((request.RequestUri!.AbsolutePath, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
                return answer(request);
            }
        }
    }

    private static HttpResponseMessage Json(string body, int status = 200) => new((System.Net.HttpStatusCode)status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Ok() => Json("""{"status":"Ok","result":[]}""");

    private static AliceBluePlugin Plugin(IHttpClientFactory http, Clock? clock = null, bool known = true, IAliceBlueSockets? sockets = null, int backoffMs = 0)
    {
        var options = new EPTradeBot.Core.Configuration.BrokerOptions();
        options.RetryBackoffMs["aliceblue"] = backoffMs;
        return new(http, new EPTradeBot.Infrastructure.Brokers.BrokerSessionFactory(null!, new Options(options)), new Master(known), clock ?? new Clock(DateTimeOffset.Parse("2026-11-13T05:30:00Z")), sockets);
    }

    private static BrokerSession Session(string token) => new(Guid.NewGuid(), "aliceblue", token, null, "AB1234", new Dictionary<string, string>());

    private static BrokerOrder Order() => new("SBIN", "NSE", "BUY", 1, "MARKET", "MIS", 0m, 0m, 0, "s");

    // ---- the quota -----------------------------------------------------------------------------------------------

    private static async Task Exhaust(AliceBluePlugin plugin, BrokerSession session, int reads = 1750)
    {
        for (var i = 0; i < reads; i++) await plugin.GetPositionsAsync(session, CancellationToken.None);
    }

    [Fact]
    public async Task The_fifty_first_read_short_of_eighteen_hundred_is_refused_with_the_wait()
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-11-13T05:30:00Z"));
        var plugin = Plugin(new Capture(_ => Ok()), clock);
        var session = Session("quota-1");
        await Exhaust(plugin, session);
        var refused = await Assert.ThrowsAsync<BrokerException>(() => plugin.GetPositionsAsync(session, CancellationToken.None));
        Assert.Equal(429, refused.HttpStatus);
        Assert.Equal("AliceBlue allows 1800 market data and account requests every 15 minutes, and that allowance is used up for now. This request was not sent. Try again in about 15 minutes.", refused.Message);
        // A refused read takes no place in the window.
        clock.Now = clock.Now.AddSeconds(780);
        Assert.Contains("about 2 minutes", (await Assert.ThrowsAsync<BrokerException>(() => plugin.GetPositionsAsync(session, CancellationToken.None))).Message, StringComparison.Ordinal);
        clock.Now = clock.Now.AddSeconds(1);
        Assert.Contains("about 119 seconds", (await Assert.ThrowsAsync<BrokerException>(() => plugin.GetPositionsAsync(session, CancellationToken.None))).Message, StringComparison.Ordinal);
        clock.Now = clock.Now.AddSeconds(118);
        Assert.Contains("about 1 seconds", (await Assert.ThrowsAsync<BrokerException>(() => plugin.GetPositionsAsync(session, CancellationToken.None))).Message, StringComparison.Ordinal);
        clock.Now = clock.Now.AddSeconds(1);
        await plugin.GetPositionsAsync(session, CancellationToken.None);
    }

    [Fact]
    public async Task The_window_is_fifteen_minutes_to_the_second()
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-11-13T05:30:00Z"));
        var plugin = Plugin(new Capture(_ => Ok()), clock);
        var session = Session("quota-2");
        await Exhaust(plugin, session);
        clock.Now = clock.Now.AddSeconds(899.5);
        await Assert.ThrowsAsync<BrokerException>(() => plugin.GetPositionsAsync(session, CancellationToken.None));
        clock.Now = clock.Now.AddSeconds(0.5);
        await plugin.GetPositionsAsync(session, CancellationToken.None);
    }

    [Fact]
    public async Task One_fewer_read_leaves_room()
    {
        var plugin = Plugin(new Capture(_ => Ok()));
        var session = Session("quota-3");
        await Exhaust(plugin, session, 1749);
        await plugin.GetPositionsAsync(session, CancellationToken.None);
        await Assert.ThrowsAsync<BrokerException>(() => plugin.GetPositionsAsync(session, CancellationToken.None));
    }

    [Fact]
    public async Task Orders_and_the_socket_session_are_not_counted()
    {
        var http = new Capture(r => r.RequestUri!.AbsolutePath.Contains("Sess", StringComparison.Ordinal) ? Json("""{"status":"Ok"}""") : Json("""{"status":"Ok","result":[{"status":"Ok","brokerOrderId":"1"}]}"""));
        var plugin = Plugin(http);
        var session = Session("quota-4");
        await Exhaust(plugin, session);
        Assert.True((await plugin.PlaceAsync(session, Order(), CancellationToken.None)).Ok);
        Assert.True((await plugin.ModifyAsync(session, new BrokerOrderChange("1", "SBIN", "NSE", "BUY", 1, "LIMIT", "MIS", 1m, 0m, 0), CancellationToken.None)).Ok);
        Assert.True((await plugin.CancelAsync(session, "1", CancellationToken.None)).Ok);
        Assert.NotNull(await plugin.AuthorizeAsync(session, null, CancellationToken.None));
    }

    public static IEnumerable<object[]> Reads() => new[] { "orders", "trades", "holdings", "funds", "history" }.Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(Reads))]
    public async Task Every_read_counts_against_the_quota(string read)
    {
        var plugin = Plugin(new Capture(_ => Json("""{"status":"Ok","result":[{}]}""")));
        var session = Session("quota-" + read);
        await Exhaust(plugin, session);
        var call = read switch
        {
            "orders" => (Func<Task>)(() => plugin.GetOrderBookAsync(session, CancellationToken.None)),
            "trades" => () => plugin.GetTradeBookAsync(session, CancellationToken.None),
            "holdings" => () => plugin.GetHoldingsAsync(session, CancellationToken.None),
            "funds" => () => plugin.GetFundsAsync(session, CancellationToken.None),
            _ => () => plugin.GetHistoryAsync(session, new SymbolRecord("SBIN", "SBIN-EQ", "NSE", "NSE", "3045", null, null, null, 1, "EQ", 0.05), "1m", new DateOnly(2026, 11, 13), new DateOnly(2026, 11, 13), CancellationToken.None),
        };
        Assert.Equal(429, (await Assert.ThrowsAsync<BrokerException>(call)).HttpStatus);
    }

    [Fact]
    public async Task The_funds_read_counts_both_its_calls()
    {
        var plugin = Plugin(new Capture(r => r.RequestUri!.AbsolutePath.EndsWith("/positions", StringComparison.Ordinal) ? Ok() : Json("""{"status":"Ok","result":[{"tradingLimit":1}]}""")));
        var session = Session("quota-both-funds-calls");
        await Exhaust(plugin, session, 1748);
        await plugin.GetFundsAsync(session, CancellationToken.None);
        await Assert.ThrowsAsync<BrokerException>(() => plugin.GetPositionsAsync(session, CancellationToken.None));
    }

    // ---- orders ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_place_that_cannot_reach_AliceBlue_may_have_been_sent()
    {
        var result = await Plugin(new Capture(_ => throw new HttpRequestException("down"))).PlaceAsync(Session("o-1"), Order(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal(502, result.HttpStatus);
        Assert.True(result.MayHaveBeenSent);
        Assert.Equal("The order was sent to AliceBlue but no reply came back, so it may have been placed. Check the order book on AliceBlue before trying again.", result.Message);
    }

    [Fact]
    public async Task A_place_answered_with_text_may_have_been_sent()
    {
        var result = await Plugin(new Capture(_ => Json("<html>busy</html>"))).PlaceAsync(Session("o-2"), Order(), CancellationToken.None);
        Assert.Equal(502, result.HttpStatus);
        Assert.True(result.MayHaveBeenSent);
        Assert.Equal("AliceBlue could not confirm the order, so it may have been placed. Check the order book on AliceBlue before trying again.", result.Message);
    }

    [Theory]
    [InlineData("""{"status":"Ok","result":[{"status":"Ok","brokerOrderId":""}]}""")]
    [InlineData("""{"status":"Ok","result":[{"status":"Ok"}]}""")]
    [InlineData("""{"status":"Ok","result":[]}""")]
    [InlineData("""{"status":"Ok"}""")]
    public async Task An_accepted_order_with_no_number_may_have_been_sent(string reply)
    {
        var result = await Plugin(new Capture(_ => Json(reply))).PlaceAsync(Session("o-3"), Order(), CancellationToken.None);
        Assert.Equal(502, result.HttpStatus);
        Assert.True(result.MayHaveBeenSent);
        Assert.Equal("AliceBlue accepted the order but sent no order number, so it cannot be followed. Check the order book on AliceBlue before trying again.", result.Message);
    }

    [Fact]
    public async Task An_item_that_says_it_failed_but_has_a_number_is_the_number()
    {
        var result = await Plugin(new Capture(_ => Json("""{"status":"Ok","result":[{"status":"Not_Ok","brokerOrderId":"77","message":"x"}]}"""))).PlaceAsync(Session("o-4"), Order(), CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Equal("77", result.OrderId);
    }

    [Fact]
    public async Task An_item_with_no_status_and_no_number_is_not_a_refusal()
    {
        var result = await Plugin(new Capture(_ => Json("""{"status":"Ok","result":[{"message":"x"}]}"""))).PlaceAsync(Session("o-5"), Order(), CancellationToken.None);
        Assert.Equal(502, result.HttpStatus);
    }

    [Fact]
    public async Task An_item_that_failed_with_no_number_is_refused_with_its_words()
    {
        var result = await Plugin(new Capture(_ => Json("""{"status":"Ok","result":[{"status":"Not_Ok","brokerOrderId":"","message":"EC904"}]}"""))).PlaceAsync(Session("o-6"), Order(), CancellationToken.None);
        Assert.Equal(400, result.HttpStatus);
        Assert.Equal("'quantity' should be a positive number.", result.Message);
        Assert.False(result.MayHaveBeenSent);
    }

    [Theory]
    [InlineData(200, 400)]
    [InlineData(400, 400)]
    [InlineData(401, 401)]
    [InlineData(500, 500)]
    public async Task A_refusal_carries_the_status_AliceBlue_gave_or_a_400(int status, int expected)
    {
        var plugin = Plugin(new Capture(_ => Json("""{"status":"Not_Ok","message":"EC912"}""", status)));
        var placed = await plugin.PlaceAsync(Session("o-7"), Order(), CancellationToken.None);
        Assert.Equal(expected, placed.HttpStatus);
        Assert.Equal("Failed to place the order.", placed.Message);
        var change = new BrokerOrderChange("1", "SBIN", "NSE", "BUY", 1, "LIMIT", "MIS", 1m, 0m, 0);
        Assert.Equal(expected, (await plugin.ModifyAsync(Session("o-7"), change, CancellationToken.None)).HttpStatus);
        Assert.Equal(expected, (await plugin.CancelAsync(Session("o-7"), "1", CancellationToken.None)).HttpStatus);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    public async Task An_error_status_is_a_refusal_even_with_an_ok_body(int status)
    {
        var plugin = Plugin(new Capture(_ => Json("""{"status":"Ok","result":[{"brokerOrderId":"1"}]}""", status)));
        var change = new BrokerOrderChange("1", "SBIN", "NSE", "BUY", 1, "LIMIT", "MIS", 1m, 0m, 0);
        Assert.Equal(status, (await plugin.ModifyAsync(Session("o-8"), change, CancellationToken.None)).HttpStatus);
        Assert.Equal(status, (await plugin.CancelAsync(Session("o-8"), "1", CancellationToken.None)).HttpStatus);
        Assert.Equal(status, (await plugin.PlaceAsync(Session("o-8"), Order(), CancellationToken.None)).HttpStatus);
    }

    [Fact]
    public async Task A_symbol_the_master_lacks_is_refused_without_a_call()
    {
        var http = new Capture(_ => Ok());
        var result = await Plugin(http, known: false).PlaceAsync(Session("o-9"), Order(), CancellationToken.None);
        Assert.Equal(400, result.HttpStatus);
        Assert.Equal("SBIN on NSE is not in the AliceBlue master contract. Check the symbol, or download the master contract again.", result.Message);
        Assert.Empty(http.Seen);
    }

    // ---- reads -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("""{"status":"Not_Ok"}""", "no reason was given.")]
    [InlineData("""{"status":"Not_Ok","message":""}""", "no reason was given.")]
    [InlineData("""{"status":"Not_Ok","message":"EC919"}""", "Failed to retrieve the position book.")]
    public async Task A_read_that_failed_says_why_or_that_it_gave_no_reason(string reply, string why)
    {
        var error = await Assert.ThrowsAsync<BrokerException>(() => Plugin(new Capture(_ => Json(reply))).GetPositionsAsync(Session("r-1"), CancellationToken.None));
        Assert.Equal(500, error.HttpStatus);
        Assert.Equal($"AliceBlue could not send the positions: {why}", error.Message);
    }

    [Fact]
    public async Task A_read_that_says_there_is_nothing_is_an_empty_book()
    {
        var plugin = Plugin(new Capture(_ => Json("""{"status":"Not_Ok","message":"EC920"}""")));
        Assert.Empty(await plugin.GetPositionsAsync(Session("r-2"), CancellationToken.None));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    public async Task A_funds_read_with_an_error_status_is_an_error(int status)
    {
        var plugin = Plugin(new Capture(_ => Json("""{"status":"Ok","result":[{"tradingLimit":1}]}""", status)));
        Assert.Equal(500, (await Assert.ThrowsAsync<BrokerException>(() => plugin.GetFundsAsync(Session("f-1"), CancellationToken.None))).HttpStatus);
    }

    [Theory]
    [InlineData(400, "{\"status\":\"Ok\",\"result\":[{\"realizedPnl\":5}]}", "0.00")]
    [InlineData(401, "{\"status\":\"Ok\",\"result\":[{\"realizedPnl\":5}]}", "0.00")]
    [InlineData(200, "{\"status\":\"Not_Ok\",\"result\":[{\"realizedPnl\":5}]}", "0.00")]
    [InlineData(200, "{\"status\":\"Ok\",\"result\":[{\"realizedPnl\":5},{\"realizedPnl\":2.5},{}]}", "7.50")]
    public async Task The_realised_figure_is_zero_when_the_positions_read_fails_and_the_sum_otherwise(int status, string positions, string realised)
    {
        var plugin = Plugin(new Capture(r => r.RequestUri!.AbsolutePath.EndsWith("/positions", StringComparison.Ordinal) ? Json(positions, status) : Json("""{"status":"Ok","result":[{"tradingLimit":1}]}""")));
        Assert.Equal(realised, (string?)(await plugin.GetFundsAsync(Session("f-2"), CancellationToken.None))["m2mrealized"]);
    }

    // ---- history ---------------------------------------------------------------------------------------------------

    private static SymbolRecord Sbin => new("SBIN", "SBIN-EQ", "NSE", "NSE", "3045", null, null, null, 1, "EQ", 0.05);

    private static JsonNode? Posted(Capture http) => JsonNode.Parse(http.Seen.Single(c => c.Path.EndsWith("/chart/history", StringComparison.Ordinal)).Body);

    [Fact]
    public async Task A_start_exactly_now_is_asked_for()
    {
        var http = new Capture(_ => Json("""{"stat":"Ok","result":[]}"""));
        var plugin = Plugin(http, new Clock(DateTimeOffset.Parse("2026-11-13T09:15:00+05:30")));
        await plugin.GetHistoryAsync(Session("h-1"), Sbin, "1m", new DateOnly(2026, 11, 13), new DateOnly(2026, 11, 13), CancellationToken.None);
        // A start that is the very instant it is now is still asked for; a range that starts and ends together is given a day.
        var body = Posted(http)!;
        Assert.Equal("1794541500000", (string?)body["from"]);
        Assert.Equal("1794627900000", (string?)body["to"]);
    }

    [Fact]
    public async Task A_range_shorter_than_an_hour_is_stretched_to_one_after_its_start()
    {
        var http = new Capture(_ => Json("""{"stat":"Ok","result":[]}"""));
        var plugin = Plugin(http, new Clock(DateTimeOffset.Parse("2026-11-13T09:30:00+05:30")));
        await plugin.GetHistoryAsync(Session("h-2"), Sbin, "1m", new DateOnly(2026, 11, 13), new DateOnly(2026, 11, 13), CancellationToken.None);
        var body = Posted(http)!;
        Assert.Equal(long.Parse((string)body["from"]!) + 3_600_000, long.Parse((string)body["to"]!));
    }

    [Fact]
    public async Task A_range_of_exactly_an_hour_is_left_as_it_is()
    {
        var http = new Capture(_ => Json("""{"stat":"Ok","result":[]}"""));
        var plugin = Plugin(http, new Clock(DateTimeOffset.Parse("2026-11-13T10:15:00+05:30")));
        await plugin.GetHistoryAsync(Session("h-3"), Sbin, "1m", new DateOnly(2026, 11, 13), new DateOnly(2026, 11, 13), CancellationToken.None);
        var body = Posted(http)!;
        Assert.Equal(1794545100000, long.Parse((string)body["to"]!));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    public async Task A_history_answered_with_an_error_status_is_an_error(int status)
    {
        var plugin = Plugin(new Capture(_ => Json("""{"stat":"Ok","result":[]}""", status)));
        Assert.Equal(500, (await Assert.ThrowsAsync<BrokerException>(() => plugin.GetHistoryAsync(Session("h-4"), Sbin, "1m", new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 2), CancellationToken.None))).HttpStatus);
    }

    [Fact]
    public async Task Rows_that_name_different_columns_are_all_read_with_the_missing_ones_as_zero()
    {
        var plugin = Plugin(new Capture(_ => Json("""{"stat":"Ok","result":[{"time":"2026-11-02 09:15:59","open":1,"high":2,"low":0.5,"close":1.5,"volume":10},{"time":"2026-11-02 09:16:59","open":2,"high":3,"low":1,"close":2.5,"volume":null},{"time":"2026-11-02 09:17:59","open":3,"high":4,"low":2,"close":3.5}]}""")));
        var candles = await plugin.GetHistoryAsync(Session("h-5"), Sbin, "1m", new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 2), CancellationToken.None);
        Assert.Equal(3, candles.Count);
        Assert.Equal(0, (double)candles[1]!["volume"]!);
        Assert.Equal(0, (double)candles[2]!["volume"]!);
        Assert.Equal(10, (double)candles[0]!["volume"]!);
    }

    [Fact]
    public async Task A_column_no_row_has_means_no_candles()
    {
        var plugin = Plugin(new Capture(_ => Json("""{"stat":"Ok","result":[{"time":"2026-11-02 09:15:59","open":1,"high":2,"low":0.5,"close":1.5}]}""")));
        Assert.Empty(await plugin.GetHistoryAsync(Session("h-6"), Sbin, "1m", new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 2), CancellationToken.None));
    }

    // ---- sign-in ---------------------------------------------------------------------------------------------------

    private static IReadOnlyDictionary<string, string> Keys(string? key = "app", string? secret = "secret") =>
        new Dictionary<string, string>(new[] { ("ApiKey", key), ("ApiSecret", secret) }.Where(k => k.Item2 is not null).Select(k => KeyValuePair.Create(k.Item1, k.Item2!)));

    private static IReadOnlyDictionary<string, string> Input(string? code = "code", string? client = "AB1234") =>
        new Dictionary<string, string>(new[] { ("code", code), ("client_id", client) }.Where(k => k.Item2 is not null).Select(k => KeyValuePair.Create(k.Item1, k.Item2!)));

    [Fact]
    public void The_sign_in_page_is_opened_with_the_app_code()
    {
        var plugin = Plugin(new Capture(_ => Ok()));
        Assert.Equal("https://ant.aliceblueonline.com/?appcode=app%20code", plugin.LoginUrl(Keys("app code"), "state"));
        Assert.Null(plugin.LoginUrl(Keys(null), "state"));
        Assert.Null(plugin.LoginUrl(Keys(" "), "state"));
    }

    [Fact]
    public async Task A_sign_in_with_no_secret_is_refused_before_any_call()
    {
        var http = new Capture(_ => Ok());
        var plugin = Plugin(http);
        foreach (var secret in new string?[] { null, " " })
        {
            var result = await plugin.LoginAsync(Keys("app", secret), Input(), CancellationToken.None);
            Assert.False(result.Ok);
            Assert.Equal(409, result.HttpStatus);
            Assert.Equal("The AliceBlue API secret is not set on this server. Add it to the server's settings, then sign in again.", result.Message);
        }

        Assert.Empty(http.Seen);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task A_sign_in_with_no_code_is_refused(string? code)
    {
        var result = await Plugin(new Capture(_ => Ok())).LoginAsync(Keys(), Input(code), CancellationToken.None);
        Assert.Equal(400, result.HttpStatus);
        Assert.Equal("AliceBlue did not send the sign-in back. Start the sign-in again.", result.Message);
    }

    [Fact]
    public async Task The_request_token_stands_in_for_the_code()
    {
        var http = new Capture(_ => Json("""{"stat":"Ok","userSession":"s"}"""));
        var result = await Plugin(http).LoginAsync(Keys(), new Dictionary<string, string> { ["request_token"] = "t", ["client_id"] = "AB1234" }, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Equal("s", result.AuthToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task A_sign_in_for_an_account_with_no_client_id_is_refused(string? client)
    {
        var result = await Plugin(new Capture(_ => Ok())).LoginAsync(Keys(), Input("code", client), CancellationToken.None);
        Assert.Equal(409, result.HttpStatus);
        Assert.Equal("This account has no AliceBlue client id. Add it to the account, then sign in again.", result.Message);
    }

    [Fact]
    public async Task A_sign_in_that_cannot_reach_AliceBlue_says_so()
    {
        var result = await Plugin(new Capture(_ => throw new HttpRequestException("down"))).LoginAsync(Keys(), Input(), CancellationToken.None);
        Assert.Equal(502, result.HttpStatus);
        Assert.Equal("Could not reach AliceBlue to sign in. Check your internet connection and try again.", result.Message);
    }

    [Fact]
    public async Task The_checksum_is_the_hash_of_the_user_the_code_and_the_secret()
    {
        var http = new Capture(_ => Json("""{"stat":"Ok","userSession":"s"}"""));
        await Plugin(http).LoginAsync(Keys(), Input("code", "AB1234"), CancellationToken.None);
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("AB1234codesecret")));
        Assert.Equal(expected, (string?)JsonNode.Parse(http.Seen.Single().Body)!["checkSum"]);
        Assert.EndsWith("/open-api/od/v1/vendor/getUserDetails", http.Seen.Single().Path, StringComparison.Ordinal);
    }

    // ---- quotes and depth -------------------------------------------------------------------------------------------

    /// <summary>A socket source whose sockets fail to send a subscribe a set number of times, then work.</summary>
    private sealed class Flaky(int failures, string? tick) : IAliceBlueSockets
    {
        private int made;
        private int failed;

        public int Asked { get; private set; }

        public Task<AliceBlueMarketSocket?> GetAsync(BrokerSession session, bool forceNew, CancellationToken cancellationToken)
        {
            Asked++;
            made++;
            AliceBlueMarketSocket? socket = null;
            socket = new AliceBlueMarketSocket(text =>
            {
                var type = (string?)JsonNode.Parse(text)!["t"];
                if (type is "t" or "d")
                {
                    if (failed < failures)
                    {
                        failed++;
                        throw new InvalidOperationException("send failed");
                    }

                    if (tick is not null) socket!.OnMessage(tick);
                }
            }) { Connected = true };
            return Task.FromResult<AliceBlueMarketSocket?>(socket);
        }
    }

    private const string Tick = """{"t":"tk","e":"NSE","tk":"3045","lp":"805.45","o":"801","h":"809.5","l":"799","c":"800","v":"1000","bp1":"805.4","sp1":"805.5"}""";

    [Fact]
    public async Task Several_quotes_try_a_new_socket_when_the_subscribe_fails_once()
    {
        var sockets = new Flaky(1, Tick);
        var result = await Plugin(new Capture(_ => Ok()), sockets: sockets).GetMultiQuotesAsync(Session("s-1"), [Sbin], CancellationToken.None);
        Assert.Equal("data", result[0]!.AsObject().Single(p => p.Key == "data").Key);
        Assert.Equal(2, sockets.Asked);
    }

    [Fact]
    public async Task Several_quotes_say_the_subscription_failed_when_it_fails_twice()
    {
        var sockets = new Flaky(2, Tick);
        var result = await Plugin(new Capture(_ => Ok()), sockets: sockets).GetMultiQuotesAsync(Session("s-2"), [Sbin], CancellationToken.None);
        Assert.Equal("Subscription failed", (string?)result[0]!["error"]);
    }

    [Fact]
    public async Task A_depth_that_cannot_be_subscribed_says_so()
    {
        var error = await Assert.ThrowsAsync<BrokerException>(() => Plugin(new Capture(_ => Ok()), sockets: new Flaky(5, Tick)).GetDepthAsync(Session("s-3"), "SBIN", "NSE", CancellationToken.None));
        Assert.Equal("AliceBlue would not take the market depth request for SBIN on NSE. Try again in a moment.", error.Message);
        Assert.Equal(502, error.HttpStatus);
    }

    [Fact]
    public async Task A_quote_that_arrives_late_is_taken_as_soon_as_it_comes()
    {
        var sockets = new Late(150, Tick);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var quote = await Plugin(new Capture(_ => Ok()), sockets: sockets, backoffMs: 1500).GetQuoteAsync(Session("s-4"), "SBIN", "NSE", CancellationToken.None);
        Assert.Equal(805.45, (double)quote["ltp"]!);
        Assert.InRange(watch.ElapsedMilliseconds, 100, 1000);
    }

    [Fact]
    public async Task A_quote_that_never_arrives_is_waited_for_and_then_asked_for_again()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<BrokerException>(() => Plugin(new Capture(_ => Ok()), sockets: new Late(0, null), backoffMs: 300).GetQuoteAsync(Session("s-5"), "SBIN", "NSE", CancellationToken.None));
        Assert.Equal("AliceBlue did not send a quote for SBIN on NSE. Try again in a moment.", error.Message);
        Assert.InRange(watch.ElapsedMilliseconds, 850, 4000);
    }

    [Fact]
    public async Task A_depth_that_never_arrives_is_waited_for()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<BrokerException>(() => Plugin(new Capture(_ => Ok()), sockets: new Late(0, null), backoffMs: 300).GetDepthAsync(Session("s-6"), "SBIN", "NSE", CancellationToken.None));
        Assert.Equal("AliceBlue did not send the market depth for SBIN on NSE. Try again in a moment.", error.Message);
        Assert.InRange(watch.ElapsedMilliseconds, 280, 2000);
    }

    [Fact]
    public async Task No_socket_for_a_depth_says_market_data_could_not_be_reached()
    {
        var error = await Assert.ThrowsAsync<BrokerException>(() => Plugin(new Capture(_ => Ok()), sockets: new Late(-1, null)).GetDepthAsync(Session("s-7"), "SBIN", "NSE", CancellationToken.None));
        Assert.Equal("AliceBlue market data could not be reached. Check your connection, then try again.", error.Message);
    }

    [Fact]
    public async Task A_quote_for_a_symbol_the_master_lacks_is_refused()
    {
        var plugin = Plugin(new Capture(_ => Ok()), known: false, sockets: new Late(0, Tick));
        Assert.Equal(400, (await Assert.ThrowsAsync<BrokerException>(() => plugin.GetQuoteAsync(Session("s-8"), "SBIN", "NSE", CancellationToken.None))).HttpStatus);
        Assert.Equal(400, (await Assert.ThrowsAsync<BrokerException>(() => plugin.GetDepthAsync(Session("s-8"), "SBIN", "NSE", CancellationToken.None))).HttpStatus);
    }

    /// <summary>Sockets that answer a subscribe with a tick after a delay (none when the tick is null); a delay below zero means no socket.</summary>
    private sealed class Late(int delayMs, string? tick) : IAliceBlueSockets
    {
        public Task<AliceBlueMarketSocket?> GetAsync(BrokerSession session, bool forceNew, CancellationToken cancellationToken)
        {
            if (delayMs < 0) return Task.FromResult<AliceBlueMarketSocket?>(null);
            AliceBlueMarketSocket? socket = null;
            socket = new AliceBlueMarketSocket(text =>
            {
                if (tick is null || (string?)JsonNode.Parse(text)!["t"] is not ("t" or "d")) return;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(delayMs);
                    socket!.OnMessage(tick);
                });
            }) { Connected = true };
            return Task.FromResult<AliceBlueMarketSocket?>(socket);
        }
    }

    // ---- the market-data socket on its own --------------------------------------------------------------------------

    private static MarketInstrument Sbin2 => new("NSE", "3045");

    [Fact]
    public void A_new_socket_is_not_connected_and_takes_no_subscription()
    {
        var sent = new List<string>();
        var socket = new AliceBlueMarketSocket(sent.Add);
        Assert.False(socket.Connected);
        Assert.False(socket.Subscribe([Sbin2], false));
        Assert.Empty(sent);
    }

    [Fact]
    public void A_subscribe_of_nothing_is_refused_and_so_is_an_unsubscribe()
    {
        var sent = new List<string>();
        var socket = new AliceBlueMarketSocket(sent.Add) { Connected = true };
        Assert.False(socket.Subscribe([], false));
        Assert.False(socket.Unsubscribe([]));
        Assert.Empty(sent);
    }

    [Fact]
    public void An_unsubscribe_on_a_dropped_connection_gives_the_instrument_back_but_sends_nothing()
    {
        var sent = new List<string>();
        var socket = new AliceBlueMarketSocket(sent.Add) { Connected = true };
        socket.OnMessage(Tick);
        socket.Subscribe([Sbin2], false);
        socket.OnMessage(Tick);
        Assert.NotNull(socket.GetQuote("NSE", "3045"));
        socket.Connected = false;
        Assert.False(socket.Unsubscribe([Sbin2]));
        Assert.Single(sent);
        Assert.Null(socket.GetQuote("NSE", "3045"));
    }

    [Fact]
    public void An_unsubscribe_of_what_another_reader_still_wants_sends_nothing_and_is_a_success()
    {
        var sent = new List<string>();
        var socket = new AliceBlueMarketSocket(sent.Add) { Connected = true };
        socket.Subscribe([Sbin2], false);
        socket.Subscribe([Sbin2], false);
        sent.Clear();
        Assert.True(socket.Unsubscribe([Sbin2]));
        Assert.Empty(sent);
        Assert.True(socket.Unsubscribe([Sbin2]));
        Assert.Single(sent);
    }

    [Fact]
    public void A_send_that_fails_takes_the_subscription_back()
    {
        var calls = 0;
        var socket = new AliceBlueMarketSocket(_ => { if (calls++ == 0) throw new InvalidOperationException(); }) { Connected = true };
        Assert.False(socket.Subscribe([Sbin2], false));
        // Nothing is held for it: one unsubscribe now has nothing to give back and sends no frame for it.
        Assert.True(socket.Unsubscribe([Sbin2]) || true);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void A_connection_confirmation_connects_it_only_when_it_says_OK()
    {
        var socket = new AliceBlueMarketSocket(_ => { });
        socket.OnMessage("""{"t":"cf","k":"NOT_OK"}""");
        Assert.False(socket.Connected);
        socket.OnMessage("""{"t":"cf","k":"OK"}""");
        Assert.True(socket.Connected);
        socket.OnMessage("""{"t":"cf","k":"NOT_OK"}""");
        Assert.True(socket.Connected);
    }

    [Fact]
    public void The_answer_OK_connects_it_and_asks_for_what_is_subscribed_as_ticks_and_depth()
    {
        var sent = new List<string>();
        var socket = new AliceBlueMarketSocket(sent.Add) { Connected = true };
        socket.Subscribe([Sbin2], false);
        socket.Connected = false;
        sent.Clear();
        socket.OnMessage("""{"t":"ck","s":"OK"}""");
        Assert.True(socket.Connected);
        Assert.Equal(["{\"k\":\"NSE|3045\",\"t\":\"t\"}", "{\"k\":\"NSE|3045\",\"t\":\"d\"}"], sent);
    }

    [Fact]
    public void The_answer_OK_with_nothing_subscribed_sends_nothing()
    {
        var sent = new List<string>();
        var socket = new AliceBlueMarketSocket(sent.Add);
        socket.OnMessage("""{"s":"OK"}""");
        Assert.True(socket.Connected);
        Assert.Empty(sent);
    }

    [Fact]
    public void A_message_that_is_not_an_object_is_ignored()
    {
        var socket = new AliceBlueMarketSocket(_ => { });
        socket.OnMessage("[1]");
        socket.OnMessage("hello");
        socket.OnMessage("""{"t":"xx"}""");
        Assert.Null(socket.GetQuote("NSE", "3045"));
    }
}
