using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Entities.Audit;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.Legacy.Core;
using EPTradeBot.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EPTradeBot.IntegrationTests;

/// <summary>/api/v1 as external platforms use it: the key picks the account, and the guard and risk limits apply.</summary>
public sealed class OpenAlgoApiTests
{
    private const string Positions = """{"status":"success","data":{"net":[{"tradingsymbol":"SBIN","exchange":"NSE","product":"MIS","quantity":10,"average_price":800,"last_price":805,"pnl":50}],"day":[]}}""";
    private const string OrderOk = """{"status":"success","data":{"order_id":"9001"}}""";
    private const string Orders = """{"status":"success","data":[{"order_id":"9002","exchange":"NSE","tradingsymbol":"SBIN","transaction_type":"BUY","order_type":"LIMIT","product":"MIS","quantity":1,"price":700,"trigger_price":0,"status":"OPEN","order_timestamp":"2026-11-03 09:20:01"}]}""";

    private static async Task<(TestHost Host, FakeBroker Broker, Guid Account, string Key)> LiveHostAsync(string environment, int maxQty = 1000)
    {
        var broker = new FakeBroker();
        var host = new TestHost(environment, b =>
        {
            b.UseSetting("Brokers:Enabled:0", "zerodha");
            b.ConfigureServices(s => s.ConfigureHttpClientDefaults(h => h.ConfigurePrimaryHttpMessageHandler(() => broker)));
        });
        var account = await host.CreateAccountAsync(AccountType.LIVE, "kite", 1_000_000, maxQty, 10_000_000);
        (await host.Admin.PutAsJsonAsync($"/api/v1/accounts/{account}/broker-session", new { auth_token = "key:token" })).EnsureSuccessStatusCode();
        var key = (await host.Admin.PostAsJsonAsync($"/api/v1/accounts/{account}/apikeys", new { label = "tradingview" }));
        key.EnsureSuccessStatusCode();
        var apiKey = (await key.Content.ReadFromJsonAsync<JsonObject>())!["apikey"]!.GetValue<string>();
        await using (var db = host.NewDb())
        {
            db.Set<SymToken>().Add(new SymToken { Broker = "zerodha", Symbol = "SBIN", Brsymbol = "SBIN", Exchange = "NSE", Brexchange = "NSE", Token = "779521::::3045", Lotsize = 1 });
            await db.SaveChangesAsync();
        }

        host.SetQuote("NSE", "SBIN", 805);
        broker.Reset(new Dictionary<string, (int, string)>
        {
            ["GET /portfolio/positions"] = (200, Positions),
            ["GET /orders"] = (200, Orders),
            ["POST /orders/regular"] = (200, OrderOk),
            ["PUT /orders/regular/9002"] = (200, """{"status":"success","data":{"order_id":"9002"}}"""),
            ["DELETE /orders/regular/9002"] = (200, """{"status":"success","data":{"order_id":"9002"}}"""),
        });
        return (host, broker, account, apiKey);
    }

