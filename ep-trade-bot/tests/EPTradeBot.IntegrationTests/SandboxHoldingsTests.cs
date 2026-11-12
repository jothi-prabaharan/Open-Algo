using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Entities.Audit;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.Legacy.Sandbox;
using EPTradeBot.Core.Trading;
using EPTradeBot.Infrastructure.Sandbox;
using EPTradeBot.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EPTradeBot.IntegrationTests;

/// <summary>Delivery (CNC) positions become holdings the day after, with the margin and sale proceeds moved as OpenAlgo's sandbox moves them, and holdings are read back at the latest prices.</summary>
public sealed class SandboxHoldingsTests
{
    private static OrderIntent Cnc(Guid account, string action, int quantity) => new()
    {
        AccountId = account, Source = OrderSource.Manual, Symbol = "SBIN", Exchange = "NSE", Action = action, Quantity = quantity, Product = "CNC", PriceType = "MARKET",
    };

    private static async Task Backdate(TestHost host, string userId, int daysAgo)
    {
        await using var db = host.NewDb();
        foreach (var p in await db.Set<SandboxPosition>().Where(p => p.UserId == userId && p.Product == "CNC").ToListAsync()) p.CreatedAt = DateTime.UtcNow.AddDays(-daysAgo);
        await db.SaveChangesAsync();
    }

    [PostgresFact]
    public async Task A_purchase_becomes_a_holding_the_next_day_and_later_purchases_and_sales_adjust_it()
    {
        await using var host = new TestHost("Development");
        var id = await host.CreateAccountAsync(AccountType.PAPER, "p-hold", 10_000, 1000, 10_000_000);
        var key = (await host.Get<ApiKeyService>().IssueAsync(id, "holdings")).Key;
        var sandbox = host.Get<SandboxEngine>();
        var userId = SandboxEngine.UserId(id);
        var nowIst = EPTradeBot.Core.Time.Ist.FromUtc(DateTime.UtcNow);

        host.SetQuote("NSE", "SBIN", 500);
        await sandbox.PlaceAsync(Cnc(id, "BUY", 10));
        var reserved = (await sandbox.GetFundsAsync(id)).UsedMargin;
        Assert.Equal(5000m, reserved);

        // Bought today: not a holding yet.
        Assert.Equal(0, await sandbox.SettleT1Async(nowIst));
        Assert.Empty(await sandbox.GetHoldingsAsync(id));

        await Backdate(host, userId, 1);
        Assert.Equal(1, await sandbox.SettleT1Async(nowIst));
        var holding = Assert.Single(await sandbox.GetHoldingsAsync(id));
        Assert.Equal((10, 500m), (holding.Quantity, holding.AveragePrice));
        Assert.Equal(DateOnly.FromDateTime(nowIst), holding.SettlementDate);
        Assert.Empty(await sandbox.GetPositionsAsync(id));
        Assert.Equal(0m, (await sandbox.GetFundsAsync(id)).UsedMargin);
        Assert.Equal(0, await sandbox.SettleT1Async(nowIst));

        // A second purchase of 30 at 520 is folded in at the weighted average (10 at 500 and 30 at 520 make 515).
        host.SetQuote("NSE", "SBIN", 520);
        await sandbox.PlaceAsync(Cnc(id, "BUY", 30));
        await Backdate(host, userId, 1);
        var availableBefore = (await sandbox.GetFundsAsync(id)).AvailableBalance;
        Assert.Equal(1, await sandbox.SettleT1Async(nowIst));
        holding = Assert.Single(await sandbox.GetHoldingsAsync(id));
        Assert.Equal((40, 515m), (holding.Quantity, holding.AveragePrice));
        Assert.Equal(0m, (await sandbox.GetFundsAsync(id)).UsedMargin);

        // A sale of 5 comes off the holding and its proceeds (5 x 530) are credited.
        host.SetQuote("NSE", "SBIN", 530);
        await sandbox.PlaceAsync(Cnc(id, "SELL", 5));
        await Backdate(host, userId, 1);
        availableBefore = (await sandbox.GetFundsAsync(id)).AvailableBalance;
        Assert.Equal(1, await sandbox.SettleT1Async(nowIst));
        holding = Assert.Single(await sandbox.GetHoldingsAsync(id));
        Assert.Equal(35, holding.Quantity);
        Assert.Equal(availableBefore + 5 * 530m, (await sandbox.GetFundsAsync(id)).AvailableBalance);

        // The holdings answer shows it marked to the latest price.
        host.SetQuote("NSE", "SBIN", 540);
        using var client = host.Anonymous();
        var response = await client.PostAsJsonAsync("/api/v1/holdings", new { apikey = key });
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var row = Assert.Single(body["data"]!["holdings"]!.AsArray())!;
        Assert.Equal(("SBIN", 35, 515.0, 540.0), (row["symbol"]!.GetValue<string>(), row["quantity"]!.GetValue<int>(), row["average_price"]!.GetValue<double>(), row["ltp"]!.GetValue<double>()));
        Assert.Equal(875.0, row["pnl"]!.GetValue<double>());
        Assert.Equal(18025.0, body["data"]!["statistics"]!["totalinvvalue"]!.GetValue<double>());
        Assert.Equal(18900.0, body["data"]!["statistics"]!["totalholdingvalue"]!.GetValue<double>());
    }

    [PostgresFact]
    public async Task Selling_out_a_holding_removes_it()
    {
        await using var host = new TestHost("Development");
        var id = await host.CreateAccountAsync(AccountType.PAPER, "p-out", 10_000, 1000, 10_000_000);
        var sandbox = host.Get<SandboxEngine>();
        var userId = SandboxEngine.UserId(id);
        var nowIst = EPTradeBot.Core.Time.Ist.FromUtc(DateTime.UtcNow);
        host.SetQuote("NSE", "SBIN", 500);
        await sandbox.PlaceAsync(Cnc(id, "BUY", 4));
        await Backdate(host, userId, 1);
        await sandbox.SettleT1Async(nowIst);
        await sandbox.PlaceAsync(Cnc(id, "SELL", 4));
        await Backdate(host, userId, 1);
        Assert.Equal(1, await sandbox.SettleT1Async(nowIst));
        Assert.Empty(await sandbox.GetHoldingsAsync(id));
        await using var db = host.NewDb();
        Assert.Empty(await db.Set<SandboxHolding>().Where(h => h.UserId == userId).ToListAsync());
    }
}
