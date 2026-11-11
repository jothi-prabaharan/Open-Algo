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

/// <summary>The Action Center: a semi-auto key holds its orders; approval decides again; nothing is sent twice.</summary>
public sealed class ActionCenterTests
{
    private sealed class Clock(DateTimeOffset at) : TimeProvider
    {
        public DateTimeOffset At { get; set; } = at;
        public override DateTimeOffset GetUtcNow() => At;
    }

    private sealed record Setup(TestHost Host, Guid Account, string AutoKey, string SemiKey);

    private static async Task<Setup> NewAsync(Clock? clock = null, int maxQty = 1000)
    {
        var host = new TestHost("Development", b => b.ConfigureServices(s => { if (clock is not null) s.AddSingleton<TimeProvider>(clock); }));
        var account = await host.CreateAccountAsync(AccountType.PAPER, "paper", 100_000, maxQty, 10_000_000);
        var issuer = host.Get<ApiKeyService>();
        var autoKey = (await issuer.IssueAsync(account, "auto")).Key;
        var (semiRecord, semiKey) = await issuer.IssueAsync(account, "semi");
        (await host.Admin.PutAsJsonAsync($"/api/v1/accounts/{account}/apikeys/{semiRecord.Id}/order-mode", new { mode = "semi_auto" })).EnsureSuccessStatusCode();
        await using (var db = host.NewDb())
        {
            db.Set<SymToken>().Add(new SymToken { Broker = "sandbox", Symbol = "SBIN", Brsymbol = "SBIN", Exchange = "NSE", Brexchange = "NSE", Token = "3045", Lotsize = 1 });
            await db.SaveChangesAsync();
        }

        host.SetQuote("NSE", "SBIN", 500);
        return new Setup(host, account, autoKey, semiKey);
    }

    private static object Order(string key, int quantity = 10) => new { apikey = key, strategy = "tv", symbol = "SBIN", exchange = "NSE", action = "BUY", quantity, pricetype = "MARKET", product = "MIS" };

