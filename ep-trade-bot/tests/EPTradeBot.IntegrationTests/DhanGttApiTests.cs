using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EPTradeBot.IntegrationTests;

/// <summary>Which client id a Dhan Forever Order carries: the one saved with the key, else the account's own.</summary>
public sealed class DhanGttApiTests
{
    private static async Task<(string Client, string Header)> PlaceAsync(string apiKey, string accountClient)
    {
        var broker = new FakeBroker();
        await using var host = new TestHost("Production", b =>
        {
            b.UseSetting("Brokers:Enabled:0", "dhan");
            b.UseSetting("Brokers:Keys:dhan:ApiKey", apiKey);
            b.ConfigureServices(s => s.ConfigureHttpClientDefaults(h => h.ConfigurePrimaryHttpMessageHandler(() => broker)));
        });
        var account = await host.CreateAccountAsync(AccountType.LIVE, "dh", 1_000_000, 1000, 10_000_000, broker: "dhan", clientId: accountClient);
        (await host.Admin.PutAsJsonAsync($"/api/v1/accounts/{account}/broker-session", new { auth_token = "dhan-token" })).EnsureSuccessStatusCode();
        var key = await host.Admin.PostAsJsonAsync($"/api/v1/accounts/{account}/apikeys", new { label = "tv" });
        key.EnsureSuccessStatusCode();
        await using (var db = host.NewDb())
        {
            db.Set<SymToken>().Add(new SymToken { Broker = "dhan", Symbol = "SBIN", Brsymbol = "SBIN", Exchange = "NSE", Brexchange = "NSE_EQ", Token = "3045", Lotsize = 1, TickSize = 0.05 });
            await db.SaveChangesAsync();
        }

        broker.Reset(new Dictionary<string, (int, string)>
        {
            ["POST /v2/forever/orders"] = (200, """{"orderId":"9001","orderStatus":"TRANSIT"}"""),
            ["GET /v2/positions"] = (200, "[]"),
            ["GET /v2/orders"] = (200, "[]"),
        });
        using var client = host.Anonymous();
        var response = await client.PostAsJsonAsync("/api/v1/placegttorder", new
        {
            apikey = (await key.Content.ReadFromJsonAsync<JsonObject>())!["apikey"]!.GetValue<string>(), strategy = "tv", trigger_type = "SINGLE", exchange = "NSE", symbol = "SBIN",
            action = "BUY", product = "CNC", quantity = "1", pricetype = "LIMIT", price = "790", triggerprice_sl = "795",
        });
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(broker.Sent, r => r.Path == "/v2/forever/orders");
        return (sent.Json!["dhanClientId"]!.GetValue<string>(), sent.Headers["client-id"]);
    }

    [PostgresFact]
    public async Task The_client_id_saved_with_the_key_wins_over_the_accounts()
    {
        var (body, header) = await PlaceAsync("KEYCLIENT:::app-key", "ACCOUNTCLIENT");
        Assert.Equal("KEYCLIENT", body);
        Assert.Equal("KEYCLIENT", header);
    }

    [PostgresFact]
    public async Task A_key_saved_alone_uses_the_accounts_client_id()
    {
        var (body, header) = await PlaceAsync("app-key-alone", "ACCOUNTCLIENT");
        Assert.Equal("ACCOUNTCLIENT", body);
        Assert.Equal("ACCOUNTCLIENT", header);
    }
}
