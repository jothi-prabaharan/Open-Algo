using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.Legacy.Sandbox;
using EPTradeBot.Infrastructure.Sandbox;
using EPTradeBot.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EPTradeBot.IntegrationTests;

/// <summary>Good-till-triggered orders in the sandbox: margin reserved while they rest and given back once, one trigger firing a single order, an OCO cancelling its sibling, and the refusals a trader would hit.</summary>
public sealed class SandboxGttTests
{
    private static async Task<(TestHost Host, Guid Account, string Key)> NewAsync()
    {
        var host = new TestHost("Development");
        var account = await host.CreateAccountAsync(AccountType.PAPER, "p-gtt", 10_000, 1000, 10_000_000);
        host.SetQuote("NSE", "SBIN", 500);
        var key = (await host.Get<ApiKeyService>().IssueAsync(account, "gtt")).Key;
        return (host, account, key);
    }

    private static async Task<(int Status, JsonObject Body)> PostAsync(TestHost host, string path, object body)
    {
        using var client = host.Anonymous();
        var response = await client.PostAsJsonAsync(path, body);
        return ((int)response.StatusCode, (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    private static object Single(string key, string? triggerId = null, decimal quantity = 10, decimal trigger = 480, decimal price = 481, string action = "BUY", string? expires = null) => new
    {
        apikey = key, strategy = "dip", trigger_id = triggerId, trigger_type = "SINGLE", exchange = "NSE", symbol = "SBIN", action, product = "CNC", quantity, pricetype = "LIMIT", price,
        triggerprice_sl = trigger, expires_at = expires,
    };

    private static object Oco(string key, string? triggerId = null, decimal quantity = 10, string action = "SELL") => new
    {
        apikey = key, strategy = "guard", trigger_id = triggerId, trigger_type = "OCO", exchange = "NSE", symbol = "SBIN", action, product = "CNC", quantity, pricetype = "LIMIT", price = 0,
        triggerprice_sl = 480, stoploss = 479, triggerprice_tg = 520, target = 521,
    };

    [PostgresFact]
    public async Task A_resting_trigger_reserves_margin_lists_as_active_and_gives_it_back_once_on_cancel()
    {
        var (host, account, key) = await NewAsync();
        await using var _ = host;
        var sandbox = host.Get<SandboxEngine>();
        var before = await sandbox.GetFundsAsync(account);

        var (status, placed) = await PostAsync(host, "/api/v1/placegttorder", Single(key));
        Assert.Equal(200, status);
        var id = placed["trigger_id"]!.GetValue<string>();
        Assert.Matches(@"^GTT-\d{6}-[0-9a-f]{8}$", id);
        Assert.Equal("analyze", placed["mode"]!.GetValue<string>());
        var funds = await sandbox.GetFundsAsync(account);
        Assert.Equal(4810m, funds.UsedMargin - before.UsedMargin);
        Assert.Equal(before.AvailableBalance - 4810m, funds.AvailableBalance);

        var (_, book) = await PostAsync(host, "/api/v1/gttorderbook", new { apikey = key });
        var row = Assert.Single(book["data"]!.AsArray())!;
        Assert.Equal((id, "single", "active", "SBIN", 4810.0), (row["trigger_id"]!.GetValue<string>(), row["trigger_type"]!.GetValue<string>(), row["status"]!.GetValue<string>(), row["symbol"]!.GetValue<string>(), row["margin_blocked"]!.GetValue<double>()));
        Assert.Equal([480.0], row["trigger_prices"]!.AsArray().Select(t => t!.GetValue<double>()));
        Assert.Equal(500.0, row["last_price"]!.GetValue<double>());
        Assert.Equal("dip", row["strategy"]!.GetValue<string>());
        // Left to default, a trigger rests for a year.
        Assert.StartsWith(EPTradeBot.Core.Time.Ist.FromUtc(DateTime.UtcNow).AddDays(365).ToString("yyyy-MM-dd"), row["expires_at"]!.GetValue<string>());

        (status, var cancelled) = await PostAsync(host, "/api/v1/cancelgttorder", new { apikey = key, strategy = "dip", trigger_id = id });
        Assert.Equal(200, status);
        Assert.Equal(id, cancelled["trigger_id"]!.GetValue<string>());
        funds = await sandbox.GetFundsAsync(account);
        Assert.Equal(before.UsedMargin, funds.UsedMargin);
        Assert.Equal(before.AvailableBalance, funds.AvailableBalance);
        Assert.Empty((await PostAsync(host, "/api/v1/gttorderbook", new { apikey = key })).Body["data"]!.AsArray());
        Assert.Equal("cancelled", Assert.Single((await PostAsync(host, "/api/v1/gttorderbook", new { apikey = key, status = "all" })).Body["data"]!.AsArray())!["status"]!.GetValue<string>());

        (status, var again) = await PostAsync(host, "/api/v1/cancelgttorder", new { apikey = key, strategy = "dip", trigger_id = id });
        Assert.Equal(404, status);
        Assert.Contains("No active GTT", again["message"]!.GetValue<string>());
        Assert.Equal(before.AvailableBalance, (await sandbox.GetFundsAsync(account)).AvailableBalance);
    }

    [PostgresFact]
    public async Task When_the_price_reaches_the_trigger_one_order_is_placed_and_the_reservation_becomes_the_orders_own()
    {
        var (host, account, key) = await NewAsync();
        await using var _ = host;
        var sandbox = host.Get<SandboxEngine>();
        var (_, placed) = await PostAsync(host, "/api/v1/placegttorder", Single(key));
        var id = placed["trigger_id"]!.GetValue<string>();

        host.SetQuote("NSE", "SBIN", 490);
        Assert.Equal(0, await sandbox.MatchGttAsync("NSE", "SBIN", 490));
        host.SetQuote("NSE", "SBIN", 480);
        await sandbox.MatchAsync("NSE", "SBIN");
        // A second tick on the same price does not fire it again.
        await sandbox.MatchAsync("NSE", "SBIN");

        var order = Assert.Single(await sandbox.GetOrdersAsync(account));
        Assert.Equal(("BUY", 10, "dip", "complete"), (order.Action, order.Quantity, order.Strategy, order.OrderStatus));
        var position = Assert.Single(await sandbox.GetPositionsAsync(account));
        Assert.Equal(10, position.Quantity);

        var row = Assert.Single((await PostAsync(host, "/api/v1/gttorderbook", new { apikey = key, status = "all" })).Body["data"]!.AsArray())!;
        Assert.Equal("triggered", row["status"]!.GetValue<string>());
        Assert.Equal(order.Orderid, row["legs"]![0]!["triggered_order_id"]!.GetValue<string>());
        Assert.Equal(0.0, row["margin_blocked"]!.GetValue<double>());
        // The reservation was released and the filled order reserved its own once: 10 shares at the 481 limit it filled at.
        Assert.Equal(4810m, (await sandbox.GetFundsAsync(account)).UsedMargin);
        Assert.Empty((await PostAsync(host, "/api/v1/gttorderbook", new { apikey = key })).Body["data"]!.AsArray());
        Assert.Equal(id, row["trigger_id"]!.GetValue<string>());
    }

    [PostgresFact]
    public async Task An_OCO_reserves_the_larger_leg_and_the_first_trigger_reached_cancels_the_other()
    {
        var (host, account, key) = await NewAsync();
        await using var _ = host;
        var sandbox = host.Get<SandboxEngine>();
        var before = await sandbox.GetFundsAsync(account);
        var (status, placed) = await PostAsync(host, "/api/v1/placegttorder", Oco(key));
        Assert.Equal(200, status);
        var id = placed["trigger_id"]!.GetValue<string>();
        // The stoploss leg sells at 479 and the target leg at 521: the larger, 5210, is what is reserved.
        Assert.Equal(5210m, (await sandbox.GetFundsAsync(account)).UsedMargin - before.UsedMargin);

        host.SetQuote("NSE", "SBIN", 521);
        await sandbox.MatchAsync("NSE", "SBIN");

        var order = Assert.Single(await sandbox.GetOrdersAsync(account));
        Assert.Equal(("SELL", 521m), (order.Action, order.Price ?? 0));
        await using var db = host.NewDb();
        var legs = await db.Set<SandboxGttLeg>().Where(l => l.GttId == id).OrderBy(l => l.LegNumber).ToListAsync();
        Assert.Equal(["cancelled", "triggered"], legs.Select(l => l.LegStatus));
        Assert.Equal(order.Orderid, legs[1].TriggeredOrderId);
        Assert.Equal("triggered", (await db.Set<SandboxGtt>().SingleAsync(g => g.GttId == id)).GttStatus);
    }

    [PostgresFact]
    public async Task Modifying_reprices_a_resting_trigger_and_settles_the_margin_difference_but_not_what_the_contract_fixes()
    {
        var (host, account, key) = await NewAsync();
        await using var _ = host;
        var sandbox = host.Get<SandboxEngine>();
        var before = await sandbox.GetFundsAsync(account);
        var id = (await PostAsync(host, "/api/v1/placegttorder", Single(key))).Body["trigger_id"]!.GetValue<string>();

        var (status, modified) = await PostAsync(host, "/api/v1/modifygttorder", Single(key, id, quantity: 20, trigger: 470, price: 471));
        Assert.Equal(200, status);
        Assert.Equal(id, modified["trigger_id"]!.GetValue<string>());
        Assert.Equal(9420m, (await sandbox.GetFundsAsync(account)).UsedMargin - before.UsedMargin);
        var row = Assert.Single((await PostAsync(host, "/api/v1/gttorderbook", new { apikey = key })).Body["data"]!.AsArray())!;
        Assert.Equal([470.0], row["trigger_prices"]!.AsArray().Select(t => t!.GetValue<double>()));
        Assert.Equal(20, row["legs"]![0]!["quantity"]!.GetValue<int>());

        var (flip, flipped) = await PostAsync(host, "/api/v1/modifygttorder", Single(key, id, action: "SELL"));
        Assert.Equal(400, flip);
        Assert.Contains("action cannot be changed", flipped["message"]!.GetValue<string>());
        var (shape, reshaped) = await PostAsync(host, "/api/v1/modifygttorder", Oco(key, id, action: "BUY"));
        Assert.Equal(400, shape);
        Assert.Contains("trigger_type cannot be changed", reshaped["message"]!.GetValue<string>());
        Assert.Equal(404, (await PostAsync(host, "/api/v1/modifygttorder", Single(key, "GTT-000000-deadbeef"))).Status);
        Assert.Equal(9420m, (await sandbox.GetFundsAsync(account)).UsedMargin - before.UsedMargin);

        // A change the funds cannot carry leaves the GTT as it was.
        var (huge, refused) = await PostAsync(host, "/api/v1/modifygttorder", Single(key, id, quantity: 100_000_000, trigger: 470, price: 471));
        Assert.Equal(400, huge);
        Assert.Contains("The GTT is unchanged", refused["message"]!.GetValue<string>());
        Assert.Equal(9420m, (await sandbox.GetFundsAsync(account)).UsedMargin - before.UsedMargin);
    }

    [PostgresFact]
    public async Task A_GTT_the_funds_cannot_carry_is_refused_and_one_past_its_expiry_gives_its_margin_back()
    {
        var (host, account, key) = await NewAsync();
        await using var _ = host;
        var sandbox = host.Get<SandboxEngine>();
        var before = await sandbox.GetFundsAsync(account);

        var (status, refused) = await PostAsync(host, "/api/v1/placegttorder", Single(key, quantity: 100_000_000));
        Assert.Equal(400, status);
        Assert.Contains("Not enough sandbox funds", refused["message"]!.GetValue<string>());
        Assert.Equal(before.UsedMargin, (await sandbox.GetFundsAsync(account)).UsedMargin);

        var id = (await PostAsync(host, "/api/v1/placegttorder", Single(key, expires: "2020-01-01"))).Body["trigger_id"]!.GetValue<string>();
        Assert.Equal(1, await sandbox.ExpireDueGttsAsync());
        Assert.Equal(0, await sandbox.ExpireDueGttsAsync());
        Assert.Equal(before.UsedMargin, (await sandbox.GetFundsAsync(account)).UsedMargin);
        Assert.Equal("expired", Assert.Single((await PostAsync(host, "/api/v1/gttorderbook", new { apikey = key, status = "all" })).Body["data"]!.AsArray())!["status"]!.GetValue<string>());
        Assert.Equal(404, (await PostAsync(host, "/api/v1/cancelgttorder", new { apikey = key, strategy = "dip", trigger_id = id })).Status);
    }

    [PostgresFact]
    public async Task A_market_trigger_has_no_limit_so_its_margin_is_sized_at_the_trigger()
    {
        var (host, account, key) = await NewAsync();
        await using var _ = host;
        var before = await host.Get<SandboxEngine>().GetFundsAsync(account);
        var (status, _) = await PostAsync(host, "/api/v1/placegttorder", new
        {
            apikey = key, strategy = "dip", trigger_type = "SINGLE", exchange = "NSE", symbol = "SBIN", action = "BUY", product = "CNC", quantity = 10, pricetype = "MARKET", price = 0, triggerprice_sl = 480,
        });
        Assert.Equal(200, status);
        Assert.Equal(4800m, (await host.Get<SandboxEngine>().GetFundsAsync(account)).UsedMargin - before.UsedMargin);
    }

    [Theory]
    [InlineData("below", 480, 480, true)]
    [InlineData("below", 480, 480.05, false)]
    [InlineData("below", 480, 470, true)]
    [InlineData("above", 520, 520, true)]
    [InlineData("above", 520, 519.95, false)]
    [InlineData("above", 520, 530, true)]
    public void A_trigger_fires_when_touched_in_its_own_direction_whatever_the_action(string direction, double trigger, double ltp, bool fires) =>
        Assert.Equal(fires, SandboxEngine.IsTriggeredBy(direction, (decimal)trigger, (decimal)ltp));
}
