using System.Net;
using System.Net.Http.Json;
using EPTradeBot.Core.Entities.Audit;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.Legacy.Sandbox;
using EPTradeBot.Core.Trading;
using Microsoft.EntityFrameworkCore;

namespace EPTradeBot.IntegrationTests;

/// <summary>Development trades PAPER accounts only, from every entry path; Production trades both.</summary>
public sealed class GuardTests
{
    private static OrderIntent Order(Guid account, OrderSource source) => new()
    {
        AccountId = account, Source = source, Symbol = "SBIN", Exchange = "NSE", Action = "BUY", Quantity = 1, Actor = "test",
    };

    [PostgresFact]
    public async Task Live_order_over_http_in_Development_is_refused_with_403_before_the_handler()
    {
        await using var host = new TestHost("Development");
        var live = await host.CreateAccountAsync(AccountType.LIVE, "live-1", 10_000, 100, 1_000_000);
        host.SetQuote("NSE", "SBIN", 500);

        var response = await host.Admin.PostAsJsonAsync($"/api/v1/accounts/{live}/orders", new { exchange = "NSE", symbol = "SBIN", action = "BUY", quantity = 1 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("live_locked", await response.Content.ReadAsStringAsync());
        await using var db = host.NewDb();
        Assert.Equal(0, await db.Set<SandboxOrder>().CountAsync());
        Assert.Equal(0, await db.OrderAuditLogs.CountAsync(a => a.Outcome == OrderOutcome.Accepted));
    }

    [PostgresFact]
    public async Task Live_order_in_Development_is_refused_from_every_source_and_audited()
    {
        await using var host = new TestHost("Development");
        var live = await host.CreateAccountAsync(AccountType.LIVE, "live-2", 10_000, 100, 1_000_000);
        host.SetQuote("NSE", "SBIN", 500);
        var dispatcher = host.Get<IOrderDispatcher>();
        var sources = Enum.GetValues<OrderSource>().Where(s => s != OrderSource.KillSwitch).ToList();

        foreach (var source in sources)
        {
            var result = await dispatcher.DispatchAsync(Order(live, source));
            Assert.Equal(DispatchStatus.Refused, result.Status);
            Assert.Equal("live_locked", result.Code);
        }

        await using var db = host.NewDb();
        var audit = await db.OrderAuditLogs.Where(a => a.AccountId == live).ToListAsync();
        Assert.Equal(sources.Count, audit.Count);
        Assert.All(audit, a =>
        {
            Assert.Equal(OrderOutcome.Refused, a.Outcome);
            Assert.Equal("Development", a.Environment);
            Assert.StartsWith("live_locked", a.Reason);
            Assert.Equal(a.OccurredAtUtc.AddHours(5.5), a.OccurredAtIst);
        });
        Assert.Equal(sources.Order(), audit.Select(a => a.Source).Order());
        Assert.Equal(0, await db.Set<SandboxOrder>().CountAsync());
    }

    [PostgresFact]
    public async Task Development_reports_live_accounts_as_locked()
    {
        await using var host = new TestHost("Development");
        var live = await host.CreateAccountAsync(AccountType.LIVE, "live-3");
        var paper = await host.CreateAccountAsync(AccountType.PAPER, "paper-3");

        var env = await host.Admin.GetFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>("/api/v1/environment");
        Assert.False(env!["live_trading_allowed"].GetBoolean());
        Assert.Equal([live.ToString()], env["locked_account_ids"].EnumerateArray().Select(e => e.GetString()));

        var accounts = await host.Admin.GetFromJsonAsync<List<Dictionary<string, System.Text.Json.JsonElement>>>("/api/v1/accounts");
        Assert.True(accounts!.Single(a => a["id"].GetString() == live.ToString())["locked"].GetBoolean());
        Assert.False(accounts!.Single(a => a["id"].GetString() == paper.ToString())["locked"].GetBoolean());
    }

    [PostgresFact]
    public async Task Production_lets_live_orders_past_the_guard_and_paper_orders_fill()
    {
        await using var host = new TestHost("Production");
        var live = await host.CreateAccountAsync(AccountType.LIVE, "live-4", 10_000, 100, 1_000_000);
        var paper = await host.CreateAccountAsync(AccountType.PAPER, "paper-4");
        host.SetQuote("NSE", "SBIN", 500);
        var dispatcher = host.Get<IOrderDispatcher>();

        // Past the guard and the limits; no broker connection exists yet, so it fails closed, and says so.
        var liveResult = await dispatcher.DispatchAsync(Order(live, OrderSource.Api));
        Assert.NotEqual("live_locked", liveResult.Code);
        Assert.Equal("exposure_unavailable", liveResult.Code);

        var paperResult = await dispatcher.DispatchAsync(Order(paper, OrderSource.Api));
        Assert.Equal(DispatchStatus.Accepted, paperResult.Status);

        var env = await host.Admin.GetFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>("/api/v1/environment");
        Assert.True(env!["live_trading_allowed"].GetBoolean());
    }

    [PostgresFact]
    public async Task Management_endpoints_require_the_admin_token()
    {
        await using var host = new TestHost("Development");
        using var anonymous = host.Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/killswitch/global", new { confirmation = "KILL ALL" })).StatusCode);
        anonymous.DefaultRequestHeaders.Add("X-Admin-Token", "wrong-token-wrong-token-wrong-token-00");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/accounts")).StatusCode);
    }
}
