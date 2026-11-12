using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.Legacy.Core;
using EPTradeBot.Infrastructure.OpenAlgoApi;
using Microsoft.EntityFrameworkCore;

namespace EPTradeBot.IntegrationTests;

/// <summary>The strategy webhook: what each stage refuses and in which order, that a refusal never says whether a token exists, that nothing secret is stored, and that a start and a stop reach the engine.</summary>
public sealed class StrategyWebhookTests
{
    private static int next = 5000;
    private const string Good = "oaws_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789_-abc";

    private static async Task<(TestHost Host, int Id, string Token)> NewAsync(Action<SmStrategy>? tweak = null, bool account = true)
    {
        var host = new TestHost("Development");
        if (account)
        {
            await host.CreateAccountAsync(AccountType.PAPER, "p-hook", 10_000, 1000, 10_000_000);
            host.SetQuote("NSE", "SBIN", 500);
        }

        var id = Interlocked.Increment(ref next);
        var token = $"oaws_{Guid.NewGuid():N}{Guid.NewGuid():N}"[..48];
        var strategy = new SmStrategy
        {
            Id = id, UserId = "admin", Name = $"Hook {id}", Underlying = "SBIN", UnderlyingExchange = "NSE", Product = "MIS", LiveEnabled = false, WebhookTokenHash = StrategyWebhookService.Hash(token),
            Legs = """[{"leg_id":1,"segment":"cash","symbol":"SBIN","exchange":"NSE","position":"B","lots":10}]""",
        };
        tweak?.Invoke(strategy);
        await using var db = host.NewDb();
        db.Set<SmStrategy>().Add(strategy);
        await db.SaveChangesAsync();
        return (host, id, token);
    }

