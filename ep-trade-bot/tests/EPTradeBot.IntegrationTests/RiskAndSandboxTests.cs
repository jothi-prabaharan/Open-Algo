using System.Net;
using System.Net.Http.Json;
using EPTradeBot.Core.Entities.Audit;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.Legacy.Sandbox;
using EPTradeBot.Core.Trading;
using EPTradeBot.Infrastructure.Sandbox;
using Microsoft.EntityFrameworkCore;

namespace EPTradeBot.IntegrationTests;

public sealed class RiskAndSandboxTests
{
    private static object Market(string action, int qty, string symbol = "SBIN") => new { exchange = "NSE", symbol, action, quantity = qty };

    private static async Task<string> Code(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>())!.TryGetValue("code", out var c) ? c.GetString()! : "";

    [PostgresFact]
    public async Task Max_order_quantity_boundary()
    {
        await using var host = new TestHost("Development");
        var id = await host.CreateAccountAsync(AccountType.PAPER, "p-qty", 10_000, 100, 10_000_000);
        host.SetQuote("NSE", "SBIN", 500);

        Assert.Equal(HttpStatusCode.OK, (await host.Admin.PostAsJsonAsync($"/api/v1/accounts/{id}/orders", Market("BUY", 100))).StatusCode);
        var over = await host.Admin.PostAsJsonAsync($"/api/v1/accounts/{id}/orders", Market("BUY", 101));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, over.StatusCode);
        Assert.Equal("above_max_order_quantity", await Code(over));
    }

    [PostgresFact]
    public async Task Max_daily_loss_blocks_new_positions_but_not_the_exit()
    {
        await using var host = new TestHost("Development");
        var id = await host.CreateAccountAsync(AccountType.PAPER, "p-loss", 10_000, 1000, 10_000_000);
        host.SetQuote("NSE", "SBIN", 500);
        Assert.Equal(HttpStatusCode.OK, (await host.Admin.PostAsJsonAsync($"/api/v1/accounts/{id}/orders", Market("BUY", 100))).StatusCode);

        host.SetQuote("NSE", "SBIN", 399); // -10,100 marked to market
        var add = await host.Admin.PostAsJsonAsync($"/api/v1/accounts/{id}/orders", Market("BUY", 1));
        Assert.Equal("max_daily_loss_reached", await Code(add));

        var exit = await host.Admin.PostAsJsonAsync($"/api/v1/accounts/{id}/orders", Market("SELL", 100));
        Assert.Equal(HttpStatusCode.OK, exit.StatusCode);

        // The loss is now realised; it still counts for the rest of the day.
        var again = await host.Admin.PostAsJsonAsync($"/api/v1/accounts/{id}/orders", Market("BUY", 1));
        Assert.Equal("max_daily_loss_reached", await Code(again));

        var funds = await host.Get<SandboxEngine>().GetFundsAsync(id);
        Assert.Equal(-10_100m, funds.TodayRealizedPnl);
        Assert.Equal(10_000_000m - 10_100m, funds.AvailableBalance);
        Assert.Equal(0m, funds.UsedMargin);
    }

    [PostgresFact]
    public async Task Max_position_value_boundary_counts_what_is_already_held()
    {
        await using var host = new TestHost("Development");
        var id = await host.CreateAccountAsync(AccountType.PAPER, "p-value", 100_000, 1000, 60_000);
        host.SetQuote("NSE", "SBIN", 500);
        Assert.Equal(HttpStatusCode.OK, (await host.Admin.PostAsJsonAsync($"/api/v1/accounts/{id}/orders", Market("BUY", 100))).StatusCode);
        Assert.Equal("above_max_position_value", await Code(await host.Admin.PostAsJsonAsync($"/api/v1/accounts/{id}/orders", Market("BUY", 21))));
        Assert.Equal(HttpStatusCode.OK, (await host.Admin.PostAsJsonAsync($"/api/v1/accounts/{id}/orders", Market("BUY", 20))).StatusCode);
    }

    [PostgresFact]
    public async Task Concurrent_orders_cannot_spend_the_same_margin()
    {
        await using var host = new TestHost("Development");
        var id = await host.CreateAccountAsync(AccountType.PAPER, "p-race");
        host.SetQuote("NSE", "SBIN", 1000);
        var sandbox = host.Get<SandboxEngine>();
        await sandbox.GetFundsAsync(id); // create the funds row first so every writer contends on it

        // CNC is 1x: each order needs 40,00,000 of the 1,00,00,000 capital, so exactly two fit.
        // The engine is called directly, bypassing the dispatcher's account lock, so the
        // database row versions are the only thing standing between these writers.
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            await gate.Task;
            return await sandbox.PlaceAsync(new OrderIntent
            {
                AccountId = id, Source = OrderSource.Api, Symbol = "SBIN", Exchange = "NSE", Action = "BUY", Quantity = 4000, Product = "CNC",
            });
        }).ToList();
        gate.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(2, results.Count(r => r.Status == DispatchStatus.Accepted));
        Assert.Equal(8, results.Count(r => r.Code == "insufficient_funds"));
        await using var db = host.NewDb();
        var funds = await db.Set<SandboxFund>().SingleAsync();
        Assert.Equal(2_000_000m, funds.AvailableBalance);
        Assert.Equal(8_000_000m, funds.UsedMargin);
        var position = await db.Set<SandboxPosition>().SingleAsync();
        Assert.Equal(8000, position.Quantity);
        Assert.Equal(8_000_000m, position.MarginBlocked);
        Assert.Equal(2, await db.Set<SandboxTrade>().CountAsync());
        Assert.Equal(2, await db.Set<SandboxOrder>().CountAsync());
    }

    [PostgresFact]
    public async Task A_limit_order_blocks_margin_until_it_fills_or_is_cancelled()
    {
        await using var host = new TestHost("Development");
        var id = await host.CreateAccountAsync(AccountType.PAPER, "p-limit");
        host.SetQuote("NSE", "SBIN", 500);
        var sandbox = host.Get<SandboxEngine>();
        object Limit(decimal price) => new { exchange = "NSE", symbol = "SBIN", action = "BUY", quantity = 100, pricetype = "LIMIT", price, product = "CNC" };

        var first = await host.Admin.PostAsJsonAsync($"/api/v1/accounts/{id}/orders", Limit(490));
        var firstId = (await first.Content.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>())!["order_id"].GetString()!;
        Assert.Equal(49_000m, (await sandbox.GetFundsAsync(id)).UsedMargin);
        Assert.Equal(HttpStatusCode.OK, (await host.Admin.DeleteAsync($"/api/v1/accounts/{id}/orders/{firstId}")).StatusCode);
        Assert.Equal(0m, (await sandbox.GetFundsAsync(id)).UsedMargin);

        await host.Admin.PostAsJsonAsync($"/api/v1/accounts/{id}/orders", Limit(490));
        host.SetQuote("NSE", "SBIN", 489.5m);
        Assert.Equal(1, await sandbox.MatchAsync("NSE", "SBIN"));
        var position = (await sandbox.GetPositionsAsync(id)).Single();
        Assert.Equal(100, position.Quantity);
        Assert.Equal(490m, position.AveragePrice);
        var funds = await sandbox.GetFundsAsync(id);
        Assert.Equal(49_000m, funds.UsedMargin);
        Assert.Equal(10_000_000m - 49_000m, funds.AvailableBalance);
    }

    [PostgresFact]
    public async Task Every_dispatch_is_audited_with_its_outcome()
    {
        await using var host = new TestHost("Development");
        var id = await host.CreateAccountAsync(AccountType.PAPER, "p-audit", 10_000, 100, 10_000_000);
        host.SetQuote("NSE", "SBIN", 500);
        await host.Admin.PostAsJsonAsync($"/api/v1/accounts/{id}/orders", Market("BUY", 10));
        await host.Admin.PostAsJsonAsync($"/api/v1/accounts/{id}/orders", Market("BUY", 101));

        await using var db = host.NewDb();
        var rows = await db.OrderAuditLogs.Where(a => a.AccountId == id).OrderBy(a => a.Id).ToListAsync();
        Assert.Equal([OrderOutcome.Accepted, OrderOutcome.Refused], rows.Select(r => r.Outcome));
        Assert.NotNull(rows[0].BrokerOrderId);
        Assert.StartsWith("above_max_order_quantity", rows[1].Reason);
    }
}
