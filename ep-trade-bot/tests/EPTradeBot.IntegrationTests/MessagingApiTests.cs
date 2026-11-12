using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.Legacy.Audit;
using EPTradeBot.Core.Entities.Legacy.Core;
using EPTradeBot.Infrastructure.Messaging;
using EPTradeBot.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EPTradeBot.IntegrationTests;

/// <summary>The Telegram and WhatsApp routes: settings, the bot's on and off, the people linked to it, sending, and the refusals a trader would hit.</summary>
public sealed class MessagingApiTests
{
    private sealed class FakeTelegram : HttpMessageHandler
    {
        public List<(string Path, string Body)> Calls { get; } = [];

        public HttpStatusCode GetMeStatus { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            Calls.Add((request.RequestUri!.AbsolutePath, body));
            if (request.RequestUri.AbsolutePath.EndsWith("/getMe", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(GetMeStatus) { Content = new StringContent("""{"ok":true,"result":{"username":"ep_trade_bot"}}""", Encoding.UTF8, "application/json") });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json") });
        }
    }

    private sealed class PairedDevice : IWhatsAppTransport
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

    private static async Task<(TestHost Host, string Key, FakeTelegram Telegram, PairedDevice? Device)> HostAsync(bool paired = false, string? attachmentRoot = null)
    {
        var telegram = new FakeTelegram();
        var device = paired ? new PairedDevice() : null;
        var host = new TestHost("Production", b =>
        {
            if (attachmentRoot is not null) b.UseSetting("WhatsApp:AttachmentRoots", attachmentRoot);
            b.ConfigureServices(s =>
            {
                s.ConfigureHttpClientDefaults(h => h.ConfigurePrimaryHttpMessageHandler(() => telegram));
            if (device is not null) s.AddSingleton<IWhatsAppTransport>(device);
            });
        });
        var account = await host.CreateAccountAsync(AccountType.LIVE, "kite");
        var key = (await host.Get<ApiKeyService>().IssueAsync(account, "messaging")).Key;
        return (host, key, telegram, device);
    }

    private static async Task<(int Status, JsonObject Body)> Send(HttpClient client, HttpMethod method, string path, object? body = null, string? key = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (key is not null) request.Headers.Add("X-API-KEY", key);
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, text.StartsWith('{') ? (JsonObject)JsonNode.Parse(text)! : new JsonObject { ["text"] = text });
    }

    [PostgresFact]
    public async Task Settings_show_a_masked_token_and_refuse_a_bad_key_or_rate()
    {
        var (host, key, _, _) = await HostAsync();
        await using var _ = host;
        using var client = host.Anonymous();

        var (status, body) = await Send(client, HttpMethod.Get, "/api/v1/telegram/config", key: "wrong");
        Assert.Equal(401, status);
        Assert.Equal("Invalid or missing API key", body["message"]!.GetValue<string>());

        Assert.Equal(400, (await Send(client, HttpMethod.Post, "/api/v1/telegram/config", new { rate_limit_per_minute = 500 }, key)).Status);
        Assert.Equal(400, (await Send(client, HttpMethod.Post, "/api/v1/telegram/config", new { rate_limit_per_minute = "many" }, key)).Status);
        Assert.Equal(200, (await Send(client, HttpMethod.Post, "/api/v1/telegram/config", new { token = "1234567890:ABCdefGHIjklMNO", rate_limit_per_minute = 20, broadcast_enabled = false }, key)).Status);

        (status, body) = await Send(client, HttpMethod.Get, "/api/v1/telegram/config", key: key);
        Assert.Equal(200, status);
        var data = body["data"]!;
        Assert.Equal("1234567890...", data["bot_token"]!.GetValue<string>());
        Assert.Equal("1234567890...", data["token"]!.GetValue<string>());
        Assert.Equal(20, data["rate_limit_per_minute"]!.GetValue<int>());
        Assert.False(data["broadcast_enabled"]!.GetValue<bool>());
    }