    private static async Task<(int Status, JsonObject Body)> HookAsync(TestHost host, string token, string body)
    {
        using var client = host.Anonymous();
        var response = await client.PostAsync($"/api/v1/strategy/webhook/{token}", new StringContent(body, Encoding.UTF8, "application/json"));
        return ((int)response.StatusCode, (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    private static async Task<List<SmWebhookEvent>> AuditAsync(TestHost host, int id)
    {
        await using var db = host.NewDb();
        return await db.Set<SmWebhookEvent>().AsNoTracking().Where(e => e.StrategyId == id).OrderBy(e => e.Id).ToListAsync();
    }

    [PostgresFact]
    public async Task An_unknown_and_a_malformed_token_get_the_same_answer_and_are_audited_without_a_strategy()
    {
        var (host, id, _) = await NewAsync();
        await using var _ = host;
        var unknown = await HookAsync(host, Good, """{"action":"start","mode":"sandbox"}""");
        var malformed = await HookAsync(host, "not-a-token", """{"action":"start","mode":"sandbox"}""");
        Assert.Equal(404, unknown.Status);
        Assert.Equal(unknown.Status, malformed.Status);
        Assert.Equal(unknown.Body.ToJsonString(), malformed.Body.ToJsonString());
        Assert.Equal("rejected_token", unknown.Body["result"]!.GetValue<string>());
        Assert.Equal("Unknown or expired webhook token", unknown.Body["message"]!.GetValue<string>());
        await using var db = host.NewDb();
        Assert.Equal(2, await db.Set<SmWebhookEvent>().CountAsync(e => e.Result == "rejected_token" && e.StrategyId == null));
        Assert.Empty(await AuditAsync(host, id));
    }

    [PostgresFact]
    public async Task A_start_runs_the_strategy_records_the_alert_and_never_stores_the_token_or_a_secret()
    {
        var (host, id, token) = await NewAsync();
        await using var _ = host;
        var (status, body) = await HookAsync(host, token, $$"""{"action":"START","mode":"sandbox","note":"{{token}}","apikey":"hunter2","tag":"ok"}""");
        Assert.Equal(200, status);
        Assert.Equal(("ok", id), (body["result"]!.GetValue<string>(), body["strategy_id"]!.GetValue<int>()));
        var audit = Assert.Single(await AuditAsync(host, id));
        Assert.Equal(("ok", "start", "sandbox"), (audit.Result, audit.Action, audit.Mode));
        Assert.DoesNotContain(token, audit.Payload!);
        Assert.DoesNotContain("hunter2", audit.Payload!);
        Assert.Contains("\"tag\":\"ok\"", audit.Payload!.Replace(" ", ""));
        await using var db = host.NewDb();
        var run = await db.Set<SmStrategyRun>().AsNoTracking().SingleAsync(r => r.StrategyId == id);
        Assert.Equal(("webhook", audit.Id), (run.TriggerSource, run.WebhookEventId!.Value));
        Assert.Equal("running", (await db.Set<SmStrategy>().AsNoTracking().SingleAsync(s => s.Id == id)).Status);
    }

    [PostgresFact]
    public async Task A_repeat_inside_a_minute_is_one_signal_and_a_start_right_after_a_stop_is_refused()
    {
        var (host, id, token) = await NewAsync();
        await using var _ = host;
        Assert.Equal(200, (await HookAsync(host, token, """{"action":"start","mode":"sandbox"}""")).Status);
        var repeat = await HookAsync(host, token, """{"action":"start","mode":"sandbox"}""");
        Assert.Equal((200, "rejected_dedupe"), (repeat.Status, repeat.Body["result"]!.GetValue<string>()));
        await using (var db = host.NewDb()) Assert.Equal(1, await db.Set<SmStrategyRun>().CountAsync(r => r.StrategyId == id));

        var stop = await HookAsync(host, token, """{"action":"stop"}""");
        Assert.Equal((200, "ok"), (stop.Status, stop.Body["result"]!.GetValue<string>()));
        await using (var db = host.NewDb()) Assert.Equal("stopped", (await db.Set<SmStrategy>().AsNoTracking().SingleAsync(s => s.Id == id)).Status);

        // A live start is refused for its own reason before anything else is weighed.
        var again = await HookAsync(host, token, """{"action":"start","mode":"live"}""");
        Assert.Equal(403, again.Status);
        // Two minutes on, the first start is no longer a repeat, so only the cooling-off window stands in the way.
        await using (var db = host.NewDb()) await db.Set<SmWebhookEvent>().Where(e => e.StrategyId == id && e.Action == "start").ExecuteUpdateAsync(u => u.SetProperty(e => e.ReceivedAt, DateTime.UtcNow.AddMinutes(-2)));
        var cooling = await HookAsync(host, token, """{"action":"start","mode":"sandbox"}""");
        Assert.Equal(("rejected_cooling_off", 409), (cooling.Body["result"]!.GetValue<string>(), cooling.Status));
        Assert.Contains("try again in", cooling.Body["message"]!.GetValue<string>());
    }

    [PostgresFact]
    public async Task Each_stage_refuses_in_its_own_order_and_says_why()
    {
        var (host, id, token) = await NewAsync(s => s.WebhookLocked = true);
        await using var _ = host;
        var locked = await HookAsync(host, token, """{"action":"start","mode":"sandbox"}""");
        Assert.Equal((403, "rejected_locked"), (locked.Status, locked.Body["result"]!.GetValue<string>()));
        await using (var db = host.NewDb()) await db.Set<SmStrategy>().Where(s => s.Id == id).ExecuteUpdateAsync(u => u.SetProperty(s => s.WebhookLocked, false).SetProperty(s => s.WebhookIpAllowlist, """["203.0.113.0/24"]"""));
        // The test server presents no caller address, and a listed allowlist is closed.
        var outside = await HookAsync(host, token, "not json at all");
        Assert.Equal((403, "rejected_ip"), (outside.Status, outside.Body["result"]!.GetValue<string>()));
        await using (var db = host.NewDb()) await db.Set<SmStrategy>().Where(s => s.Id == id).ExecuteUpdateAsync(u => u.SetProperty(s => s.WebhookIpAllowlist, (string?)null));

        foreach (var (body, result, status) in new[]
        {
            ("not json at all", "rejected_payload", 400), ("[1,2]", "rejected_payload", 400), ("", "rejected_payload", 400),
            ("""{"action":"fly"}""", "rejected_invalid_action", 400), ("""{"mode":"sandbox"}""", "rejected_invalid_action", 400),
            ("""{"action":"start"}""", "rejected_invalid_action", 400), ("""{"action":"start","mode":"paper"}""", "rejected_invalid_action", 400),
            ("""{"action":"start","mode":"live"}""", "rejected_live_disabled", 403),
        })
        {
            var answer = await HookAsync(host, token, body);
            Assert.Equal((status, result), (answer.Status, answer.Body["result"]!.GetValue<string>()));
        }

        var big = await HookAsync(host, token, "{\"action\":\"start\",\"pad\":\"" + new string('x', 17_000) + "\"}");
        Assert.Equal(("rejected_payload", 400), (big.Body["result"]!.GetValue<string>(), big.Status));
        Assert.Contains("larger than 16384 bytes", big.Body["message"]!.GetValue<string>());
        Assert.All(await AuditAsync(host, id), e => Assert.NotEqual("ok", e.Result));
        await using var check = host.NewDb();
        Assert.Equal(0, await check.Set<SmStrategyRun>().CountAsync(r => r.StrategyId == id));
    }

    [PostgresFact]
    public async Task A_batch_action_on_a_signal_strategy_and_a_start_with_no_account_are_refused_plainly()
    {
        var (host, _, token) = await NewAsync(s => s.StrategyKind = "signal");
        await using var _ = host;
        var batch = await HookAsync(host, token, """{"action":"start","mode":"sandbox"}""");
        Assert.Equal(400, batch.Status);
        Assert.Contains("long_entry", batch.Body["message"]!.GetValue<string>());

        var (bare, _, bareToken) = await NewAsync(account: false);
        await using var _2 = bare;
        var none = await HookAsync(bare, bareToken, """{"action":"start","mode":"sandbox"}""");
        Assert.Equal(500, none.Status);
        Assert.Contains("no enabled sandbox account", none.Body["message"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null, "[]", true)]
    [InlineData(null, "", true)]
    [InlineData(null, """["10.0.0.0/8"]""", false)]
    [InlineData("", """["10.0.0.0/8"]""", false)]
    [InlineData("junk", """["10.0.0.0/8"]""", false)]
    [InlineData("10.1.2.3", """["10.0.0.0/8"]""", true)]
    [InlineData("11.1.2.3", """["10.0.0.0/8"]""", false)]
    [InlineData("203.0.113.7", """["203.0.113.7"]""", true)]
    [InlineData("203.0.113.8", """["203.0.113.7"]""", false)]
    [InlineData("::ffff:10.1.2.3", """["10.0.0.0/8"]""", true)]
    [InlineData("2001:db8::1", """["2001:db8::/32"]""", true)]
    [InlineData("2001:db9::1", """["2001:db8::/32"]""", false)]
    [InlineData("11.1.2.3", """["not-an-ip","11.1.0.0/16"]""", true)]
    [InlineData("10.1.2.3", """["10.0.0.0/33"]""", false)]
    [InlineData("192.168.1.130", """["192.168.1.128/25"]""", true)]
    [InlineData("192.168.1.100", """["192.168.1.128/25"]""", false)]
    public void The_allowlist_is_closed_when_it_has_entries_and_skips_a_malformed_one(string? ip, string list, bool expected) =>
        Assert.Equal(expected, StrategyWebhookService.IpAllowed(ip, list));

    // ---- signal mode ----

    private const string SignalLegs = """[{"id":1,"symbol":"SBIN","exchange":"NSE","side":"both","qty":10,"sl_pts":5},{"id":2,"symbol":"INFY","exchange":"NSE","side":"long","qty":5}]""";

    private static async Task<(TestHost Host, int Id, string Token)> SignalAsync(Action<SmStrategy>? tweak = null)
    {
        var made = await NewAsync(st => { st.StrategyKind = "signal"; st.Legs = SignalLegs; tweak?.Invoke(st); });
        made.Host.SetQuote("NSE", "INFY", 1500);
        return made;
    }

    private static async Task<(string Result, string Message, int Status)> SignalHookAsync(TestHost host, string token, string action, int leg = 1)
    {
        var (status, body) = await HookAsync(host, token, $$"""{"action":"{{action}}","leg_id":{{leg}}}""");
        return (body["result"]!.GetValue<string>(), body["message"]!.GetValue<string>(), status);
    }

    private static async Task<(SmStrategy Strategy, SmStrategyRun Run, List<SmStrategyOrder> Orders)> StateAsync(TestHost host, int id)
    {
        await using var db = host.NewDb();
        var strategy = await db.Set<SmStrategy>().AsNoTracking().SingleAsync(s => s.Id == id);
        var run = await db.Set<SmStrategyRun>().AsNoTracking().Where(r => r.StrategyId == id).OrderByDescending(r => r.Id).FirstAsync();
        var orders = await db.Set<SmStrategyOrder>().AsNoTracking().Where(o => o.RunId == run.Id).OrderBy(o => o.Id).ToListAsync();
        return (strategy, run, orders);
    }

    [PostgresFact]
    public async Task The_first_signal_of_the_day_opens_the_run_and_entries_exits_and_repeats_behave()
    {
        var (host, id, token) = await SignalAsync();
        await using var _ = host;
        var entry = await SignalHookAsync(host, token, "long_entry");
        Assert.Equal(("ok", 200), (entry.Result, entry.Status));
        var (strategy, run, orders) = await StateAsync(host, id);
        Assert.Equal(("running", "sandbox", "webhook"), (strategy.Status, run.Mode, run.TriggerSource));
        Assert.Equal(("entry", "BUY", 10, "complete", 500m), (orders[0].Kind, orders[0].Action, orders[0].Qty, orders[0].Status, orders[0].AvgFillPrice!.Value));

        Assert.Contains("already_long", (await SignalHookAsync(host, token, "long_entry")).Message);
        Assert.Contains("no_matching_position", (await SignalHookAsync(host, token, "short_exit")).Message);

        host.SetQuote("NSE", "SBIN", 510);
        Assert.Equal("ok", (await SignalHookAsync(host, token, "long_exit")).Result);
        var after = await StateAsync(host, id);
        // The run stays open for the rest of the day; the leg is flat with its profit booked.
        Assert.Equal(("running", null), (after.Strategy.Status, after.Run.StoppedAt?.ToString()));
        Assert.Equal(["entry", "exit_signal"], after.Orders.Select(o => o.Kind));
        Assert.Contains("no_matching_position", (await SignalHookAsync(host, token, "long_exit")).Message);
        Assert.Equal(1, await CountRunsAsync(host, id));

        // The same leg can be signalled again the same day, into the same run.
        host.SetQuote("NSE", "SBIN", 505);
        Assert.Equal("ok", (await SignalHookAsync(host, token, "long_entry")).Result);
        Assert.Equal(1, await CountRunsAsync(host, id));
        Assert.Equal(["entry", "exit_signal", "entry"], (await StateAsync(host, id)).Orders.Select(o => o.Kind));
    }

    private static async Task<int> CountRunsAsync(TestHost host, int id)
    {
        await using var db = host.NewDb();
        return await db.Set<SmStrategyRun>().CountAsync(r => r.StrategyId == id);
    }

    [PostgresFact]
    public async Task A_signal_on_the_other_side_closes_the_held_position_then_opens_the_new_one()
    {
        var (host, id, token) = await SignalAsync();
        await using var _ = host;
        Assert.Equal("ok", (await SignalHookAsync(host, token, "long_entry")).Result);
        host.SetQuote("NSE", "SBIN", 504);
        Assert.Equal("ok", (await SignalHookAsync(host, token, "short_entry")).Result);
        var (_, _, orders) = await StateAsync(host, id);
        Assert.Equal([("entry", "BUY"), ("exit_signal", "SELL"), ("entry", "SELL")], orders.Select(o => (o.Kind, o.Action)));
        Assert.All(orders, o => Assert.Equal("complete", o.Status));
        Assert.Contains("already_short", (await SignalHookAsync(host, token, "short_entry")).Message);
    }

    [PostgresFact]
    public async Task Signals_that_contradict_the_configuration_are_refused_and_never_reach_the_broker()
    {
        var (host, _, token) = await SignalAsync();
        await using var _h = host;
        var side = await SignalHookAsync(host, token, "short_entry", leg: 2);
        Assert.Equal(("rejected_invalid_action", 400), (side.Result, side.Status));
        Assert.Contains("only accepts long", side.Message);
        var none = await SignalHookAsync(host, token, "long_entry", leg: 9);
        Assert.Equal(400, none.Status);
        Assert.Contains("No leg matches", none.Message);
        var bySymbol = await HookAsync(host, token, """{"action":"long_entry","symbol":"INFY","exchange":"NSE"}""");
        Assert.Equal(200, bySymbol.Status);
        await using var db = host.NewDb();
        Assert.Equal(1, await db.Set<SmStrategyOrder>().CountAsync());

        var (h2, _, t2) = await SignalAsync(st => st.Direction = "long_only");
        await using var _2 = h2;
        var direction = await SignalHookAsync(h2, t2, "short_entry");
        Assert.Equal(400, direction.Status);
        Assert.Contains("long_only", direction.Message);

        var (h3, _, t3) = await SignalAsync(st => st.Product = "CNC");
        await using var _3 = h3;
        var naked = await SignalHookAsync(h3, t3, "short_entry");
        Assert.Equal(400, naked.Status);
        Assert.Contains("cannot be held short overnight", naked.Message);
    }

    [PostgresFact]
    public async Task The_trading_window_makes_a_signal_an_accepted_no_op_and_exits_are_allowed_early()
    {
        var (host, _, token) = await SignalAsync(st => st.EntryTime = new TimeOnly(23, 59));
        await using var _h = host;
        var early = await SignalHookAsync(host, token, "long_entry");
        Assert.Equal(("ok", 200), (early.Result, early.Status));
        Assert.Contains("outside_entry_window", early.Message);
        Assert.Contains("no_matching_position", (await SignalHookAsync(host, token, "long_exit")).Message.Replace("outside_entry_window", ""));
        await using var db = host.NewDb();
        Assert.Equal(0, await db.Set<SmStrategyOrder>().CountAsync());

        var (h2, _, t2) = await SignalAsync(st => st.ExitTime = new TimeOnly(0, 0));
        await using var _2 = h2;
        var late = await SignalHookAsync(h2, t2, "long_entry");
        Assert.Contains("outside_trading_window", late.Message);
        Assert.Equal(0, await h2.NewDb().Set<SmStrategyRun>().CountAsync());
    }

    [PostgresFact]
    public async Task A_leg_stopped_out_goes_flat_and_the_run_stays_open_and_the_exit_time_closes_it()
    {
        var (host, id, token) = await SignalAsync();
        await using var _ = host;
        Assert.Equal("ok", (await SignalHookAsync(host, token, "long_entry")).Result);
        host.SetQuote("NSE", "SBIN", 494);
        await host.Get<StrategyRunMonitor>().VisitAsync(CancellationToken.None);
        var stopped = await StateAsync(host, id);
        Assert.Equal(["entry", "exit_sl"], stopped.Orders.Select(o => o.Kind));
        Assert.Equal(("running", null), (stopped.Strategy.Status, stopped.Run.StoppedAt?.ToString()));

        // A position is open when the exit time passes: the monitor squares it off and ends the run.
        host.SetQuote("NSE", "SBIN", 500);
        Assert.Equal("ok", (await SignalHookAsync(host, token, "long_entry")).Result);
        await using (var db = host.NewDb()) await db.Set<SmStrategy>().Where(s => s.Id == id).ExecuteUpdateAsync(u => u.SetProperty(s => s.ExitTime, new TimeOnly(0, 0)));
        await host.Get<StrategyRunMonitor>().VisitAsync(CancellationToken.None);
        var done = await StateAsync(host, id);
        Assert.Equal(("stopped", "eod_squareoff"), (done.Strategy.Status, done.Run.StopReason));
        Assert.Equal("exit_eod", done.Orders[^1].Kind);
    }

    [PostgresFact]
    public async Task A_run_from_an_earlier_day_is_closed_by_the_first_signal_of_the_next_and_a_new_one_opens()
    {
        var (host, id, token) = await SignalAsync();
        await using var _ = host;
        Assert.Equal("ok", (await SignalHookAsync(host, token, "long_entry")).Result);
        await using (var db = host.NewDb()) await db.Set<SmStrategyRun>().Where(r => r.StrategyId == id).ExecuteUpdateAsync(u => u.SetProperty(r => r.StartedAt, DateTime.UtcNow.AddDays(-2)));
        Assert.Equal("ok", (await SignalHookAsync(host, token, "long_entry")).Result);
        await using var check = host.NewDb();
        var runs = await check.Set<SmStrategyRun>().AsNoTracking().Where(r => r.StrategyId == id).OrderBy(r => r.Id).ToListAsync();
        Assert.Equal(2, runs.Count);
        Assert.Equal("eod", runs[0].StopReason);
        Assert.Null(runs[1].StoppedAt);
        Assert.Equal(runs[1].Id, (await check.Set<SmStrategy>().AsNoTracking().SingleAsync(s => s.Id == id)).CurrentRunId);
    }

    [PostgresFact]
    public async Task Realised_profit_accumulates_across_round_trips_and_lots_are_multiplied_by_the_lot_size()
    {
        var (host, id, token) = await SignalAsync(st => st.Legs = """[{"id":1,"symbol":"SBIN","exchange":"NSE","side":"both","qty":10},{"id":3,"symbol":"INFY","exchange":"NSE","side":"both","qty":2,"qty_mode":"lots"}]""");
        await using var _ = host;
        await using (var seed = host.NewDb())
        {
            seed.Set<SymToken>().Add(new SymToken { Broker = "sandbox", Symbol = "INFY", Brsymbol = "INFY", Exchange = "NSE", Brexchange = "NSE", Token = "1594", Lotsize = 3 });
            await seed.SaveChangesAsync();
        }

        Assert.Equal("ok", (await SignalHookAsync(host, token, "long_entry", leg: 3)).Result);
        Assert.Equal(6, (await StateAsync(host, id)).Orders[0].Qty);
        Assert.Equal("ok", (await SignalHookAsync(host, token, "long_exit", leg: 3)).Result);

        Assert.Equal("ok", (await SignalHookAsync(host, token, "long_entry")).Result);
        host.SetQuote("NSE", "SBIN", 510);
        Assert.Equal("ok", (await SignalHookAsync(host, token, "long_exit")).Result);
        Assert.Equal("ok", (await SignalHookAsync(host, token, "long_entry")).Result);
        host.SetQuote("NSE", "SBIN", 505);
        Assert.Equal("ok", (await SignalHookAsync(host, token, "long_exit")).Result);

        // 10 x (510 - 500) then 10 x (505 - 510): the run's realised figure is the sum, not the last trip.
        await using var db = host.NewDb();
        var run = await db.Set<SmStrategyRun>().AsNoTracking().SingleAsync(r => r.StrategyId == id);
        var checkpoint = await db.Set<SmStrategyCheckpoint>().AsNoTracking().SingleAsync(c => c.RunId == run.Id);
        Assert.Equal(50m, checkpoint.PnlRealized);
        Assert.Contains("\"status\":\"flat\"", checkpoint.LegState.Replace(" ", ""));
    }

    [PostgresFact]
    public async Task A_symbol_on_another_exchange_does_not_match_a_leg()
    {
        var (host, _, token) = await SignalAsync();
        await using var _ = host;
        var wrong = await HookAsync(host, token, """{"action":"long_entry","symbol":"INFY","exchange":"BSE"}""");
        Assert.Equal(400, wrong.Status);
        Assert.Contains("No leg matches", wrong.Body["message"]!.GetValue<string>());
        Assert.Equal(200, (await HookAsync(host, token, """{"action":"long_entry","symbol":"infy"}""")).Status);
    }
}
