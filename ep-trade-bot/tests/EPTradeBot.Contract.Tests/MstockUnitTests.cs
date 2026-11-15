using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Infrastructure.Brokers.Mstock;

namespace EPTradeBot.Contract.Tests;

/// <summary>What the recorded cases cannot show: the headers every call carries, the paths an order can fail on after it was sent, the refusals of a sign-in that is missing something, and how many days one history request holds.</summary>
public sealed class MstockUnitTests
{
    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => start;
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
        public List<(string Path, HttpRequestMessage Request)> Seen { get; } = [];

        public HttpClient CreateClient(string name) => new(new Handler(this, answer));

        private sealed class Handler(Capture owner, Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Seen.Add((request.RequestUri!.AbsolutePath, request));
                return Task.FromResult(answer(request));
            }
        }
    }

    private static HttpResponseMessage Json(string body, int status = 200) => new((System.Net.HttpStatusCode)status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static MstockPlugin Plugin(IHttpClientFactory http, bool known = true)
    {
        var options = new EPTradeBot.Core.Configuration.BrokerOptions();
        options.RetryBackoffMs["mstock"] = 0;
        options.HistoryGapMs["mstock"] = 0;
        return new(http, new EPTradeBot.Infrastructure.Brokers.BrokerSessionFactory(null!, new Options(options)), new Master(known), new Clock(DateTimeOffset.Parse("2026-11-13T05:30:00Z")));
    }

    private static BrokerSession Session() => new(Guid.NewGuid(), "mstock", "tok", null, null, new Dictionary<string, string> { ["ApiKey"] = "client", ["ApiSecret"] = "private" });

    private static BrokerOrder Order() => new("SBIN", "NSE", "BUY", 1, "MARKET", "MIS", 0m, 0m, 0, "s");

    // ---- headers -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_call_carries_the_version_the_bearer_token_and_the_private_key()
    {
        var http = new Capture(_ => Json("""{"status":true,"data":{"orderid":"1"}}"""));
        await Plugin(http).PlaceAsync(Session(), Order(), CancellationToken.None);
        var request = http.Seen.Single().Request;
        Assert.Equal("1", request.Headers.GetValues("X-Mirae-Version").Single());
        Assert.Equal("private", request.Headers.GetValues("X-PrivateKey").Single());
        Assert.Equal("Bearer tok", request.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task The_sign_in_calls_carry_the_version_and_the_second_one_the_private_key()
    {
        var http = new Capture(request => request.RequestUri!.AbsolutePath.EndsWith("/connect/login", StringComparison.Ordinal)
            ? Json("""{"status":true,"data":{"refreshToken":"r"}}""")
            : Json("""{"status":true,"data":{"jwtToken":"j"}}"""));
        var result = await Plugin(http).LoginAsync(new Dictionary<string, string> { ["ApiKey"] = "client", ["ApiSecret"] = "private" }, new Dictionary<string, string> { ["password"] = "p", ["totp"] = "1" }, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.All(http.Seen, s => Assert.Equal("1", s.Request.Headers.GetValues("X-Mirae-Version").Single()));
        Assert.False(http.Seen[0].Request.Headers.Contains("X-PrivateKey"));
        Assert.Equal("private", http.Seen[1].Request.Headers.GetValues("X-PrivateKey").Single());
    }

    // ---- an order that may have been sent ------------------------------------------------------------------------

    [Fact]
    public async Task An_order_whose_call_fails_after_it_was_sent_says_it_may_have_been_placed()
    {
        var http = new Capture(_ => throw new HttpRequestException("reset"));
        var result = await Plugin(http).PlaceAsync(Session(), Order(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal(502, result.HttpStatus);
        Assert.True(result.MayHaveBeenSent);
        Assert.Contains("may have been placed", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_order_answered_with_something_that_is_not_json_says_it_may_have_been_placed()
    {
        var http = new Capture(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("<html>", System.Text.Encoding.UTF8, "text/html") });
        var result = await Plugin(http).PlaceAsync(Session(), Order(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal(502, result.HttpStatus);
        Assert.True(result.MayHaveBeenSent);
        Assert.Contains("could not confirm the order", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_order_accepted_with_no_number_says_it_cannot_be_followed()
    {
        var http = new Capture(_ => Json("""{"status":true,"data":{}}"""));
        var result = await Plugin(http).PlaceAsync(Session(), Order(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal(502, result.HttpStatus);
        Assert.True(result.MayHaveBeenSent);
        Assert.Contains("sent no order number", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_order_for_a_symbol_the_master_lacks_is_refused_with_400_before_anything_is_sent()
    {
        var http = new Capture(_ => Json("{}"));
        var result = await Plugin(http, known: false).PlaceAsync(Session(), Order(), CancellationToken.None);
        Assert.Equal(400, result.HttpStatus);
        Assert.Empty(http.Seen);
    }

    [Fact]
    public async Task A_refusal_keeps_the_status_mstock_answered_with_when_it_is_an_error()
    {
        var http = new Capture(_ => Json("""{"status":false,"message":"Margin short"}""", 403));
        var result = await Plugin(http).PlaceAsync(Session(), Order(), CancellationToken.None);
        Assert.Equal(403, result.HttpStatus);
        Assert.Equal("Margin short", result.Message);
    }

    [Fact]
    public async Task A_refusal_answered_with_status_200_is_a_400()
    {
        var http = new Capture(_ => Json("""{"status":false,"message":"Margin short"}"""));
        Assert.Equal(400, (await Plugin(http).PlaceAsync(Session(), Order(), CancellationToken.None)).HttpStatus);
    }

    // ---- sign-in -------------------------------------------------------------------------------------------------

    private static Task<BrokerLoginResult> SignIn(IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> input, Capture? http = null) =>
        Plugin(http ?? new Capture(_ => throw new InvalidOperationException("nothing should be sent"))).LoginAsync(keys, input, CancellationToken.None);

    private static readonly Dictionary<string, string> Keys = new() { ["ApiKey"] = "client", ["ApiSecret"] = "private" };
    private static readonly Dictionary<string, string> Input = new() { ["password"] = "p", ["totp"] = "123456" };

    [Theory]
    [InlineData("ApiKey")]
    [InlineData("ApiSecret")]
    public async Task A_sign_in_with_a_key_missing_is_refused_with_409(string missing)
    {
        var keys = Keys.Where(k => k.Key != missing).ToDictionary(k => k.Key, k => k.Value);
        var result = await SignIn(keys, Input);
        Assert.False(result.Ok);
        Assert.Equal(409, result.HttpStatus);
        Assert.Contains("not both set", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ApiKey")]
    [InlineData("ApiSecret")]
    public async Task A_sign_in_with_a_blank_key_is_refused_with_409(string blank)
    {
        var keys = Keys.ToDictionary(k => k.Key, k => k.Key == blank ? " " : k.Value);
        Assert.Equal(409, (await SignIn(keys, Input)).HttpStatus);
    }

    [Theory]
    [InlineData("password", "Enter the password")]
    [InlineData("totp", "Enter the one-time code")]
    public async Task A_sign_in_without_an_input_asks_for_it(string field, string words)
    {
        var missing = Input.Where(k => k.Key != field).ToDictionary(k => k.Key, k => k.Value);
        var empty = Input.ToDictionary(k => k.Key, k => k.Key == field ? "" : k.Value);
        foreach (var input in new[] { missing, empty })
        {
            var result = await SignIn(Keys, input);
            Assert.False(result.Ok);
            Assert.Contains(words, result.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_sign_in_that_cannot_reach_mstock_says_to_check_the_connection()
    {
        var result = await SignIn(Keys, Input, new Capture(_ => throw new HttpRequestException("down")));
        Assert.False(result.Ok);
        Assert.Equal(502, result.HttpStatus);
        Assert.Contains("Check your internet connection", result.Message, StringComparison.Ordinal);
    }

    // ---- history -------------------------------------------------------------------------------------------------

    public static IEnumerable<object[]> Chunks() =>
        [["1m", 2], ["3m", 8], ["5m", 13], ["10m", 26], ["15m", 40], ["30m", 76], ["1h", 166], ["D", 1000]];

    [Theory]
    [MemberData(nameof(Chunks))]
    public async Task One_request_holds_exactly_this_many_days(string interval, int days)
    {
        var start = new DateOnly(2023, 1, 2);
        var record = new SymbolRecord("SBIN", "SBIN-EQ", "NSE", "NSE", "3045", null, null, null, 1, "EQ", 0.05);
        var empty = """{"status":true,"data":{"candles":[]}}""";

        var one = new Capture(_ => Json(empty));
        await Plugin(one).GetHistoryAsync(Session(), record, interval, start, start.AddDays(days - 1), CancellationToken.None);
        Assert.Single(one.Seen);

        var two = new Capture(_ => Json(empty));
        await Plugin(two).GetHistoryAsync(Session(), record, interval, start, start.AddDays(days), CancellationToken.None);
        Assert.Equal(2, two.Seen.Count);
    }

    [Fact]
    public async Task An_interval_mstock_lacks_is_400()
    {
        var record = new SymbolRecord("SBIN", "SBIN-EQ", "NSE", "NSE", "3045", null, null, null, 1, "EQ", 0.05);
        var error = await Assert.ThrowsAsync<BrokerException>(() => Plugin(new Capture(_ => Json("{}"))).GetHistoryAsync(Session(), record, "2m", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2), CancellationToken.None));
        Assert.Equal(400, error.HttpStatus);
    }
}