    [PostgresFact]
    public async Task The_bot_starts_only_with_a_token_telegram_accepts_and_stops_once()
    {
        var (host, key, telegram, _) = await HostAsync();
        await using var _ = host;
        using var client = host.Anonymous();

        var (status, body) = await Send(client, HttpMethod.Post, "/api/v1/telegram/start", new { }, key);
        Assert.Equal(400, status);
        Assert.Equal("Bot token not configured", body["message"]!.GetValue<string>());

        await Send(client, HttpMethod.Post, "/api/v1/telegram/config", new { token = "111:AAA" }, key);
        telegram.GetMeStatus = HttpStatusCode.Unauthorized;
        (status, body) = await Send(client, HttpMethod.Post, "/api/v1/telegram/start", new { }, key);
        Assert.Equal(500, status);
        Assert.Contains("did not accept this bot token", body["message"]!.GetValue<string>());

        telegram.GetMeStatus = HttpStatusCode.OK;
        (status, body) = await Send(client, HttpMethod.Post, "/api/v1/telegram/start", new { }, key);
        Assert.Equal(200, status);
        Assert.Equal("Bot started successfully", body["message"]!.GetValue<string>());
        Assert.True((await Send(client, HttpMethod.Get, "/api/v1/telegram/config", key: key)).Body["data"]!["is_active"]!.GetValue<bool>());
        Assert.Equal("ep_trade_bot", (await Send(client, HttpMethod.Get, "/api/v1/telegram/config", key: key)).Body["data"]!["bot_username"]!.GetValue<string>());

        Assert.Equal("Bot stopped successfully", (await Send(client, HttpMethod.Post, "/api/v1/telegram/stop", new { }, key)).Body["message"]!.GetValue<string>());
        (status, body) = await Send(client, HttpMethod.Post, "/api/v1/telegram/stop", new { }, key);
        Assert.Equal(500, status);
        Assert.Equal("Bot is not running", body["message"]!.GetValue<string>());
    }

    [PostgresFact]
    public async Task Notifications_reach_a_linked_user_only_while_the_bot_is_running()
    {
        var (host, key, telegram, _) = await HostAsync();
        await using var _ = host;
        using var client = host.Anonymous();
        await using (var db = host.NewDb())
        {
            db.Set<TelegramUser>().Add(new TelegramUser { TelegramId = 777, OpenalgoUsername = "trader", Broker = "kite", FirstName = "Asha", IsActive = true, NotificationsEnabled = true });
            db.Set<TelegramUser>().Add(new TelegramUser { TelegramId = 888, OpenalgoUsername = "quiet", Broker = "kite", IsActive = true, NotificationsEnabled = false });
            await db.SaveChangesAsync();
        }

        await Send(client, HttpMethod.Post, "/api/v1/telegram/config", new { token = "111:AAA" }, key);
        var (status, body) = await Send(client, HttpMethod.Post, "/api/v1/telegram/notify", new { username = "trader", message = "hello" }, key);
        Assert.Equal(409, status);
        Assert.Contains("Telegram bot is stopped", body["message"]!.GetValue<string>());

        await Send(client, HttpMethod.Post, "/api/v1/telegram/start", new { }, key);
        Assert.Equal(404, (await Send(client, HttpMethod.Post, "/api/v1/telegram/notify", new { username = "nobody", message = "hello" }, key)).Status);
        Assert.Equal(400, (await Send(client, HttpMethod.Post, "/api/v1/telegram/notify", new { username = "trader" }, key)).Status);

        (status, body) = await Send(client, HttpMethod.Post, "/api/v1/telegram/notify", new { username = "trader", message = "NIFTY crossed 24000", wait_for_delivery = true }, key);
        Assert.Equal(200, status);
        Assert.Equal("Notification sent successfully", body["message"]!.GetValue<string>());
        var call = Assert.Single(telegram.Calls, c => c.Path.EndsWith("/sendMessage", StringComparison.Ordinal));
        Assert.Contains("\"chat_id\":777", call.Body);
        Assert.Contains("NIFTY crossed 24000", call.Body);
        Assert.Contains("/bot111:AAA/", call.Path);

        var users = await Send(client, HttpMethod.Get, "/api/v1/telegram/users?notifications_enabled=true", key: key);
        Assert.Equal(1, users.Body["count"]!.GetValue<int>());
        Assert.Equal("trader", users.Body["data"]![0]!["openalgo_username"]!.GetValue<string>());
        Assert.Equal(2, (await Send(client, HttpMethod.Get, "/api/v1/telegram/users", key: key)).Body["count"]!.GetValue<int>());
    }

    [PostgresFact]
    public async Task A_broadcast_reaches_everyone_who_wants_messages_and_respects_the_switch()
    {
        var (host, key, telegram, _) = await HostAsync();
        await using var _ = host;
        using var client = host.Anonymous();
        await using (var db = host.NewDb())
        {
            for (var i = 1; i <= 3; i++) db.Set<TelegramUser>().Add(new TelegramUser { TelegramId = i, OpenalgoUsername = $"u{i}", IsActive = true, NotificationsEnabled = i != 3 });
            await db.SaveChangesAsync();
        }

        await Send(client, HttpMethod.Post, "/api/v1/telegram/config", new { token = "111:AAA" }, key);
        Assert.Equal(400, (await Send(client, HttpMethod.Post, "/api/v1/telegram/broadcast", new { }, key)).Status);
        Assert.Equal(400, (await Send(client, HttpMethod.Post, "/api/v1/telegram/broadcast", new { message = new string('x', 4097) }, key)).Status);

        var (status, body) = await Send(client, HttpMethod.Post, "/api/v1/telegram/broadcast", new { message = "market closes early" }, key);
        Assert.Equal(200, status);
        Assert.Equal(2, body["success_count"]!.GetValue<int>());
        Assert.Equal(2, telegram.Calls.Count(c => c.Path.EndsWith("/sendMessage", StringComparison.Ordinal)));

        await Send(client, HttpMethod.Post, "/api/v1/telegram/config", new { broadcast_enabled = false }, key);
        Assert.Equal(403, (await Send(client, HttpMethod.Post, "/api/v1/telegram/broadcast", new { message = "again" }, key)).Status);
    }

