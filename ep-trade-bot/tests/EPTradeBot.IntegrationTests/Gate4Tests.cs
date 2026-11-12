using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.Legacy.Core;
using EPTradeBot.Infrastructure.Flow;
using EPTradeBot.Infrastructure.Security;
using EPTradeBot.Infrastructure.Streaming;
using Microsoft.Extensions.DependencyInjection;

namespace EPTradeBot.IntegrationTests;

/// <summary>
/// Gate 4, end to end through the real host: a live tick crosses a level, a Flow workflow reads an indicator over the symbol's recent candles, and the order it sends reaches the sandbox only through
/// the environment guard and the account's risk limits. The same chain is run in Semi-Auto (the order waits for a person), and against an order the account's limits refuse.
/// </summary>
public sealed class Gate4Tests
{
    private sealed record Setup(TestHost Host, Guid Account, string ReadKey);

    private const string Quote = """{"status":"success","data":{"NSE:SBIN":{"instrument_token":1,"last_price":500,"ohlc":{"open":498,"high":510,"low":495,"close":497},"depth":{"buy":[],"sell":[]}}}}""";

    /// <summary>Forty five-minute candles whose last three closes average <paramref name="lastCloses"/>.</summary>
    private static string Candles(double lastCloses)
    {
        var rows = Enumerable.Range(0, 40).Select(i =>
        {
            var close = i >= 37 ? lastCloses : 495 + i * 0.1;
            return $"[\"2026-11-02T{9 + i / 12:D2}:{i % 12 * 5:D2}:00+0530\",{close - 1},{close + 1},{close - 2},{close},1000,0]";
        });
        return "{\"status\":\"success\",\"data\":{\"candles\":[" + string.Join(",", rows) + "]}}";
    }

    private static async Task<Setup> NewAsync(double lastCloses, int maxQty = 1000)
    {
        var broker = new FakeBroker();
        var host = new TestHost("Development", b =>
        {
            b.UseSetting("Brokers:Enabled:0", "zerodha");
            b.ConfigureServices(s => s.ConfigureHttpClientDefaults(h => h.ConfigurePrimaryHttpMessageHandler(() => broker)));
        });
        var account = await host.CreateAccountAsync(AccountType.PAPER, "paper", 100_000, maxQty, 10_000_000, broker: "zerodha");
        (await host.Admin.PutAsJsonAsync($"/api/v1/accounts/{account}/broker-session", new { auth_token = "key:token" })).EnsureSuccessStatusCode();
        var key = (await host.Get<ApiKeyService>().IssueAsync(account, "read")).Key;
        await using (var db = host.NewDb())
        {
            db.Set<SymToken>().Add(new SymToken { Broker = "zerodha", Symbol = "SBIN", Brsymbol = "SBIN", Exchange = "NSE", Brexchange = "NSE", Token = "779521::::3045", Lotsize = 1, TickSize = 0.05 });
            await db.SaveChangesAsync();
        }

        broker.Reset(new Dictionary<string, (int, string)> { ["GET /quote"] = (200, Quote), ["GET /instruments/historical/779521/5minute"] = (200, Candles(lastCloses)) });
        host.SetQuote("NSE", "SBIN", 500);
        return new Setup(host, account, key);
    }

    private static JsonObject Node(string id, string type, JsonObject? data = null) => new() { ["id"] = id, ["type"] = type, ["data"] = data ?? [] };

    private static JsonObject Edge(string source, string target, string handle = "") => new() { ["source"] = source, ["target"] = target, ["sourceHandle"] = handle };

    /// <summary>priceAlert (SBIN crossing up through 505) -> 3-bar average of the 5 minute closes -> only if that average is above 500 -> buy.</summary>
    private static (JsonArray Nodes, JsonArray Edges) Graph(int quantity = 10) => (
        [
            Node("a", "priceAlert", new() { ["symbol"] = "SBIN", ["exchange"] = "NSE", ["condition"] = "crossing_up", ["price"] = 505 }),
            Node("i", "indicator", new() { ["indicatorName"] = "sma", ["symbol"] = "SBIN", ["exchange"] = "NSE", ["interval"] = "5m", ["params"] = "{\"period\": 3}", ["outputVariable"] = "ind", ["lookbackBars"] = 40 }),
            Node("c", "varCondition", new() { ["leftValue"] = "{{ind.latest.value}}", ["operator"] = ">", ["rightValue"] = 500 }),
            Node("o", "placeOrder", new() { ["symbol"] = "SBIN", ["exchange"] = "NSE", ["action"] = "BUY", ["quantity"] = quantity, ["priceType"] = "MARKET", ["product"] = "MIS" }),
        ],
        [Edge("a", "i"), Edge("i", "c"), Edge("c", "o", "true")]);

