using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.Legacy.Core;
using EPTradeBot.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EPTradeBot.IntegrationTests;

/// <summary>The market calendar seed, chart preferences and the sandbox day P&amp;L, end to end.</summary>
public sealed class PlatformApiTests
{
    private static async Task<(int Status, JsonObject Body)> PostAsync(TestHost host, string path, object body)
    {
        using var client = host.Anonymous();
        var response = await client.PostAsJsonAsync(path, body);
        return ((int)response.StatusCode, (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    private static async Task<JsonObject> GetChartAsync(TestHost host, string key)
    {
        using var client = host.Anonymous();
        return (JsonObject)JsonNode.Parse(await client.GetStringAsync($"/api/v1/chart?apikey={Uri.EscapeDataString(key)}"))!;
    }

    [PostgresFact]
    public async Task Market_calendar_is_seeded_once_and_an_edited_entry_is_kept()
    {
        await using var host = new TestHost("Development");
        await using (var db = host.NewDb())
        {
            // OpenAlgo's fresh install: 2025 and 2026, after the 2026 correction (31 entries), and nine exchanges' timings.
            Assert.Equal(31, await db.Set<MarketHoliday>().CountAsync());
            Assert.Equal(9, await db.Set<MarketTiming>().CountAsync());
            Assert.Equal(56_400_000, (await db.Set<MarketTiming>().SingleAsync(t => t.ExchangeCode == "NFO")).EndOffset);
            var muhurat = await db.Set<MarketHoliday>().SingleAsync(h => h.HolidayDate == new DateOnly(2026, 11, 8));
            Assert.Equal("SPECIAL_SESSION", muhurat.HolidayType);
            Assert.Equal(8, await db.Set<MarketHolidayExchange>().CountAsync(x => x.HolidayId == muhurat.Id && x.IsOpen));

            var nse = await db.Set<MarketTiming>().SingleAsync(t => t.ExchangeCode == "NSE");
            nse.EndOffset = 55_000_000;
            db.Set<MarketHoliday>().Remove(await db.Set<MarketHoliday>().SingleAsync(h => h.HolidayDate == new DateOnly(2026, 12, 25)));
            await db.SaveChangesAsync();
        }

        await EPTradeBot.Infrastructure.Persistence.DatabaseMigrator.MigrateAsync(host.Services);
        await using (var db = host.NewDb())
        {
            Assert.Equal(55_000_000, (await db.Set<MarketTiming>().SingleAsync(t => t.ExchangeCode == "NSE")).EndOffset);
            Assert.Equal(30, await db.Set<MarketHoliday>().CountAsync());
        }
    }

    [PostgresFact]
    public async Task The_default_year_is_the_current_year_in_india()
    {
        // 31 December 19:00 UTC is already 1 January in India.
        var clock = new StoppedClock(new DateTimeOffset(2026, 12, 31, 19, 0, 0, TimeSpan.Zero));
        await using var host = new TestHost("Development", b => b.ConfigureServices(s => s.AddSingleton<TimeProvider>(clock)));
        var account = await host.CreateAccountAsync(AccountType.PAPER, "paper");
        var key = (await host.Get<ApiKeyService>().IssueAsync(account, "calendar")).Key;

        var (status, body) = await PostAsync(host, "/api/v1/market/holidays", new { apikey = key });
        Assert.Equal(200, status);
        Assert.Equal(2027, body["year"]!.GetValue<int>());
        Assert.Empty(body["data"]!.AsArray());
    }

    [PostgresFact]
    public async Task Chart_preferences_belong_to_the_account_and_read_back_as_saved()
    {
        await using var host = new TestHost("Development");
        var first = await host.CreateAccountAsync(AccountType.PAPER, "first");
        var second = await host.CreateAccountAsync(AccountType.PAPER, "second");
        var keys = host.Get<ApiKeyService>();
        var firstKey = (await keys.IssueAsync(first, "chart")).Key;
        var secondKey = (await keys.IssueAsync(second, "chart")).Key;

        var (status, _) = await PostAsync(host, "/api/v1/chart", new { apikey = firstKey, tv_theme = "dark", zoom = 5, layout = new { panes = 2 }, cleared = (string?)null });
        Assert.Equal(200, status);
        (status, _) = await PostAsync(host, "/api/v1/chart", new { apikey = firstKey, tv_theme = "light" });
        Assert.Equal(200, status);

        var saved = (JsonObject)(await GetChartAsync(host, firstKey))["data"]!;
        Assert.Equal("light", saved["tv_theme"]!.GetValue<string>());
        Assert.Equal("5", saved["zoom"]!.GetValue<string>());
        Assert.Equal("""{"panes":2}""", saved["layout"]!.GetValue<string>());
        Assert.True(saved.ContainsKey("cleared"));
        Assert.Null(saved["cleared"]);

        // Another account's key reads its own, empty, preferences.
        Assert.Empty((JsonObject)(await GetChartAsync(host, secondKey))["data"]!);
    }

    [PostgresFact]
    public async Task Day_pnl_by_symbol_follows_sandbox_trades_and_the_latest_price()
    {
        await using var host = new TestHost("Development");
        var account = await host.CreateAccountAsync(AccountType.PAPER, "paper", 100_000, 1000, 10_000_000);
        var key = (await host.Get<ApiKeyService>().IssueAsync(account, "pnl")).Key;
        await using (var db = host.NewDb())
        {
            db.Set<SymToken>().Add(new SymToken { Broker = "sandbox", Symbol = "SBIN", Brsymbol = "SBIN", Exchange = "NSE", Brexchange = "NSE", Token = "3045", Lotsize = 1 });
            await db.SaveChangesAsync();
        }

        host.SetQuote("NSE", "SBIN", 500);
        object Order(string action, int quantity) => new { apikey = key, strategy = "pnl", symbol = "SBIN", exchange = "NSE", action, quantity, pricetype = "MARKET", product = "MIS" };
        Assert.Equal(200, (await PostAsync(host, "/api/v1/placeorder", Order("BUY", 10))).Status);
        host.SetQuote("NSE", "SBIN", 510);
        Assert.Equal(200, (await PostAsync(host, "/api/v1/placeorder", Order("SELL", 4))).Status);
        host.SetQuote("NSE", "SBIN", 520);

        var (status, body) = await PostAsync(host, "/api/v1/pnl/symbols", new { apikey = key });
        Assert.Equal(200, status);
        var row = body["data"]!.AsArray().Single()!;
        Assert.Equal(6, row["quantity"]!.GetValue<int>());
        Assert.Equal(40.0, row["today_realized_pnl"]!.GetValue<double>()); // 4 sold 10 above cost
        Assert.Equal(120.0, row["unrealized_pnl"]!.GetValue<double>()); // 6 held, marked 20 above cost
        Assert.Equal(160.0, row["total_pnl_today"]!.GetValue<double>());
        Assert.Equal(160.0, body["total_pnl"]!.GetValue<double>());
        Assert.Equal("analyze", body["mode"]!.GetValue<string>());
    }

    [PostgresFact]
    public async Task Today_s_realized_pnl_runs_from_0300_ist_not_from_midnight()
    {
        var clock = new StoppedClock(new DateTimeOffset(2026, 11, 3, 22, 0, 0, TimeSpan.FromHours(5.5)));
        await using var host = new TestHost("Development", b => b.ConfigureServices(s => s.AddSingleton<TimeProvider>(clock)));
        var account = await host.CreateAccountAsync(AccountType.PAPER, "paper", 100_000, 1000, 10_000_000);
        var key = (await host.Get<ApiKeyService>().IssueAsync(account, "pnl")).Key;
        object Order(string action, int quantity) => new { apikey = key, strategy = "pnl", symbol = "SBIN", exchange = "NSE", action, quantity, pricetype = "MARKET", product = "MIS" };

        host.SetQuote("NSE", "SBIN", 500);
        Assert.Equal(200, (await PostAsync(host, "/api/v1/placeorder", Order("BUY", 10))).Status);
        host.SetQuote("NSE", "SBIN", 510);
        Assert.Equal(200, (await PostAsync(host, "/api/v1/placeorder", Order("SELL", 4))).Status); // 40 realized at 22:00

        // After midnight, still the same session: the 22:00 realized P&L stays today's.
        clock.At = new DateTimeOffset(2026, 11, 4, 1, 0, 0, TimeSpan.FromHours(5.5));
        host.SetQuote("NSE", "SBIN", 520);
        Assert.Equal(200, (await PostAsync(host, "/api/v1/placeorder", Order("SELL", 2))).Status); // 40 more
        var (_, body) = await PostAsync(host, "/api/v1/pnl/symbols", new { apikey = key });
        Assert.Equal(80.0, body["data"]!.AsArray().Single()!["today_realized_pnl"]!.GetValue<double>());

        // A new session at 03:00: the MIS position was settled at the boundary and leaves the day's book.
        clock.At = new DateTimeOffset(2026, 11, 4, 3, 30, 0, TimeSpan.FromHours(5.5));
        (_, body) = await PostAsync(host, "/api/v1/pnl/symbols", new { apikey = key });
        Assert.Empty(body["data"]!.AsArray());
        Assert.Equal(0.0, body["total_today_realized_pnl"]!.GetValue<double>());
    }

    private sealed class StoppedClock(DateTimeOffset at) : TimeProvider
    {
        public DateTimeOffset At { get; set; } = at;
        public override DateTimeOffset GetUtcNow() => At;
    }
}
