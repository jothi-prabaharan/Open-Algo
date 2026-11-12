using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.MarketData;
using EPTradeBot.Infrastructure.Security;

namespace EPTradeBot.IntegrationTests;

/// <summary>The SIP and sector rotation routes end to end over Historify candles. The maths is pinned by the parity tests; this proves the loading, the validation and the answers.</summary>
public sealed class PortfolioApiTests
{
    private static async Task<(TestHost Host, string Key)> HostAsync()
    {
        var host = new TestHost("Production");
        var account = await host.CreateAccountAsync(AccountType.LIVE, "kite");
        var key = (await host.Get<ApiKeyService>().IssueAsync(account, "portfolio")).Key;
        await using var db = host.NewDb();
        // Weekday closes from 2025-11-03 to 2026-11-10, stamped at Indian midnight (18:30 UTC the day before), three series that drift differently.
        var day = new DateTime(2025, 11, 3, 0, 0, 0, DateTimeKind.Utc);
        var n = 0;
        while (day <= new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc))
        {
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                var ts = day.AddHours(-5.5);
                db.Candles.Add(new Candle { Symbol = "INFY", Exchange = "NSE", Interval = "D", Ts = ts, Open = 1, High = 1, Low = 1, Close = 1500 + n * 0.8 + Math.Sin(n / 7.0) * 20 });
                db.Candles.Add(new Candle { Symbol = "NIFTY", Exchange = "NSE_INDEX", Interval = "D", Ts = ts, Open = 1, High = 1, Low = 1, Close = 24000 + n * 6 + Math.Sin(n / 11.0) * 150 });
                db.Candles.Add(new Candle { Symbol = "NIFTYIT", Exchange = "NSE_INDEX", Interval = "D", Ts = ts, Open = 1, High = 1, Low = 1, Close = 36000 + n * 4 + Math.Cos(n / 9.0) * 300 });
                n++;
            }

            day = day.AddDays(1);
        }

        await db.SaveChangesAsync();
        return (host, key);
    }

    [PostgresFact]
    public async Task Frequencies_and_the_sector_options_are_listed()
    {
        var (host, _) = await HostAsync();
        await using var _ = host;
        using var client = host.Anonymous();
        var frequencies = JsonNode.Parse(await client.GetStringAsync("/api/v1/sip/frequencies"))!;
        Assert.Equal("success", frequencies["status"]!.GetValue<string>());
        Assert.Equal(["monthly", "fortnightly", "weekly", "quarterly"], frequencies["data"]!.AsArray().Select(x => x!["value"]!.GetValue<string>()));
        var options = JsonNode.Parse(await client.GetStringAsync("/api/v1/sectorrotation/options"))!;
        Assert.Equal(40, options["data"]!["max_instruments"]!.GetValue<int>());
        Assert.Contains("NIFTY", options["data"]!["benchmarks"]!.AsArray().Select(x => x!["symbol"]!.GetValue<string>()));
    }

    [PostgresFact]
    public async Task A_sip_runs_over_historify_closes_and_compares_with_an_index()
    {
        var (host, key) = await HostAsync();
        await using var _ = host;
        using var client = host.Anonymous();
        var response = await client.PostAsJsonAsync("/api/v1/sip/backtest", new
        {
            apikey = key, symbol = "INFY", exchange = "NSE", start_date = "2026-01-01", end_date = "2026-10-30", amount = 10000, frequency = "monthly", day_of_month = 4,
            benchmark = "NIFTY", benchmark_exchange = "NSE_INDEX", include_grids = false,
        });
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        var body = JsonNode.Parse(text)!;
        Assert.Equal("success", body["status"]!.GetValue<string>());
        Assert.Equal("db", body["request"]!["source"]!.GetValue<string>());
        Assert.NotNull(body["headline"]);
        Assert.NotEmpty(body["installments"]!.AsArray());
        Assert.Equal("INFY", body["request"]!["symbol"]!.GetValue<string>());
        // Candles are stamped at Indian midnight (18:30 UTC the day before); the session is the Indian day, so an installment due on Sunday the 4th executes on Monday the 5th.
        Assert.Equal("2026-01-05", body["installments"]![0]!["executed"]!.GetValue<string>());
    }

    [PostgresFact]
    public async Task A_sip_names_missing_history_and_refuses_bad_fields()
    {
        var (host, key) = await HostAsync();
        await using var _ = host;
        using var client = host.Anonymous();

        var missing = await client.PostAsJsonAsync("/api/v1/sip/backtest", new { apikey = key, symbol = "NOSUCH", exchange = "NSE", start_date = "2026-01-01", end_date = "2026-10-30", amount = 5000, include_grids = false });
        Assert.Equal(422, (int)missing.StatusCode);
        Assert.Contains("no D history in Historify", await missing.Content.ReadAsStringAsync());

        var badAmount = await client.PostAsJsonAsync("/api/v1/sip/backtest", new { apikey = key, symbol = "INFY", start_date = "2026-01-01", end_date = "2026-10-30", amount = 0 });
        Assert.Equal(400, (int)badAmount.StatusCode);

        var wrongKey = await client.PostAsJsonAsync("/api/v1/sip/backtest", new { apikey = "nope", symbol = "INFY", start_date = "2026-01-01", end_date = "2026-10-30", amount = 5000 });
        Assert.Equal(403, (int)wrongKey.StatusCode);
    }

    [PostgresFact]
    public async Task The_sector_map_places_what_has_history_and_names_what_does_not()
    {
        var (host, key) = await HostAsync();
        await using var _ = host;
        using var client = host.Anonymous();
        var response = await client.PostAsJsonAsync("/api/v1/sectorrotation", new
        {
            apikey = key, benchmark = "NIFTY", timeframe = "daily", source = "db",
            instruments = new object[] { new { symbol = "NIFTYIT", exchange = "NSE_INDEX", label = "IT" }, new { symbol = "NIFTYPHARMA", exchange = "NSE_INDEX" } },
        });
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        var data = JsonNode.Parse(text)!["data"]!;
        var placed = Assert.Single(data["instruments"]!.AsArray());
        Assert.Equal("NIFTYIT", placed!["symbol"]!.GetValue<string>());
        Assert.Contains(placed["quadrant"]!.GetValue<string>(), new[] { "leading", "weakening", "improving", "lagging" });
        var gone = Assert.Single(data["unavailable"]!.AsArray());
        Assert.Equal("NIFTYPHARMA", gone!["symbol"]!.GetValue<string>());
        Assert.Contains("Historify has no daily history", gone["reason"]!.GetValue<string>());

        var refused = await client.PostAsJsonAsync("/api/v1/sectorrotation", new { apikey = key, benchmark = "DOW" });
        Assert.Equal(400, (int)refused.StatusCode);
        Assert.StartsWith("These map settings were not accepted", JsonNode.Parse(await refused.Content.ReadAsStringAsync())!["message"]!.GetValue<string>());
    }
}
