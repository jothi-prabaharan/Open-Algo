using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EPTradeBot.Core.Entities.Audit;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Time;
using EPTradeBot.Infrastructure.Persistence.Interceptors;
using EPTradeBot.Infrastructure.Sandbox;
using Microsoft.EntityFrameworkCore;

namespace EPTradeBot.IntegrationTests;

public sealed class KillSwitchAndAuditTests
{
    private static object Order(string action, int qty, string symbol, string type = "MARKET", decimal price = 0) =>
        new { exchange = "NSE", symbol, action, quantity = qty, pricetype = type, price };

    private static async Task SeedPaperBookAsync(TestHost host, Guid id)
    {
        Assert.Equal(HttpStatusCode.OK, (await host.Admin.PostAsJsonAsync($"/api/v1/accounts/{id}/orders", Order("BUY", 100, "SBIN"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Admin.PostAsJsonAsync($"/api/v1/accounts/{id}/orders", Order("SELL", 50, "INFY"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Admin.PostAsJsonAsync($"/api/v1/accounts/{id}/orders", Order("BUY", 10, "SBIN", "LIMIT", 400))).StatusCode);
    }

    [PostgresFact]
    public async Task Account_kill_switch_cancels_squares_off_blocks_and_audits()
    {
        await using var host = new TestHost("Development");
        var id = await host.CreateAccountAsync(AccountType.PAPER, "p-kill");
        host.SetQuote("NSE", "SBIN", 500);
        host.SetQuote("NSE", "INFY", 1500);
        await SeedPaperBookAsync(host, id);

        var engage = await host.Admin.PostAsJsonAsync($"/api/v1/killswitch/account/{id}", new { engage = true, reason = "test" });
        Assert.Equal(HttpStatusCode.OK, engage.StatusCode);
        var body = await engage.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("completed").GetBoolean());
        Assert.Equal(1, body.GetProperty("cancelled_orders").GetArrayLength());
        Assert.Equal(2, body.GetProperty("squared_off").GetArrayLength());

        var sandbox = host.Get<SandboxEngine>();
        Assert.All(await sandbox.GetPositionsAsync(id), p => Assert.Equal(0, p.Quantity));
        Assert.DoesNotContain(await sandbox.GetOrdersAsync(id), o => o.OrderStatus == "open");
        Assert.Equal(0m, (await sandbox.GetFundsAsync(id)).UsedMargin);

        var blocked = await host.Admin.PostAsJsonAsync($"/api/v1/accounts/{id}/orders", Order("BUY", 1, "SBIN"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        Assert.Contains("kill_switch_engaged", await blocked.Content.ReadAsStringAsync());

        await using (var db = host.NewDb())
        {
            var account = await db.BrokerAccounts.SingleAsync(a => a.Id == id);
            Assert.True(account.KillSwitchEngaged);
            var ev = await db.KillSwitchEvents.SingleAsync();
            Assert.Equal(KillSwitchScope.Account, ev.Scope);
            Assert.True(ev.Engaged);
            Assert.True(ev.Completed);
            Assert.Equal(Ist.FromUtc(ev.OccurredAtUtc), ev.OccurredAtIst);
            Assert.Equal(2, await db.OrderAuditLogs.CountAsync(a => a.Source == OrderSource.KillSwitch && a.Outcome == OrderOutcome.Accepted));
        }

        Assert.Equal(HttpStatusCode.OK, (await host.Admin.PostAsJsonAsync($"/api/v1/killswitch/account/{id}", new { engage = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Admin.PostAsJsonAsync($"/api/v1/accounts/{id}/orders", Order("BUY", 1, "SBIN"))).StatusCode);
    }

    [PostgresFact]
    public async Task Global_kill_switch_needs_confirmation_and_never_reports_a_failed_account_as_done()
    {
        await using var host = new TestHost("Development");
        var a = await host.CreateAccountAsync(AccountType.PAPER, "p-g1");
        var b = await host.CreateAccountAsync(AccountType.PAPER, "p-g2");
        var live = await host.CreateAccountAsync(AccountType.LIVE, "l-g3", 10_000, 100, 1_000_000);
        host.SetQuote("NSE", "SBIN", 500);
        host.SetQuote("NSE", "INFY", 1500);
        await SeedPaperBookAsync(host, a);
        await SeedPaperBookAsync(host, b);
        await using (var db = host.NewDb())
        {
            db.HostedStrategies.Add(new HostedStrategy { Name = "s1", ScriptPath = "s1.py", IsEnabled = true });
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await host.Admin.PostAsJsonAsync("/api/v1/killswitch/global", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Admin.PostAsJsonAsync("/api/v1/killswitch/global", new { confirmation = "kill all" })).StatusCode);

        var response = await host.Admin.PostAsJsonAsync("/api/v1/killswitch/global", new { confirmation = "KILL ALL", reason = "drill" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, body.GetProperty("accounts").GetInt32());
        Assert.Equal(1, body.GetProperty("strategies_halted").GetInt32());

        // The live account has no broker connection in this build: its positions could not be read,
        // so the switch is reported as not completed for it, and for the whole.
        Assert.False(body.GetProperty("completed").GetBoolean());
        var results = body.GetProperty("results").EnumerateArray().ToDictionary(r => r.GetProperty("account_id").GetGuid());
        Assert.True(results[a].GetProperty("completed").GetBoolean());
        Assert.True(results[b].GetProperty("completed").GetBoolean());
        Assert.False(results[live].GetProperty("completed").GetBoolean());

        var sandbox = host.Get<SandboxEngine>();
        foreach (var id in new[] { a, b })
            Assert.All(await sandbox.GetPositionsAsync(id), p => Assert.Equal(0, p.Quantity));

        await using var check = host.NewDb();
        Assert.True(await check.BrokerAccounts.AllAsync(x => x.KillSwitchEngaged));
        Assert.False((await check.HostedStrategies.SingleAsync()).IsEnabled);
        Assert.Equal(1, await check.KillSwitchEvents.CountAsync(e => e.Scope == KillSwitchScope.Global && !e.Completed));
        Assert.Equal(3, await check.KillSwitchEvents.CountAsync(e => e.Scope == KillSwitchScope.Account));
    }

    [PostgresFact]
    public async Task Audit_records_cannot_be_updated_or_deleted_by_any_ef_path()
    {
        await using var host = new TestHost("Development");
        var id = await host.CreateAccountAsync(AccountType.PAPER, "p-imm");
        host.SetQuote("NSE", "SBIN", 500);
        await host.Admin.PostAsJsonAsync($"/api/v1/accounts/{id}/orders", Order("BUY", 1, "SBIN"));
        await host.Admin.PostAsJsonAsync($"/api/v1/killswitch/account/{id}", new { engage = true });

        await using (var db = host.NewDb())
        {
            var row = await db.OrderAuditLogs.FirstAsync();
            db.Entry(row).Property(r => r.Reason).CurrentValue = "edited";
            await Assert.ThrowsAsync<AuditRecordImmutableException>(() => db.SaveChangesAsync());
        }

        await using (var db = host.NewDb())
        {
            db.KillSwitchEvents.Remove(await db.KillSwitchEvents.FirstAsync());
            await Assert.ThrowsAsync<AuditRecordImmutableException>(() => db.SaveChangesAsync());
        }

        await using (var db = host.NewDb())
        {
            await Assert.ThrowsAsync<AuditRecordImmutableException>(() => db.OrderAuditLogs.ExecuteDeleteAsync());
            await Assert.ThrowsAsync<AuditRecordImmutableException>(() => db.KillSwitchEvents.ExecuteUpdateAsync(u => u.SetProperty(e => e.Reason, "x")));
            await Assert.ThrowsAsync<AuditRecordImmutableException>(() => db.ActionCenterDecisions.ExecuteDeleteAsync());
        }

        await using var final = host.NewDb();
        Assert.Equal(1, await final.KillSwitchEvents.CountAsync());
        Assert.False(await final.OrderAuditLogs.AnyAsync(r => r.Reason == "edited"));
        Assert.True(await final.OrderAuditLogs.CountAsync() >= 2);
    }
}