    private static async Task<(int Status, JsonObject Body)> PostAsync(TestHost host, string path, object? body = null, bool admin = false)
    {
        using var anonymous = admin ? null : host.Anonymous();
        var client = admin ? host.Admin : anonymous!;
        var response = await client.PostAsJsonAsync(path, body ?? new { });
        return ((int)response.StatusCode, (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    /// <summary>Fills the account has had: a market order in the sandbox fills at once, so a placed order shows here.</summary>
    private static async Task<JsonArray> TradesAsync(TestHost host, string key) =>
        (JsonArray)(await PostAsync(host, "/api/v1/tradebook", new { apikey = key })).Body["data"]!;

    private static async Task<JsonArray> PendingAsync(TestHost host, string status = "all") =>
        (JsonArray)JsonNode.Parse(await host.Admin.GetStringAsync($"/api/v1/action-center/orders?status={status}"))!["data"]!;

    [PostgresFact]
    public async Task A_semi_auto_key_holds_its_orders_while_an_auto_key_on_the_same_account_sends_them()
    {
        var s = await NewAsync();
        await using var _host = s.Host;

        var (status, held) = await PostAsync(s.Host, "/api/v1/placeorder", Order(s.SemiKey));
        Assert.Equal(200, status);
        Assert.Equal("semi_auto", held["mode"]!.GetValue<string>());
        Assert.Equal("Order queued for approval in Action Center", held["message"]!.GetValue<string>());
        var id = held["pending_order_id"]!.GetValue<int>();

        Assert.Empty(await TradesAsync(s.Host, s.AutoKey));

        var waiting = await PendingAsync(s.Host, "pending");
        Assert.Equal(id, waiting.Single()!["id"]!.GetValue<int>());
        Assert.Equal("placeorder", waiting[0]!["api_type"]!.GetValue<string>());
        Assert.Equal(10, waiting[0]!["order_data"]!["quantity"]!.GetValue<int>());
        Assert.Null(waiting[0]!["order_data"]!["apikey"]);

        (status, var sent) = await PostAsync(s.Host, "/api/v1/placeorder", Order(s.AutoKey));
        Assert.Equal(200, status);
        Assert.NotNull(sent["orderid"]);
    }

    [PostgresFact]
    public async Task Approving_sends_the_order_from_the_action_center_and_records_who_decided()
    {
        var s = await NewAsync();
        await using var _host = s.Host;
        var (_, held) = await PostAsync(s.Host, "/api/v1/placeorder", Order(s.SemiKey));
        var id = held["pending_order_id"]!.GetValue<int>();

        var (status, approved) = await PostAsync(s.Host, $"/api/v1/action-center/orders/{id}/approve", admin: true);
        Assert.Equal(200, status);
        Assert.NotNull(approved["orderid"]);

        Assert.Single(await TradesAsync(s.Host, s.AutoKey));

        var row = (await PendingAsync(s.Host, "approved")).Single()!;
        Assert.Equal("admin", row["approved_by"]!.GetValue<string>());
        Assert.Equal(approved["orderid"]!.ToString(), row["broker_order_id"]!.GetValue<string>());

        await using var db = s.Host.NewDb();
        var audit = await db.OrderAuditLogs.Where(a => a.Source == OrderSource.ActionCenter).ToListAsync();
        Assert.Single(audit);
        Assert.Equal(OrderOutcome.Accepted, audit[0].Outcome);
        var decision = await db.ActionCenterDecisions.SingleAsync();
        Assert.Equal((id, ApprovalDecision.Approved, "admin", false), (decision.PendingOrderId, decision.Decision, decision.Actor, decision.BulkAction));
    }

    [PostgresFact]
    public async Task The_guard_and_the_risk_limits_are_decided_again_at_approval()
    {
        var s = await NewAsync(maxQty: 50);
        await using var _host = s.Host;

        // Fine when queued; the account's quantity limit then drops below it, and the kill switch goes on for a second order.
        var (_, over) = await PostAsync(s.Host, "/api/v1/placeorder", Order(s.SemiKey, 40));
        var (_, killed) = await PostAsync(s.Host, "/api/v1/placeorder", Order(s.SemiKey, 10));
        await using (var db = s.Host.NewDb())
        {
            await db.AccountRiskSettings.Where(r => r.AccountId == s.Account).ExecuteUpdateAsync(u => u.SetProperty(r => r.MaxOrderQuantity, 20));
        }

        var (status, refused) = await PostAsync(s.Host, $"/api/v1/action-center/orders/{over["pending_order_id"]}/approve", admin: true);
        Assert.Equal(400, status);
        Assert.Contains("40", refused["message"]!.GetValue<string>());

        (await s.Host.Admin.PostAsJsonAsync($"/api/v1/killswitch/account/{s.Account}", new { engage = true, reason = "test" })).EnsureSuccessStatusCode();
        (status, refused) = await PostAsync(s.Host, $"/api/v1/action-center/orders/{killed["pending_order_id"]}/approve", admin: true);
        Assert.True(status >= 400, refused.ToJsonString());

        var rows = await PendingAsync(s.Host);
        Assert.All(rows, r => Assert.Equal("refused", r!["status"]!.GetValue<string>()));
        await using (var audit = s.Host.NewDb())
        {
            // Each refusal is in the decision log with its reason.
            var decisions = await audit.ActionCenterDecisions.OrderBy(d => d.Id).ToListAsync();
            Assert.Equal(2, decisions.Count);
            Assert.All(decisions, d => Assert.False(string.IsNullOrEmpty(d.Reason)));
        }

        Assert.All(rows, r => Assert.False(string.IsNullOrEmpty(r!["reason"]!.GetValue<string>())));
        Assert.Empty(await TradesAsync(s.Host, s.AutoKey));
    }

    [PostgresFact]
    public async Task A_rejected_order_is_never_sent_and_cannot_then_be_approved()
    {
        var s = await NewAsync();
        await using var _host = s.Host;
        var (_, held) = await PostAsync(s.Host, "/api/v1/placeorder", Order(s.SemiKey));
        var id = held["pending_order_id"]!.GetValue<int>();

        var (status, _) = await PostAsync(s.Host, $"/api/v1/action-center/orders/{id}/reject", new { reason = "wrong size" }, admin: true);
        Assert.Equal(200, status);
        (status, var late) = await PostAsync(s.Host, $"/api/v1/action-center/orders/{id}/approve", admin: true);
        Assert.Equal(409, status);
        Assert.Contains("already rejected", late["message"]!.GetValue<string>());

        // An order that was approved cannot be rejected afterwards: it is already at the broker.
        var (_, second) = await PostAsync(s.Host, "/api/v1/placeorder", Order(s.SemiKey));
        var secondId = second["pending_order_id"]!.GetValue<int>();
        Assert.Equal(200, (await PostAsync(s.Host, $"/api/v1/action-center/orders/{secondId}/approve", admin: true)).Status);
        (status, var tooLate) = await PostAsync(s.Host, $"/api/v1/action-center/orders/{secondId}/reject", new { reason = "changed my mind" }, admin: true);
        Assert.Equal(409, status);
        Assert.Contains("already approved", tooLate["message"]!.GetValue<string>());
        Assert.Equal("approved", (await PendingAsync(s.Host, "approved")).Single()!["status"]!.GetValue<string>());

        var row = (await PendingAsync(s.Host, "rejected")).Single()!;
        Assert.Equal("wrong size", row["reason"]!.GetValue<string>());
        await using var db = s.Host.NewDb();
        var decisions = await db.ActionCenterDecisions.OrderBy(d => d.Id).ToListAsync();
        // The refused rejection of the approved order leaves no decision behind: one rejection, one approval.
        Assert.Equal(2, decisions.Count);
        Assert.Equal((ApprovalDecision.Rejected, "wrong size"), (decisions[0].Decision, decisions[0].Reason));
        Assert.Equal(ApprovalDecision.Approved, decisions[1].Decision);
        // Only the approved order reached the broker.
        Assert.Single(await TradesAsync(s.Host, s.AutoKey));
    }

    [PostgresFact]
    public async Task Two_approvals_of_one_order_send_it_once()
    {
        var s = await NewAsync();
        await using var _host = s.Host;
        var (_, held) = await PostAsync(s.Host, "/api/v1/placeorder", Order(s.SemiKey));
        var path = $"/api/v1/action-center/orders/{held["pending_order_id"]}/approve";

        var answers = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => PostAsync(s.Host, path, admin: true)));
        Assert.Equal(1, answers.Count(a => a.Status == 200));
        Assert.Equal(5, answers.Count(a => a.Status == 409));
        Assert.Single(await TradesAsync(s.Host, s.AutoKey));
    }

    [PostgresFact]
    public async Task Approve_all_and_reject_all_work_through_the_queue_in_order_and_mark_the_decisions_as_bulk()
    {
        var s = await NewAsync();
        await using var _host = s.Host;
        for (var i = 0; i < 3; i++) await PostAsync(s.Host, "/api/v1/placeorder", Order(s.SemiKey, 5 + i));

        var (status, done) = await PostAsync(s.Host, "/api/v1/action-center/approve-all", admin: true);
        Assert.Equal(200, status);
        Assert.Equal(3, done["count"]!.GetValue<int>());
        Assert.Equal([1, 2, 3], done["results"]!.AsArray().Select(r => r!["pending_order_id"]!.GetValue<int>()));
        Assert.All(done["results"]!.AsArray(), r => Assert.Equal(200, r!["http_status"]!.GetValue<int>()));

        await PostAsync(s.Host, "/api/v1/placeorder", Order(s.SemiKey));
        (_, done) = await PostAsync(s.Host, "/api/v1/action-center/reject-all", new { reason = "end of day" }, admin: true);
        Assert.Equal(1, done["count"]!.GetValue<int>());

        await using var db = s.Host.NewDb();
        Assert.Equal(4, await db.ActionCenterDecisions.CountAsync(d => d.BulkAction));
    }

    [PostgresFact]
    public async Task In_semi_auto_the_calls_that_change_what_is_live_are_refused_with_the_reason()
    {
        var s = await NewAsync();
        await using var _host = s.Host;
        var refused = new (string Path, object Body, string Phrase)[]
        {
            ("/api/v1/cancelorder", new { apikey = s.SemiKey, strategy = "tv", orderid = "1" }, "Cancel order operation is not allowed in Semi-Auto mode"),
            ("/api/v1/cancelallorder", new { apikey = s.SemiKey, strategy = "tv" }, "Cancel all orders operation is not allowed in Semi-Auto mode"),
            ("/api/v1/closeposition", new { apikey = s.SemiKey, strategy = "tv" }, "Close position operation is not allowed in Semi-Auto mode"),
            ("/api/v1/modifyorder", new { apikey = s.SemiKey, strategy = "tv", symbol = "SBIN", exchange = "NSE", orderid = "1", action = "BUY", quantity = "1", pricetype = "LIMIT", product = "MIS", price = "1", disclosed_quantity = "0", trigger_price = "0" }, "Modify order operation is not allowed in Semi-Auto mode"),
        };
        foreach (var (path, body, phrase) in refused)
        {
            var (status, answer) = await PostAsync(s.Host, path, body);
            Assert.True(status == 403, $"{path} answered {status}");
            Assert.Contains(phrase, answer["message"]!.GetValue<string>());
        }

        // The same calls on the auto key are not blocked by the mode.
        var (autoStatus, _) = await PostAsync(s.Host, "/api/v1/closeposition", new { apikey = s.AutoKey, strategy = "tv" });
        Assert.NotEqual(403, autoStatus);
    }

    [PostgresFact]
    public async Task An_order_not_approved_by_the_end_of_the_trading_day_expires()
    {
        var clock = new Clock(new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.FromHours(5.5)));
        var s = await NewAsync(clock);
        await using var _host = s.Host;
        var (_, held) = await PostAsync(s.Host, "/api/v1/placeorder", Order(s.SemiKey));
        var id = held["pending_order_id"]!.GetValue<int>();

        // Still the same session at 02:00 the next morning, past it at 03:30.
        clock.At = new DateTimeOffset(2026, 11, 4, 2, 0, 0, TimeSpan.FromHours(5.5));
        Assert.Single(await PendingAsync(s.Host, "pending"));
        clock.At = new DateTimeOffset(2026, 11, 4, 3, 30, 0, TimeSpan.FromHours(5.5));
        Assert.Empty(await PendingAsync(s.Host, "pending"));
        Assert.Equal("expired", (await PendingAsync(s.Host, "expired")).Single()!["status"]!.GetValue<string>());

        var (status, _) = await PostAsync(s.Host, $"/api/v1/action-center/orders/{id}/approve", admin: true);
        Assert.Equal(409, status);
        await using var db = s.Host.NewDb();
        Assert.Equal(ApprovalDecision.Expired, (await db.ActionCenterDecisions.SingleAsync()).Decision);
    }