    private static async Task<(int Status, JsonObject Body)> PostAsync(TestHost host, string path, object body)
    {
        using var client = host.Anonymous();
        var response = await client.PostAsJsonAsync(path, body);
        return ((int)response.StatusCode, (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    [PostgresFact]
    public async Task Every_write_to_a_live_broker_is_refused_in_Development_and_nothing_reaches_the_broker()
    {
        var (host, broker, _, key) = await LiveHostAsync("Development");
        await using var _ = host;

        var writes = new (string Path, object Body)[]
        {
            ("/api/v1/placeorder", new { apikey = key, strategy = "tv", symbol = "SBIN", exchange = "NSE", action = "BUY", quantity = "1", pricetype = "MARKET", product = "MIS" }),
            ("/api/v1/placesmartorder", new { apikey = key, strategy = "tv", symbol = "SBIN", exchange = "NSE", action = "BUY", quantity = "1", position_size = "0", pricetype = "MARKET", product = "MIS" }),
            ("/api/v1/modifyorder", new { apikey = key, strategy = "tv", symbol = "SBIN", exchange = "NSE", orderid = "9002", action = "BUY", quantity = "1", pricetype = "LIMIT", product = "MIS", price = "701", disclosed_quantity = "0", trigger_price = "0" }),
            ("/api/v1/cancelorder", new { apikey = key, strategy = "tv", orderid = "9002" }),
            ("/api/v1/cancelallorder", new { apikey = key, strategy = "tv" }),
            ("/api/v1/placegttorder", new { apikey = key, strategy = "tv", trigger_type = "SINGLE", exchange = "NSE", symbol = "SBIN", action = "BUY", product = "CNC", quantity = "1", pricetype = "LIMIT", price = "790", triggerprice_sl = "795" }),
            ("/api/v1/modifygttorder", new { apikey = key, strategy = "tv", trigger_id = "1", trigger_type = "SINGLE", exchange = "NSE", symbol = "SBIN", action = "BUY", product = "CNC", quantity = "1", pricetype = "LIMIT", price = "790", triggerprice_sl = "795" }),
            ("/api/v1/cancelgttorder", new { apikey = key, strategy = "tv", trigger_id = "1" }),
            ("/api/v1/closeposition", new { apikey = key, strategy = "tv" }),
            ("/api/v1/basketorder", new { apikey = key, strategy = "tv", orders = new[] { new { symbol = "SBIN", exchange = "NSE", action = "BUY", quantity = 1 } } }),
            ("/api/v1/splitorder", new { apikey = key, strategy = "tv", symbol = "SBIN", exchange = "NSE", action = "BUY", quantity = 20, splitsize = 10 }),
            ("/api/v1/optionsorder", new { apikey = key, strategy = "tv", underlying = "NIFTY", exchange = "NSE_INDEX", expiry_date = "30DEC30", offset = "ATM", option_type = "CE", action = "BUY", quantity = 75 }),
            ("/api/v1/optionsmultiorder", new { apikey = key, strategy = "tv", underlying = "NIFTY", exchange = "NSE_INDEX", expiry_date = "30DEC30", legs = new[] { new { offset = "ATM", option_type = "CE", action = "BUY", quantity = 75 } } }),
        };

        foreach (var (path, body) in writes)
        {
            var (status, answer) = await PostAsync(host, path, body);
            Assert.True(status == 403, $"{path} answered {status}: {answer.ToJsonString()}");
            Assert.Equal("error", answer["status"]!.GetValue<string>());
        }

        Assert.DoesNotContain(broker.Sent, s => s.Method != "GET");
        await using var db = host.NewDb();
        Assert.Equal(1, await db.OrderAuditLogs.CountAsync(a => a.Source == OrderSource.Api && a.Outcome == OrderOutcome.Refused));
    }

    [PostgresFact]
    public async Task Live_api_order_in_Production_is_sent_and_one_over_the_quantity_limit_is_not()
    {
        var (host, broker, _, key) = await LiveHostAsync("Production", maxQty: 10);
        await using var _ = host;

        var (status, answer) = await PostAsync(host, "/api/v1/placeorder", new { apikey = key, strategy = "tv", symbol = "SBIN", exchange = "NSE", action = "BUY", quantity = 10, pricetype = "MARKET", product = "MIS" });
        Assert.Equal(200, status);
        Assert.Equal("9001", answer["orderid"]!.GetValue<string>());
        Assert.Single(broker.Sent, s => s.Method == "POST");

        (status, answer) = await PostAsync(host, "/api/v1/placeorder", new { apikey = key, strategy = "tv", symbol = "SBIN", exchange = "NSE", action = "BUY", quantity = 11, pricetype = "MARKET", product = "MIS" });
        Assert.Equal(400, status);
        Assert.Contains("11", answer["message"]!.GetValue<string>());
        Assert.Single(broker.Sent, s => s.Method == "POST");
    }

    [PostgresFact]
    public async Task Header_key_is_accepted_and_a_revoked_key_is_refused()
    {
        var (host, _, account, key) = await LiveHostAsync("Production");
        await using var _ = host;
        using var client = host.Anonymous();

        using var withHeader = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ping") { Content = JsonContent.Create(new { }) };
        withHeader.Headers.Add("X-API-KEY", key);
        var ping = await client.SendAsync(withHeader);
        Assert.Equal(HttpStatusCode.OK, ping.StatusCode);
        Assert.Equal("zerodha", (await ping.Content.ReadFromJsonAsync<JsonObject>())!["data"]!["broker"]!.GetValue<string>());

        var keyId = (await host.Admin.GetFromJsonAsync<JsonArray>($"/api/v1/accounts/{account}/apikeys"))!.Single()!["id"]!.GetValue<string>();
        (await host.Admin.DeleteAsync($"/api/v1/accounts/{account}/apikeys/{keyId}")).EnsureSuccessStatusCode();
        var (status, answer) = await PostAsync(host, "/api/v1/ping", new { apikey = key });
        Assert.Equal(403, status);
        Assert.Equal("Invalid openalgo apikey", answer["message"]!.GetValue<string>());
    }

    [PostgresFact]
    public async Task Stored_keys_and_tokens_are_not_kept_in_plain_text()
    {
        var (host, _, account, key) = await LiveHostAsync("Production");
        await using var _ = host;
        await using var db = host.NewDb();
        var stored = await db.AccountApiKeys.SingleAsync(k => k.AccountId == account);
        Assert.NotEqual(key, stored.KeyHash);
        Assert.DoesNotContain(key, stored.KeyHash);
        var acc = await db.BrokerAccounts.SingleAsync(a => a.Id == account);
        Assert.DoesNotContain("token", acc.AuthTokenEncrypted!);

        // A token copied onto another account does not decrypt there.
        var protector = host.Get<TokenProtector>();
        Assert.Equal("key:token", protector.Unprotect(account, "auth_token", acc.AuthTokenEncrypted));
        Assert.Null(protector.Unprotect(Guid.NewGuid(), "auth_token", acc.AuthTokenEncrypted));
        Assert.Null(protector.Unprotect(account, "feed_token", acc.AuthTokenEncrypted));
    }

    [PostgresFact]
    public async Task Sandbox_account_trades_through_the_same_api_and_answers_in_analyze_mode()
    {
        await using var host = new TestHost("Development");
        var account = await host.CreateAccountAsync(AccountType.PAPER, "paper", 100_000, 1000, 10_000_000);
        var key = (await host.Get<ApiKeyService>().IssueAsync(account, "tv")).Key;
        host.SetQuote("NSE", "SBIN", 500);

        var (status, placed) = await PostAsync(host, "/api/v1/placeorder", new { apikey = key, strategy = "tv", symbol = "SBIN", exchange = "NSE", action = "BUY", quantity = "10", pricetype = "MARKET", product = "MIS" });
        Assert.Equal(200, status);
        Assert.Equal("analyze", placed["mode"]!.GetValue<string>());
        Assert.StartsWith("SB", placed["orderid"]!.GetValue<string>());

        var (_, open) = await PostAsync(host, "/api/v1/openposition", new { apikey = key, strategy = "tv", symbol = "SBIN", exchange = "NSE", product = "MIS" });
        Assert.Equal(10, open["quantity"]!.GetValue<int>());

        // Smart order to a target of 4: sells 6.
        var (_, smart) = await PostAsync(host, "/api/v1/placesmartorder", new { apikey = key, strategy = "tv", symbol = "SBIN", exchange = "NSE", action = "BUY", quantity = "0", position_size = "4", pricetype = "MARKET", product = "MIS" });
        Assert.Equal("success", smart["status"]!.GetValue<string>());
        (_, open) = await PostAsync(host, "/api/v1/openposition", new { apikey = key, strategy = "tv", symbol = "SBIN", exchange = "NSE", product = "MIS" });
        Assert.Equal(4, open["quantity"]!.GetValue<int>());

        var (_, book) = await PostAsync(host, "/api/v1/orderbook", new { apikey = key });
        Assert.Equal(2, book["data"]!["orders"]!.AsArray().Count);
        Assert.Equal(2, book["data"]!["statistics"]!["total_completed_orders"]!.GetValue<int>());

        var (closeStatus, closed) = await PostAsync(host, "/api/v1/closeposition", new { apikey = key, strategy = "tv" });
        Assert.Equal(200, closeStatus);
        Assert.Equal("All Open Positions Squared Off", closed["message"]!.GetValue<string>());
        (_, open) = await PostAsync(host, "/api/v1/openposition", new { apikey = key, strategy = "tv", symbol = "SBIN", exchange = "NSE", product = "MIS" });
        Assert.Equal(0, open["quantity"]!.GetValue<int>());

        var (_, funds) = await PostAsync(host, "/api/v1/funds", new { apikey = key });
        Assert.Equal(0.0, funds["data"]!["utiliseddebits"]!.GetValue<double>());

        await using var db = host.NewDb();
        Assert.Equal(3, await db.OrderAuditLogs.CountAsync(a => a.AccountId == account && a.Source == OrderSource.Api && a.Outcome == OrderOutcome.Accepted));
    }

    [PostgresFact]
    public async Task A_live_position_the_broker_cannot_price_still_counts_against_the_position_value_limit()
    {
        var broker = new FakeBroker();
        await using var host = new TestHost("Production", b =>
        {
            b.UseSetting("Brokers:Enabled:0", "dhan");
            b.ConfigureServices(s => s.ConfigureHttpClientDefaults(h => h.ConfigurePrimaryHttpMessageHandler(() => broker)));
        });
        var account = await host.CreateAccountAsync(AccountType.LIVE, "dhan", 1_000_000, 1000, 100_000, broker: "dhan", clientId: "1100000001");
        (await host.Admin.PutAsJsonAsync($"/api/v1/accounts/{account}/broker-session", new { auth_token = "dhan-token" })).EnsureSuccessStatusCode();
        var key = (await host.Get<ApiKeyService>().IssueAsync(account, "tv")).Key;
        await using (var db = host.NewDb())
        {
            db.Set<SymToken>().Add(new SymToken { Broker = "dhan", Symbol = "SBIN", Brsymbol = "SBIN", Exchange = "NSE", Brexchange = "NSE", Token = "3045", Lotsize = 1, TickSize = 0.05 });
            db.Set<SymToken>().Add(new SymToken { Broker = "dhan", Symbol = "INFY", Brsymbol = "INFY", Exchange = "NSE", Brexchange = "NSE", Token = "1594", Lotsize = 1, TickSize = 0.05 });
            await db.SaveChangesAsync();
        }

        // 100 SBIN held at 800 (80,000), and Dhan's quote limit refuses the price. No feed price for SBIN either.
        broker.Reset(new Dictionary<string, (int, string)>
        {
            ["GET /v2/positions"] = (200, """[{"tradingSymbol":"SBIN","securityId":"3045","exchangeSegment":"NSE_EQ","productType":"INTRADAY","netQty":100,"costPrice":800,"realizedProfit":0,"unrealizedProfit":0}]"""),
            ["POST /v2/marketfeed/quote"] = (200, """{"status":"failed","data":{"805":"Too many requests"}}"""),
            ["POST /v2/orders"] = (200, """{"orderId":"1","orderStatus":"PENDING"}"""),
        });
        host.SetQuote("NSE", "INFY", 1500);

        // 20 INFY at 1,500 is 30,000 more: 1,10,000 against a 1,00,000 limit.
        var (status, answer) = await PostAsync(host, "/api/v1/placeorder", new { apikey = key, strategy = "tv", symbol = "INFY", exchange = "NSE", action = "BUY", quantity = 20, pricetype = "MARKET", product = "MIS" });
        Assert.Equal(400, status);
        Assert.Equal("error", answer["status"]!.GetValue<string>());
        Assert.DoesNotContain(broker.Sent, s => s.Path == "/v2/orders");

        // 10 INFY (15,000) fits: 95,000.
        (status, _) = await PostAsync(host, "/api/v1/placeorder", new { apikey = key, strategy = "tv", symbol = "INFY", exchange = "NSE", action = "BUY", quantity = 10, pricetype = "MARKET", product = "MIS" });
        Assert.Equal(200, status);
        Assert.Single(broker.Sent, s => s.Path == "/v2/orders");
    }

    [PostgresFact]
    public async Task A_fractional_crypto_quantity_is_refused_not_truncated()
    {
        await using var host = new TestHost("Development");
        var account = await host.CreateAccountAsync(AccountType.PAPER, "crypto-paper", 100_000, 1000, 10_000_000);
        var key = (await host.Get<ApiKeyService>().IssueAsync(account, "tv")).Key;
        host.SetQuote("CRYPTO", "BTCUSD", 67000);

        var (status, answer) = await PostAsync(host, "/api/v1/placeorder", new { apikey = key, strategy = "s", symbol = "BTCUSD", exchange = "CRYPTO", action = "BUY", quantity = "0.5", pricetype = "MARKET", product = "NRML" });

        Assert.Equal(400, status);
        Assert.StartsWith("Fractional quantities (0.5)", answer["message"]!.GetValue<string>());
        await using var db = host.NewDb();
        Assert.Equal(0, await db.Set<EPTradeBot.Core.Entities.Legacy.Sandbox.SandboxOrder>().CountAsync());
    }

    [PostgresFact]
    public async Task Each_order_of_a_basket_and_each_part_of_a_split_passes_the_risk_check_on_its_own()
    {
        var (host, broker, _, key) = await LiveHostAsync("Production", maxQty: 10);
        await using var _ = host;

        var (status, basket) = await PostAsync(host, "/api/v1/basketorder", new
        {
            apikey = key, strategy = "tv",
            orders = new object[] { new { symbol = "SBIN", exchange = "NSE", action = "SELL", quantity = 50 }, new { symbol = "SBIN", exchange = "NSE", action = "BUY", quantity = 5 } },
        });
        Assert.Equal(200, status);
        var results = basket["results"]!.AsArray();
        Assert.Equal("success", results[0]!["status"]!.GetValue<string>());
        Assert.Equal("BUY", broker.Sent.Single(s => s.Method == "POST").Form!.Single(kv => kv.Key == "transaction_type").Value);
        Assert.Equal("error", results[1]!["status"]!.GetValue<string>());
        Assert.Contains("10", results[1]!["message"]!.GetValue<string>());

        broker.ClearSent();
        (status, var split) = await PostAsync(host, "/api/v1/splitorder", new { apikey = key, strategy = "tv", symbol = "SBIN", exchange = "NSE", action = "BUY", quantity = 25, splitsize = 10 });
        Assert.Equal(200, status);
        Assert.Equal([10, 10, 5], split["results"]!.AsArray().Select(r => r!["quantity"]!.GetValue<int>()));
        Assert.All(split["results"]!.AsArray(), r => Assert.Equal("success", r!["status"]!.GetValue<string>()));
        Assert.Equal(3, broker.Sent.Count(s => s.Method == "POST"));

        broker.ClearSent();
        (status, split) = await PostAsync(host, "/api/v1/splitorder", new { apikey = key, strategy = "tv", symbol = "SBIN", exchange = "NSE", action = "BUY", quantity = 40, splitsize = 20 });
        Assert.Equal(200, status);
        Assert.All(split["results"]!.AsArray(), r => Assert.Equal("error", r!["status"]!.GetValue<string>()));
        Assert.DoesNotContain(broker.Sent, s => s.Method == "POST");
    }

    [PostgresFact]
    public async Task A_sandbox_basket_fills_in_the_sandbox_and_is_marked_analyze()
    {
        await using var host = new TestHost("Development");
        var account = await host.CreateAccountAsync(AccountType.PAPER, "paper", 1_000_000, 1000, 10_000_000);
        var key = (await host.Get<ApiKeyService>().IssueAsync(account, "tv")).Key;
        host.SetQuote("NSE", "SBIN", 805);
        host.SetQuote("NSE", "INFY", 1500);

        var (status, body) = await PostAsync(host, "/api/v1/basketorder", new
        {
            apikey = key, strategy = "tv",
            orders = new object[] { new { symbol = "INFY", exchange = "NSE", action = "SELL", quantity = 1 }, new { symbol = "SBIN", exchange = "NSE", action = "BUY", quantity = 2 } },
        });
        Assert.Equal(200, status);
        Assert.Equal("analyze", body["mode"]!.GetValue<string>());
        var results = body["results"]!.AsArray();
        Assert.Equal(["SBIN", "INFY"], results.Select(r => r!["symbol"]!.GetValue<string>()));
        Assert.All(results, r => Assert.Equal("success", r!["status"]!.GetValue<string>()));
        Assert.Equal([false, true], results.Select(r => r!["is_last_order"]!.GetValue<bool>()));
        Assert.Equal(2, (await host.Get<EPTradeBot.Infrastructure.Sandbox.SandboxEngine>().GetOrdersAsync(account)).Count);
    }

    [PostgresFact]
    public async Task Instruments_download_as_csv_in_openalgo_format()
    {
        var (host, _, _, key) = await LiveHostAsync("Production");
        await using var _ = host;
        await using (var db = host.NewDb())
        {
            db.Set<SymToken>().Add(new SymToken { Broker = "zerodha", Symbol = "NIFTY30DEC3024000CE", Brsymbol = "NIFTY30DEC24000CE", Name = "NIFTY, INDEX", Exchange = "NFO", Brexchange = "NFO",
                Token = "1::::2", Expiry = "30-DEC-30", Strike = 24000, Lotsize = 75, Instrumenttype = "CE", TickSize = 0.05 });
            await db.SaveChangesAsync();
        }

        using var client = host.Anonymous();
        var response = await client.GetAsync($"/api/v1/instruments?apikey={key}&exchange=NFO&format=CSV");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment; filename=instruments_NFO.csv", response.Content.Headers.ContentDisposition?.ToString());
        Assert.Equal(
            "symbol,brsymbol,name,exchange,brexchange,token,expiry,strike,lotsize,instrumenttype,tick_size\r\n"
            + "NIFTY30DEC3024000CE,NIFTY30DEC24000CE,\"NIFTY, INDEX\",NFO,NFO,1::::2,30-DEC-30,24000.0,75,CE,0.05\r\n",
            await response.Content.ReadAsStringAsync());
    }

    [PostgresFact]
    public async Task Freeze_limits_are_seeded_once_and_an_edited_limit_is_kept()
    {
        await using var host = new TestHost("Development");
        await using (var db = host.NewDb())
        {
            var nifty = await db.Set<QtyFreeze>().SingleAsync(q => q.Exchange == "NFO" && q.Symbol == "NIFTY");
            Assert.Equal(1800, nifty.FreezeQty);
            Assert.Equal(214, await db.Set<QtyFreeze>().CountAsync(q => q.Exchange == "NFO"));
            nifty.FreezeQty = 1234;
            await db.SaveChangesAsync();
        }

        await EPTradeBot.Infrastructure.Persistence.DatabaseMigrator.MigrateAsync(host.Services);
        await using (var db = host.NewDb())
            Assert.Equal(1234, (await db.Set<QtyFreeze>().SingleAsync(q => q.Exchange == "NFO" && q.Symbol == "NIFTY")).FreezeQty);
    }
}

