using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.Legacy.Core;
using EPTradeBot.Infrastructure.OpenAlgoApi;
using EPTradeBot.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EPTradeBot.IntegrationTests;

/// <summary>
/// The batch run engine on a sandbox account: entries longs first, each leg's stop, target and trail, the combined limits, the exit time and the daily loss limit, a refused exit that leaves the
/// run open and watched, and the manual stop, close-all and close-leg. Legs are cash legs so the whole path runs on the platform's own order path and quote book.
/// </summary>
public sealed class StrategyRunEngineTests
{
    private static int next = 1000;

    private static async Task<(TestHost Host, Guid Account, string Key)> NewAsync()
    {
        var host = new TestHost("Development");
        var account = await host.CreateAccountAsync(AccountType.PAPER, "p-run", 10_000, 1000, 10_000_000);
        host.SetQuote("NSE", "SBIN", 500);
        host.SetQuote("NSE", "INFY", 1500);
        var key = (await host.Get<ApiKeyService>().IssueAsync(account, "run")).Key;
        return (host, account, key);
    }

    private static async Task<int> AddStrategyAsync(TestHost host, string legs, Action<SmStrategy>? tweak = null)
    {
        var id = Interlocked.Increment(ref next);
        var strategy = new SmStrategy
        {
            Id = id, UserId = "admin", Name = $"Run test {id}", Underlying = "SBIN", UnderlyingExchange = "NSE", Product = "MIS", Legs = legs, LiveEnabled = true, WebhookTokenHash = $"run-hash-{id}",
        };
        tweak?.Invoke(strategy);
        await using var db = host.NewDb();
        db.Set<SmStrategy>().Add(strategy);
        await db.SaveChangesAsync();
        return id;
    }

    private static string Cash(int id, string position, int qty, string symbol = "SBIN", string extra = "") =>
        $$"""{"leg_id":{{id}},"segment":"cash","symbol":"{{symbol}}","exchange":"NSE","position":"{{position}}","lots":{{qty}}{{extra}}}""";

    private static async Task<(int Status, JsonObject Body)> PostAsync(TestHost host, string path, object body)
    {
        using var client = host.Anonymous();
        var response = await client.PostAsJsonAsync(path, body);
        return ((int)response.StatusCode, (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    private static Task<(int Status, JsonObject Body)> StartAsync(TestHost host, string key, int id, string mode = "sandbox") =>
        PostAsync(host, "/api/v1/strategy/start", new { apikey = key, strategy_id = id, mode });

    private static Task Visit(TestHost host) => host.Get<StrategyRunMonitor>().VisitAsync(CancellationToken.None);

    private static async Task<(SmStrategy Strategy, SmStrategyRun Run, List<SmStrategyOrder> Orders, List<string> Events)> ReadAsync(TestHost host, int id)
    {
        await using var db = host.NewDb();
        var strategy = await db.Set<SmStrategy>().AsNoTracking().SingleAsync(s => s.Id == id);
        var run = await db.Set<SmStrategyRun>().AsNoTracking().Where(r => r.StrategyId == id).OrderByDescending(r => r.Id).FirstAsync();
        var orders = await db.Set<SmStrategyOrder>().AsNoTracking().Where(o => o.RunId == run.Id).OrderBy(o => o.Id).ToListAsync();
        var events = await db.Set<SmStrategyEvent>().AsNoTracking().Where(e => e.StrategyId == id).OrderBy(e => e.Id).Select(e => e.Kind).ToListAsync();
        return (strategy, run, orders, events);
    }

    [PostgresFact]
    public async Task Start_places_the_entry_records_the_fill_and_refuses_a_second_start()
    {
        var (host, _, key) = await NewAsync();
        await using var _ = host;
        var id = await AddStrategyAsync(host, $"[{Cash(1, "B", 10, extra: ",\"sl_pts\":5,\"target_pts\":10")}]");

        var (status, body) = await StartAsync(host, key, id);
        Assert.Equal(200, status);
        Assert.Equal("analyze", body["mode"]!.GetValue<string>());

        var (strategy, run, orders, events) = await ReadAsync(host, id);
        Assert.Equal(("running", run.Id), (strategy.Status, strategy.CurrentRunId!.Value));
        Assert.Equal(("sandbox", null), (run.Mode, run.StoppedAt?.ToString()));
        var entry = Assert.Single(orders);
        Assert.Equal(("entry", "BUY", 10, "complete", 500m), (entry.Kind, entry.Action, entry.Qty, entry.Status, entry.AvgFillPrice!.Value));
        Assert.Equal(["run_started", "leg_entry_placed", "leg_entry_filled"], events);
        var again = await StartAsync(host, key, id);
        Assert.Equal(409, again.Status);
    }

    [PostgresFact]
    public async Task A_leg_that_hits_its_stop_is_closed_at_the_market_and_the_run_ends_with_the_loss()
    {
        var (host, _, key) = await NewAsync();
        await using var _ = host;
        var id = await AddStrategyAsync(host, $"[{Cash(1, "B", 10, extra: ",\"sl_pts\":5,\"target_pts\":10")}]");
        Assert.Equal(200, (await StartAsync(host, key, id)).Status);

