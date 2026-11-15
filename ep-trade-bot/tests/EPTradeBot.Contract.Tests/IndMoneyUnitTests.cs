using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Infrastructure.Brokers.IndMoney;

namespace EPTradeBot.Contract.Tests;

/// <summary>What the recorded cases cannot show: a pasted token with spaces, a sign-in missing its Client ID, how a leg with half a number is added up, and where the feed connects.</summary>
public sealed class IndMoneyUnitTests
{
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-11-13T05:30:00Z");
    }

    private sealed class Options(EPTradeBot.Core.Configuration.BrokerOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<EPTradeBot.Core.Configuration.BrokerOptions>
    {
        public EPTradeBot.Core.Configuration.BrokerOptions CurrentValue => value;

        public EPTradeBot.Core.Configuration.BrokerOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<EPTradeBot.Core.Configuration.BrokerOptions, string?> listener) => null;
    }

    private sealed class Master : ISymbolMaster
    {
        public Task<SymbolRecord?> FindAsync(string broker, string symbol, string exchange, CancellationToken cancellationToken) => Task.FromResult<SymbolRecord?>(null);

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
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Seen.Add(request);
                owner.Bodies.Add(request.Content is null ? "" : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
                return Task.FromResult(answer(request));
            }
        }
    }

    private static HttpResponseMessage Json(string body, int status = 200) => new((System.Net.HttpStatusCode)status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static IndMoneyPlugin Plugin(IHttpClientFactory http)
    {
        var options = new EPTradeBot.Core.Configuration.BrokerOptions();
        options.RetryBackoffMs["indmoney"] = 0;
        return new(http, new EPTradeBot.Infrastructure.Brokers.BrokerSessionFactory(null!, new Options(options)), new Master(), new Clock());
    }

    private static readonly IReadOnlyDictionary<string, string> NoInput = new Dictionary<string, string>();

    private static MarginLeg Leg(string token) => new(new SymbolRecord("SBIN", "SBIN", "NSE", "NSE", token, null, null, null, 1, "EQ", 0.05), "BUY", 1, "MIS", "MARKET", 0m, 0m);

    [Fact]
    public async Task A_pasted_token_is_trimmed_before_it_is_checked_and_stored()
    {
        var http = new Capture(_ => Json("{}"));
        var result = await Plugin(http).LoginAsync(new Dictionary<string, string> { ["ApiSecret"] = "  spaced-token  " }, NoInput, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Equal("spaced-token", result.AuthToken);
        Assert.Equal("spaced-token", http.Seen.Single().Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task A_sign_in_with_a_code_and_no_client_id_is_refused_before_anything_is_sent()
    {
        var http = new Capture(_ => Json("{}"));
        var result = await Plugin(http).LoginAsync(new Dictionary<string, string>(), new Dictionary<string, string> { ["mpin"] = "1", ["totp"] = "2" }, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal(409, result.HttpStatus);
        Assert.Empty(http.Seen);
    }

    [Fact]
    public async Task A_code_beats_a_pasted_token_and_the_mpin_and_code_are_trimmed()
    {
        var http = new Capture(_ => Json("""{"data":{"token":"t"}}"""));
        var result = await Plugin(http).LoginAsync(new Dictionary<string, string> { ["ApiKey"] = " cid ", ["ApiSecret"] = "pasted" }, new Dictionary<string, string> { ["mpin"] = " 12 ", ["totp"] = " 004321 " }, CancellationToken.None);
        Assert.Equal("t", result.AuthToken);
        var sent = http.Seen.Single();
        Assert.Equal("/generate/token", sent.RequestUri!.AbsolutePath);
        Assert.Equal("cid", sent.Headers.GetValues("x-api-key").Single());
        var body = JsonNode.Parse(http.Bodies.Single())!;
        Assert.Equal("12", (string?)body["mpin"]);
        Assert.Equal("004321", (string?)body["totp"]);
    }

    [Fact]
    public async Task A_leg_with_a_number_and_then_a_word_adds_the_number_as_OpenAlgo_does()
    {
        var answers = new Queue<string>(["""{"data":{"total_margin":100,"span_margin":60,"exposure_margin":40}}""", """{"data":{"total_margin":7,"span_margin":"x","exposure_margin":9}}"""]);
        var result = await Plugin(new Capture(_ => Json(answers.Dequeue()))).CalculateMarginAsync(new BrokerSession(Guid.NewGuid(), "indmoney", "tok", null, null, new Dictionary<string, string>()), [Leg("1"), Leg("2")], CancellationToken.None);
        Assert.Equal(107, (double)result["total_margin_required"]!);
        Assert.Equal(60, (double)result["span_margin"]!);
        Assert.Equal(40, (double)result["exposure_margin"]!);
    }

    [Fact]
    public void The_feed_connects_with_the_token_as_the_authorization_header_and_not_without_one()
    {
        var plugin = Plugin(new Capture(_ => Json("{}")));
        var endpoint = plugin.FeedEndpoint(new BrokerSession(Guid.NewGuid(), "indmoney", "tok", null, null, new Dictionary<string, string>()), null)!;
        Assert.Equal("wss://ws-prices.indstocks.com/api/v1/ws/prices", endpoint.Url.AbsoluteUri);
        Assert.Equal("tok", endpoint.Headers["Authorization"]);
        Assert.Null(plugin.FeedEndpoint(new BrokerSession(Guid.NewGuid(), "indmoney", " ", null, null, new Dictionary<string, string>()), null));
        Assert.Equal("wss://other/feed", plugin.FeedEndpoint(new BrokerSession(Guid.NewGuid(), "indmoney", "tok", null, null, new Dictionary<string, string>()), "wss://other/feed")!.Url.AbsoluteUri);
    }
}
