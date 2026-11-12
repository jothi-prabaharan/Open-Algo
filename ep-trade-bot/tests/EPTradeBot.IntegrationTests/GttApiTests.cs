using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EPTradeBot.IntegrationTests;

/// <summary>The GTT endpoints under EP Trade bot's own rules: the environment guard, the kill switch, and sandbox accounts.</summary>
public sealed class GttApiTests
{
    private const string Quote = """{"status":"success","data":{"NSE:SBIN":{"instrument_token":1,"last_price":805.45,"ohlc":{"open":1,"high":1,"low":1,"close":1},"depth":{"buy":[],"sell":[]}}}}""";
    private const string Created = """{"status":"success","data":{"trigger_id":77}}""";
    private const string Book = """{"status":"success","data":[]}""";

    private static async Task<(TestHost Host, FakeBroker Broker, Guid Account, string Key)> LiveHostAsync(string environment)
    {
        var broker = new FakeBroker();
        var host = new TestHost(environment, b =>
        {
            b.UseSetting("Brokers:Enabled:0", "zerodha");
            b.ConfigureServices(s => s.ConfigureHttpClientDefaults(h => h.ConfigurePrimaryHttpMessageHandler(() => broker)));
        });
        var account = await host.CreateAccountAsync(AccountType.LIVE, "kite", 1_000_000, 1000, 10_000_000);
        (await host.Admin.PutAsJsonAsync($"/api/v1/accounts/{account}/broker-session", new { auth_token = "key:token" })).EnsureSuccessStatusCode();
        var key = await host.Admin.PostAsJsonAsync($"/api/v1/accounts/{account}/apikeys", new { label = "tradingview" });
        key.EnsureSuccessStatusCode();
        await using (var db = host.NewDb())
        {
            db.Set<SymToken>().Add(new SymToken { Broker = "zerodha", Symbol = "SBIN", Brsymbol = "SBIN", Exchange = "NSE", Brexchange = "NSE", Token = "779521::::3045", Lotsize = 1, TickSize = 0.05 });
            await db.SaveChangesAsync();
        }

        broker.Reset(new Dictionary<string, (int, string)>
        {
            ["GET /quote"] = (200, Quote),
            ["POST /gtt/triggers"] = (200, Created),
            ["PUT /gtt/triggers/77"] = (200, Created),
            ["DELETE /gtt/triggers/77"] = (200, Created),
            ["GET /gtt/triggers"] = (200, Book),
            ["GET /orders"] = (200, """{"status":"success","data":[]}"""),
            ["GET /portfolio/positions"] = (200, """{"status":"success","data":{"net":[],"day":[]}}"""),
        });
        return (host, broker, account, (await key.Content.ReadFromJsonAsync<JsonObject>())!["apikey"]!.GetValue<string>());
    }

    private static async Task<(int Status, JsonObject Body)> PostAsync(TestHost host, string path, object body)
    {
        using var client = host.Anonymous();
        var response = await client.PostAsJsonAsync(path, body);
        return ((int)response.StatusCode, (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    private static object Gtt(string key, string? triggerId = null) => new
    {
        apikey = key, strategy = "tv", trigger_id = triggerId ?? "77", trigger_type = "SINGLE", exchange = "NSE", symbol = "SBIN", action = "BUY", product = "CNC",
        quantity = "1", pricetype = "LIMIT", price = "790", triggerprice_sl = "795",
    };

    [PostgresFact]
    public async Task The_kill_switch_stops_placing_and_changing_a_trigger_but_not_cancelling_one_or_reading_the_book()
    {
        var (host, broker, account, key) = await LiveHostAsync("Production");
        await using var _host = host;

        var (status, answer) = await PostAsync(host, "/api/v1/placegttorder", Gtt(key));
        Assert.Equal(200, status);
        Assert.Equal("77", answer["trigger_id"]!.GetValue<string>());

        Assert.Equal(System.Net.HttpStatusCode.OK, (await host.Admin.PostAsJsonAsync($"/api/v1/killswitch/account/{account}", new { engage = true, reason = "test" })).StatusCode);
        broker.ClearSent();

        foreach (var path in new[] { "/api/v1/placegttorder", "/api/v1/modifygttorder" })
        {
            (status, answer) = await PostAsync(host, path, Gtt(key));
            Assert.True(status == 400, $"{path} answered {status}: {answer.ToJsonString()}");
            Assert.Contains("kill switch", answer["message"]!.GetValue<string>());
        }

        Assert.DoesNotContain(broker.Sent, s => s.Path.StartsWith("/gtt", StringComparison.Ordinal));

        (status, answer) = await PostAsync(host, "/api/v1/cancelgttorder", new { apikey = key, strategy = "tv", trigger_id = "77" });
        Assert.Equal(200, status);
        (status, answer) = await PostAsync(host, "/api/v1/gttorderbook", new { apikey = key });
        Assert.Equal(200, status);
        Assert.Contains(broker.Sent, s => s.Method == "DELETE" && s.Path == "/gtt/triggers/77");
    }
}
