using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.Legacy.Core;
using EPTradeBot.Infrastructure.Security;

namespace EPTradeBot.IntegrationTests;

/// <summary>The strategy routes: what is read from the stored strategies, runs, orders and events, the refusals each route owes, and the honest answer for the routes that need a run engine.</summary>
public sealed class StrategyApiTests
{
    private static async Task<(TestHost Host, string Key)> HostAsync()
    {
        var host = new TestHost("Production");
        var account = await host.CreateAccountAsync(AccountType.LIVE, "kite");
        var key = (await host.Get<ApiKeyService>().IssueAsync(account, "strategy")).Key;
        await using var db = host.NewDb();
        var t0 = new DateTime(2026, 11, 2, 4, 0, 0, 123, DateTimeKind.Utc).AddTicks(4560);
        db.Set<SmStrategy>().AddRange(
            new SmStrategy { Id = 1, UserId = "admin", Name = "Short Straddle", Underlying = "NIFTY", UnderlyingExchange = "NSE_INDEX", Legs = """[{"leg_id":1,"action":"SELL"}]""", EntryTime = new TimeOnly(9, 20), ExitTime = new TimeOnly(15, 10),
                OverallSlMtm = 5000m, Status = "running", CurrentRunId = 11, LiveEnabled = false, CreatedAt = t0, UpdatedAt = t0, WebhookTokenHash = "hash1" },
            new SmStrategy { Id = 2, UserId = "admin", Name = "Iron Condor", Underlying = "BANKNIFTY", UnderlyingExchange = "NSE_INDEX", Status = "stopped", LiveEnabled = true, CreatedAt = t0.AddDays(1), UpdatedAt = t0, WebhookTokenHash = "hash2" },
            new SmStrategy { Id = 3, UserId = "admin", Name = "Signal Trend", StrategyKind = "signal", Underlying = "NIFTY", UnderlyingExchange = "NSE_INDEX", Status = "stopped", LiveEnabled = true, CreatedAt = t0.AddDays(2), UpdatedAt = new DateTime(2026, 11, 2, 4, 0, 0, DateTimeKind.Utc), WebhookTokenHash = "hash3" });
        db.Set<SmStrategyRun>().AddRange(
            new SmStrategyRun { Id = 10, StrategyId = 1, Mode = "sandbox", Broker = "sandbox", StartedAt = t0.AddHours(-30), StoppedAt = t0.AddHours(-24), StopReason = "manual", PnlRealized = 1250.5m },
            new SmStrategyRun { Id = 11, StrategyId = 1, Mode = "sandbox", Broker = "sandbox", StartedAt = t0, TriggerSource = "scheduler", ResolvedExpiries = """{"1":"13NOV26"}""" },
            new SmStrategyRun { Id = 20, StrategyId = 2, Mode = "live", Broker = "zerodha", StartedAt = t0, StoppedAt = t0.AddHours(1), PnlRealized = -300m });
        db.Set<SmStrategyOrder>().AddRange(
            new SmStrategyOrder { Id = 1, RunId = 10, LegId = 1, Kind = "entry", Symbol = "NIFTY13NOV2624000CE", Exchange = "NFO", Action = "SELL", Qty = 75, Product = "NRML", Status = "complete", PlacedAt = t0.AddHours(-30), AvgFillPrice = 120.5m, FilledQty = 75 },
            new SmStrategyOrder { Id = 2, RunId = 11, LegId = 1, Kind = "entry", Symbol = "NIFTY13NOV2624000CE", Exchange = "NFO", Action = "SELL", Qty = 75, Status = "pending", PlacedAt = t0 },
            new SmStrategyOrder { Id = 3, RunId = 20, LegId = 2, Kind = "entry", Symbol = "BANKNIFTY", Exchange = "NFO", Action = "BUY", Qty = 15, Status = "rejected", PlacedAt = t0, RejectReason = "margin" });
        db.Set<SmStrategyEvent>().AddRange(
            new SmStrategyEvent { Id = 1, StrategyId = 1, RunId = 11, UserId = "admin", Ts = t0, Kind = "run_started", Message = "Run started" },
            new SmStrategyEvent { Id = 2, StrategyId = 1, RunId = 11, UserId = "admin", Ts = t0.AddMinutes(5), Kind = "leg_sl_hit", Severity = "warn", LegId = 1, Message = "Stop hit", Payload = """{"price":150}""" },
            new SmStrategyEvent { Id = 3, StrategyId = 1, RunId = 10, UserId = "admin", Ts = t0.AddHours(-24), Kind = "run_stopped", Message = "Stopped" },
            new SmStrategyEvent { Id = 4, StrategyId = 2, UserId = "admin", Ts = t0, Kind = "strategy_created", Message = "Created" });
        await db.SaveChangesAsync();
        return (host, key);
    }

