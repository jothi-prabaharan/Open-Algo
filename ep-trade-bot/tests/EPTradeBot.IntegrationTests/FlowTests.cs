using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.Legacy.Core;
using EPTradeBot.Infrastructure.Flow;
using EPTradeBot.Infrastructure.Scheduling;
using EPTradeBot.Infrastructure.Security;
using EPTradeBot.Infrastructure.Streaming;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace EPTradeBot.IntegrationTests;

/// <summary>Workflows end to end: saved, switched on, triggered, run through the guard and risk limits into the sandbox, held for approval, and recorded.</summary>
public sealed class FlowTests
{
    private sealed class Clock(DateTimeOffset at) : TimeProvider
    {
        public DateTimeOffset At { get; set; } = at;

        public override DateTimeOffset GetUtcNow() => At;
    }

    private sealed record Setup(TestHost Host, Guid Account, string ReadKey);

    private const string Quote = """{"status":"success","data":{"NSE:SBIN":{"instrument_token":1,"last_price":500,"ohlc":{"open":498,"high":510,"low":495,"close":497},"depth":{"buy":[],"sell":[]}}}}""";

    /// <summary>A paper account whose market data comes from a (fake) Zerodha, as it does in OpenAlgo's analyze mode.</summary>
    private static async Task<Setup> NewAsync(Clock? clock = null, int maxQty = 1000)
    {
        var broker = new FakeBroker();
        var host = new TestHost("Development", b =>
        {
            b.UseSetting("Brokers:Enabled:0", "zerodha");
            b.ConfigureServices(s =>
            {
                s.ConfigureHttpClientDefaults(h => h.ConfigurePrimaryHttpMessageHandler(() => broker));
                if (clock is not null) s.AddSingleton<TimeProvider>(clock);
            });
        });
        var account = await host.CreateAccountAsync(AccountType.PAPER, "paper", 100_000, maxQty, 10_000_000, broker: "zerodha");
        (await host.Admin.PutAsJsonAsync($"/api/v1/accounts/{account}/broker-session", new { auth_token = "key:token" })).EnsureSuccessStatusCode();
        var key = (await host.Get<ApiKeyService>().IssueAsync(account, "read")).Key;
        await using (var db = host.NewDb())
        {
            db.Set<SymToken>().Add(new SymToken { Broker = "zerodha", Symbol = "SBIN", Brsymbol = "SBIN", Exchange = "NSE", Brexchange = "NSE", Token = "779521::::3045", Lotsize = 1, TickSize = 0.05 });
            await db.SaveChangesAsync();
        }

        broker.Reset(new Dictionary<string, (int, string)> { ["GET /quote"] = (200, Quote) });
        host.SetQuote("NSE", "SBIN", 500);
        return new Setup(host, account, key);
    }

    private static JsonObject Node(string id, string type, JsonObject? data = null) => new() { ["id"] = id, ["type"] = type, ["data"] = data ?? [] };

    private static JsonObject Edge(string source, string target, string handle = "") => new() { ["source"] = source, ["target"] = target, ["sourceHandle"] = handle };

    private static JsonObject Buy(int quantity = 10) => new() { ["symbol"] = "SBIN", ["exchange"] = "NSE", ["action"] = "BUY", ["quantity"] = quantity, ["priceType"] = "MARKET", ["product"] = "MIS" };

    /// <summary>start -> price check -> (true) buy.</summary>
    private static (JsonArray Nodes, JsonArray Edges) BuyIfAbove(double level, int quantity = 10) => (
        [Node("s", "start"), Node("c", "priceCondition", new() { ["symbol"] = "SBIN", ["exchange"] = "NSE", ["field"] = "ltp", ["operator"] = ">", ["value"] = level }), Node("o", "placeOrder", Buy(quantity))],
        [Edge("s", "c"), Edge("c", "o", "true")]);

