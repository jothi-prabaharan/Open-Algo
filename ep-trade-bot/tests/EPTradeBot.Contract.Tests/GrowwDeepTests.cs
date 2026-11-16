using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Streaming;
using EPTradeBot.Core.Streaming.Feeds;
using EPTradeBot.Infrastructure.Brokers.Groww;
using Xunit;

namespace EPTradeBot.Contract.Tests;

/// <summary>Groww's plugin and codec called directly, with answers scripted a request at a time: the paths and boundaries the recorded cases do not reach.</summary>
public sealed class GrowwDeepTests
{
    private sealed record Seen(string Method, string Path, string Query, string Body);

    private sealed class Script(Func<Seen, (int Status, string Body)> answer) : IHttpClientFactory
    {
        private readonly Func<Seen, (int Status, string Body)> reply = answer;

        public List<Seen> Requests { get; } = [];

        public IEnumerable<Seen> To(string path) => Requests.Where(r => r.Path == path);

        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler(Script owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var seen = new Seen(request.Method.Method, request.RequestUri!.AbsolutePath, Uri.UnescapeDataString(request.RequestUri.Query), request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
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

    /// <summary>Every symbol of the NSE and BSE and NFO is in the master: its broker symbol is its name, and its token is its position.</summary>
    private sealed class Master(Func<string, string, SymbolRecord?>? find = null) : ISymbolMaster
    {
        public static SymbolRecord Row(string symbol, string exchange, string? expiry = null, string type = "EQ", double? strike = null) =>
            new(symbol, symbol, exchange, exchange is "NFO" or "NSE_INDEX" ? "NSE" : exchange is "BFO" or "BSE_INDEX" ? "BSE" : exchange, "100", symbol, expiry, strike, 1, type, 0.05);

        public Task<SymbolRecord?> FindAsync(string broker, string symbol, string exchange, CancellationToken cancellationToken) => Task.FromResult(find is null ? Row(symbol, exchange) : find(symbol, exchange));

        public Task<SymbolRecord?> FindByBrokerSymbolAsync(string broker, string brokerSymbol, string exchange, CancellationToken cancellationToken) => Task.FromResult(exchange == "NSE" ? Row(brokerSymbol, exchange) : null);

        public Task<SymbolRecord?> FindByTokenAsync(string broker, string token, string exchange, CancellationToken cancellationToken) => Task.FromResult<SymbolRecord?>(null);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static GrowwPlugin Plugin(IHttpClientFactory http, ISymbolMaster? master = null, TimeProvider? clock = null)
    {
        var options = new EPTradeBot.Core.Configuration.BrokerOptions();
        options.PacingMs["groww"] = 0;
        options.RetryBackoffMs["groww"] = 0;
        return new GrowwPlugin(http, new EPTradeBot.Infrastructure.Brokers.BrokerSessionFactory(null!, new Options(options)), master ?? new Master(), clock ?? TimeProvider.System);
    }

    private static readonly BrokerSession Session = new(Guid.Empty, "groww", "tok", null, null, new Dictionary<string, string>());

    private static BrokerOrder Order(int quantity = 10, string symbol = "SBIN") => new(symbol, "NSE", "BUY", quantity, "LIMIT", "MIS", 800.5m, 0, 0, "s");

    private static BrokerOrderChange Change(string id = "GM1", int quantity = 10) => new(id, "SBIN", "NSE", "BUY", quantity, "LIMIT", "MIS", 800.5m, 0, 0);

    private const string Ok = """{"status":"SUCCESS","payload":{}}""";

    private static string Failure(string message = "Bad") => $$$"""{"status":"FAILURE","error":{"code":"X","message":"{{{message}}}"}}""";

    // ---- orders ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_order_that_could_not_be_sent_may_have_been_placed()
    {
        var result = await Plugin(new Down()).PlaceAsync(Session, Order(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal(500, result.HttpStatus);
        Assert.True(result.MayHaveBeenSent);
        Assert.Equal("Could not reach Groww to place the order. Check the order book before retrying.", result.Message);
    }

    [Theory]
    [InlineData(200, "<html>", 502, true, "sent an answer that could not be read")]
    [InlineData(200, """{"status":"SUCCESS","payload":{"order_status":"OPEN"}}""", 502, true, "did not return an order ID")]
    [InlineData(200, """{"status":"SUCCESS","payload":{"groww_order_id":"G1","order_status":"FAILED","remark":"Out of range"}}""", 400, false, "Out of range")]
    [InlineData(200, """{"status":"SUCCESS","payload":{"groww_order_id":"G1","order_status":"REJECTED"}}""", 400, false, "Groww rejected the order")]
    [InlineData(200, """{"status":"FAILURE","error":{"message":"No margin"}}""", 400, false, "No margin")]
    [InlineData(400, """{"status":"FAILURE","error":{"message":"No margin"}}""", 400, false, "No margin")]
    [InlineData(401, """{"status":"FAILURE","error":{"message":"Bad token"}}""", 401, false, "Bad token")]
    [InlineData(500, """{}""", 500, false, "Groww did not accept the order (HTTP 500)")]
    [InlineData(404, """{"message":"Gone"}""", 404, false, "Gone")]
    public async Task What_groww_answers_to_an_order_is_what_the_caller_is_told(int status, string body, int expectedStatus, bool mayHaveBeenSent, string expectedText)
    {
        var result = await Plugin(new Script(_ => (status, body))).PlaceAsync(Session, Order(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal(expectedStatus, result.HttpStatus);
        Assert.Equal(mayHaveBeenSent, result.MayHaveBeenSent);
        Assert.Contains(expectedText, result.Message);
    }

    [Fact]
    public async Task An_order_with_nothing_to_send_is_refused_before_it_is_sent()
    {
        var http = new Script(_ => (200, Ok));
        var plugin = Plugin(http);
        foreach (var quantity in new[] { 0, -1 })
        {
            var result = await plugin.PlaceAsync(Session, Order(quantity), CancellationToken.None);
            Assert.Equal((false, 400), (result.Ok, result.HttpStatus));
            Assert.Contains("Invalid quantity format", result.Message);
        }

        var unknown = await Plugin(http, new Master((_, _) => null)).PlaceAsync(Session, Order(), CancellationToken.None);
        Assert.Equal(400, unknown.HttpStatus);
        Assert.Contains("is not in the NSE master contract", unknown.Message);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task A_change_that_could_not_be_sent_or_read_may_have_been_made()
    {
        var down = await Plugin(new Down()).ModifyAsync(Session, Change(), CancellationToken.None);
        Assert.Equal((false, 500, true), (down.Ok, down.HttpStatus, down.MayHaveBeenSent));
        Assert.Contains("Could not reach Groww to modify the order", down.Message);
        var unread = await Plugin(new Script(_ => (200, "<html>"))).ModifyAsync(Session, Change(), CancellationToken.None);
        Assert.Equal((false, 502, true), (unread.Ok, unread.HttpStatus, unread.MayHaveBeenSent));
        Assert.Contains("the change may have been made", unread.Message);
    }

    [Theory]
    [InlineData(200, 400)]
    [InlineData(400, 400)]
    [InlineData(401, 401)]
    [InlineData(500, 500)]
    public async Task A_change_groww_refuses_keeps_its_status_and_reason(int status, int expected)
    {
        var result = await Plugin(new Script(_ => (status, Failure("Not open")))).ModifyAsync(Session, Change(), CancellationToken.None);
        Assert.Equal((false, expected, false), (result.Ok, result.HttpStatus, result.MayHaveBeenSent));
        Assert.Equal("Not open", result.Message);
        var bare = await Plugin(new Script(_ => (status, "{}"))).ModifyAsync(Session, Change(), CancellationToken.None);
        Assert.Contains("Groww could not modify order GM1", bare.Message);
    }

    [Fact]
    public async Task A_change_with_nothing_to_change_is_refused()
    {
        var plugin = Plugin(new Script(_ => (200, Ok)));
        var noId = await plugin.ModifyAsync(Session, Change(""), CancellationToken.None);
        Assert.Equal((false, 400, "Order ID (orderid) is required for order modification"), (noId.Ok, noId.HttpStatus, noId.Message));
        foreach (var quantity in new[] { 0, -5 })
        {
            var result = await plugin.ModifyAsync(Session, Change("GM1", quantity), CancellationToken.None);
            Assert.Equal((false, 400), (result.Ok, result.HttpStatus));
            Assert.Contains("Invalid quantity format", result.Message);
        }

        Assert.True((await plugin.ModifyAsync(Session, Change(), CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task A_cancel_that_could_not_be_sent_or_read_may_have_been_made()
    {
        var down = await Plugin(new Down()).CancelAsync(Session, "GM1", CancellationToken.None);
        Assert.Equal((false, 500, true), (down.Ok, down.HttpStatus, down.MayHaveBeenSent));
        var unread = await Plugin(new Script(r => r.Path == "/v1/order/cancel" ? (200, "<html>") : (200, """{"status":"SUCCESS","payload":{"order_list":[]}}"""))).CancelAsync(Session, "GM1", CancellationToken.None);
        Assert.Equal((false, 502, true), (unread.Ok, unread.HttpStatus, unread.MayHaveBeenSent));
        Assert.Contains("may have been cancelled", unread.Message);
    }

    [Theory]
    [InlineData(200, 400)]
    [InlineData(401, 401)]
    [InlineData(404, 404)]
    public async Task A_cancel_groww_refuses_in_every_segment_keeps_the_last_answer(int status, int expected)
    {
        var http = new Script(r => r.Path == "/v1/order/cancel" ? (status, Failure("No such order")) : (200, """{"status":"SUCCESS","payload":{"order_list":[]}}"""));
        var result = await Plugin(http).CancelAsync(Session, "GM1", CancellationToken.None);
        Assert.Equal((false, expected, false), (result.Ok, result.HttpStatus, result.MayHaveBeenSent));
        Assert.Equal("No such order", result.Message);
        Assert.Equal(["CASH", "FNO"], http.To("/v1/order/cancel").Select(r => JsonNode.Parse(r.Body)!["segment"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_cancel_goes_to_the_segment_the_order_book_names_and_to_both_when_it_does_not()
    {
        string Book(string id, string segment) => $$$"""{"status":"SUCCESS","payload":{"order_list":[{"groww_order_id":"{{{id}}}","segment":"{{{segment}}}"}]}}""";
        var known = new Script(r => r.Path == "/v1/order/cancel" ? (200, Ok) : r.Query.Contains("CASH") ? (200, Book("GM1", "FNO")) : (200, Book("GM2", "FNO")));
        Assert.True((await Plugin(known).CancelAsync(Session, "GM1", CancellationToken.None)).Ok);
        Assert.Equal(["FNO"], known.To("/v1/order/cancel").Select(r => JsonNode.Parse(r.Body)!["segment"]!.GetValue<string>()));
        var odd = new Script(r => r.Path == "/v1/order/cancel" ? (200, Failure()) : (200, Book("GM1", "COMMODITY")));
        await Plugin(odd).CancelAsync(Session, "GM1", CancellationToken.None);
        Assert.Equal(2, odd.To("/v1/order/cancel").Count());
        var unreadable = new Script(r => r.Path == "/v1/order/cancel" ? (200, Failure()) : (500, Failure()));
        await Plugin(unreadable).CancelAsync(Session, "GM1", CancellationToken.None);
        Assert.Equal(2, unreadable.To("/v1/order/cancel").Count());
    }

    // ---- positions, holdings, books -------------------------------------------------------------------------------

    private static string Positions(params (string Symbol, string Exchange, double Quantity, double Average)[] rows)
    {
        var items = rows.Select(r => $$"""{"trading_symbol":"{{r.Symbol}}","exchange":"{{r.Exchange}}","segment":"CASH","product":"MIS","quantity":{{r.Quantity}},"net_price":{{r.Average}},"credit_price":{{r.Average}},"debit_price":0,"realised_pnl":0}""");
        return """{"status":"SUCCESS","payload":{"positions":[""" + string.Join(",", items) + "]}}";
    }

    [Fact]
    public async Task A_cash_book_that_could_not_be_read_says_why()
    {
        var down = await Assert.ThrowsAsync<BrokerException>(() => Plugin(new Script(r => r.Query.Contains("CASH") ? (500, Failure("Down")) : (200, Positions()))).GetPositionsAsync(Session, CancellationToken.None));
        Assert.Contains("Down", down.Message);
        var bare = await Assert.ThrowsAsync<BrokerException>(() => Plugin(new Script(r => r.Query.Contains("CASH") ? (500, "oops") : (200, Positions()))).GetPositionsAsync(Session, CancellationToken.None));
        Assert.Contains("HTTP 500", bare.Message);
        var unread = await Assert.ThrowsAsync<BrokerException>(() => Plugin(new Script(r => r.Query.Contains("CASH") ? (200, "\"x\"") : (200, Positions()))).GetPositionsAsync(Session, CancellationToken.None));
        Assert.Contains("the answer could not be read", unread.Message);
    }

    [Theory]
    [InlineData(400, false)]
    [InlineData(403, false)]
    [InlineData(499, false)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    public async Task An_fno_book_groww_refuses_for_the_account_is_skipped_and_one_it_could_not_give_is_not(int status, bool throws)
    {
        var http = new Script(r => r.Query.Contains("FNO") ? (status, Failure("No F&O")) : (200, Positions(("SBIN", "NSE", 10, 800))));
        var read = Plugin(http).GetPositionsAsync(Session, CancellationToken.None);
        if (throws) Assert.Contains("F&O positions could not be read", (await Assert.ThrowsAsync<BrokerException>(() => read)).Message);
        else Assert.Single(await read);
    }

    [Fact]
    public async Task An_fno_book_that_could_not_be_reached_or_read_is_not_skipped()
    {
        var calls = 0;
        var http = new Script(r => r.Query.Contains("FNO") ? (200, "<html>") : (200, Positions(("SBIN", "NSE", 10, 800))));
        await Assert.ThrowsAsync<BrokerException>(() => Plugin(http).GetPositionsAsync(Session, CancellationToken.None));
        var flaky = new Flaky(r => r.Query.Contains("FNO") ? throw new HttpRequestException("down") : (200, Positions(("SBIN", "NSE", 10, 800))));
        await Assert.ThrowsAsync<BrokerException>(() => Plugin(flaky).GetPositionsAsync(Session, CancellationToken.None));
        Assert.Equal(0, calls);
        var empty = new Script(r => r.Query.Contains("FNO") ? (404, Failure("No positions found")) : (200, Positions(("SBIN", "NSE", 10, 800))));
        Assert.Single(await Plugin(empty).GetPositionsAsync(Session, CancellationToken.None));
    }

    /// <summary>Like <see cref="Script"/>, but the answer may throw as a connection does.</summary>
    private sealed class Flaky(Func<Seen, (int Status, string Body)> answer) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(answer));

        private sealed class Handler(Func<Seen, (int Status, string Body)> answer) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var (status, body) = answer(new Seen(request.Method.Method, request.RequestUri!.AbsolutePath, Uri.UnescapeDataString(request.RequestUri.Query), request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
                return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            }
        }
    }

    [Fact]
    public async Task A_price_read_that_fails_leaves_the_positions_unpriced()
    {
        var flaky = new Flaky(r => r.Path == "/v1/live-data/ltp" ? throw new HttpRequestException("down") : (200, r.Query.Contains("CASH") ? Positions(("SBIN", "NSE", 10, 800)) : Positions()));
        var rows = await Plugin(flaky).GetPositionsAsync(Session, CancellationToken.None);
        Assert.Equal(0, rows[0]!["ltp"]!.GetValue<double>());
    }

    [Fact]
    public async Task Prices_are_asked_for_fifty_symbols_at_a_time_and_a_price_of_nothing_is_no_price()
    {
        var symbols = Enumerable.Range(0, 51).Select(i => ("S" + i, "NSE", 1.0, 100.0)).ToArray();
        var http = new Script(r => r.Path == "/v1/live-data/ltp" ? (200, """{"status":"SUCCESS","payload":{"NSE_S0":0,"NSE_S1":12.5}}""") : (200, r.Query.Contains("CASH") ? Positions(symbols) : Positions()));
        var rows = await Plugin(http).GetPositionsAsync(Session, CancellationToken.None);
        var asked = http.To("/v1/live-data/ltp").ToList();
        Assert.Equal(2, asked.Count);
        Assert.Equal(50, asked[0].Query.Split("exchange_symbols=")[1].Split(',').Length);
        Assert.Equal("NSE_S50", asked[1].Query.Split("exchange_symbols=")[1]);
        Assert.Equal(0, rows[0]!["ltp"]!.GetValue<double>());
        Assert.Equal(12.5, rows[1]!["ltp"]!.GetValue<double>());
    }

    [Fact]
    public async Task A_message_that_names_an_empty_book_is_short_and_in_a_message_field()
    {
        string Cash(string field, string text) => $$"""{"status":"FAILURE","{{field}}":"{{text}}"}""";
        Task<JsonArray> Read(string body) => Plugin(new Script(r => r.Query.Contains("CASH") ? (404, body) : (200, Positions()))).GetPositionsAsync(Session, CancellationToken.None);
        Assert.Empty(await Read(Cash("message", "No data found")));
        Assert.Empty(await Read(Cash("msg", "No Position Found")));
        Assert.Empty(await Read(Cash("s", "record not found")));
        await Assert.ThrowsAsync<BrokerException>(() => Read(Cash("message", "x" + new string(' ', 2000) + "no data")));
        await Assert.ThrowsAsync<BrokerException>(() => Read(Cash("other", "no data")));
        Assert.Empty(await Read(Cash("message", new string('x', 1990) + "no data")));
    }

    [Fact]
    public async Task Holdings_are_priced_and_rounded_to_paise()
    {
        var http = new Script(r => r.Path == "/v1/live-data/ltp"
            ? (200, """{"status":"SUCCESS","payload":{"NSE_SBIN":105.4567,"NSE_INFY":0}}""")
            : (200, """{"status":"SUCCESS","payload":{"holdings":[{"trading_symbol":"SBIN","quantity":3,"average_price":100.123},{"trading_symbol":"INFY","quantity":2,"average_price":10.5}]}}"""));
        var book = await Plugin(http).GetHoldingsAsync(Session, CancellationToken.None);
        var first = book.Rows[0]!;
        Assert.Equal(105.46, first["ltp"]!.GetValue<double>());
        Assert.Equal(16.0, first["pnl"]!.GetValue<double>());
        Assert.Equal(5.33, first["pnlpercent"]!.GetValue<double>());
        Assert.False(book.Rows[1]!.AsObject().ContainsKey("ltp"));
        Assert.Equal(0.0, book.Rows[1]!["pnl"]!.GetValue<double>());
        Assert.Equal(337.37, book.Statistics["totalholdingvalue"]!.GetValue<double>());
        Assert.Equal(321.37, book.Statistics["totalinvvalue"]!.GetValue<double>());
        Assert.Equal(16.0, book.Statistics["totalprofitandloss"]!.GetValue<double>());
        Assert.Equal(4.98, book.Statistics["totalpnlpercentage"]!.GetValue<double>());
    }

    [Fact]
    public async Task A_trade_without_an_order_id_belongs_to_the_order_it_was_read_for()
    {
        var http = new Script(r => r.Path switch
        {
            "/v1/order/list" => (200, r.Query.Contains("CASH") ? """{"status":"SUCCESS","payload":{"order_list":[{"groww_order_id":"GM2","trading_symbol":"SBIN","exchange":"NSE","segment":"CASH","filled_quantity":5}]}}""" : """{"status":"SUCCESS","payload":{"order_list":[]}}"""),
            _ => (200, """{"status":"SUCCESS","payload":{"trade_list":[{"groww_trade_id":"T1","trading_symbol":"SBIN","exchange":"NSE","quantity":5,"price":800}]}}"""),
        });
        var rows = await Plugin(http).GetTradeBookAsync(Session, CancellationToken.None);
        Assert.Equal("GM2", rows[0]!["orderid"]!.GetValue<string>());
    }

    // ---- margin --------------------------------------------------------------------------------------------------

    private static MarginLeg Leg(string symbol = "NIFTY", string exchange = "NFO") => new(Master.Row(symbol, exchange), "BUY", 75, "NRML", "MARKET", 0, 0);

    [Fact]
    public async Task Margin_with_nothing_to_price_or_that_groww_cannot_work_out_says_so()
    {
        var plugin = Plugin(new Script(_ => (200, Ok)));
        var none = await Assert.ThrowsAsync<BrokerException>(() => plugin.CalculateMarginAsync(Session, [], CancellationToken.None));
        Assert.Equal((400, "No positions to calculate margin for"), (none.HttpStatus, none.Message));
        foreach (var (status, body, expected, text) in new (int, string, int, string)[]
        {
            (200, "<html>", 400, "returned no margin data"), (500, "<html>", 500, "returned no margin data"), (200, """{"status":"FAILURE"}""", 400, "could not calculate the margin"),
            (500, """{"status":"FAILURE","error":{"message":"Down"}}""", 500, "Down"), (200, """{"status":"FAILURE","error":{"message":"No"}}""", 400, "No"),
        })
        {
            var ex = await Assert.ThrowsAsync<BrokerException>(() => Plugin(new Script(_ => (status, body))).CalculateMarginAsync(Session, [Leg()], CancellationToken.None));
            Assert.Equal(expected, ex.HttpStatus);
            Assert.Contains(text, ex.Message);
        }
    }

    // ---- sign-in -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("", "secret")]
    [InlineData("key", "")]
    [InlineData(" ", "secret")]
    public async Task Sign_in_needs_both_the_key_and_the_secret(string key, string secret)
    {
        var http = new Script(_ => (200, Ok));
        var result = await Plugin(http).LoginAsync(new Dictionary<string, string> { ["ApiKey"] = key, ["ApiSecret"] = secret }, new Dictionary<string, string>(), CancellationToken.None);
        Assert.Equal((false, 409), (result.Ok, result.HttpStatus));
        Assert.Contains("not both set on this server", result.Message);
        Assert.Empty(http.Requests);
        var absent = await Plugin(http).LoginAsync(new Dictionary<string, string> { ["ApiSecret"] = "s" }, new Dictionary<string, string>(), CancellationToken.None);
        Assert.Equal(409, absent.HttpStatus);
    }

    [Theory]
    [InlineData(429, "limiting login requests")]
    [InlineData(500, "not responding right now")]
    [InlineData(503, "not responding right now")]
    [InlineData(499, "Check the API key and secret")]
    [InlineData(401, "Check the API key and secret")]
    public async Task A_refused_sign_in_gives_advice_that_fits_why(int status, string advice)
    {
        var result = await Plugin(new Script(_ => (status, "{}"))).LoginAsync(new Dictionary<string, string> { ["ApiKey"] = "k", ["ApiSecret"] = "s" }, new Dictionary<string, string>(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains(advice, result.Message);
    }

    [Fact]
    public async Task A_sign_in_that_could_not_be_sent_or_that_expires_now_is_refused()
    {
        var keys = new Dictionary<string, string> { ["ApiKey"] = "k", ["ApiSecret"] = "s" };
        var down = await Plugin(new Down()).LoginAsync(keys, new Dictionary<string, string>(), CancellationToken.None);
        Assert.Equal((false, 502), (down.Ok, down.HttpStatus));
        Assert.Contains("Could not reach Groww to log in", down.Message);
        var now = new DateTimeOffset(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);
        var clock = new Clock(now);
        foreach (var (expiry, refused) in new[] { ("2026-11-11T10:00:00+00:00", true), ("2026-11-11T10:00:01+00:00", false), ("2026-11-11T09:59:59+00:00", true) })
        {
            var result = await Plugin(new Script(_ => (200, $$"""{"token":"t","isActive":true,"expiry":"{{expiry}}"}""")), null, clock).LoginAsync(keys, new Dictionary<string, string>(), CancellationToken.None);
            Assert.Equal(refused, !result.Ok);
        }
    }
}
