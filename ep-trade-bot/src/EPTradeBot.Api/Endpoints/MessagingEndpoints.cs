using System.Text.Json.Nodes;
using EPTradeBot.Infrastructure.Messaging;
using EPTradeBot.Infrastructure.Security;

namespace EPTradeBot.Api.Endpoints;

/// <summary>/api/v1/telegram/* and /api/v1/whatsapp/notify, with OpenAlgo's paths, bodies and answers. Their refusals are 401 for a bad key (the rest of /api/v1 answers 403).</summary>
internal static class MessagingEndpoints
{
    private const string BadKey = "Invalid or missing API key";

    public static readonly string[] Paths = ["/telegram/config", "/telegram/start", "/telegram/stop", "/telegram/webhook", "/telegram/users", "/telegram/broadcast", "/telegram/notify", "/telegram/stats", "/telegram/preferences", "/whatsapp/notify"];

    private static Task Reply(HttpContext c, int status, JsonObject body)
    {
        c.Response.StatusCode = status;
        c.Response.ContentType = "application/json";
        return c.Response.WriteAsync(body.ToJsonString(), c.RequestAborted);
    }

    private static Task Error(HttpContext c, int status, string message) => Reply(c, status, new JsonObject { ["status"] = "error", ["message"] = message });

    private static async Task<JsonObject> BodyAsync(HttpContext c)
    {
        if (c.Request.ContentLength is 0 || !(c.Request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) ?? false)) return [];
        try { return await JsonNode.ParseAsync(c.Request.Body, cancellationToken: c.RequestAborted) as JsonObject ?? []; }
        catch (System.Text.Json.JsonException) { return []; }
    }

    /// <summary>The key from the body, the X-API-KEY header or the query string, in that order.</summary>
    private static async Task<bool> KeyedAsync(HttpContext c, JsonObject body)
    {
        var key = (body["apikey"] as JsonValue)?.TryGetValue<string>(out var b) == true && b.Length > 0 ? b : c.Request.Headers["X-API-KEY"].FirstOrDefault() ?? c.Request.Query["apikey"].FirstOrDefault();
        if (string.IsNullOrEmpty(key)) return false;
        return await c.RequestServices.GetRequiredService<ApiKeyService>().ResolveCallAsync(key, c.RequestAborted) is not null;
    }

    private static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static RouteGroupBuilder MapMessaging(this RouteGroupBuilder v1)
    {
        v1.MapGet("/telegram/config", async (HttpContext c) =>
        {
            if (!await KeyedAsync(c, [])) { await Error(c, 401, BadKey); return; }
            await Reply(c, 200, new JsonObject { ["status"] = "success", ["data"] = await c.RequestServices.GetRequiredService<TelegramService>().GetConfigAsync(c.RequestAborted) });
        });

        v1.MapPost("/telegram/config", async (HttpContext c) =>
        {
            var body = await BodyAsync(c);
            if (!await KeyedAsync(c, body)) { await Error(c, 401, BadKey); return; }
            int? rate = null;
            if (body.ContainsKey("rate_limit_per_minute"))
            {
                var raw = body["rate_limit_per_minute"];
                if (raw is not JsonValue rv || !(rv.TryGetValue<int>(out var n) || (rv.TryGetValue<string>(out var s) && int.TryParse(s, out n)) || (rv.TryGetValue<long>(out var l) && (n = (int)l) == l)))
                { await Error(c, 400, "rate_limit_per_minute must be an integer"); return; }
                if (n is < 1 or > 120) { await Error(c, 400, "rate_limit_per_minute must be between 1 and 120"); return; }
                rate = n;
            }

            var tokenGiven = body.ContainsKey("token");
            bool? broadcast = body["broadcast_enabled"] is JsonValue bv && bv.TryGetValue<bool>(out var bb) ? bb : null;
            await c.RequestServices.GetRequiredService<TelegramService>().UpdateConfigAsync(Text(body["token"]), tokenGiven, null, broadcast, rate, null, c.RequestAborted);
            await Reply(c, 200, new JsonObject { ["status"] = "success", ["message"] = "Bot configuration updated" });
        });

        v1.MapPost("/telegram/start", async (HttpContext c) =>
        {
            var body = await BodyAsync(c);
            if (!await KeyedAsync(c, body)) { await Error(c, 401, BadKey); return; }
            var telegram = c.RequestServices.GetRequiredService<TelegramService>();
            var token = await telegram.StoredTokenAsync(c.RequestAborted);
            if (string.IsNullOrEmpty(token)) { await Error(c, 400, "Bot token not configured"); return; }
            var (ok, message) = await telegram.InitializeAsync(token, c.RequestAborted);
            if (!ok) { await Error(c, 500, message); return; }
            (ok, message) = await telegram.StartAsync(c.RequestAborted);
            if (ok) await Reply(c, 200, new JsonObject { ["status"] = "success", ["message"] = message });
            else await Error(c, 500, message);
        });

        v1.MapPost("/telegram/stop", async (HttpContext c) =>
        {
            var body = await BodyAsync(c);
            if (!await KeyedAsync(c, body)) { await Error(c, 401, BadKey); return; }
            var (ok, message) = await c.RequestServices.GetRequiredService<TelegramService>().StopAsync(c.RequestAborted);
            if (ok) await Reply(c, 200, new JsonObject { ["status"] = "success", ["message"] = message });
            else await Error(c, 500, message);
        });

        v1.MapPost("/telegram/webhook", async (HttpContext c) =>
        {
            var telegram = c.RequestServices.GetRequiredService<TelegramService>();
            var expected = await telegram.WebhookSecretAsync(c.RequestAborted);
            if (expected is not null)
            {
                var received = c.Request.Headers["X-Telegram-Bot-Api-Secret-Token"].FirstOrDefault();
                if (string.IsNullOrEmpty(received)) { c.Response.StatusCode = 401; await c.Response.WriteAsync("Unauthorized", c.RequestAborted); return; }
                if (!string.Equals(received, expected, StringComparison.Ordinal)) { c.Response.StatusCode = 403; await c.Response.WriteAsync("Forbidden", c.RequestAborted); return; }
            }

            var update = await BodyAsync(c);
            if (update.Count == 0) { c.Response.StatusCode = 200; return; }
            if (!update.ContainsKey("update_id")) { c.Response.StatusCode = 400; await c.Response.WriteAsync("Bad Request", c.RequestAborted); return; }
            c.Response.StatusCode = 200;
        });

        v1.MapGet("/telegram/users", async (HttpContext c) =>
        {
            if (!await KeyedAsync(c, [])) { await Error(c, 401, BadKey); return; }
            var broker = c.Request.Query["broker"].FirstOrDefault();
            var flag = c.Request.Query["notifications_enabled"].FirstOrDefault();
            var users = await c.RequestServices.GetRequiredService<TelegramService>().UsersAsync(string.IsNullOrEmpty(broker) ? null : broker, string.IsNullOrEmpty(flag) ? null : flag.Equals("true", StringComparison.OrdinalIgnoreCase), c.RequestAborted);
            await Reply(c, 200, new JsonObject { ["status"] = "success", ["data"] = users, ["count"] = users.Count });
        });

        v1.MapPost("/telegram/broadcast", async (HttpContext c) =>
        {
            var body = await BodyAsync(c);
            if (!await KeyedAsync(c, body)) { await Error(c, 401, BadKey); return; }
            var message = Text(body["message"]);
            if (string.IsNullOrEmpty(message)) { await Error(c, 400, "Message is required"); return; }
            if (message.Length > TelegramService.MaxMessageLength) { await Error(c, 400, "Message must not exceed 4096 characters"); return; }
            var telegram = c.RequestServices.GetRequiredService<TelegramService>();
            if (!await telegram.BroadcastEnabledAsync(c.RequestAborted)) { await Error(c, 403, "Broadcast is disabled"); return; }
            var (sent, failed) = await telegram.BroadcastAsync(message, c.RequestAborted);
            await Reply(c, 200, new JsonObject { ["status"] = "success", ["message"] = $"Broadcast sent to {sent} users, failed for {failed} users", ["success_count"] = sent, ["fail_count"] = failed });
        });

        v1.MapPost("/telegram/notify", async (HttpContext c) =>
        {
            var body = await BodyAsync(c);
            if (!await KeyedAsync(c, body)) { await Error(c, 401, BadKey); return; }
            var telegram = c.RequestServices.GetRequiredService<TelegramService>();
            // Stopping the bot stops automated messages too.
            if (!await telegram.IsActiveAsync(c.RequestAborted)) { await Error(c, 409, "Telegram bot is stopped. Start the bot to send notifications."); return; }
            var username = Text(body["username"]);
            var message = Text(body["message"]);
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(message)) { await Error(c, 400, "Username and message are required"); return; }
            var user = await telegram.UserByUsernameAsync(username, c.RequestAborted);
            if (user is null) { await Error(c, 404, "User not found or not linked to Telegram"); return; }
            var wait = body["wait_for_delivery"] is JsonValue w && w.TryGetValue<bool>(out var waitFlag) && waitFlag;
            if (wait) await SendNow(telegram, user.TelegramId, message, c.RequestAborted);
            else _ = SendNow(telegram, user.TelegramId, message, CancellationToken.None);
            await Reply(c, 200, new JsonObject { ["status"] = "success", ["message"] = wait ? "Notification sent successfully" : "Notification queued for delivery" });
        });

        v1.MapGet("/telegram/stats", async (HttpContext c) =>
        {
            if (!await KeyedAsync(c, [])) { await Error(c, 401, BadKey); return; }
            var days = int.TryParse(c.Request.Query["days"].FirstOrDefault(), out var d) ? Math.Min(Math.Max(d, 1), 365) : 7;
            await Reply(c, 200, new JsonObject { ["status"] = "success", ["data"] = await c.RequestServices.GetRequiredService<TelegramService>().StatsAsync(days, c.RequestAborted) });
        });

        v1.MapGet("/telegram/preferences", async (HttpContext c) =>
        {
            if (!await KeyedAsync(c, [])) { await Error(c, 401, BadKey); return; }
            if (!int.TryParse(c.Request.Query["telegram_id"].FirstOrDefault(), out var id) || id == 0) { await Error(c, 400, "telegram_id is required"); return; }
            await Reply(c, 200, new JsonObject { ["status"] = "success", ["data"] = await c.RequestServices.GetRequiredService<TelegramService>().PreferencesAsync(id, c.RequestAborted) });
        });

        v1.MapPost("/telegram/preferences", async (HttpContext c) =>
        {
            var body = await BodyAsync(c);
            if (!await KeyedAsync(c, body)) { await Error(c, 401, BadKey); return; }
            var id = body["telegram_id"] is JsonValue iv && (iv.TryGetValue<int>(out var n) || (iv.TryGetValue<string>(out var s) && int.TryParse(s, out n))) ? n : 0;
            if (id == 0) { await Error(c, 400, "telegram_id is required"); return; }
            var changes = new JsonObject();
            foreach (var key in new[] { "order_notifications", "trade_notifications", "pnl_notifications", "daily_summary", "summary_time", "language", "timezone" })
                if (body.ContainsKey(key)) changes[key] = body[key]?.DeepClone();
            await c.RequestServices.GetRequiredService<TelegramService>().UpdatePreferencesAsync(id, changes, c.RequestAborted);
            await Reply(c, 200, new JsonObject { ["status"] = "success", ["message"] = "Preferences updated successfully" });
        });

        v1.MapPost("/whatsapp/notify", WhatsAppNotify);
        return v1;
    }

    private static async Task SendNow(TelegramService telegram, long telegramId, string message, CancellationToken ct) => await telegram.SendAsync(telegramId, message, ct);

    private static async Task WhatsAppNotify(HttpContext c)
    {
        var body = await BodyAsync(c);
        if (!await KeyedAsync(c, body)) { await Error(c, 401, BadKey); return; }
        var whatsapp = c.RequestServices.GetRequiredService<WhatsAppService>();
        var transport = whatsapp.Transport;
        // A device that is not paired refuses the send outright: queueing it would hide that alerts never go out.
        if (!transport.IsReady)
        {
            await Error(c, 409, transport.UnavailableReason ?? "WhatsApp is not paired or not connected. Pair the device first from the WhatsApp page before sending.");
            return;
        }

        var message = Text(body["message"]);
        if (message is { Length: > 4096 }) { await Error(c, 400, "Message must not exceed 4096 characters"); return; }
        var rawImage = Text(body["image_path"]);
        var rawDocument = Text(body["document_path"]);
        var caption = Text(body["caption"]);
        var filename = Text(body["filename"]);
        var wait = !(body["wait_for_delivery"] is JsonValue w && w.TryGetValue<bool>(out var wf) && !wf);
        if (string.IsNullOrEmpty(message) && string.IsNullOrEmpty(rawImage) && string.IsNullOrEmpty(rawDocument)) { await Error(c, 400, "Provide at least one of: message, image_path, document_path"); return; }
        var image = whatsapp.ValidateAttachmentPath(rawImage);
        var document = whatsapp.ValidateAttachmentPath(rawDocument);
        if (!string.IsNullOrEmpty(rawImage) && image is null) { await Error(c, 400, "image_path is not allowed"); return; }
        if (!string.IsNullOrEmpty(rawDocument) && document is null) { await Error(c, 400, "document_path is not allowed"); return; }

        var targets = new List<string>();
        var self = body["self"] is JsonValue sv && sv.TryGetValue<bool>(out var sf) && sf;
        if (self)
        {
            // An empty list is the paired device's own number.
        }
        else if (body["phones"] is { } phones && !(phones is JsonValue pv && pv.TryGetValue<bool>(out var pb) && !pb) && !(phones is JsonArray { Count: 0 }))
        {
            if (phones is not JsonArray list) { await Error(c, 400, "'phones' must be a list"); return; }
            foreach (var p in list.Take(5)) { var digits = WhatsAppService.NormalisePhone(p); if (digits.Length > 0) targets.Add(WhatsAppService.PhoneToJid(digits)); }
            if (targets.Count == 0) { await Error(c, 400, "No valid phones in list"); return; }
        }
        else if (body["phone"] is { } phone && Text(phone) != "")
        {
            var digits = WhatsAppService.NormalisePhone(phone);
            if (digits.Length == 0) { await Error(c, 400, "Invalid phone number"); return; }
            targets.Add(WhatsAppService.PhoneToJid(digits));
        }
        else if (Text(body["username"]) is { Length: > 0 } username)
        {
            if (await whatsapp.JidForUsernameAsync(username, c.RequestAborted) is { } jid) targets.Add(jid);
            else if (!await whatsapp.IsPairedOwnerAsync(username, c.RequestAborted)) { await Error(c, 404, "Username not found or not linked to WhatsApp"); return; }
        }
        else
        {
            await Error(c, 400, "Specify one of: 'self', 'username', 'phone', or 'phones'");
            return;
        }

        if (!wait)
        {
            var count = Math.Max(targets.Count, 1);
            _ = Task.Run(() => transport.SendAsync(targets, message, image, document, caption, filename, CancellationToken.None));
            await Reply(c, 200, new JsonObject { ["status"] = "success", ["message"] = $"Queued for {count} recipient(s)", ["queued"] = count });
            return;
        }

        var report = await transport.SendAsync(targets, message, image, document, caption, filename, c.RequestAborted);
        var data = new JsonObject
        {
            ["sent"] = new JsonArray(report.Sent.Select(s => (JsonNode)s).ToArray()),
            ["failed"] = new JsonArray(report.Failed.Select(f => (JsonNode)new JsonObject { ["recipient"] = f.Recipient, ["error"] = f.Error }).ToArray()),
        };
        if (report.Sent.Count == 0 && report.Failed.Count > 0)
        {
            // A send that reached nobody is an error in the body; the HTTP code stays 200 for callers that already depend on it.
            await Reply(c, 200, new JsonObject { ["status"] = "error", ["message"] = report.Failed[0].Error is { Length: > 0 } e ? e : "The message could not be delivered.", ["data"] = data });
            return;
        }

        await Reply(c, 200, new JsonObject { ["status"] = "success", ["message"] = $"Delivered to {report.Sent.Count}, failed {report.Failed.Count}", ["data"] = data });
    }
}