        host.SetQuote("NSE", "SBIN", 497);
        await Visit(host);
        Assert.Equal("running", (await ReadAsync(host, id)).Strategy.Status);

        host.SetQuote("NSE", "SBIN", 494);
        await Visit(host);
        var (strategy, run, orders, events) = await ReadAsync(host, id);
        Assert.Equal(("stopped", null), (strategy.Status, strategy.CurrentRunId));
        Assert.Equal(("all_legs_closed", -60m), (run.StopReason, run.PnlRealized));
        Assert.Equal(["entry", "exit_sl"], orders.Select(o => o.Kind));
        Assert.Equal(("SELL", 494m), (orders[1].Action, orders[1].AvgFillPrice!.Value));
        Assert.Contains("leg_sl_hit", events);
        Assert.Contains("leg_exit_filled", events);
        Assert.Equal("run_stopped", events[^1]);
    }

    [PostgresFact]
    public async Task A_short_leg_takes_its_target_on_a_fall_and_a_trail_follows_the_price_up()
    {
        var (host, _, key) = await NewAsync();
        await using var _ = host;
        var shortId = await AddStrategyAsync(host, $"[{Cash(1, "S", 10, extra: ",\"sl_pts\":20,\"target_pts\":10")}]");
        Assert.Equal(200, (await StartAsync(host, key, shortId)).Status);
        host.SetQuote("NSE", "SBIN", 489);
        await Visit(host);
        var (_, shortRun, shortOrders, shortEvents) = await ReadAsync(host, shortId);
        Assert.Equal(("all_legs_closed", 110m), (shortRun.StopReason, shortRun.PnlRealized));
        Assert.Equal("exit_target", shortOrders[1].Kind);
        Assert.Contains("leg_target_hit", shortEvents);

        host.SetQuote("NSE", "INFY", 1500);
        var trailId = await AddStrategyAsync(host, $"[{Cash(1, "B", 10, "INFY", ",\"sl_pts\":30,\"trail_x\":10")}]", s => s.Underlying = "INFY");
        Assert.Equal(200, (await StartAsync(host, key, trailId)).Status);
        host.SetQuote("NSE", "INFY", 1540);
        await Visit(host);
        Assert.Equal("running", (await ReadAsync(host, trailId)).Strategy.Status);
        host.SetQuote("NSE", "INFY", 1525);
        await Visit(host);
        var (trailStrategy, trailRun, trailOrders, trailEvents) = await ReadAsync(host, trailId);
        Assert.Equal("stopped", trailStrategy.Status);
        Assert.Equal(["entry", "exit_sl"], trailOrders.Select(o => o.Kind));
        Assert.True(trailRun.PnlRealized > 0, "A trailed stop closes in profit.");
        Assert.Contains("leg_trail_armed", trailEvents);
    }

    [PostgresFact]
    public async Task The_combined_stop_closes_every_open_leg_and_names_why()
    {
        var (host, _, key) = await NewAsync();
        await using var _ = host;
        var id = await AddStrategyAsync(host, $"[{Cash(1, "B", 10)},{Cash(2, "B", 10, "INFY")}]", s => s.OverallSlMtm = 100m);
        Assert.Equal(200, (await StartAsync(host, key, id)).Status);

        host.SetQuote("NSE", "SBIN", 495);
        host.SetQuote("NSE", "INFY", 1495);
        await Visit(host);
        var (strategy, run, orders, events) = await ReadAsync(host, id);
        Assert.Equal("stopped", strategy.Status);
        Assert.Equal(("overall_sl", -100m), (run.StopReason, run.PnlRealized));
        Assert.Equal(2, orders.Count(o => o.Kind == "exit_overall"));
        Assert.Contains("overall_sl_hit", events);
    }

    [PostgresFact]
    public async Task Entries_go_out_buys_first_and_a_refused_entry_closes_what_opened_and_ends_the_run()
    {
        var (host, _, key) = await NewAsync();
        await using var _ = host;
        // The second leg asks for far more than the account can margin.
        var id = await AddStrategyAsync(host, $"[{Cash(1, "S", 10)},{Cash(2, "B", 10, "INFY")},{Cash(3, "B", 5_000_000)}]");
        var (status, body) = await StartAsync(host, key, id);
        Assert.Equal(502, status);
        Assert.Contains("leg 3", body["message"]!.GetValue<string>());
        Assert.DoesNotContain("HTTP", body["message"]!.GetValue<string>());

        var (strategy, run, orders, events) = await ReadAsync(host, id);
        Assert.Equal(("stopped", null), (strategy.Status, strategy.CurrentRunId));
        Assert.Equal("entry_failed", run.StopReason);
        // Buys go first (legs 2 then 3). The refused one stops the run before the sell is sent, and the filled buy is closed again.
        Assert.Equal([2, 3], orders.Where(o => o.Kind == "entry").Select(o => o.LegId));
        Assert.Equal("exit_manual", orders.Last(o => o.Status == "complete").Kind);
        Assert.Equal("rejected", orders.Single(o => o.LegId == 3).Status);
        Assert.Contains("leg_entry_rejected", events);
    }

    [PostgresFact]
    public async Task A_refused_exit_leaves_the_run_open_and_watched_until_the_broker_takes_it()
    {
        var (host, account, key) = await NewAsync();
        await using var _ = host;
        var id = await AddStrategyAsync(host, $"[{Cash(1, "B", 10, extra: ",\"sl_pts\":5")}]");
        Assert.Equal(200, (await StartAsync(host, key, id)).Status);

        await SetEnabledAsync(host, account, false);
        var (status, body) = await PostAsync(host, "/api/v1/strategy/stop", new { apikey = key, strategy_id = id });
        Assert.Equal(502, status);
        Assert.Contains("still open", body["message"]!.GetValue<string>());
        var (strategy, run, orders, events) = await ReadAsync(host, id);
        Assert.Equal(("running", null), (strategy.Status, run.StoppedAt?.ToString()));
        Assert.Contains("leg_exit_rejected", events);
        Assert.Equal("rejected", orders[^1].Status);

        // The stop was asked for, so the next visit finishes it as soon as the account can trade.
        await SetEnabledAsync(host, account, true);
        await Visit(host);
        var after = await ReadAsync(host, id);
        Assert.Equal("stopped", after.Strategy.Status);
        Assert.Equal("manual_stop", after.Run.StopReason);
    }

    private static async Task SetEnabledAsync(TestHost host, Guid account, bool enabled)
    {
        await using var db = host.NewDb();
        await db.Set<BrokerAccount>().Where(a => a.Id == account).ExecuteUpdateAsync(a => a.SetProperty(x => x.IsEnabled, enabled));
    }

    [PostgresFact]
    public async Task Close_leg_closes_one_leg_and_keeps_the_run_and_close_all_ends_it()
    {
        var (host, _, key) = await NewAsync();
        await using var _ = host;
        var id = await AddStrategyAsync(host, $"[{Cash(1, "B", 10)},{Cash(2, "B", 10, "INFY")}]");
        Assert.Equal(200, (await StartAsync(host, key, id)).Status);

        Assert.Equal(404, (await PostAsync(host, "/api/v1/strategy/close_leg", new { apikey = key, strategy_id = id, leg_id = 9 })).Status);
        Assert.Equal(200, (await PostAsync(host, "/api/v1/strategy/close_leg", new { apikey = key, strategy_id = id, leg_id = 1 })).Status);
        Assert.Equal(409, (await PostAsync(host, "/api/v1/strategy/close_leg", new { apikey = key, strategy_id = id, leg_id = 1 })).Status);
        var mid = await ReadAsync(host, id);
        Assert.Equal("running", mid.Strategy.Status);
        Assert.Contains("leg_close_manual", mid.Events);

        Assert.Equal(200, (await PostAsync(host, "/api/v1/strategy/close_all", new { apikey = key, strategy_id = id })).Status);
        var done = await ReadAsync(host, id);
        Assert.Equal(("stopped", "close_all"), (done.Strategy.Status, done.Run.StopReason));
        Assert.Contains("close_all_manual", done.Events);
        Assert.Equal(409, (await PostAsync(host, "/api/v1/strategy/stop", new { apikey = key, strategy_id = id })).Status);
    }

    [PostgresFact]
    public async Task The_exit_time_closes_the_run_and_a_reached_daily_loss_limit_refuses_the_next_start()
    {
        var (host, _, key) = await NewAsync();
        await using var _ = host;
        var timed = await AddStrategyAsync(host, $"[{Cash(1, "B", 10)}]", s => s.ExitTime = new TimeOnly(0, 0));
        Assert.Equal(200, (await StartAsync(host, key, timed)).Status);
        await Visit(host);
        var (strategy, run, _, events) = await ReadAsync(host, timed);
        Assert.Equal(("stopped", "eod_squareoff"), (strategy.Status, run.StopReason));
        Assert.Contains("eod_squareoff", events);

        var limited = await AddStrategyAsync(host, $"[{Cash(1, "B", 10)}]", s => s.DailyLossLimitInr = 50m);
        Assert.Equal(200, (await StartAsync(host, key, limited)).Status);
        host.SetQuote("NSE", "SBIN", 490);
        await Visit(host);
        var after = await ReadAsync(host, limited);
        Assert.Equal(("stopped", "daily_loss_limit"), (after.Strategy.Status, after.Run.StopReason));
        var (status, body) = await StartAsync(host, key, limited);
        Assert.Equal(409, status);
        Assert.Contains("daily loss limit", body["message"]!.GetValue<string>());
    }

    [PostgresFact]
    public async Task A_mode_that_does_not_match_the_account_and_unusable_legs_are_refused_before_anything_is_placed()
    {
        var (host, _, key) = await NewAsync();
        await using var _ = host;
        var id = await AddStrategyAsync(host, $"[{Cash(1, "B", 10)}]");
        var (status, body) = await StartAsync(host, key, id, "live");
        Assert.Equal(409, status);
        Assert.Contains("sandbox account", body["message"]!.GetValue<string>());

        foreach (var (legs, expected) in new[]
        {
            ("[]", "no legs"),
            ("""[{"leg_id":1,"segment":"cash","symbol":"SBIN","exchange":"NSE","lots":1}]""", "buy or sell"),
            ("""[{"leg_id":1,"segment":"cash","symbol":"SBIN","exchange":"NSE","position":"B","lots":0}]""", "no lots"),
        })
        {
            var bad = await AddStrategyAsync(host, legs);
            (status, body) = await StartAsync(host, key, bad);
            Assert.Equal(400, status);
            Assert.Contains(expected, body["message"]!.GetValue<string>());
            await using var db = host.NewDb();
            var row = await db.Set<SmStrategy>().AsNoTracking().SingleAsync(s => s.Id == bad);
            Assert.Equal(("stopped", null), (row.Status, row.CurrentRunId));
            Assert.Empty(await db.Set<SmStrategyOrder>().AsNoTracking().Where(o => db.Set<SmStrategyRun>().Where(r => r.StrategyId == bad).Select(r => r.Id).Contains(o.RunId)).ToListAsync());
        }

        var priced = await AddStrategyAsync(host, $"[{Cash(1, "B", 10)}]", s => s.Pricetype = "LIMIT");
        Assert.Equal(400, (await StartAsync(host, key, priced)).Status);
    }

    [PostgresFact]
    public async Task When_a_stop_fires_and_the_strategy_asks_for_it_the_other_leg_is_made_risk_free()
    {
        var (host, _, key) = await NewAsync();
        await using var _ = host;
        var id = await AddStrategyAsync(host, $"[{Cash(1, "B", 10, extra: ",\"sl_pts\":5")},{Cash(2, "B", 10, "INFY", ",\"sl_pts\":100")}]", s => s.TrailSlToEntry = true);
        Assert.Equal(200, (await StartAsync(host, key, id)).Status);

        // Leg 1 loses its stop while leg 2 is ahead, so leg 2's stop moves up to its own entry.
        host.SetQuote("NSE", "INFY", 1520);
        host.SetQuote("NSE", "SBIN", 494);
        await Visit(host);
        var mid = await ReadAsync(host, id);
        Assert.Equal("running", mid.Strategy.Status);
        Assert.Contains("trail_to_entry_activated", mid.Events);

        // Falling back to its entry now closes leg 2, long before its configured stop 100 points away.
        host.SetQuote("NSE", "INFY", 1499);
        await Visit(host);
        var done = await ReadAsync(host, id);
        Assert.Equal("stopped", done.Strategy.Status);
        Assert.Equal(["entry", "entry", "exit_sl", "exit_sl"], done.Orders.Select(o => o.Kind));
        Assert.Equal(-60m + -10m, done.Run.PnlRealized);
    }

    [PostgresFact]
    public async Task Two_starts_at_once_open_one_run()
    {
        var (host, _, key) = await NewAsync();
        await using var _ = host;
        var id = await AddStrategyAsync(host, $"[{Cash(1, "B", 10)}]");
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => StartAsync(host, key, id)));
        Assert.Equal(1, results.Count(r => r.Status == 200));
        Assert.All(results.Where(r => r.Status != 200), r => Assert.Equal(409, r.Status));
        await using var db = host.NewDb();
        Assert.Equal(1, await db.Set<SmStrategyRun>().CountAsync(r => r.StrategyId == id));
        Assert.Single(await db.Set<SmStrategyOrder>().Where(o => o.Kind == "entry" && db.Set<SmStrategyRun>().Where(r => r.StrategyId == id).Select(r => r.Id).Contains(o.RunId)).ToListAsync());
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static StrategySchedulerService Scheduler(TestHost host, FixedClock clock) =>
        new(host.Get<OpenAlgoApiService>(), host.Get<Microsoft.EntityFrameworkCore.IDbContextFactory<EPTradeBot.Infrastructure.Persistence.EpTradeDbContext>>(), clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<StrategySchedulerService>.Instance);

    // Wednesday 2026-11-11, IST time given, as an instant.
    private static DateTimeOffset Ist(int h, int m, int s = 0) => new DateTimeOffset(2026, 11, 11, h, m, s, TimeSpan.FromHours(5.5)).ToUniversalTime();

    [PostgresFact]
    public async Task The_schedule_starts_the_run_at_its_minute_once_and_squares_it_off_at_the_stop_time()
    {
        var (host, _, key) = await NewAsync();
        await using var _ = host;
        var id = await AddStrategyAsync(host, $"[{Cash(1, "B", 10)}]", s => s.Scheduler = """{"enabled":true,"days":["WED"],"start_time":"09:20","auto_stop_time":"15:15","default_mode":"sandbox"}""");
        var clock = new FixedClock(Ist(9, 19, 50));
        var scheduler = Scheduler(host, clock);

        await scheduler.VisitAsync(CancellationToken.None);
        Assert.Equal("stopped", (await ReadStrategyAsync(host, id)).Status);

        clock.Now = Ist(9, 20, 5);
        await scheduler.VisitAsync(CancellationToken.None);
        var started = await ReadAsync(host, id);
        Assert.Equal(("running", "scheduler", "sandbox"), (started.Strategy.Status, started.Run.TriggerSource, started.Run.Mode));

        // Stopped by hand inside the same minute, the slot is spent: visiting again opens nothing new.
        Assert.Equal(200, (await PostAsync(host, "/api/v1/strategy/stop", new { apikey = key, strategy_id = id })).Status);
        clock.Now = Ist(9, 20, 20);
        await scheduler.VisitAsync(CancellationToken.None);
        await using (var db = host.NewDb()) Assert.Equal(1, await db.Set<SmStrategyRun>().CountAsync(r => r.StrategyId == id));

        // Started again by hand later, the auto-stop still closes it.
        Assert.Equal(200, (await StartAsync(host, key, id)).Status);

        clock.Now = Ist(15, 15, 3);
        await scheduler.VisitAsync(CancellationToken.None);
        var done = await ReadAsync(host, id);
        Assert.Equal(("stopped", "scheduled_stop"), (done.Strategy.Status, done.Run.StopReason));
    }

    [PostgresFact]
    public async Task The_schedule_skips_other_days_a_missed_slot_and_a_strategy_that_is_off()
    {
        var (host, _, _) = await NewAsync();
        await using var _ = host;
        var otherDay = await AddStrategyAsync(host, $"[{Cash(1, "B", 10)}]", s => s.Scheduler = """{"enabled":true,"days":["MON"],"start_time":"09:20","default_mode":"sandbox"}""");
        var off = await AddStrategyAsync(host, $"[{Cash(1, "B", 10)}]", s => s.Scheduler = """{"enabled":false,"days":["WED"],"start_time":"09:20","default_mode":"sandbox"}""");
        var late = await AddStrategyAsync(host, $"[{Cash(1, "B", 10)}]", s => s.Scheduler = """{"enabled":true,"days":["WED"],"start_time":"09:20","default_mode":"sandbox"}""");
        // Two minutes after the slot: too late to enter a market that has moved, whatever the day or switch.
        await Scheduler(host, new FixedClock(Ist(9, 22, 0))).VisitAsync(CancellationToken.None);
        Assert.Equal("stopped", (await ReadStrategyAsync(host, late)).Status);
        // On the slot's minute, the Monday-only and switched-off strategies still stay stopped.
        await Scheduler(host, new FixedClock(Ist(9, 20, 5))).VisitAsync(CancellationToken.None);
        Assert.Equal("stopped", (await ReadStrategyAsync(host, otherDay)).Status);
        Assert.Equal("stopped", (await ReadStrategyAsync(host, off)).Status);
        Assert.Equal("running", (await ReadStrategyAsync(host, late)).Status);
    }

    [PostgresFact]
    public async Task A_missed_start_with_no_account_to_trade_on_is_said_plainly()
    {
        var host = new TestHost("Development");
        await using var _ = host;
        var id = await AddStrategyAsync(host, $"[{Cash(1, "B", 10)}]", s => s.Scheduler = """{"enabled":true,"days":["WED"],"start_time":"09:20","default_mode":"sandbox"}""");
        await Scheduler(host, new FixedClock(Ist(9, 20, 5))).VisitAsync(CancellationToken.None);
        await using var db = host.NewDb();
        var ev = await db.Set<SmStrategyEvent>().AsNoTracking().SingleAsync(e => e.StrategyId == id);
        Assert.Contains("no enabled sandbox account", ev.Message);
        Assert.Equal("stopped", (await ReadStrategyAsync(host, id)).Status);
    }

    private static async Task<SmStrategy> ReadStrategyAsync(TestHost host, int id)
    {
        await using var db = host.NewDb();
        return await db.Set<SmStrategy>().AsNoTracking().SingleAsync(s => s.Id == id);
    }
}