    private static async Task<(HttpStatusCode Status, JsonObject Body)> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null)
    {
        var response = await client.SendAsync(new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) });
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 && JsonNode.Parse(text) is JsonObject o ? o : []);
    }

    private static async Task<Guid> CreateAsync(Setup s, string name, (JsonArray Nodes, JsonArray Edges) graph, string mode = "auto", bool withAccount = true, object? extra = null)
    {
        var body = new JsonObject { ["name"] = name, ["nodes"] = graph.Nodes.DeepClone(), ["edges"] = graph.Edges.DeepClone(), ["execution_mode"] = mode };
        if (withAccount) body["account_id"] = s.Account;
        if (extra is JsonObject more) foreach (var (k, v) in more.ToList()) { more.Remove(k); body[k] = v; }
        var (status, created) = await SendAsync(s.Host.Admin, HttpMethod.Post, "/api/v1/flows", body);
        Assert.Equal(HttpStatusCode.Created, status);
        return Guid.Parse(created["id"]!.GetValue<string>());
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

    [PostgresFact]
    public async Task A_half_built_workflow_can_be_saved_but_not_switched_on_and_an_export_carries_nothing_of_this_install()
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        var id = await CreateAsync(s, "draft", (new JsonArray(Node("l", "log")), new JsonArray()));

        var (status, refused) = await SendAsync(s.Host.Admin, HttpMethod.Post, $"/api/v1/flows/{id}/activate");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("trigger", refused["message"]!.GetValue<string>());
        Assert.False((await SendAsync(s.Host.Admin, HttpMethod.Get, $"/api/v1/flows/{id}")).Body["is_active"]!.GetValue<bool>());

        var (_, exported) = await SendAsync(s.Host.Admin, HttpMethod.Get, $"/api/v1/flows/{id}/export");
        Assert.Equal(["description", "edges", "name", "nodes"], exported.Select(kv => kv.Key).Order(StringComparer.Ordinal).ToArray());

        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(s.Host.Admin, HttpMethod.Post, "/api/v1/flows", new { name = "draft" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(s.Host.Admin, HttpMethod.Post, "/api/v1/flows", new { name = "bad", nodes = new[] { new { id = "a", type = "teleport" } }, edges = Array.Empty<object>() })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(s.Host.Admin, HttpMethod.Put, $"/api/v1/flows/{id}", new { edges = new[] { new { source = "l", target = "ghost" } } })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(s.Host.Admin, HttpMethod.Get, $"/api/v1/flows/{Guid.NewGuid()}")).Status);
    }

    [PostgresFact]
    public async Task A_node_the_platform_cannot_run_yet_stops_the_workflow_at_switch_on_and_at_run()
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        var id = await CreateAsync(s, "basket", ([Node("s", "start"), Node("b", "subscribeLtp")], [Edge("s", "b")]));

        var (status, refused) = await SendAsync(s.Host.Admin, HttpMethod.Post, $"/api/v1/flows/{id}/activate");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("cannot run subscribeLtp", refused["message"]!.GetValue<string>());
        var (runStatus, run) = await SendAsync(s.Host.Admin, HttpMethod.Post, $"/api/v1/flows/{id}/run");
        Assert.Equal(HttpStatusCode.Conflict, runStatus);
        Assert.Contains("cannot run subscribeLtp", run["message"]!.GetValue<string>());
    }

    [PostgresFact]
    public async Task A_run_in_auto_mode_sends_the_order_through_the_sandbox_and_records_what_happened()
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        var id = await CreateAsync(s, "buy-above", BuyIfAbove(400));

        var (status, ran) = await SendAsync(s.Host.Admin, HttpMethod.Post, $"/api/v1/flows/{id}/run");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("success", ran["status"]!.GetValue<string>());

        var trade = Assert.Single(await TradesAsync(s));
        Assert.Equal(("SBIN", "BUY"), (trade!["symbol"]!.GetValue<string>(), trade["action"]!.GetValue<string>()));

        var (_, runs) = await SendAsync(s.Host.Admin, HttpMethod.Get, $"/api/v1/flows/{id}/runs");
        var summary = Assert.Single(runs.Count == 0 ? JsonNode.Parse(await s.Host.Admin.GetStringAsync($"/api/v1/flows/{id}/runs"))!.AsArray() : []);
        Assert.Equal(("completed", "manual"), (summary!["status"]!.GetValue<string>(), summary["trigger"]!.GetValue<string>()));
        var detail = await SendAsync(s.Host.Admin, HttpMethod.Get, $"/api/v1/flows/{id}/runs/{ran["run_id"]!.GetValue<string>()}");
        Assert.Contains(detail.Body["logs"]!.AsArray(), l => l!["message"]!.GetValue<string>().StartsWith("Placing order: SBIN BUY qty=10"));
        Assert.Equal("completed", (await SendAsync(s.Host.Admin, HttpMethod.Get, $"/api/v1/flows/{id}")).Body["last_run_status"]!.GetValue<string>());
    }

    [PostgresFact]
    public async Task A_condition_that_is_not_met_sends_nothing_and_the_run_still_completes()
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        var id = await CreateAsync(s, "buy-above-900", BuyIfAbove(900));
        var (status, body) = await SendAsync(s.Host.Admin, HttpMethod.Post, $"/api/v1/flows/{id}/run");
        Assert.True(status == HttpStatusCode.OK, body.ToJsonString());
        Assert.Empty(await TradesAsync(s));
    }

    [PostgresFact]
    public async Task In_semi_auto_mode_the_order_waits_in_the_action_center_until_it_is_approved()
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        var id = await CreateAsync(s, "semi", BuyIfAbove(400), mode: "semi_auto");

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(s.Host.Admin, HttpMethod.Post, $"/api/v1/flows/{id}/run")).Status);
        Assert.Empty(await TradesAsync(s));
        var waiting = JsonNode.Parse(await s.Host.Admin.GetStringAsync("/api/v1/action-center/orders?status=pending"))!["data"]!.AsArray();
        var held = Assert.Single(waiting);
        Assert.Equal("placeorder", held!["api_type"]!.GetValue<string>());
        Assert.Equal("semi_auto", (await SendAsync(s.Host.Admin, HttpMethod.Get, $"/api/v1/flows/{id}")).Body["execution_mode"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(s.Host.Admin, HttpMethod.Post, $"/api/v1/action-center/orders/{held["id"]!.GetValue<int>()}/approve")).Status);
        Assert.Single(await TradesAsync(s));
    }

    [PostgresFact]
    public async Task An_order_over_the_accounts_limit_is_refused_and_the_run_fails_with_the_reason()
    {
        var s = await NewAsync(maxQty: 5);
        await using var _ = s.Host;
        var id = await CreateAsync(s, "too-big", BuyIfAbove(400, quantity: 10));

        var (status, ran) = await SendAsync(s.Host.Admin, HttpMethod.Post, $"/api/v1/flows/{id}/run");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("error", ran["status"]!.GetValue<string>());
        Assert.Equal("placeOrder", ran["errors"]![0]!["type"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(ran["errors"]![0]!["message"]!.GetValue<string>()));
        Assert.Empty(await TradesAsync(s));
        Assert.Equal("failed", (await SendAsync(s.Host.Admin, HttpMethod.Get, $"/api/v1/flows/{id}")).Body["last_run_status"]!.GetValue<string>());
    }

    [PostgresFact]
    public async Task A_workflow_does_not_run_for_a_missing_switched_off_or_killed_account()
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        var noAccount = await CreateAsync(s, "orphan", BuyIfAbove(400), withAccount: false);
        var (status, body) = await SendAsync(s.Host.Admin, HttpMethod.Post, $"/api/v1/flows/{noAccount}/run");
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("Choose the account", body["message"]!.GetValue<string>());

        var id = await CreateAsync(s, "killed", BuyIfAbove(400));
        await s.Host.Admin.PostAsJsonAsync($"/api/v1/killswitch/account/{s.Account}", new { engage = true, reason = "test" });
        (status, body) = await SendAsync(s.Host.Admin, HttpMethod.Post, $"/api/v1/flows/{id}/run");
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("kill switch", body["message"]!.GetValue<string>());
        Assert.Empty(await TradesAsync(s));
    }

    [PostgresFact]
    public async Task A_workflow_that_is_already_running_is_not_started_a_second_time()
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        var id = await CreateAsync(s, "slow", ([Node("s", "start"), Node("d", "delay", new() { ["delayValue"] = 2 }), Node("o", "placeOrder", Buy())], [Edge("s", "d"), Edge("d", "o")]));

        var first = SendAsync(s.Host.Admin, HttpMethod.Post, $"/api/v1/flows/{id}/run");
        await Task.Delay(500);
        var (status, second) = await SendAsync(s.Host.Admin, HttpMethod.Post, $"/api/v1/flows/{id}/run");
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.True(second["already_running"]!.GetValue<bool>());

        Assert.Equal(HttpStatusCode.OK, (await first).Status);
        Assert.Single(await TradesAsync(s)); // one run, one order
    }

    [PostgresFact]
    public async Task A_webhook_runs_the_workflow_with_what_it_was_sent_and_refuses_anything_else()
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        var buy = Buy();
        buy["quantity"] = "{{webhook.qty}}";
        var id = await CreateAsync(s, "hook", ([Node("w", "webhookTrigger"), Node("o", "placeOrder", buy)], [Edge("w", "o")]));

        var (_, on) = await SendAsync(s.Host.Admin, HttpMethod.Post, $"/api/v1/flows/{id}/activate");
        var token = on["webhook_token"]!.GetValue<string>();
        var url = $"/api/v1/flow-webhook/{id}/{token}";
        using var anonymous = s.Host.Anonymous();

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(anonymous, HttpMethod.Post, url, new { qty = 7 })).Status);
        var trade = Assert.Single(await TradesAsync(s));
        Assert.Equal(7, trade!["quantity"]!.GetValue<int>());

        // Left out of the body, the quantity is not guessed: the order is refused and nothing is sent.
        var (missing, body) = await SendAsync(anonymous, HttpMethod.Post, url, new { other = 1 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing);
        Assert.Contains("unresolved", body["errors"]![0]!["message"]!.GetValue<string>());
        Assert.Single(await TradesAsync(s));

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(anonymous, HttpMethod.Post, $"/api/v1/flow-webhook/{id}/wrong", new { qty = 1 })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(anonymous, HttpMethod.Post, $"/api/v1/flow-webhook/{Guid.NewGuid()}/{token}", new { qty = 1 })).Status);

        // A new token ends the old one; switching the workflow off ends the address altogether.
        var (_, rotated) = await SendAsync(s.Host.Admin, HttpMethod.Post, $"/api/v1/flows/{id}/webhook-token");
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(anonymous, HttpMethod.Post, url, new { qty = 1 })).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(anonymous, HttpMethod.Post, $"/api/v1/flow-webhook/{id}/{rotated["webhook_token"]!.GetValue<string>()}", new { qty = 2 })).Status);
        await SendAsync(s.Host.Admin, HttpMethod.Post, $"/api/v1/flows/{id}/deactivate");
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(anonymous, HttpMethod.Post, $"/api/v1/flow-webhook/{id}/{rotated["webhook_token"]!.GetValue<string>()}", new { qty = 2 })).Status);
        Assert.Equal(2, (await TradesAsync(s)).Count);
    }

    private static async Task PublishAsync(Setup s, double ltp) =>
        s.Host.Get<MarketDataPublisher>().Publish("NSE_SBIN_LTP", new JsonObject { ["ltp"] = ltp, ["symbol"] = "SBIN", ["exchange"] = "NSE" });

    [PostgresFact]
    public async Task A_price_alert_starts_the_workflow_when_a_tick_crosses_the_level_and_a_once_only_alert_is_spent()
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        var alert = new JsonObject { ["symbol"] = "SBIN", ["exchange"] = "NSE", ["condition"] = "crossing_up", ["price"] = 505 };
        var id = await CreateAsync(s, "alert", ([Node("a", "priceAlert", alert), Node("o", "placeOrder", Buy())], [Edge("a", "o")]));
        var (status, _) = await SendAsync(s.Host.Admin, HttpMethod.Post, $"/api/v1/flows/{id}/activate");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, s.Host.Get<FlowPriceMonitor>().WatchCount);

        // Below the level: nothing. Across it: one run, one order. The publisher needs a moment to connect, so it keeps ticking until the order shows.
        await PublishAsync(s, 500);
        Assert.True(await WaitAsync(async () => { await PublishAsync(s, 507); return (await TradesAsync(s)).Count > 0; }));
        await Task.Delay(500);
        Assert.Single(await TradesAsync(s));

        var (_, after) = await SendAsync(s.Host.Admin, HttpMethod.Get, $"/api/v1/flows/{id}");
        Assert.False(after["is_active"]!.GetValue<bool>());
        Assert.Equal(0, s.Host.Get<FlowPriceMonitor>().WatchCount);
        var run = JsonNode.Parse(await s.Host.Admin.GetStringAsync($"/api/v1/flows/{id}/runs"))!.AsArray().Single()!;
        Assert.Equal(("completed", "price_alert"), (run["status"]!.GetValue<string>(), run["trigger"]!.GetValue<string>()));
    }

    [PostgresFact]
    public async Task A_price_alert_that_cannot_ever_fire_is_reported_when_the_workflow_is_switched_on()
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        var alert = new JsonObject { ["symbol"] = "SBIN", ["exchange"] = "NSE", ["condition"] = "crossing_up" }; // no price
        var id = await CreateAsync(s, "noprice", ([Node("a", "priceAlert", alert), Node("o", "placeOrder", Buy())], [Edge("a", "o")]));
        var (status, on) = await SendAsync(s.Host.Admin, HttpMethod.Post, $"/api/v1/flows/{id}/activate");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("needs a price above zero", on["warning"]!.GetValue<string>());
        Assert.Equal(0, s.Host.Get<FlowPriceMonitor>().WatchCount);
    }

    [PostgresFact]
    public async Task Price_alerts_of_active_workflows_are_restored_after_a_restart()
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        var alert = new JsonObject { ["symbol"] = "SBIN", ["exchange"] = "NSE", ["condition"] = "greater_than", ["price"] = 900, ["trigger"] = "every_time" };
        var id = await CreateAsync(s, "restore", ([Node("a", "priceAlert", alert), Node("o", "placeOrder", Buy())], [Edge("a", "o")]));
        await SendAsync(s.Host.Admin, HttpMethod.Post, $"/api/v1/flows/{id}/activate");
        var monitor = s.Host.Get<FlowPriceMonitor>();
        await monitor.UnwatchAsync(id, default); // what a restart does to memory
        Assert.Equal(0, monitor.WatchCount);

        await s.Host.Get<FlowTriggers>().StartAsync(default);

        Assert.Equal(1, monitor.WatchCount);
        await SendAsync(s.Host.Admin, HttpMethod.Post, $"/api/v1/flows/{id}/deactivate");
        Assert.Equal(0, monitor.WatchCount);
    }

    [PostgresFact]
    public async Task A_scheduled_workflow_fires_in_india_time_on_its_days_and_not_on_a_market_holiday()
    {
        var clock = new Clock(new DateTimeOffset(2026, 11, 10, 9, 20, 0, TimeSpan.FromHours(5.5))); // the seeded Balipratipada holiday
        var s = await NewAsync(clock);
        await using var _ = s.Host;
        var id = await CreateAsync(s, "morning", BuyIfAbove(400), extra: new JsonObject { ["is_scheduled"] = true, ["schedule_time"] = "09:20", ["schedule_days"] = new JsonArray("mon", "tue", "wed", "thu", "fri") });
        await SendAsync(s.Host.Admin, HttpMethod.Post, $"/api/v1/flows/{id}/activate");

        var scheduler = await s.Host.Get<ISchedulerFactory>().GetScheduler();
        var trigger = await scheduler.GetTrigger(new TriggerKey(ScheduleRegistry.FlowJobKey(id).Name, ScheduleRegistry.FlowJobKey(id).Group));
        var after = new DateTimeOffset(2030, 1, 4, 16, 0, 0, TimeSpan.FromHours(5.5)); // a Friday afternoon, in a week that is always ahead
        Assert.Equal(new DateTimeOffset(2030, 1, 7, 9, 20, 0, TimeSpan.FromHours(5.5)).ToUniversalTime(), trigger!.GetFireTimeAfter(after)!.Value.ToUniversalTime());

        var runner = s.Host.Get<FlowScheduleRunner>();
        Assert.Equal("holiday", await runner.RunAsync(id, default));
        Assert.Empty(await TradesAsync(s));

        clock.At = new DateTimeOffset(2026, 11, 11, 9, 20, 0, TimeSpan.FromHours(5.5));
        Assert.Equal("completed", await runner.RunAsync(id, default));
        Assert.Single(await TradesAsync(s));

        await SendAsync(s.Host.Admin, HttpMethod.Post, $"/api/v1/flows/{id}/deactivate");
        Assert.Null(await scheduler.GetTrigger(new TriggerKey(ScheduleRegistry.FlowJobKey(id).Name, ScheduleRegistry.FlowJobKey(id).Group)));
    }

    [PostgresFact]
    public async Task Deleting_a_workflow_removes_it_and_its_runs_and_the_admin_token_is_required()
    {
        var s = await NewAsync();
        await using var _ = s.Host;
        var id = await CreateAsync(s, "gone", BuyIfAbove(900));
        await SendAsync(s.Host.Admin, HttpMethod.Post, $"/api/v1/flows/{id}/run");
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(s.Host.Admin, HttpMethod.Delete, $"/api/v1/flows/{id}")).Status);
        await using var db = s.Host.NewDb();
        Assert.Empty(await db.FlowRuns.Where(r => r.FlowId == id).ToListAsync());
        using var anonymous = s.Host.Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(anonymous, HttpMethod.Get, "/api/v1/flows")).Status);
    }
}
