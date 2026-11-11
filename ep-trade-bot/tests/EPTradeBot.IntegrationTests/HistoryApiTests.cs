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

    private sealed class StoppedClock(DateTimeOffset at) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => at;
    }
}