    [PostgresFact]
    public async Task Preferences_default_then_change_and_stats_count_commands()
    {
        var (host, key, _, _) = await HostAsync();
        await using var _ = host;
        using var client = host.Anonymous();
        await using (var db = host.NewDb())
        {
            db.Set<TelegramUser>().Add(new TelegramUser { TelegramId = 5, OpenalgoUsername = "trader", TelegramUsername = "asha", IsActive = true });
            foreach (var command in new[] { "/orders", "/orders", "/positions" }) db.Set<TelegramCommandLog>().Add(new TelegramCommandLog { TelegramId = 5, Command = command, ExecutedAt = DateTime.UtcNow.AddHours(-1) });
            db.Set<TelegramCommandLog>().Add(new TelegramCommandLog { TelegramId = 5, Command = "/old", ExecutedAt = DateTime.UtcNow.AddDays(-30) });
            await db.SaveChangesAsync();
        }

        Assert.Equal(400, (await Send(client, HttpMethod.Get, "/api/v1/telegram/preferences", key: key)).Status);
        var defaults = (await Send(client, HttpMethod.Get, "/api/v1/telegram/preferences?telegram_id=5", key: key)).Body["data"]!;
        Assert.Equal("18:00", defaults["summary_time"]!.GetValue<string>());
        Assert.True(defaults["order_notifications"]!.GetValue<bool>());

        Assert.Equal(200, (await Send(client, HttpMethod.Post, "/api/v1/telegram/preferences", new { telegram_id = 5, order_notifications = false, summary_time = "20:15" }, key)).Status);
        var changed = (await Send(client, HttpMethod.Get, "/api/v1/telegram/preferences?telegram_id=5", key: key)).Body["data"]!;
        Assert.False(changed["order_notifications"]!.GetValue<bool>());
        Assert.Equal("20:15", changed["summary_time"]!.GetValue<string>());
        Assert.True(changed["pnl_notifications"]!.GetValue<bool>());

        var stats = (await Send(client, HttpMethod.Get, "/api/v1/telegram/stats?days=7", key: key)).Body["data"]!;
        Assert.Equal(3, stats["total_commands"]!.GetValue<int>());
        Assert.Equal(2, stats["commands_by_type"]!["/orders"]!.GetValue<int>());
        Assert.Equal(1, stats["active_users"]!.GetValue<int>());
        Assert.Equal("asha", stats["top_users"]![0]![0]!.GetValue<string>());
        Assert.Equal(4, (await Send(client, HttpMethod.Get, "/api/v1/telegram/stats?days=365", key: key)).Body["data"]!["total_commands"]!.GetValue<int>());
    }

