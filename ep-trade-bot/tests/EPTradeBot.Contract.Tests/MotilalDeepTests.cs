using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming;
using EPTradeBot.Core.Streaming.Feeds;
using EPTradeBot.Infrastructure.Brokers.Motilal;
using Xunit;

namespace EPTradeBot.Contract.Tests;

/// <summary>Motilal's plugin, packets, codec and socket called directly, with answers scripted a request at a time: the paths and boundaries the recorded cases do not reach.</summary>
public sealed class MotilalDeepTests
{
    private sealed record Seen(string Path, string Body, Dictionary<string, string> Headers);

    private sealed class Script(Func<Seen, (int Status, string Body)> answer) : IHttpClientFactory
    {
        private readonly Func<Seen, (int Status, string Body)> reply = answer;

        public List<Seen> Requests { get; } = [];

        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler(Script owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
                var seen = new Seen(request.RequestUri!.AbsolutePath, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken), headers);
                owner.Requests.Add(seen);
                var (status, body) = owner.reply(seen);
                return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            }
        }
    }

    private sealed class Down : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Refuse());

        private sealed class Refuse : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new HttpRequestException("down");
        }
    }

    private sealed class Options(EPTradeBot.Core.Configuration.BrokerOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<EPTradeBot.Core.Configuration.BrokerOptions>
    {
        public EPTradeBot.Core.Configuration.BrokerOptions CurrentValue => value;

        public EPTradeBot.Core.Configuration.BrokerOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<EPTradeBot.Core.Configuration.BrokerOptions, string?> listener) => null;
    }

    private sealed class Master(Func<string, string, SymbolRecord?> find) : ISymbolMaster
    {
        public Task<SymbolRecord?> FindAsync(string broker, string symbol, string exchange, CancellationToken cancellationToken) => Task.FromResult(find(symbol, exchange));

        public Task<SymbolRecord?> FindByBrokerSymbolAsync(string broker, string brokerSymbol, string exchange, CancellationToken cancellationToken) => Task.FromResult<SymbolRecord?>(null);

        public Task<SymbolRecord?> FindByTokenAsync(string broker, string token, string exchange, CancellationToken cancellationToken) => Task.FromResult(exchange == "NSE" && token == "3045" ? Row("SBIN", "3045") : null);
    }

    private static SymbolRecord Row(string symbol, string token, string exchange = "NSE", int lot = 1, double? tick = 0.05) => new(symbol, symbol, exchange, exchange, token, symbol, null, null, lot, "EQ", tick);

    private static MotilalPlugin Plugin(IHttpClientFactory http, Func<string, string, SymbolRecord?>? find = null, IMotilalSockets? sockets = null, TimeProvider? clock = null)
    {
        var sessions = new EPTradeBot.Infrastructure.Brokers.BrokerSessionFactory(null!, new Options(new EPTradeBot.Core.Configuration.BrokerOptions()));
        return new MotilalPlugin(http, sessions, new Master(find ?? ((s, e) => Row(s, "3045", e))), clock ?? TimeProvider.System, sockets);
    }

    private static BrokerSession Session(string token = "auth:::access", string? secret = "secret") =>
        new(Guid.Empty, "motilal", token, null, "MO100", secret is null ? new Dictionary<string, string> { ["ApiKey"] = "key" } : new Dictionary<string, string> { ["ApiKey"] = "key", ["ApiSecret"] = secret });

    private static BrokerOrder Order(string type = "LIMIT", decimal price = 800.5m, decimal trigger = 0, int quantity = 10) => new("SBIN", "NSE", "BUY", quantity, type, "MIS", price, trigger, 0, "s");

    private static BrokerOrderChange Change(int quantity = 10) => new("OID1", "SBIN", "NSE", "BUY", quantity, "LIMIT", "MIS", 800.5m, 0, 0);

    private const string Placed = """{"status":"SUCCESS","uniqueorderid":"OID1"}""";

    // ---- headers -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("auth:::access", "auth", "access")]
    [InlineData("auth", "auth", null)]
    [InlineData("auth:::", "auth", null)]
    [InlineData(":::access", null, "access")]
    [InlineData("a:::b:::c", "a", "b:::c")]
    public async Task The_stored_session_is_split_into_the_auth_token_and_the_access_token(string stored, string? auth, string? access)
    {
        var http = new Script(_ => (200, Placed));
        await Plugin(http).CancelAsync(Session(stored), "OID1", CancellationToken.None);
        var headers = http.Requests.Single().Headers;
        Assert.Equal(auth, headers.GetValueOrDefault("authorization"));
        Assert.Equal(access, headers.GetValueOrDefault("accesstoken"));
    }

    [Fact]
    public async Task The_common_headers_and_the_secret_only_when_there_is_one()
    {
        var http = new Script(_ => (200, Placed));
        await Plugin(http).CancelAsync(Session(), "OID1", CancellationToken.None);
        await Plugin(http).CancelAsync(Session(secret: null), "OID1", CancellationToken.None);
        await Plugin(http).CancelAsync(Session(secret: ""), "OID1", CancellationToken.None);
        var first = http.Requests[0].Headers;
        Assert.Equal("Windows 10", first["osname"]);
        Assert.Equal("10.0.19041", first["osversion"]);
        Assert.Equal("AHV", first["devicemodel"]);
        Assert.Equal("DELL", first["manufacturer"]);
        Assert.Equal("MO100", first["vendorinfo"]);
        Assert.Equal("key", first["apikey"]);
        Assert.Equal("secret", first["apisecretkey"]);
        Assert.False(http.Requests[1].Headers.ContainsKey("apisecretkey"));
        Assert.False(http.Requests[2].Headers.ContainsKey("apisecretkey"));
    }

    // ---- orders ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_write_that_got_no_answer_may_have_been_made_and_says_so()
    {
        var plugin = Plugin(new Down());
        foreach (var result in new[]
        {
            await plugin.PlaceAsync(Session(), Order(), CancellationToken.None),
            await plugin.CancelAsync(Session(), "OID1", CancellationToken.None),
        })
        {
            Assert.Equal((false, 502, true), (result.Ok, result.HttpStatus, result.MayHaveBeenSent));
            Assert.Contains("no reply came back", result.Message);
        }

        var down = await Plugin(new Down()).ModifyAsync(Session(), Change(), CancellationToken.None);
        Assert.False(down.Ok);
    }

    [Theory]
    [InlineData("<html>")]
    [InlineData("[1]")]
    public async Task An_answer_that_is_not_an_object_may_mean_the_write_was_made(string body)
    {
        var http = new Script(r => r.Path.Contains("orderdetail") ? (200, """{"status":"SUCCESS","data":[{"uniqueorderid":"OID1","lastmodifiedtime":"t"}]}""") : (200, body));
        foreach (var result in new[] { await Plugin(http).PlaceAsync(Session(), Order(), CancellationToken.None), await Plugin(http).ModifyAsync(Session(), Change(), CancellationToken.None), await Plugin(http).CancelAsync(Session(), "OID1", CancellationToken.None) })
        {
            Assert.Equal((false, 502, true), (result.Ok, result.HttpStatus, result.MayHaveBeenSent));
            Assert.Contains("may have been", result.Message);
        }
    }

    [Fact]
    public async Task An_order_with_no_number_cannot_be_followed()
    {
        var result = await Plugin(new Script(_ => (200, """{"status":"SUCCESS"}"""))).PlaceAsync(Session(), Order(), CancellationToken.None);
        Assert.Equal((false, 502, true), (result.Ok, result.HttpStatus, result.MayHaveBeenSent));
        Assert.Contains("sent no order number", result.Message);
        var empty = await Plugin(new Script(_ => (200, """{"status":"SUCCESS","uniqueorderid":""}"""))).PlaceAsync(Session(), Order(), CancellationToken.None);
        Assert.False(empty.Ok);
    }

    [Theory]
    [InlineData(200, 400)]
    [InlineData(400, 400)]
    [InlineData(401, 401)]
    [InlineData(500, 500)]
    public async Task A_refusal_keeps_the_status_unless_it_is_a_success_status(int status, int expected)
    {
        var http = new Script(r => r.Path.Contains("orderdetail") ? (200, """{"status":"SUCCESS","data":[{"uniqueorderid":"OID1","lastmodifiedtime":"t"}]}""") : (status, """{"status":"FAILURE","message":"No","errorcode":"MO1"}"""));
        var place = await Plugin(http).PlaceAsync(Session(), Order(), CancellationToken.None);
        var change = await Plugin(http).ModifyAsync(Session(), Change(), CancellationToken.None);
        var cancel = await Plugin(http).CancelAsync(Session(), "OID1", CancellationToken.None);
        Assert.All(new[] { place, change, cancel }, r => Assert.Equal((false, expected, false), (r.Ok, r.HttpStatus, r.MayHaveBeenSent)));
        Assert.Equal("No", place.Message);
        Assert.Equal("No (errorcode: MO1)", change.Message);
        Assert.Equal("No (errorcode: MO1)", cancel.Message);
        var bare = await Plugin(new Script(_ => (status, """{"status":"FAILURE"}"""))).CancelAsync(Session(), "OID1", CancellationToken.None);
        Assert.Equal("Motilal API returned FAILURE", bare.Message);
    }

    [Fact]
    public async Task A_stop_market_with_no_trigger_goes_as_asked_and_a_tick_of_zero_is_not_a_tick()
    {
        var http = new Script(_ => (200, Placed));
        await Plugin(http).PlaceAsync(Session(), Order("SL-M", 5m, 0m), CancellationToken.None);
        var body = JsonNode.Parse(http.Requests.Last().Body)!;
        Assert.Equal("STOPLOSS", body["ordertype"]!.GetValue<string>());
        Assert.Equal(5.0, body["price"]!.GetValue<double>());
        var quote = """{"status":"SUCCESS","data":{"ltp":12349}}""";
        var market = new Script(r => r.Path.EndsWith("getltpdata", StringComparison.Ordinal) ? (200, quote) : (200, Placed));
        await Plugin(market, (s, e) => Row(s, "3045", e, 1, 0)).PlaceAsync(Session(), Order("MARKET", 0m), CancellationToken.None);
        Assert.Equal(124.72, JsonNode.Parse(market.Requests.Last().Body)!["price"]!.GetValue<double>());
    }

    [Fact]
    public async Task The_time_of_the_last_change_falls_back_to_the_insert_time_when_it_is_empty()
    {
        string Row(string last, string insert, string entry) => $$"""{"status":"SUCCESS","data":[{"uniqueorderid":"OID1","lastmodifiedtime":"{{last}}","recordinserttime":"{{insert}}","entrydatetime":"{{entry}}"}]}""";
        async Task<string> Sent(string reply)
        {
            var http = new Script(r => r.Path.Contains("orderdetail") ? (200, reply) : (200, "{\"status\":\"SUCCESS\"}"));
            await Plugin(http).ModifyAsync(Session(), Change(), CancellationToken.None);
            return JsonNode.Parse(http.Requests.Last().Body)!["lastmodifiedtime"]!.GetValue<string>();
        }

        Assert.Equal("T3", await Sent(Row("T3", "T1", "T2")));
        Assert.Equal("T1", await Sent(Row("", "T1", "T2")));
        Assert.Equal("T1", await Sent(Row("0", "T1", "T2")));
        Assert.Equal("T2", await Sent(Row("0", "0", "T2")));
        Assert.Equal("T2", await Sent(Row("", "", "T2")));
        Assert.Equal("", await Sent(Row("", "", "")));
        Assert.Equal("0", await Sent(Row("0", "0", "0")));
    }

    // ---- books -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_book_that_failed_says_why_and_one_that_could_not_be_read_says_so()
    {
        var failed = await Assert.ThrowsAsync<BrokerException>(() => Plugin(new Script(_ => (403, """{"status":"FAILURE","message":"Expired","errorcode":"MO8002"}"""))).GetOrderBookAsync(Session(), CancellationToken.None));
        Assert.Equal(("Expired (errorcode: MO8002)", 403), (failed.Message, failed.HttpStatus));
        var unreadable = await Assert.ThrowsAsync<BrokerException>(() => Plugin(new Script(_ => (200, "<html>"))).GetOrderBookAsync(Session(), CancellationToken.None));
        Assert.Contains("could not send the order book", unreadable.Message);
        var ok200 = await Assert.ThrowsAsync<BrokerException>(() => Plugin(new Script(_ => (200, """{"status":"FAILURE","message":"x"}"""))).GetTradeBookAsync(Session(), CancellationToken.None));
        Assert.Equal(500, ok200.HttpStatus);
    }

    [Fact]
    public async Task An_order_is_scaled_by_its_precision_and_rounded_to_two_places()
    {
        var rows = """
            {"status":"SUCCESS","data":[
             {"uniqueorderid":"1","symboltoken":3045,"exchange":"NSE","buyorsell":"BUY","ordertype":"Limit","producttype":"VALUEPLUS","orderstatus":"Confirm","orderqty":1,"price":800123456,"triggerprice":800126789,"averageprice":0,"precision":8},
             {"uniqueorderid":"2","symboltoken":3045,"exchange":"NSE","buyorsell":"BUY","ordertype":"Limit","producttype":"VALUEPLUS","orderstatus":"Confirm","orderqty":1,"price":80012,"triggerprice":0,"averageprice":0,"precision":9},
             {"uniqueorderid":"3","symboltoken":3045,"exchange":"NSE","buyorsell":"BUY","ordertype":"Limit","producttype":"VALUEPLUS","orderstatus":"Confirm","orderqty":1,"price":1.005,"triggerprice":1.005,"averageprice":0,"lastmodifiedtime":"","entrydatetime":"E","recordinserttime":"R"}]}
            """;
        var book = await Plugin(new Script(_ => (200, rows))).GetOrderBookAsync(Session(), CancellationToken.None);
        Assert.Equal(8.0, book.Rows[0]!["price"]!.GetValue<double>());
        Assert.Equal(8.0, book.Rows[0]!["trigger_price"]!.GetValue<double>());
        Assert.Equal(800.12, book.Rows[1]!["price"]!.GetValue<double>());
        Assert.Equal(1.0, book.Rows[2]!["price"]!.GetValue<double>());
        Assert.Equal(1.0, book.Rows[2]!["trigger_price"]!.GetValue<double>());
        Assert.Equal("E", book.Rows[2]!["timestamp"]!.GetValue<string>());
        Assert.Equal("SL", (await Plugin(new Script(_ => (200, rows.Replace("\"Limit\"", "\"Stoploss\"")))).GetOrderBookAsync(Session(), CancellationToken.None)).Rows[0]!["pricetype"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_trade_price_has_two_places_and_a_position_averages_what_it_traded()
    {
        var trades = """{"status":"SUCCESS","data":[{"uniqueorderid":"1","symboltoken":3045,"exchange":"NSE","buyorsell":"BUY","producttype":"DELIVERY","tradeqty":3,"tradeprice":123456789,"tradevalue":987654321,"precision":4}]}""";
        var row = (await Plugin(new Script(_ => (200, trades))).GetTradeBookAsync(Session(), CancellationToken.None))[0]!;
        Assert.Equal(12345.68, row["average_price"]!.GetValue<double>());
        Assert.Equal(98765.43, row["trade_value"]!.GetValue<double>());
        var positions = """{"status":"SUCCESS","data":[{"symboltoken":3045,"exchange":"NSE","productname":"VALUEPLUS","buyquantity":3,"sellquantity":0,"buyamount":10,"sellamount":0,"LTP":1,"marktomarket":2,"bookedprofitloss":3},{"symboltoken":3045,"exchange":"NSE","productname":"VALUEPLUS","buyquantity":0,"sellquantity":3,"buyamount":0,"sellamount":10}]}""";
        var book = await Plugin(new Script(_ => (200, positions))).GetPositionsAsync(Session(), CancellationToken.None);
        Assert.Equal(10.0 / 3, book[0]!["average_price"]!.GetValue<double>());
        Assert.Equal(10.0 / 3, book[1]!["average_price"]!.GetValue<double>());
        Assert.Equal(5.0, book[0]!["pnl"]!.GetValue<double>());
    }

    [Fact]
    public async Task Holdings_are_rounded_to_two_places_each()
    {
        var rows = """{"status":"SUCCESS","data":[{"scripname":"SBIN EQ","dpquantity":3,"buyavgprice":100.12345,"nsesymboltoken":3045,"LTP":105.6789},{"scripname":"B EQ","dpquantity":7,"buyavgprice":33.3333,"bsescripcode":500570,"LTP":35.5555}]}""";
        var book = await Plugin(new Script(_ => (200, rows))).GetHoldingsAsync(Session(), CancellationToken.None);
        Assert.Equal(100.12, book.Rows[0]!["average_price"]!.GetValue<double>());
        Assert.Equal(16.67, book.Rows[0]!["pnl"]!.GetValue<double>());
        Assert.Equal(5.55, book.Rows[0]!["pnlpercent"]!.GetValue<double>());
        Assert.Equal(565.93, book.Statistics["totalholdingvalue"]!.GetValue<double>());
        Assert.Equal(533.7, book.Statistics["totalinvvalue"]!.GetValue<double>());
        Assert.Equal(32.22, book.Statistics["totalprofitandloss"]!.GetValue<double>());
        Assert.Equal(6.04, book.Statistics["totalpnlpercentage"]!.GetValue<double>());
    }

    // ---- funds -------------------------------------------------------------------------------------------------

    private static async Task<JsonObject> Funds(params (int Srno, double Amount)[] rows) =>
        await Plugin(new Script(_ => (200, new JsonObject { ["status"] = "SUCCESS", ["data"] = new JsonArray(rows.Select(r => (JsonNode?)new JsonObject { ["srno"] = r.Srno, ["amount"] = r.Amount }).ToArray()) }.ToJsonString()))).GetFundsAsync(Session(), CancellationToken.None);

    [Theory]
    [InlineData(301)]
    [InlineData(321)]
    [InlineData(340)]
    [InlineData(360)]
    [InlineData(380)]
    [InlineData(381)]
    public async Task Each_segment_row_adds_to_the_margin_used_when_there_is_no_total(int srno)
    {
        var funds = await Funds((srno, 12.5), (201, 1));
        Assert.Equal("12.50", funds["utiliseddebits"]!.GetValue<string>());
        Assert.Equal("0.00", (await Funds((201, 1), (srno + 7, 12.5)))["utiliseddebits"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(402, "m2munrealized")]
    [InlineData(422, "m2munrealized")]
    [InlineData(442, "m2munrealized")]
    [InlineData(462, "m2munrealized")]
    [InlineData(482, "m2munrealized")]
    [InlineData(403, "m2mrealized")]
    [InlineData(423, "m2mrealized")]
    [InlineData(443, "m2mrealized")]
    [InlineData(463, "m2mrealized")]
    [InlineData(483, "m2mrealized")]
    public async Task Each_split_row_is_summed_by_segment(int srno, string field)
    {
        var funds = await Funds((201, 1), (srno, 7.25));
        Assert.Equal("7.25", funds[field]!.GetValue<string>());
        Assert.Equal("0.00", (await Funds((201, 1), (srno + 7, 7.25)))[field]!.GetValue<string>());
    }

    [Fact]
    public async Task Free_cash_is_worked_back_from_the_net_row_with_the_combined_profit()
    {
        var funds = await Funds((102, 1000), (220, 100), (300, 50), (600, 10));
        Assert.Equal("940.00", funds["availablecash"]!.GetValue<string>());
        var from400 = await Funds((102, 1000), (220, 100), (300, 50), (400, 20));
        Assert.Equal("930.00", from400["availablecash"]!.GetValue<string>());
        var near = await Funds((102, 1000), (601, 99), (400, 20));
        Assert.Equal("980.00", near["availablecash"]!.GetValue<string>());
        Assert.Equal("10.00", (await Funds((201, 1), (600, 10)))["m2munrealized"]!.GetValue<string>());
        Assert.Equal("0.00", (await Funds((201, 1), (601, 10)))["m2munrealized"]!.GetValue<string>());
    }

    // ---- quotes and the dealer login ---------------------------------------------------------------------------

    [Fact]
    public async Task A_dealer_login_sends_its_client_code_from_then_on_and_asks_again_only_once()
    {
        MotilalPlugin.ForgetLearned();
        var calls = 0;
        var http = new Script(r =>
        {
            calls++;
            return r.Body.Contains("clientcode", StringComparison.Ordinal) ? (200, """{"status":"SUCCESS","data":{"ltp":100}}""") : (200, """{"status":"FAILURE","message":"x","errorcode":"MO1062"}""");
        });
        var plugin = Plugin(http);
        await plugin.GetQuoteAsync(Session(), "SBIN", "NSE", CancellationToken.None);
        Assert.Equal(2, calls);
        await plugin.GetQuoteAsync(Session(), "SBIN", "NSE", CancellationToken.None);
        Assert.Equal(3, calls);
        MotilalPlugin.ForgetLearned();
        var noCode = new BrokerSession(Guid.Empty, "motilal", "t", null, null, new Dictionary<string, string>());
        calls = 0;
        await Assert.ThrowsAsync<BrokerException>(() => plugin.GetQuoteAsync(noCode, "SBIN", "NSE", CancellationToken.None));
        Assert.Equal(1, calls);
        MotilalPlugin.ForgetLearned();
    }

    [Fact]
    public async Task A_quote_that_failed_says_why_and_one_with_no_data_says_so()
    {
        MotilalPlugin.ForgetLearned();
        var failed = await Assert.ThrowsAsync<BrokerException>(() => Plugin(new Script(_ => (200, """{"status":"FAILURE","message":"Expired","errorcode":"MO8002"}"""))).GetQuoteAsync(Session(), "SBIN", "NSE", CancellationToken.None));
        Assert.Contains("Expired (errorcode: MO8002)", failed.Message);
        Assert.Contains("Try again in a moment", failed.Message);
        var bare = await Assert.ThrowsAsync<BrokerException>(() => Plugin(new Script(_ => (500, """{"status":"FAILURE"}"""))).GetQuoteAsync(Session(), "SBIN", "NSE", CancellationToken.None));
        Assert.Equal(500, bare.HttpStatus);
        var none = await Assert.ThrowsAsync<BrokerException>(() => Plugin(new Script(_ => (200, """{"status":"SUCCESS","data":{}}"""))).GetQuoteAsync(Session(), "SBIN", "NSE", CancellationToken.None));
        Assert.Equal(502, none.HttpStatus);
        var unlisted = await Assert.ThrowsAsync<BrokerException>(() => Plugin(new Script(_ => (200, "{}")), (_, _) => null).GetQuoteAsync(Session(), "NOPE", "NSE", CancellationToken.None));
        Assert.Equal(400, unlisted.HttpStatus);
        Assert.Contains("master contract", unlisted.Message);
        var badToken = await Assert.ThrowsAsync<BrokerException>(() => Plugin(new Script(_ => (200, "{}")), (s, e) => Row(s, "x", e)).GetQuoteAsync(Session(), "SBIN", "NSE", CancellationToken.None));
        Assert.Contains("no token", badToken.Message);
    }

    [Fact]
    public async Task Today_is_taken_in_india_and_the_low_is_the_smallest_price_that_is_set()
    {
        var quote = """{"status":"SUCCESS","data":{"ltp":10000,"open":9000,"high":0,"low":0,"volume":7}}""";
        var late = new DateTimeOffset(2026, 11, 11, 20, 0, 0, TimeSpan.Zero);
        var plugin = Plugin(new Script(_ => (200, quote)), null, null, new Fixed(late));
        var rows = await plugin.GetHistoryAsync(Session(), Row("SBIN", "3045"), "D", new DateOnly(2026, 11, 12), new DateOnly(2026, 11, 12), CancellationToken.None);
        Assert.Single(rows);
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), rows[0]!["timestamp"]!.GetValue<long>());
        Assert.Equal(90.0, rows[0]!["low"]!.GetValue<double>());
        Assert.Equal(100.0, rows[0]!["high"]!.GetValue<double>());
        Assert.Empty(await plugin.GetHistoryAsync(Session(), Row("SBIN", "3045"), "D", new DateOnly(2026, 11, 13), new DateOnly(2026, 11, 13), CancellationToken.None));
        Assert.Empty(await plugin.GetHistoryAsync(Session(), Row("SBIN", "3045"), "D", new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 11), CancellationToken.None));
        var refused = await Assert.ThrowsAsync<BrokerException>(() => plugin.GetHistoryAsync(Session(), Row("SBIN", "3045"), "5m", new DateOnly(2026, 11, 12), new DateOnly(2026, 11, 12), CancellationToken.None));
        Assert.Equal(400, refused.HttpStatus);
        var allZero = Plugin(new Script(_ => (200, """{"status":"SUCCESS","data":{"ltp":0,"open":0,"high":0,"low":0}}""")), null, null, new Fixed(late));
        Assert.Empty(await allZero.GetHistoryAsync(Session(), Row("SBIN", "3045"), "D", new DateOnly(2026, 11, 12), new DateOnly(2026, 11, 12), CancellationToken.None));
    }

    private sealed class Fixed(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // ---- sign-in ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Sign_in_asks_for_each_missing_detail_by_name()
    {
        var keys = new Dictionary<string, string> { ["ApiKey"] = "key" };
        var plugin = Plugin(new Script(_ => (200, "{}")));
        var all = new Dictionary<string, string> { ["clientcode"] = "C", ["password"] = "p", ["dob"] = "01/01/1990", ["totp"] = "123456" };
        Assert.Equal(409, (await plugin.LoginAsync(new Dictionary<string, string>(), all, CancellationToken.None)).HttpStatus);
        Assert.Equal(409, (await plugin.LoginAsync(new Dictionary<string, string> { ["ApiKey"] = " " }, all, CancellationToken.None)).HttpStatus);
        foreach (var (missing, text) in new[] { ("clientcode", "client code"), ("password", "password"), ("dob", "date of birth"), ("totp", "authenticator") })
        {
            foreach (var value in new[] { null, "", " " })
            {
                var input = new Dictionary<string, string>(all);
                if (value is null) input.Remove(missing);
                else input[missing] = value;
                var result = await plugin.LoginAsync(keys, input, CancellationToken.None);
                Assert.False(result.Ok);
                if (missing == "password" && value == " ") Assert.False(result.Ok is false && result.Message.Length == 0);
                else Assert.Contains(text, result.Message);
            }
        }
    }

    [Fact]
    public async Task The_sign_in_hashes_the_password_with_the_key_and_the_access_token_step_needs_the_secret()
    {
        var http = new Script(r => r.Path.Contains("authdirectapi") ? (200, """{"status":"SUCCESS","AuthToken":"A","isAuthTokenVerified":"TRUE"}""") : (200, """{"status":"SUCCESS","accesstoken":"X"}"""));
        var input = new Dictionary<string, string> { ["clientcode"] = "C", ["password"] = "p", ["dob"] = "01/01/1990", ["totp"] = "123456" };
        var result = await Plugin(http).LoginAsync(new Dictionary<string, string> { ["ApiKey"] = "key", ["ApiSecret"] = "s" }, input, CancellationToken.None);
        Assert.Equal("A:::X", result.AuthToken);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("pkey"))), JsonNode.Parse(http.Requests[0].Body)!["password"]!.GetValue<string>());
        Assert.False(http.Requests[0].Headers.ContainsKey("authorization"));
        Assert.Equal("A", http.Requests[1].Headers["authorization"]);
        var plain = new Script(r => r.Path.Contains("authdirectapi") ? (200, """{"status":"SUCCESS","AuthToken":"A"}""") : (200, """{"status":"SUCCESS","accesstoken":"X"}"""));
        Assert.Equal("A", (await Plugin(plain).LoginAsync(new Dictionary<string, string> { ["ApiKey"] = "key" }, input, CancellationToken.None)).AuthToken);
        Assert.Single(plain.Requests);
        var down = await Plugin(new Down()).LoginAsync(new Dictionary<string, string> { ["ApiKey"] = "key" }, input, CancellationToken.None);
        Assert.Equal(502, down.HttpStatus);
        Assert.Contains("Could not reach", down.Message);
        var withDown = new Script(r => r.Path.Contains("authdirectapi") ? (200, """{"status":"SUCCESS","AuthToken":"A"}""") : throw new HttpRequestException("x"));
        Assert.Equal("A", (await Plugin(withDown).LoginAsync(new Dictionary<string, string> { ["ApiKey"] = "key", ["ApiSecret"] = "s" }, input, CancellationToken.None)).AuthToken);
    }

    // ---- packets and the store ---------------------------------------------------------------------------------

    private static byte[] Packet(char exchange, int scrip, char kind, byte[] body)
    {
        var bytes = new byte[30];
        bytes[0] = (byte)exchange;
        BitConverter.GetBytes(scrip).CopyTo(bytes, 1);
        bytes[9] = (byte)kind;
        body.CopyTo(bytes, 10);
        return bytes;
    }

    private static byte[] Floats(params float[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();

    [Fact]
    public void A_wire_byte_of_128_or_more_names_no_exchange_and_an_unknown_packet_is_not_counted()
    {
        var book = new MotilalBook();
        var wide = Packet('N', 5, 'A', Floats(1f, 0, 0, 0, 0));
        wide[0] = 128;
        Assert.Single(book.Feed(wide));
        Assert.Null(book.GetQuote("NSE", 5));
        var type = Packet('N', 5, 'A', Floats(1f, 0, 0, 0, 0));
        type[9] = 128;
        Assert.Empty(book.Feed(type));
        Assert.Empty(book.Feed(Packet('N', 5, 'Z', new byte[20])));
        Assert.Single(book.Feed(Packet('N', 5, 'A', Floats(1f, 0, 0, 0, 0))));
    }

    [Fact]
    public void Unregistering_a_different_exchange_on_the_same_wire_character_keeps_the_registration()
    {
        var book = new MotilalBook();
        book.Register("NSEFO", 7);
        book.Unregister("NSE", 7);
        book.Feed(Packet('N', 7, 'A', Floats(2f, 0, 0, 0, 0)));
        Assert.NotNull(book.GetQuote("NSEFO", 7));
        Assert.Null(book.GetQuote("NSE", 7));
        book.Unregister("NSEFO", 7);
        book.Feed(Packet('N', 7, 'A', Floats(3f, 0, 0, 0, 0)));
        Assert.NotNull(book.GetQuote("NSE", 7));
    }

    [Fact]
    public void A_snapshot_with_a_book_of_one_side_only_or_a_book_without_prices_is_not_whole()
    {
        var book = new MotilalBook();
        byte[] Level(float bid, float ask) => BitConverter.GetBytes(bid).Concat(BitConverter.GetBytes(100)).Concat(new byte[2]).Concat(BitConverter.GetBytes(ask)).Concat(BitConverter.GetBytes(110)).Concat(new byte[2]).ToArray();
        book.Feed(Packet('N', 1, 'A', Floats(5f, 0, 0, 0, 0)));
        book.Feed(Packet('N', 1, 'G', Floats(1f, 2f, 0.5f, 1f).Concat(new byte[4]).ToArray()));
        for (var i = 0; i < 5; i++) book.Feed(Packet('N', 1, "BCDEF"[i], Level(5f, 6f)));
        Assert.True(book.HasSnapshot("NSE", 1, false, true));
        var one = new MotilalBook();
        one.Feed(Packet('N', 1, 'A', Floats(5f, 0, 0, 0, 0)));
        one.Feed(Packet('N', 1, 'G', Floats(1f, 2f, 0.5f, 1f).Concat(new byte[4]).ToArray()));
        one.Feed(Packet('N', 1, 'B', Level(5f, 0f)));
        Assert.False(one.HasSnapshot("NSE", 1, false, false));
        one.Feed(Packet('N', 1, 'B', Level(0f, 6f)));
        Assert.False(one.HasSnapshot("NSE", 1, false, false));
        one.Feed(Packet('N', 1, 'C', Level(5f, 6f)));
        one.Feed(Packet('N', 1, 'D', Level(5f, 6f)));
        one.Feed(Packet('N', 1, 'E', Level(5f, 6f)));
        one.Feed(Packet('N', 1, 'F', Level(5f, 6f)));
        Assert.True(one.HasSnapshot("NSE", 1, false, true));
    }

    // ---- the codec ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_codec_sends_bytes_keeps_each_mode_apart_and_writes_whole_numbers_as_whole_numbers()
    {
        var codec = new MotilalFeedCodec();
        Assert.True(codec.MessagesAreHex);
        codec.Begin(new BrokerSession(Guid.Empty, "motilal", "t", null, "MO100", new Dictionary<string, string>()));
        codec.Resubscribe(0);
        codec.DecodeBinary(new byte[30], 0);
        codec.Subscribe([new FeedRequest("NSE", "SBIN", "3045", "NSE", StreamMode.Depth), new FeedRequest("NSE", "SBIN", "3045", "NSE", StreamMode.Quote)], 0);
        codec.Unsubscribe(new FeedTarget("NSE", "SBIN", "3045", "NSE"), StreamMode.Depth, 0);
        var frame = Packet('N', 3045, 'A', Floats(805.5f, 25f, 0, 0, 0).Take(0).Concat(BitConverter.GetBytes(805.5f)).Concat(BitConverter.GetBytes(25)).Concat(BitConverter.GetBytes(1234)).Concat(BitConverter.GetBytes(804f)).Concat(BitConverter.GetBytes(0)).ToArray());
        var ticks = codec.DecodeBinary(frame, 99);
        var tick = Assert.Single(ticks);
        Assert.Equal("NSE_SBIN_QUOTE", tick.Topic);
        var json = tick.Data.ToJsonString();
        Assert.Contains("\"ltq\":25,", json);
        Assert.Contains("\"volume\":1234,", json);
        Assert.Contains("\"ltp\":805.5", json);
    }

    [Fact]
    public void A_depth_tick_needs_a_quote_and_at_least_one_level_and_a_mode_two_tick_needs_a_quote()
    {
        var codec = new MotilalFeedCodec();
        codec.Begin(new BrokerSession(Guid.Empty, "motilal", "t", null, "MO100", new Dictionary<string, string>()));
        codec.Resubscribe(0);
        codec.DecodeBinary(new byte[30], 0);
        codec.Subscribe([new FeedRequest("NSE", "SBIN", "3045", "NSE", StreamMode.Depth), new FeedRequest("NSE", "SBIN", "3045", "NSE", StreamMode.Ltp), new FeedRequest("NSE", "SBIN", "3045", "NSE", StreamMode.Quote)], 0);
        byte[] Level(float bid, float ask) => BitConverter.GetBytes(bid).Concat(BitConverter.GetBytes(100)).Concat(new byte[2]).Concat(BitConverter.GetBytes(ask)).Concat(BitConverter.GetBytes(110)).Concat(new byte[2]).ToArray();
        Assert.Empty(codec.DecodeBinary(Packet('N', 3045, 'B', Level(5f, 6f)), 0));
        Assert.Equal(["NSE_SBIN_DEPTH", "NSE_SBIN_LTP", "NSE_SBIN_QUOTE"], codec.DecodeBinary(Packet('N', 3045, 'A', Floats(5f, 0, 0, 0, 0)), 0).Select(t => t.Topic));
        Assert.Equal(["NSE_SBIN_DEPTH"], codec.DecodeBinary(Packet('N', 3045, 'B', Level(0f, 0f)), 0).Select(t => t.Topic).Intersect(["NSE_SBIN_DEPTH"]));
        var other = new MotilalFeedCodec();
        other.Begin(new BrokerSession(Guid.Empty, "motilal", "t", null, "MO100", new Dictionary<string, string>()));
        other.Resubscribe(0);
        other.DecodeBinary(new byte[30], 0);
        other.Subscribe([new FeedRequest("NSE", "SBIN", "3045", "NSE", StreamMode.Depth)], 0);
        other.DecodeBinary(Packet('N', 3045, 'A', Floats(5f, 0, 0, 0, 0)), 0);
        Assert.Empty(other.DecodeBinary(Packet('N', 3045, 'W', Floats(1f, 2f, 0, 0, 0)), 0));
    }

    // ---- the plugin's feed and the socket ----------------------------------------------------------------------

    private sealed class Capture : IMotilalSockets
    {
        public List<TimeSpan> Waits { get; } = [];

        public List<byte[]> Frames { get; } = [];

        public Func<byte[], bool>? OnFrame { get; private set; }

        public Task<bool> RunAsync(Uri address, byte[] login, IReadOnlyList<MotilalMessage> subscribes, IReadOnlyList<MotilalMessage> unsubscribes, Func<byte[], bool> onFrame, TimeSpan wait, CancellationToken cancellationToken)
        {
            Waits.Add(wait);
            OnFrame = onFrame;
            var stops = Frames.Select(f => onFrame(f)).ToList();
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task The_waits_are_three_seconds_for_depth_and_an_index_and_two_to_five_for_several_quotes()
    {
        var sockets = new Capture();
        var plugin = Plugin(new Script(_ => (200, "{}")), (s, e) => Row(s, "5", e == "NSE_INDEX" ? "NSE_INDEX" : e), sockets);
        await plugin.GetDepthAsync(Session(), "SBIN", "NSE", CancellationToken.None);
        await Assert.ThrowsAsync<BrokerException>(() => plugin.GetDepthAsync(Session(), "NIFTY", "NSE_INDEX", CancellationToken.None));
        await plugin.GetMultiQuotesAsync(Session(), Enumerable.Range(1, 3).Select(i => Row("S" + i, i.ToString())).ToList(), CancellationToken.None);
        await plugin.GetMultiQuotesAsync(Session(), Enumerable.Range(1, 30).Select(i => Row("S" + i, i.ToString())).ToList(), CancellationToken.None);
        await plugin.GetMultiQuotesAsync(Session(), Enumerable.Range(1, 100).Select(i => Row("S" + i, i.ToString())).ToList(), CancellationToken.None);
        Assert.Equal([3.0, 3.0, 2.0, 3.0, 5.0], sockets.Waits.Select(w => w.TotalSeconds));
    }

    [Fact]
    public async Task A_hundred_quotes_are_one_batch_and_an_mcx_index_is_refused_after_signing_in()
    {
        var sockets = new Capture();
        var plugin = Plugin(new Script(_ => (200, "{}")), (s, e) => Row(s, "5", e), sockets);
        await plugin.GetMultiQuotesAsync(Session(), Enumerable.Range(1, 100).Select(i => Row("S" + i, i.ToString())).ToList(), CancellationToken.None);
        Assert.Single(sockets.Waits);
        var refused = await Assert.ThrowsAsync<BrokerException>(() => plugin.GetDepthAsync(Session(), "CRUDE", "MCX_INDEX", CancellationToken.None));
        Assert.Equal(400, refused.HttpStatus);
        Assert.Contains("NSE and BSE indices only", refused.Message);
        Assert.True(sockets.OnFrame!(new byte[30]));
        Assert.Equal(TimeSpan.Zero, sockets.Waits.Last());
    }

    [Fact]
    public async Task Depth_keeps_waiting_until_the_whole_book_is_there_and_a_zero_index_value_is_no_value()
    {
        var sockets = new Capture();
        var plugin = Plugin(new Script(_ => (200, "{}")), (s, e) => Row(s, "5", e), sockets);
        await plugin.GetDepthAsync(Session(), "SBIN", "NSE", CancellationToken.None);
        byte[] Level(float bid, float ask) => BitConverter.GetBytes(bid).Concat(BitConverter.GetBytes(100)).Concat(new byte[2]).Concat(BitConverter.GetBytes(ask)).Concat(BitConverter.GetBytes(110)).Concat(new byte[2]).ToArray();
        var frame = Packet('N', 5, 'A', Floats(5f, 0, 0, 0, 0)).Concat(Packet('N', 5, 'G', Floats(1f, 2f, 1f, 1f).Concat(new byte[4]).ToArray())).Concat(Packet('N', 5, 'B', Level(5f, 6f))).ToArray();
        Assert.False(sockets.OnFrame!(frame));
        var index = Plugin(new Script(_ => (200, "{}")), (s, e) => Row(s, "5", e), new Capture { });
        await Assert.ThrowsAsync<BrokerException>(() => index.GetDepthAsync(Session(), "NIFTY", "NSE_INDEX", CancellationToken.None));
        var zero = new Capture();
        zero.Frames.Add(Packet('N', 5, 'H', Floats(0f, 0, 0, 0, 0)));
        await Assert.ThrowsAsync<BrokerException>(() => Plugin(new Script(_ => (200, "{}")), (s, e) => Row(s, "5", e), zero).GetDepthAsync(Session(), "NIFTY", "NSE_INDEX", CancellationToken.None));
    }

    [Fact]
    public void The_stream_sits_on_the_broadcast_feed_and_needs_a_client_code_and_a_session()
    {
        var plugin = Plugin(new Script(_ => (200, "{}")));
        Assert.Null(plugin.FeedKeepAliveText);
        Assert.Equal(TimeSpan.FromSeconds(30), plugin.FeedKeepAliveEvery);
        Assert.Equal("wss://ws1feed.motilaloswal.com/jwebsocket/jwebsocket", plugin.FeedEndpoint(Session(), null)!.Url.OriginalString);
        Assert.Null(plugin.FeedEndpoint(new BrokerSession(Guid.Empty, "motilal", "t", null, null, new Dictionary<string, string>()), null));
        Assert.Null(plugin.FeedEndpoint(new BrokerSession(Guid.Empty, "motilal", "", null, "MO100", new Dictionary<string, string>()), null));
        Assert.NotNull(plugin.FeedEndpoint(new BrokerSession(Guid.Empty, "motilal", "t", null, " ", new Dictionary<string, string>()) with { ClientId = "x" }, "wss://elsewhere/ws"));
        Assert.Equal("wss://elsewhere/ws", plugin.FeedEndpoint(Session(), "wss://elsewhere/ws")!.Url.OriginalString);
        Assert.NotNull(plugin.CreateFeedCodec());
    }

    /// <summary>A feed server of our own on the loopback: the real socket client talks to it.</summary>
    private static async Task<(HttpListener Listener, Uri Address, Task Serving, List<string> Received)> Server(Func<WebSocket, List<string>, Task> serve)
    {
        var listener = new HttpListener();
        var port = new Random().Next(20000, 40000);
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var received = new List<string>();
        var serving = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            try
            {
                await serve(socket, received);
            }
            catch (WebSocketException)
            {
            }
        });
        await Task.Yield();
        return (listener, new Uri($"ws://127.0.0.1:{port}/"), serving, received);
    }

    private static async Task<(string Kind, byte[] Bytes)> Receive(WebSocket socket)
    {
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        return (result.MessageType.ToString(), buffer[..result.Count]);
    }

    [Fact]
    public async Task The_real_connection_signs_in_subscribes_reads_until_it_is_done_and_unsubscribes()
    {
        var sockets = new MotilalSnapshotSocket();
        var (listener, address, serving, received) = await Server(async (socket, log) =>
        {
            var login = await Receive(socket);
            log.Add("login:" + login.Bytes.Length + ":" + login.Kind);
            await socket.SendAsync(new byte[30], WebSocketMessageType.Text, true, CancellationToken.None);
            await socket.SendAsync(new byte[30], WebSocketMessageType.Binary, true, CancellationToken.None);
            var sub = await Receive(socket);
            log.Add("sub:" + Convert.ToHexString(sub.Bytes) + ":" + sub.Kind);
            var text = await Receive(socket);
            log.Add("text:" + Encoding.UTF8.GetString(text.Bytes) + ":" + text.Kind);
            await socket.SendAsync(Packet('N', 1, 'A', Floats(5f, 0, 0, 0, 0)), WebSocketMessageType.Binary, true, CancellationToken.None);
            var unsub = await Receive(socket);
            log.Add("unsub:" + Convert.ToHexString(unsub.Bytes));
            var close = await Receive(socket);
            log.Add("closed:" + close.Kind);
        });
        var frames = new List<int>();
        var ok = await sockets.RunAsync(address, MotilalBook.LoginPacket("MO100"), [MotilalMessage.Of(MotilalBook.RegisterPacket("NSE", "CASH", 1, true)), MotilalMessage.Of("hello")],
            [MotilalMessage.Of(MotilalBook.RegisterPacket("NSE", "CASH", 1, false))], f =>
            {
                frames.Add(f.Length);
                return f[9] == (byte)'A';
            }, TimeSpan.FromSeconds(5), CancellationToken.None);
        await serving.WaitAsync(TimeSpan.FromSeconds(10));
        listener.Stop();
        Assert.True(ok);
        Assert.Equal([30, 30], frames);
        Assert.Equal("login:114:Binary", received[0]);
        Assert.Equal("sub:" + Convert.ToHexString(MotilalBook.RegisterPacket("NSE", "CASH", 1, true)) + ":Binary", received[1]);
        Assert.Equal("text:hello:Text", received[2]);
        Assert.Equal("unsub:" + Convert.ToHexString(MotilalBook.RegisterPacket("NSE", "CASH", 1, false)), received[3]);
    }

    [Fact]
    public async Task The_real_connection_gives_up_when_the_server_never_signs_it_in_and_reads_what_it_can_when_the_server_closes()
    {
        var sockets = new MotilalSnapshotSocket();
        var (listener, address, serving, _) = await Server(async (socket, _) =>
        {
            await Receive(socket);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        });
        Assert.False(await sockets.RunAsync(address, MotilalBook.LoginPacket("MO100"), [], [], _ => true, TimeSpan.FromSeconds(1), CancellationToken.None));
        await serving.WaitAsync(TimeSpan.FromSeconds(10));
        listener.Stop();
        var (second, secondAddress, secondServing, _) = await Server(async (socket, _) =>
        {
            await Receive(socket);
            await socket.SendAsync(new byte[30], WebSocketMessageType.Binary, true, CancellationToken.None);
            await Receive(socket);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        });
        var seen = 0;
        Assert.True(await sockets.RunAsync(secondAddress, MotilalBook.LoginPacket("MO100"), [MotilalMessage.Of(MotilalBook.RegisterPacket("NSE", "CASH", 1, true))], [], _ =>
        {
            seen++;
            return false;
        }, TimeSpan.FromSeconds(3), CancellationToken.None));
        await secondServing.WaitAsync(TimeSpan.FromSeconds(10));
        second.Stop();
        Assert.Equal(1, seen);
        Assert.False(await sockets.RunAsync(new Uri("ws://127.0.0.1:1/"), MotilalBook.LoginPacket("MO100"), [], [], _ => true, TimeSpan.FromSeconds(1), CancellationToken.None));
    }
}
