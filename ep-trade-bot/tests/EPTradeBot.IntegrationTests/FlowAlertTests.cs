using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.Legacy.Core;
using EPTradeBot.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace EPTradeBot.IntegrationTests;

/// <summary>The Telegram and WhatsApp alert nodes inside a running workflow: who the message goes to, what it says, and what a workflow sees when it cannot be sent.</summary>
public sealed class FlowAlertTests
{
    private sealed class Device : IWhatsAppTransport
    {
        public bool IsReady => true;

        public string? UnavailableReason => null;

        public List<(IReadOnlyList<string> To, string? Text)> Sent { get; } = [];

        public bool Fail { get; set; }

        public Task<WhatsAppReport> SendAsync(IReadOnlyList<string> recipients, string? text, string? imagePath, string? documentPath, string? caption, string? filename, CancellationToken ct)
        {
            Sent.Add((recipients, text));
            return Task.FromResult(Fail ? new WhatsAppReport([], [(recipients.FirstOrDefault() ?? "self", "The number is not on WhatsApp.")]) : new WhatsAppReport(recipients.Count == 0 ? ["self"] : recipients, []));
        }
    }

    private static async Task<(TestHost Host, Guid Account, FakeBroker Telegram, Device Device)> NewAsync()
    {
        var telegram = new FakeBroker();
        var device = new Device();
        var host = new TestHost("Development", b => b.ConfigureServices(s =>
        {
            s.ConfigureHttpClientDefaults(h => h.ConfigurePrimaryHttpMessageHandler(() => telegram));
            s.AddSingleton<IWhatsAppTransport>(device);
        }));
        var account = await host.CreateAccountAsync(AccountType.PAPER, "paper", 100_000, 1000, 10_000_000);
        telegram.Reset(new Dictionary<string, (int, string)>
        {
            ["POST /bot111:AAA/sendMessage"] = (200, """{"ok":true}"""),
            ["GET /bot111:AAA/getMe"] = (200, """{"ok":true,"result":{"username":"ep_trade_bot"}}"""),
        });
        return (host, account, telegram, device);
    }

    private static JsonObject Node(string id, string type, JsonObject? data = null) => new() { ["id"] = id, ["type"] = type, ["data"] = data ?? [] };