    [PostgresFact]
    public async Task The_webhook_accepts_only_calls_carrying_the_secret_telegram_was_given()
    {
        var (host, key, _, _) = await HostAsync();
        await using var _ = host;
        using var client = host.Anonymous();
        await Send(client, HttpMethod.Post, "/api/v1/telegram/config", new { token = "111:AAA" }, key);
        var secret = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("111:AAA")))[..32];

        async Task<int> Call(string? header, object body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/telegram/webhook") { Content = JsonContent.Create(body) };
            if (header is not null) request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", header);
            return (int)(await client.SendAsync(request)).StatusCode;
        }

        Assert.Equal(401, await Call(null, new { update_id = 1 }));
        Assert.Equal(403, await Call("wrong", new { update_id = 1 }));
        Assert.Equal(200, await Call(secret, new { update_id = 1 }));
        Assert.Equal(400, await Call(secret, new { message = "no id" }));
    }

    [PostgresFact]
    public async Task WhatsApp_refuses_a_send_when_no_device_is_paired()
    {
        var (host, key, _, _) = await HostAsync();
        await using var _ = host;
        using var client = host.Anonymous();
        Assert.Equal(401, (await Send(client, HttpMethod.Post, "/api/v1/whatsapp/notify", new { message = "hi", self = true }, "wrong")).Status);
        var (status, body) = await Send(client, HttpMethod.Post, "/api/v1/whatsapp/notify", new { message = "hi", self = true }, key);
        Assert.Equal(409, status);
        Assert.Contains("not paired", body["message"]!.GetValue<string>());
    }

    [PostgresFact]
    public async Task WhatsApp_routes_a_send_by_self_phone_list_or_linked_name_and_reports_delivery()
    {
        var (host, key, _, device) = await HostAsync(paired: true);
        await using var _ = host;
        using var client = host.Anonymous();
        await using (var db = host.NewDb())
        {
            db.Set<WhatsappUser>().Add(new WhatsappUser { WhatsappJid = "919800000001@s.whatsapp.net", PhoneNumber = "919800000001", OpenalgoUsername = "linked", IsActive = true });
            db.Set<WhatsappConfig>().Add(new WhatsappConfig { Id = 1, OwnerUsername = "Operator", IsPaired = true });
            await db.SaveChangesAsync();
        }

        async Task<(int, JsonObject)> Notify(object body) => await Send(client, HttpMethod.Post, "/api/v1/whatsapp/notify", body, key);

        Assert.Equal(400, (await Notify(new { self = true })).Item1);
        Assert.Equal(400, (await Notify(new { message = new string('x', 4097), self = true })).Item1);
        Assert.Equal(400, (await Notify(new { message = "hi" })).Item1);
        Assert.Equal(400, (await Notify(new { message = "hi", phone = "12" })).Item1);
        Assert.Equal(400, (await Notify(new { message = "hi", phones = "notalist" })).Item1);
        Assert.Equal(400, (await Notify(new { message = "hi", phones = new[] { "x", "y" } })).Item1);
        Assert.Equal(400, (await Notify(new { message = "hi", self = true, image_path = "/etc/passwd" })).Item1);
        Assert.Equal(404, (await Notify(new { message = "hi", username = "stranger" })).Item1);

        var (status, body) = await Notify(new { message = "hello", phone = "+91 98000 00002" });
        Assert.Equal(200, status);
        Assert.Equal("Delivered to 1, failed 0", body["message"]!.GetValue<string>());
        Assert.Equal(["919800000002@s.whatsapp.net"], device!.Sent[^1].To);

        await Notify(new { message = "many", phones = new[] { "919800000003", "919800000004", "919800000005", "919800000006", "919800000007", "919800000008" } });
        Assert.Equal(5, device.Sent[^1].To.Count);

        await Notify(new { message = "by name", username = "linked" });
        Assert.Equal(["919800000001@s.whatsapp.net"], device.Sent[^1].To);

        // The operator who paired the device is itself, even if never linked.
        await Notify(new { message = "to me", username = "operator" });
        Assert.Empty(device.Sent[^1].To);

        device.Fail = true;
        (status, body) = await Notify(new { message = "nobody home", phone = "919800000009" });
        Assert.Equal(200, status);
        Assert.Equal("error", body["status"]!.GetValue<string>());
        Assert.Equal("The number is not on WhatsApp.", body["message"]!.GetValue<string>());

        device.Fail = false;
        (status, body) = await Notify(new { message = "later", phone = "919800000010", wait_for_delivery = false });
        Assert.Equal(200, status);
        Assert.Equal("Queued for 1 recipient(s)", body["message"]!.GetValue<string>());
    }

    [PostgresFact]
    public async Task WhatsApp_attachments_must_be_real_files_inside_the_allowed_folder()
    {
        var allowed = Directory.CreateTempSubdirectory("wa-allowed").FullName;
        var elsewhere = Directory.CreateTempSubdirectory("wa-elsewhere").FullName;
        var inside = Path.Combine(allowed, "chart.png");
        var outside = Path.Combine(elsewhere, "chart.png");
        await File.WriteAllBytesAsync(inside, [1, 2, 3]);
        await File.WriteAllBytesAsync(outside, [1, 2, 3]);
        var (host, key, _, device) = await HostAsync(paired: true, attachmentRoot: allowed);
        await using var _ = host;
        using var client = host.Anonymous();

        async Task<int> Notify(object body) => (await Send(client, HttpMethod.Post, "/api/v1/whatsapp/notify", body, key)).Status;

        Assert.Equal(400, await Notify(new { self = true, image_path = outside }));
        Assert.Equal(400, await Notify(new { self = true, image_path = Path.Combine(allowed, "missing.png") }));
        Assert.Equal(400, await Notify(new { self = true, document_path = Path.Combine(allowed, "..", "wa-elsewhere", "chart.png") }));
        Assert.Equal(400, await Notify(new { self = true, image_path = "chart.png" }));
        Assert.Equal(200, await Notify(new { self = true, image_path = inside, caption = "levels" }));
        Assert.Single(device!.Sent);
    }
}