    [PostgresFact]
    public async Task An_order_the_environment_would_refuse_is_not_queued()
    {
        var host = new TestHost("Development");
        await using var _host = host;
        var live = await host.CreateAccountAsync(AccountType.LIVE, "live", broker: "zerodha");
        var (record, key) = await host.Get<ApiKeyService>().IssueAsync(live, "semi");
        (await host.Admin.PutAsJsonAsync($"/api/v1/accounts/{live}/apikeys/{record.Id}/order-mode", new { mode = "semi_auto" })).EnsureSuccessStatusCode();

        var (status, answer) = await PostAsync(host, "/api/v1/placeorder", Order(key));
        Assert.Equal(403, status);
        Assert.Equal("error", answer["status"]!.GetValue<string>());
        Assert.Empty(await PendingAsync(host));
    }

    [PostgresFact]
    public async Task The_mode_is_set_per_key_and_only_to_a_known_value()
    {
        var s = await NewAsync();
        await using var _host = s.Host;
        var keys = JsonNode.Parse(await s.Host.Admin.GetStringAsync($"/api/v1/accounts/{s.Account}/apikeys"))!.AsArray();
        var autoId = keys.Single(k => k!["label"]!.GetValue<string>() == "auto")!["id"]!.GetValue<Guid>();

        var bad = await s.Host.Admin.PutAsJsonAsync($"/api/v1/accounts/{s.Account}/apikeys/{autoId}/order-mode", new { mode = "manual" });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad.StatusCode);
        var missing = await s.Host.Admin.PutAsJsonAsync($"/api/v1/accounts/{s.Account}/apikeys/{Guid.NewGuid()}/order-mode", new { mode = "auto" });
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);

        (await s.Host.Admin.PutAsJsonAsync($"/api/v1/accounts/{s.Account}/apikeys/{autoId}/order-mode", new { mode = "semi_auto" })).EnsureSuccessStatusCode();
        var (_, held) = await PostAsync(s.Host, "/api/v1/placeorder", Order(s.AutoKey));
        Assert.Equal("semi_auto", held["mode"]!.GetValue<string>());
    }
}
