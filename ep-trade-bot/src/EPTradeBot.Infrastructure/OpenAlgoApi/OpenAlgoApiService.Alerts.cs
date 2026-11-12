using System.Text.Json.Nodes;
using EPTradeBot.Core.Entities.Core;
using Microsoft.Extensions.Logging;

namespace EPTradeBot.Infrastructure.OpenAlgoApi;

/// <summary>The alert calls Flow makes: a Telegram message to the linked person, and a WhatsApp message through the paired device.</summary>
public sealed partial class OpenAlgoApiService
{
    public async Task<V1Result> TelegramAlertAsync(BrokerAccount account, string message, CancellationToken ct)
    {
        // Stopping the bot stops automated messages too.
        if (!await telegram.IsActiveAsync(ct)) return V1Result.Error(409, "Telegram bot is stopped");
        var user = await telegram.UserForAlertAsync(account.DisplayName, ct);
        if (user is null) return V1Result.Error(404, $"No Telegram account linked for user: {account.DisplayName}");
        if (user.NotificationsEnabled != true) return V1Result.Error(403, "Telegram notifications are disabled");
        var stamp = NowIst().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var text = $"*Flow Alert*\n─────────────────────\n{message}\n\nTime: {stamp}";
        // Sent in the background, as the platform does: a slow Telegram must not hold a workflow.
        _ = Task.Run(async () =>
        {
            try { await telegram.SendAsync(user.TelegramId, text, CancellationToken.None, "Markdown"); }
            catch (Exception ex) { logger.LogWarning(ex, "Telegram alert was not delivered"); }
        }, CancellationToken.None);
        return new V1Result(200, new JsonObject { ["status"] = "success", ["data"] = new JsonObject { ["message"] = "Alert queued successfully" } });
    }

    public async Task<V1Result> WhatsAppAlertAsync(BrokerAccount account, string message, string? to, CancellationToken ct)
    {
        _ = account;
        var transport = whatsapp.Transport;
        if (!transport.IsReady) return V1Result.Error(409, transport.UnavailableReason ?? "WhatsApp is not paired or not connected. Pair the device from the WhatsApp page.");
        var targets = new List<string>();
        if (!string.IsNullOrEmpty(to))
        {
            var digits = Messaging.WhatsAppService.NormalisePhone(JsonValue.Create(to));
            if (digits.Length == 0) return V1Result.Error(400, $"Invalid WhatsApp phone number: {to}");
            targets.Add(Messaging.WhatsAppService.PhoneToJid(digits));
        }

        var report = await transport.SendAsync(targets, message, null, null, null, null, ct);
        var data = new JsonObject
        {
            ["sent"] = new JsonArray(report.Sent.Select(s => (JsonNode)s).ToArray()),
            ["failed"] = new JsonArray(report.Failed.Select(f => (JsonNode)new JsonObject { ["recipient"] = f.Recipient, ["error"] = f.Error }).ToArray()),
        };
        if (report.Failed.Count > 0) return new V1Result(502, new JsonObject { ["status"] = "error", ["message"] = report.Failed[0].Error is { Length: > 0 } e ? e : "send failed", ["data"] = data });
        return new V1Result(200, new JsonObject { ["status"] = "success", ["data"] = data });
    }
}