    private static async Task<(HttpStatusCode Status, JsonObject Body)> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null)
    {
        var response = await client.SendAsync(new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) });
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 && JsonNode.Parse(text) is JsonObject o ? o : []);
    }

    private static async Task<(string Status, JsonArray Logs)> RunAsync(TestHost host, Guid account, JsonObject alert, params JsonObject[] before)
    {
        // start -> each node in `before` -> the alert
        var chain = new List<JsonObject> { Node("s", "start") };
        chain.AddRange(before);
        chain.Add(alert);
        var edges = new JsonArray();
        for (var i = 0; i + 1 < chain.Count; i++) edges.Add(new JsonObject { ["source"] = chain[i]["id"]!.GetValue<string>(), ["target"] = chain[i + 1]["id"]!.GetValue<string>(), ["sourceHandle"] = "" });
        var (created, flow) = await SendAsync(host.Admin, HttpMethod.Post, "/api/v1/flows", new JsonObject
        {
            ["name"] = $"alerts-{Guid.NewGuid():N}", ["account_id"] = account, ["execution_mode"] = "auto",
            ["nodes"] = new JsonArray(chain.Select(n => (JsonNode)n).ToArray()), ["edges"] = edges,
        });
        Assert.Equal(HttpStatusCode.Created, created);
        var id = flow["id"]!.GetValue<string>();
        var (_, ran) = await SendAsync(host.Admin, HttpMethod.Post, $"/api/v1/flows/{id}/run");
        var (_, detail) = await SendAsync(host.Admin, HttpMethod.Get, $"/api/v1/flows/{id}/runs/{ran["run_id"]!.GetValue<string>()}");
        return (detail["status"]!.GetValue<string>(), (JsonArray)detail["logs"]!);
    }

    private static async Task SetUpTelegramAsync(TestHost host, bool active = true, bool notifications = true, string user = "paper")
    {
        await using var db = host.NewDb();
        db.Set<TelegramBotConfig>().Add(new TelegramBotConfig { Id = 1, IsActive = active, Token = host.Get<EPTradeBot.Infrastructure.Security.TokenProtector>().Protect(Guid.Empty, "telegram.bot_token", "111:AAA") });
        db.Set<TelegramUser>().Add(new TelegramUser { TelegramId = 4242, OpenalgoUsername = user, IsActive = true, NotificationsEnabled = notifications });
        await db.SaveChangesAsync();
    }

    private static async Task<bool> WaitAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until) { if (condition()) return true; await Task.Delay(50); }
        return condition();
    }

    [PostgresFact]
    public async Task A_telegram_alert_with_workflow_values_reaches_the_linked_person()
    {
        var (host, account, telegram, _) = await NewAsync();
        await using var _ = host;
        await SetUpTelegramAsync(host);
        var (status, logs) = await RunAsync(host, account, Node("a", "telegramAlert", new() { ["message"] = "SBIN is {{price}}" }), Node("v", "variable", new() { ["variableName"] = "price", ["operation"] = "set", ["value"] = "512" }));
        Assert.Equal("completed", status);
        Assert.Contains(logs, l => l!["message"]!.GetValue<string>() == "Sending Telegram alert: SBIN is 512");
        Assert.True(await WaitAsync(() => telegram.Sent.Any(r => r.Path.EndsWith("/sendMessage", StringComparison.Ordinal))));
        var sent = telegram.Sent.Single(r => r.Path.EndsWith("/sendMessage", StringComparison.Ordinal));
        Assert.Equal(4242, sent.Json!["chat_id"]!.GetValue<long>());
        Assert.Equal("Markdown", sent.Json["parse_mode"]!.GetValue<string>());
        var text = sent.Json["text"]!.GetValue<string>();
        Assert.StartsWith("*Flow Alert*\n", text);
        Assert.Contains("SBIN is 512", text);
        Assert.Matches(@"\n\nTime: \d\d:\d\d:\d\d$", text);
    }

    [PostgresFact]
    public async Task A_telegram_alert_that_cannot_go_is_an_error_the_workflow_sees()
    {
        var (host, account, telegram, _) = await NewAsync();
        await using var _ = host;
        // The bot is stopped: automated messages stop with it.
        await SetUpTelegramAsync(host, active: false);
        var (status, logs) = await RunAsync(host, account, Node("a", "telegramAlert", new() { ["message"] = "hello" }));
        Assert.Equal("failed", status);
        Assert.Contains(logs, l => l!["message"]!.GetValue<string>().Contains("Telegram bot is stopped"));
        Assert.DoesNotContain(telegram.Sent, r => r.Path.EndsWith("/sendMessage", StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task A_telegram_alert_names_what_is_missing_when_nobody_is_linked_or_alerts_are_off()
    {
        var (host, account, telegram, _) = await NewAsync();
        await using var _ = host;
        await SetUpTelegramAsync(host, notifications: false);
        var (_, off) = await RunAsync(host, account, Node("a", "telegramAlert", new() { ["message"] = "hello" }));
        Assert.Contains(off, l => l!["message"]!.GetValue<string>().Contains("Telegram notifications are disabled"));

        await using (var db = host.NewDb())
        {
            var user = db.Set<TelegramUser>().Single();
            user.OpenalgoUsername = "someone";
            db.Set<TelegramUser>().Add(new TelegramUser { TelegramId = 1, OpenalgoUsername = "another", IsActive = true });
            await db.SaveChangesAsync();
        }

        var (_, none) = await RunAsync(host, account, Node("a", "telegramAlert", new() { ["message"] = "hello" }));
        Assert.Contains(none, l => l!["message"]!.GetValue<string>().Contains("No Telegram account linked for user: paper"));
        Assert.DoesNotContain(telegram.Sent, r => r.Path.EndsWith("/sendMessage", StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task A_whatsapp_alert_goes_to_the_named_number_or_to_the_paired_device()
    {
        var (host, account, _, device) = await NewAsync();
        await using var _ = host;
        var (status, _) = await RunAsync(host, account, Node("a", "whatsappAlert", new() { ["message"] = "levels hit", ["to"] = "+91 98000 00001" }));
        Assert.Equal("completed", status);
        Assert.Equal(["919800000001@s.whatsapp.net"], device.Sent[^1].To);
        Assert.Equal("levels hit", device.Sent[^1].Text);

        (status, var logs) = await RunAsync(host, account, Node("b", "whatsappAlert", new() { ["message"] = "to me" }));
        Assert.Equal("completed", status);
        Assert.Empty(device.Sent[^1].To);
        Assert.Contains(logs, l => l!["message"]!.GetValue<string>() == "Sending WhatsApp alert to self: to me");

        device.Fail = true;
        (status, logs) = await RunAsync(host, account, Node("d", "whatsappAlert", new() { ["message"] = "nobody home", ["to"] = "919800000009" }));
        Assert.Equal("failed", status);
        Assert.Contains(logs, l => l!["message"]!.GetValue<string>().Contains("The number is not on WhatsApp."));
        device.Fail = false;

        (status, logs) = await RunAsync(host, account, Node("c", "whatsappAlert", new() { ["message"] = "bad", ["to"] = "12" }));
        Assert.Equal("failed", status);
        Assert.Contains(logs, l => l!["message"]!.GetValue<string>().Contains("Invalid WhatsApp phone number: 12"));
    }
}
