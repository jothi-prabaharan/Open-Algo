using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.Legacy.Core;
using EPTradeBot.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EPTradeBot.IntegrationTests;

/// <summary>History behaviour the recorded fixtures cannot show: pacing, and refusals EP Trade bot words itself.</summary>
public sealed class HistoryApiTests
{
    private const string Candles = """{"status":"success","data":{"candles":[["2026-11-02T09:15:00+0530",800,802.5,799,801.25,120000,0]]}}""";

    private static async Task<(TestHost Host, string Key)> HostAsync(Action<IWebHostBuilder>? configure = null)
    {
        var broker = new FakeBroker();
        var host = new TestHost("Production", b =>
        {
            b.UseSetting("Brokers:Enabled:0", "zerodha");
            b.ConfigureServices(s => s.ConfigureHttpClientDefaults(h => h.ConfigurePrimaryHttpMessageHandler(() => broker)));
            configure?.Invoke(b);
        });
        var account = await host.CreateAccountAsync(AccountType.LIVE, "kite");
        (await host.Admin.PutAsJsonAsync($"/api/v1/accounts/{account}/broker-session", new { auth_token = "key:token" })).EnsureSuccessStatusCode();
        var key = (await host.Get<ApiKeyService>().IssueAsync(account, "history")).Key;
        await using (var db = host.NewDb())
        {
            db.Set<SymToken>().Add(new SymToken { Broker = "zerodha", Symbol = "SBIN", Brsymbol = "SBIN", Exchange = "NSE", Brexchange = "NSE", Token = "779521::::3045", Lotsize = 1 });
            await db.SaveChangesAsync();
        }

        broker.Reset(new Dictionary<string, (int, string)> { ["GET /instruments/historical/779521/5minute"] = (200, Candles) });
        return (host, key);
    }

