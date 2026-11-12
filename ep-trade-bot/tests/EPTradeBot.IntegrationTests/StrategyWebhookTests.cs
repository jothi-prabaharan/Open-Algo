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
    public async Task A_signal_strategy_and_a_start_with_no_account_are_refused_plainly()
    {
        var (host, _, token) = await NewAsync(s => s.StrategyKind = "signal");
        await using var _ = host;
        var signal = await HookAsync(host, token, """{"action":"long_entry"}""");
        Assert.Equal(400, signal.Status);
        Assert.Contains("signal strategy", signal.Body["message"]!.GetValue<string>());
        Assert.Equal(400, (await HookAsync(host, token, """{"action":"start","mode":"sandbox"}""")).Status);

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
}