    private static async Task<(HttpStatusCode Status, JsonObject Body)> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null)
    {
        var response = await client.SendAsync(new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) });
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 && JsonNode.Parse(text) is JsonObject o ? o : []);
    }

    private static async Task<Guid> ActivateAsync(Setup s, string mode)
    {
        var graph = Graph(mode == "limit" ? 10 : 10);
        var body = new JsonObject { ["name"] = "gate4", ["nodes"] = graph.Nodes.DeepClone(), ["edges"] = graph.Edges.DeepClone(), ["execution_mode"] = mode == "limit" ? "auto" : mode, ["account_id"] = s.Account };
        var (status, created) = await SendAsync(s.Host.Admin, HttpMethod.Post, "/api/v1/flows", body);
        Assert.Equal(HttpStatusCode.Created, status);
        var id = Guid.Parse(created["id"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(s.Host.Admin, HttpMethod.Post, $"/api/v1/flows/{id}/activate")).Status);
        return id;
    }

    private static async Task<JsonArray> TradesAsync(Setup s)
    {
        using var client = s.Host.Anonymous();
        var (_, body) = await SendAsync(client, HttpMethod.Post, "/api/v1/tradebook", new { apikey = s.ReadKey });
        return (JsonArray)body["data"]!;
    }

    private static async Task<bool> WaitAsync(Func<Task<bool>> condition, int seconds = 15)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (await condition()) return true;
            await Task.Delay(100);
        }

        return false;
    }

    private static void Tick(Setup s, double ltp) =>
        s.Host.Get<MarketDataPublisher>().Publish("NSE_SBIN_LTP", new JsonObject { ["ltp"] = ltp, ["symbol"] = "SBIN", ["exchange"] = "NSE" });

    private static async Task<JsonObject> OnlyRunAsync(Setup s, Guid flow) => (JsonObject)JsonNode.Parse(await s.Host.Admin.GetStringAsync($"/api/v1/flows/{flow}/runs"))!.AsArray().Single()!;

    [PostgresFact]
    public async Task Auto_a_tick_through_the_level_and_a_supporting_indicator_send_one_order_into_the_sandbox()
    {
        var s = await NewAsync(lastCloses: 512);
        await using var _ = s.Host;
        var flow = await ActivateAsync(s, "auto");
        Tick(s, 500);
        Assert.True(await WaitAsync(async () => { Tick(s, 507); return (await TradesAsync(s)).Count > 0; }));
        await Task.Delay(500);

        var trade = Assert.Single(await TradesAsync(s));
        Assert.Equal(("SBIN", "BUY"), (trade!["symbol"]!.GetValue<string>(), trade["action"]!.GetValue<string>()));
        var run = await OnlyRunAsync(s, flow);
        Assert.Equal(("completed", "price_alert"), (run["status"]!.GetValue<string>(), run["trigger"]!.GetValue<string>()));
        var detail = await SendAsync(s.Host.Admin, HttpMethod.Get, $"/api/v1/flows/{flow}/runs/{run["id"]!.GetValue<string>()}");
        Assert.Contains(detail.Body["logs"]!.AsArray(), l => l!["message"]!.GetValue<string>().Contains("Var check: 512.0 > 500.0 = True"));
    }

    [PostgresFact]
    public async Task Auto_an_indicator_that_does_not_support_the_trade_stops_it_though_the_tick_crossed()
    {
        var s = await NewAsync(lastCloses: 490);
        await using var _ = s.Host;
        var flow = await ActivateAsync(s, "auto");
        Tick(s, 500);
        // The alert fires on the first tick through 505 and the run completes without an order, so wait for the run, not for a trade.
        Assert.True(await WaitAsync(async () => { Tick(s, 507); return JsonNode.Parse(await s.Host.Admin.GetStringAsync($"/api/v1/flows/{flow}/runs"))!.AsArray().Count > 0; }));
        await Task.Delay(500);

        Assert.Empty(await TradesAsync(s));
        var run = await OnlyRunAsync(s, flow);
        Assert.Equal("completed", run["status"]!.GetValue<string>());
        var detail = await SendAsync(s.Host.Admin, HttpMethod.Get, $"/api/v1/flows/{flow}/runs/{run["id"]!.GetValue<string>()}");
        Assert.Contains(detail.Body["logs"]!.AsArray(), l => l!["message"]!.GetValue<string>().Contains("= False"));
    }

    [PostgresFact]
    public async Task Semi_auto_the_same_chain_waits_in_the_action_center_and_sends_only_when_a_person_approves()
    {
        var s = await NewAsync(lastCloses: 512);
        await using var _ = s.Host;
        await ActivateAsync(s, "semi_auto");
        Tick(s, 500);
        JsonArray Pending() => JsonNode.Parse(s.Host.Admin.GetStringAsync("/api/v1/action-center/orders?status=pending").GetAwaiter().GetResult())!["data"]!.AsArray();
        Assert.True(await WaitAsync(() => { Tick(s, 507); return Task.FromResult(Pending().Count > 0); }));
        await Task.Delay(500);

        var held = Assert.Single(Pending());
        Assert.Empty(await TradesAsync(s));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(s.Host.Admin, HttpMethod.Post, $"/api/v1/action-center/orders/{held!["id"]!.GetValue<int>()}/approve")).Status);
        Assert.Single(await TradesAsync(s));
    }

    [PostgresFact]
    public async Task An_order_the_accounts_limits_refuse_is_never_sent_and_the_run_says_why()
    {
        var s = await NewAsync(lastCloses: 512, maxQty: 5);
        await using var _ = s.Host;
        var flow = await ActivateAsync(s, "limit");
        Tick(s, 500);
        Assert.True(await WaitAsync(async () => { Tick(s, 507); return JsonNode.Parse(await s.Host.Admin.GetStringAsync($"/api/v1/flows/{flow}/runs"))!.AsArray().Count > 0; }));
        await Task.Delay(500);

        Assert.Empty(await TradesAsync(s));
        var run = await OnlyRunAsync(s, flow);
        Assert.Equal("failed", run["status"]!.GetValue<string>());
        Assert.Contains("quantity", run["error"]?.ToString() ?? run.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }
}