    private static async Task<(int Status, JsonObject Body)> Post(HttpClient client, string path, object body)
    {
        var response = await client.PostAsJsonAsync(path, body);
        return ((int)response.StatusCode, (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    [PostgresFact]
    public async Task The_list_shows_newest_first_with_the_last_finished_run_and_filters_by_status_and_name()
    {
        var (host, key) = await HostAsync();
        await using var _ = host;
        using var client = host.Anonymous();
        var (status, body) = await Post(client, "/api/v1/strategy/list", new { apikey = key });
        Assert.Equal(200, status);
        var data = body["data"]!.AsArray();
        Assert.Equal(["Signal Trend", "Iron Condor", "Short Straddle"], data.Select(d => d!["name"]!.GetValue<string>()));
        Assert.Null(data[0]!["legs"]);
        var straddle = data[2]!;
        Assert.Equal(10, straddle["last_finalized_run"]!["id"]!.GetValue<int>());
        Assert.Equal(1250.5, straddle["last_finalized_run"]!["pnl_realized"]!.GetValue<double>());
        Assert.Equal("09:20", straddle["entry_time"]!.GetValue<string>());
        Assert.Equal(5000.0, straddle["overall_sl_mtm"]!.GetValue<double>());
        Assert.False(straddle.AsObject().ContainsKey("webhook_token"));
        Assert.Equal("2026-11-02T04:00:00.123456+00:00", straddle["created_at"]!.GetValue<string>());
        // A whole second is written without a fraction, as Python writes it.
        Assert.Equal("2026-11-02T04:00:00+00:00", data[0]!["updated_at"]!.GetValue<string>());

        Assert.Equal(["Short Straddle"], (await Post(client, "/api/v1/strategy/list", new { apikey = key, status = "running" })).Body["data"]!.AsArray().Select(d => d!["name"]!.GetValue<string>()));
        Assert.Equal(["Iron Condor"], (await Post(client, "/api/v1/strategy/list", new { apikey = key, q = "condor" })).Body["data"]!.AsArray().Select(d => d!["name"]!.GetValue<string>()));
        Assert.Equal(400, (await Post(client, "/api/v1/strategy/list", new { apikey = key, status = "paused-ish" })).Status);
        Assert.Equal(403, (await Post(client, "/api/v1/strategy/list", new { apikey = "wrong" })).Status);
    }

    [PostgresFact]
    public async Task Status_shows_the_strategy_with_its_legs_and_current_run_and_404_for_an_unknown_id()
    {
        var (host, key) = await HostAsync();
        await using var _ = host;
        using var client = host.Anonymous();
        var (status, body) = await Post(client, "/api/v1/strategy/status", new { apikey = key, strategy_id = 1 });
        Assert.Equal(200, status);
        Assert.Equal(1, body["data"]!["legs"]![0]!["leg_id"]!.GetValue<int>());
        Assert.Equal(11, body["run"]!["id"]!.GetValue<int>());
        Assert.Equal("scheduler", body["run"]!["trigger_source"]!.GetValue<string>());
        Assert.Equal("13NOV26", body["run"]!["resolved_expiries"]!["1"]!.GetValue<string>());
        Assert.Null((await Post(client, "/api/v1/strategy/status", new { apikey = key, strategy_id = 2 })).Body["run"]);
        var missing = await Post(client, "/api/v1/strategy/status", new { apikey = key, strategy_id = 99 });
        Assert.Equal(404, missing.Status);
        Assert.Equal("Strategy not found", missing.Body["message"]!.GetValue<string>());
        Assert.Equal(400, (await Post(client, "/api/v1/strategy/status", new { apikey = key, strategy_id = 0 })).Status);
        Assert.Equal(400, (await Post(client, "/api/v1/strategy/status", new { apikey = key })).Status);
    }

    [PostgresFact]
    public async Task Runs_orders_and_events_are_scoped_to_the_strategy_and_bounded()
    {
        var (host, key) = await HostAsync();
        await using var _ = host;
        using var client = host.Anonymous();

        var runs = (await Post(client, "/api/v1/strategy/runs", new { apikey = key, strategy_id = 1 })).Body["data"]!.AsArray();
        Assert.Equal([11, 10], runs.Select(r => r!["id"]!.GetValue<int>()));
        Assert.Single((await Post(client, "/api/v1/strategy/runs", new { apikey = key, strategy_id = 1, limit = 1 })).Body["data"]!.AsArray());
        Assert.Equal(400, (await Post(client, "/api/v1/strategy/runs", new { apikey = key, strategy_id = 1, limit = 0 })).Status);
        Assert.Equal(400, (await Post(client, "/api/v1/strategy/runs", new { apikey = key, strategy_id = 1, limit = 501 })).Status);

        var orders = (await Post(client, "/api/v1/strategy/orders", new { apikey = key, strategy_id = 1 })).Body["data"]!.AsArray();
        Assert.Equal([1, 2], orders.Select(o => o!["id"]!.GetValue<int>()));
        Assert.Equal(120.5, orders[0]!["avg_fill_price"]!.GetValue<double>());
        Assert.Equal([2], (await Post(client, "/api/v1/strategy/orders", new { apikey = key, strategy_id = 1, run_id = 11 })).Body["data"]!.AsArray().Select(o => o!["id"]!.GetValue<int>()));

        var events = (await Post(client, "/api/v1/strategy/events", new { apikey = key, strategy_id = 1 })).Body["data"]!.AsArray();
        Assert.Equal([2, 1, 3], events.Select(e => e!["id"]!.GetValue<int>()));
        Assert.Equal(150, events[0]!["payload"]!["price"]!.GetValue<int>());
        Assert.Equal([2], (await Post(client, "/api/v1/strategy/events", new { apikey = key, strategy_id = 1, kind = "leg_sl_hit" })).Body["data"]!.AsArray().Select(e => e!["id"]!.GetValue<int>()));
        Assert.Equal([2], (await Post(client, "/api/v1/strategy/events", new { apikey = key, strategy_id = 1, severity = "warn" })).Body["data"]!.AsArray().Select(e => e!["id"]!.GetValue<int>()));
        Assert.Equal([3], (await Post(client, "/api/v1/strategy/events", new { apikey = key, strategy_id = 1, run_id = 10 })).Body["data"]!.AsArray().Select(e => e!["id"]!.GetValue<int>()));
        Assert.Single((await Post(client, "/api/v1/strategy/events", new { apikey = key, strategy_id = 1, limit = 1 })).Body["data"]!.AsArray());
        Assert.Equal(400, (await Post(client, "/api/v1/strategy/events", new { apikey = key, strategy_id = 1, limit = -1 })).Status);
        Assert.Equal(400, (await Post(client, "/api/v1/strategy/events", new { apikey = key, strategy_id = 1, kind = "made_up" })).Status);
        Assert.Equal(404, (await Post(client, "/api/v1/strategy/events", new { apikey = key, strategy_id = 99 })).Status);
    }

    [PostgresFact]
    public async Task Starting_needs_a_mode_respects_live_opt_in_and_says_plainly_what_the_platform_cannot_do_yet()
    {
        var (host, key) = await HostAsync();
        await using var _ = host;
        using var client = host.Anonymous();

        Assert.Equal(400, (await Post(client, "/api/v1/strategy/start", new { apikey = key, strategy_id = 2 })).Status);
        Assert.Equal(400, (await Post(client, "/api/v1/strategy/start", new { apikey = key, strategy_id = 2, mode = "paper" })).Status);

        var (status, body) = await Post(client, "/api/v1/strategy/start", new { apikey = key, strategy_id = 1, mode = "live" });
        Assert.Equal(409, status);
        Assert.Contains("not enabled for live trading", body["message"]!.GetValue<string>());

        (status, body) = await Post(client, "/api/v1/strategy/start", new { apikey = key, strategy_id = 3, mode = "sandbox" });
        Assert.Equal(400, status);
        Assert.Contains("A signal strategy has no start", body["message"]!.GetValue<string>());

        (status, body) = await Post(client, "/api/v1/strategy/start", new { apikey = key, strategy_id = 1, mode = "sandbox" });
        Assert.Equal(409, status);
        Assert.Equal("This strategy is already running", body["message"]!.GetValue<string>());

        (status, body) = await Post(client, "/api/v1/strategy/start", new { apikey = key, strategy_id = 2, mode = "sandbox" });
        Assert.Equal(501, status);
        Assert.Contains("run engine has not been brought across", body["message"]!.GetValue<string>());
        Assert.Equal(404, (await Post(client, "/api/v1/strategy/start", new { apikey = key, strategy_id = 99, mode = "sandbox" })).Status);
    }

    [PostgresFact]
    public async Task Stopping_and_closing_need_a_running_strategy_and_never_pretend_to_have_closed_anything()
    {
        var (host, key) = await HostAsync();
        await using var _ = host;
        using var client = host.Anonymous();
        foreach (var route in new[] { "stop", "close_all" })
        {
            var idle = await Post(client, $"/api/v1/strategy/{route}", new { apikey = key, strategy_id = 2 });
            Assert.Equal(409, idle.Status);
            Assert.Equal("This strategy is not running", idle.Body["message"]!.GetValue<string>());
            Assert.Equal(501, (await Post(client, $"/api/v1/strategy/{route}", new { apikey = key, strategy_id = 1 })).Status);
            Assert.Equal(404, (await Post(client, $"/api/v1/strategy/{route}", new { apikey = key, strategy_id = 99 })).Status);
        }

        Assert.Equal(409, (await Post(client, "/api/v1/strategy/close_leg", new { apikey = key, strategy_id = 2, leg_id = 1 })).Status);
        Assert.Equal(501, (await Post(client, "/api/v1/strategy/close_leg", new { apikey = key, strategy_id = 1, leg_id = 1 })).Status);
        Assert.Equal(400, (await Post(client, "/api/v1/strategy/close_leg", new { apikey = key, strategy_id = 1 })).Status);
    }
}