    private static async Task<(int Status, JsonObject Body)> HistoryAsync(TestHost host, string key, string source = "api")
    {
        using var client = host.Anonymous();
        var response = await client.PostAsJsonAsync("/api/v1/history", new { apikey = key, symbol = "SBIN", exchange = "NSE", interval = "5m", start_date = "2026-11-02", end_date = "2026-11-02", source });
        return ((int)response.StatusCode, (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    [PostgresFact]
    public async Task History_calls_are_spaced_350_milliseconds_apart()
    {
        var (host, key) = await HostAsync();
        await using var _ = host;
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 3; i++) Assert.Equal(200, (await HistoryAsync(host, key)).Status);
        // Three calls leave two gaps of 350 ms.
        Assert.InRange(watch.ElapsedMilliseconds, 650, 5000);
    }

    [PostgresFact]
    public async Task Local_history_is_refused_plainly_and_asks_nothing_of_the_broker()
    {
        var (host, key) = await HostAsync();
        await using var _ = host;
        var (status, body) = await HistoryAsync(host, key, "db");
        Assert.Equal(501, status);
        Assert.Contains("Historify", body["message"]!.GetValue<string>());
    }

    [PostgresFact]
    public async Task Angel_One_asks_for_today_up_to_the_current_minute_in_india_and_other_days_to_2359()
    {
        var broker = new FakeBroker();
        var clock = new StoppedClock(new DateTimeOffset(2026, 11, 3, 10, 42, 30, TimeSpan.FromHours(5.5)));
        await using var host = new TestHost("Production", b =>
        {
            b.UseSetting("Brokers:Enabled:0", "angel");
            b.UseSetting("Brokers:Keys:angel:ApiKey", "key");
            b.UseSetting("Brokers:HistoryGapMs:angel", "0");
            b.UseSetting("OpenAlgoApi:HistorySpacingMs", "0");
            b.ConfigureServices(s =>
            {
                s.ConfigureHttpClientDefaults(h => h.ConfigurePrimaryHttpMessageHandler(() => broker));
                s.AddSingleton<TimeProvider>(clock);
            });
        });
        var account = await host.CreateAccountAsync(AccountType.LIVE, "angel", broker: "angel");
        (await host.Admin.PutAsJsonAsync($"/api/v1/accounts/{account}/broker-session", new { auth_token = "jwt" })).EnsureSuccessStatusCode();
        var key = (await host.Get<ApiKeyService>().IssueAsync(account, "history")).Key;
        await using (var db = host.NewDb())
        {
            db.Set<SymToken>().Add(new SymToken { Broker = "angel", Symbol = "SBIN", Brsymbol = "SBIN-EQ", Exchange = "NSE", Brexchange = "NSE", Token = "3045", Lotsize = 1 });
            await db.SaveChangesAsync();
        }

        broker.Reset(new Dictionary<string, (int, string)> { ["POST /rest/secure/angelbroking/historical/v1/getCandleData"] = (200, """{"status":true,"data":[]}""") });
        using var client = host.Anonymous();
        foreach (var end in new[] { "2026-11-03", "2026-11-02" })
        {
            broker.ClearSent();
            var response = await client.PostAsJsonAsync("/api/v1/history", new { apikey = key, symbol = "SBIN", exchange = "NSE", interval = "5m", start_date = "2026-11-02", end_date = end });
            Assert.Equal(200, (int)response.StatusCode);
            Assert.Equal(end == "2026-11-03" ? "2026-11-03 10:42" : "2026-11-02 23:59", broker.Sent.Single().Json!["todate"]!.GetValue<string>());
        }
    }

    private static async Task<(TestHost Host, FakeBroker Broker, string Key)> UpstoxHostAsync(StoppedClock clock)
    {
        var broker = new FakeBroker();
        var host = new TestHost("Production", b =>
        {
            b.UseSetting("Brokers:Enabled:0", "upstox");
            b.UseSetting("OpenAlgoApi:HistorySpacingMs", "0");
            b.ConfigureServices(s =>
            {
                s.ConfigureHttpClientDefaults(h => h.ConfigurePrimaryHttpMessageHandler(() => broker));
                s.AddSingleton<TimeProvider>(clock);
            });
        });
        var account = await host.CreateAccountAsync(AccountType.LIVE, "upstox", broker: "upstox");
        (await host.Admin.PutAsJsonAsync($"/api/v1/accounts/{account}/broker-session", new { auth_token = "token" })).EnsureSuccessStatusCode();
        var key = (await host.Get<ApiKeyService>().IssueAsync(account, "history")).Key;
        await using var db = host.NewDb();
        db.Set<SymToken>().Add(new SymToken { Broker = "upstox", Symbol = "SBIN", Brsymbol = "SBIN", Exchange = "NSE", Brexchange = "NSE_EQ", Token = "NSE_EQ|INE062A01020", Lotsize = 1 });
        await db.SaveChangesAsync();
        return (host, broker, key);
    }

    [PostgresFact]
    public async Task Upstox_asks_the_intraday_endpoint_for_today_and_keeps_only_the_requested_days()
    {
        var clock = new StoppedClock(new DateTimeOffset(2026, 11, 3, 10, 42, 30, TimeSpan.FromHours(5.5)));
        var (host, broker, key) = await UpstoxHostAsync(clock);
        await using var _ = host;
        broker.Reset(new Dictionary<string, (int, string)>
        {
            ["GET /v3/historical-candle/intraday/NSE_EQ%7CINE062A01020/minutes/5"] = (200, """
                {"status":"success","data":{"candles":[["2026-11-03T09:20:00+05:30",801,803,800,802,90,0],["2026-11-03T09:15:00+05:30",800,802,799,801,100,0],["2026-11-02T15:25:00+05:30",1,1,1,1,1,0]]}}
                """),
        });
        using var client = host.Anonymous();
        var response = await client.PostAsJsonAsync("/api/v1/history", new { apikey = key, symbol = "SBIN", exchange = "NSE", interval = "5m", start_date = "2026-11-03", end_date = "2026-11-03" });
        var body = (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal([1793677500L, 1793677800L], body["data"]!.AsArray().Select(c => c!["timestamp"]!.GetValue<long>()));
        // Today had candles, so the historical endpoint was not asked.
        Assert.Single(broker.Sent);
    }

    [PostgresFact]
    public async Task Upstox_builds_today_s_daily_candle_from_the_quote_unless_it_only_repeats_the_last_one()
    {
        var clock = new StoppedClock(new DateTimeOffset(2026, 11, 3, 10, 42, 30, TimeSpan.FromHours(5.5)));
        var (host, broker, key) = await UpstoxHostAsync(clock);
        await using var _ = host;
        const string Quote = """
            {"status":"success","data":{"NSE_EQ:SBIN":{"instrument_token":"NSE_EQ|INE062A01020","last_price":805.5,"ohlc":{"open":800,"high":806,"low":799,"close":799.95},"volume":1500,"oi":0,"depth":{"buy":[],"sell":[]}}}}
            """;
        broker.Reset(new Dictionary<string, (int, string)>
        {
            ["GET /v3/historical-candle/NSE_EQ%7CINE062A01020/days/1/2026-11-03/2026-11-02"] = (200, """{"status":"success","data":{"candles":[["2026-11-02T00:00:00+05:30",790,801,788,799.95,9000,0]]}}"""),
            ["GET /v3/market-quote/quotes"] = (200, Quote),
        });
        using var client = host.Anonymous();
        var response = await client.PostAsJsonAsync("/api/v1/history", new { apikey = key, symbol = "SBIN", exchange = "NSE", interval = "D", start_date = "2026-11-02", end_date = "2026-11-03" });
        var data = ((JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!)["data"]!.AsArray();
        Assert.Equal(2, data.Count);
        var today = data[1]!;
        Assert.Equal(1793664000L, today["timestamp"]!.GetValue<long>()); // 00:00 UTC of 3 November
        Assert.Equal(805.5, today["close"]!.GetValue<double>());
        Assert.Equal(1500, today["volume"]!.GetValue<long>());

        // A quote that is exactly the latest candle adds nothing.
        broker.Reset(new Dictionary<string, (int, string)>
        {
            ["GET /v3/historical-candle/NSE_EQ%7CINE062A01020/days/1/2026-11-03/2026-11-02"] = (200, """{"status":"success","data":{"candles":[["2026-11-02T00:00:00+05:30",800,806,799,805.5,1500,0]]}}"""),
            ["GET /v3/market-quote/quotes"] = (200, Quote),
        });
        response = await client.PostAsJsonAsync("/api/v1/history", new { apikey = key, symbol = "SBIN", exchange = "NSE", interval = "D", start_date = "2026-11-02", end_date = "2026-11-03" });
        Assert.Single(((JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!)["data"]!.AsArray());
    }

    private sealed class StoppedClock(DateTimeOffset at) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => at;
    }
}
