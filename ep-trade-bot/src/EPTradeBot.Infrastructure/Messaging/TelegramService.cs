using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Entities.Legacy.Audit;
using EPTradeBot.Core.Entities.Legacy.Core;
using EPTradeBot.Infrastructure.Persistence;
using EPTradeBot.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EPTradeBot.Infrastructure.Messaging;

/// <summary>
/// The Telegram side of the platform: the bot's saved settings, the people linked to it, their preferences, usage counts, and sending through Telegram's Bot API.
/// Alerts and notifications go out over HTTP; the interactive command bot (a person typing commands in the chat) is not part of EP Trade bot yet.
/// </summary>
public sealed class TelegramService(IDbContextFactory<EpTradeDbContext> contexts, TokenProtector protector, IHttpClientFactory http, TimeProvider clock, IConfiguration configuration, ILogger<TelegramService> logger)
{
    private const string TokenField = "telegram.bot_token";
    public const int MaxMessageLength = 4096;

    /// <summary>A date the way OpenAlgo's JSON writes one: RFC 1123, in GMT.</summary>
    public static string HttpDate(DateTime? value) => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc).ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", CultureInfo.InvariantCulture) : "";

    private static JsonNode? DateNode(DateTime? value) => value is null ? null : HttpDate(value);

    // ---- settings --------------------------------------------------------------------------------------------

    public async Task<TelegramBotConfig?> LoadConfigAsync(EpTradeDbContext db, CancellationToken ct) => await db.Set<TelegramBotConfig>().FirstOrDefaultAsync(c => c.Id == 1, ct);

    public string? Token(TelegramBotConfig? config) => config is null ? null : protector.Unprotect(Guid.Empty, TokenField, config.Token);

    /// <summary>The settings as the API shows them. The bot token is never returned in full.</summary>
    public async Task<JsonObject> GetConfigAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var config = await LoadConfigAsync(db, ct);
        var token = Token(config);
        var masked = token is null ? null : token.Length > 10 ? token[..10] + "..." : token;
        return new JsonObject
        {
            ["bot_token"] = masked, ["token"] = masked, ["is_active"] = config?.IsActive ?? false, ["bot_username"] = config?.BotUsername,
            ["max_message_length"] = config?.MaxMessageLength ?? MaxMessageLength, ["rate_limit_per_minute"] = config?.RateLimitPerMinute ?? 30, ["broadcast_enabled"] = config?.BroadcastEnabled ?? true,
            ["created_at"] = DateNode(config?.CreatedAt), ["updated_at"] = DateNode(config?.UpdatedAt),
        };
    }

    public async Task<string?> StoredTokenAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return Token(await LoadConfigAsync(db, ct));
    }

    public async Task<bool> IsActiveAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return (await LoadConfigAsync(db, ct))?.IsActive ?? false;
    }

    public async Task<bool> BroadcastEnabledAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return (await LoadConfigAsync(db, ct))?.BroadcastEnabled ?? true;
    }

    /// <summary>Saves the fields given. Nothing is written when every field already holds the value.</summary>
    public async Task UpdateConfigAsync(string? token, bool tokenGiven, string? botUsername, bool? broadcastEnabled, int? rateLimit, bool? isActive, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var config = await LoadConfigAsync(db, ct);
        var changed = config is null;
        if (config is null) { config = new TelegramBotConfig { Id = 1 }; db.Add(config); }
        if (tokenGiven && Token(config) != token)
        {
            config.Token = string.IsNullOrEmpty(token) ? null : protector.Protect(Guid.Empty, TokenField, token);
            changed = true;
        }

        if (botUsername is not null && config.BotUsername != botUsername) { config.BotUsername = botUsername; changed = true; }
        if (broadcastEnabled is { } b && config.BroadcastEnabled != b) { config.BroadcastEnabled = b; changed = true; }
        if (rateLimit is { } r && config.RateLimitPerMinute != r) { config.RateLimitPerMinute = r; changed = true; }
        if (isActive is { } a && config.IsActive != a) { config.IsActive = a; changed = true; }
        if (!changed) return;
        config.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    // ---- the bot ---------------------------------------------------------------------------------------------

    private HttpClient Client() => http.CreateClient("telegram");

    /// <summary>Asks Telegram who the token belongs to, and keeps the token and the bot's name when it is accepted.</summary>
    public async Task<(bool Ok, string Message)> InitializeAsync(string token, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await Client().GetAsync($"https://api.telegram.org/bot{token}/getMe", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Telegram token check could not reach Telegram");
            await UpdateConfigAsync(token, true, null, null, null, null, ct);
            return (true, "Token stored (will validate on start)");
        }

        using (response)
        {
            JsonNode? data = null;
            if (response.StatusCode == HttpStatusCode.OK)
            {
                try { data = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)); } catch (System.Text.Json.JsonException) { }
            }

            if (response.StatusCode == HttpStatusCode.OK && data?["ok"]?.GetValue<bool>() == true)
            {
                var username = data["result"]?["username"]?.GetValue<string>() ?? "unknown";
                await UpdateConfigAsync(token, true, username, null, null, null, ct);
                return (true, $"Bot initialized successfully: @{username}");
            }

            return response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound
                ? (false, "Telegram did not accept this bot token. Copy it again from BotFather and save it.")
                : (false, "Telegram did not confirm the bot token. Try again in a few minutes, and if it keeps failing, copy the token again from BotFather.");
        }
    }

    public async Task<(bool Ok, string Message)> StartAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (string.IsNullOrEmpty(Token(await LoadConfigAsync(db, ct)))) return (false, "Bot token not configured");
        await UpdateConfigAsync(null, false, null, null, null, true, ct);
        return (true, "Bot started successfully");
    }

    public async Task<(bool Ok, string Message)> StopAsync(CancellationToken ct)
    {
        if (!await IsActiveAsync(ct)) return (false, "Bot is not running");
        await UpdateConfigAsync(null, false, null, null, null, false, ct);
        return (true, "Bot stopped successfully");
    }

    /// <summary>Sends one message to a chat. False when Telegram refuses or cannot be reached.</summary>
    public async Task<bool> SendAsync(long telegramId, string text, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var token = Token(await LoadConfigAsync(db, ct));
        if (string.IsNullOrEmpty(token)) return false;
        try
        {
            using var body = new StringContent(new JsonObject { ["chat_id"] = telegramId, ["text"] = text }.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await Client().PostAsync($"https://api.telegram.org/bot{token}/sendMessage", body, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Telegram message to {TelegramId} was not delivered", telegramId);
            return false;
        }
    }

    // ---- the webhook -----------------------------------------------------------------------------------------

    /// <summary>The secret Telegram echoes in a header on every webhook call: the configured one, else a digest of the bot token. Null when there is neither.</summary>
    public async Task<string?> WebhookSecretAsync(CancellationToken ct)
    {
        if (configuration["Telegram:WebhookSecret"] is { Length: > 0 } configured) return configured;
        await using var db = await contexts.CreateDbContextAsync(ct);
        return Token(await LoadConfigAsync(db, ct)) is { Length: > 0 } token ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..32] : null;
    }

    // ---- people ----------------------------------------------------------------------------------------------

    private static JsonObject UserRow(TelegramUser u) => new()
    {
        ["id"] = u.Id, ["telegram_id"] = u.TelegramId, ["openalgo_username"] = u.OpenalgoUsername, ["first_name"] = u.FirstName, ["last_name"] = u.LastName, ["telegram_username"] = u.TelegramUsername,
        ["broker"] = u.Broker, ["notifications_enabled"] = u.NotificationsEnabled, ["created_at"] = DateNode(u.CreatedAt), ["last_command_at"] = DateNode(u.LastCommandAt),
    };

    public async Task<JsonArray> UsersAsync(string? broker, bool? notificationsEnabled, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var query = db.Set<TelegramUser>().AsNoTracking().Where(u => u.IsActive == true);
        if (broker is not null) query = query.Where(u => u.Broker == broker);
        if (notificationsEnabled is { } n) query = query.Where(u => u.NotificationsEnabled == n);
        return new JsonArray((await query.OrderBy(u => u.Id).ToListAsync(ct)).Select(u => (JsonNode)UserRow(u)).ToArray());
    }

    public async Task<TelegramUser?> UserByUsernameAsync(string username, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Set<TelegramUser>().AsNoTracking().Where(u => u.OpenalgoUsername == username && u.IsActive == true).FirstOrDefaultAsync(ct);
    }

    public async Task<(int Sent, int Failed)> BroadcastAsync(string text, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var ids = await db.Set<TelegramUser>().AsNoTracking().Where(u => u.IsActive == true && u.NotificationsEnabled == true).Select(u => u.TelegramId).ToListAsync(ct);
        int sent = 0, failed = 0;
        foreach (var id in ids)
        {
            if (await SendAsync(id, text, ct)) sent++; else failed++;
        }

        return (sent, failed);
    }

    // ---- usage and preferences ------------------------------------------------------------------------------

    public async Task<JsonObject> StatsAsync(int days, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var since = clock.GetUtcNow().UtcDateTime.AddDays(-days);
        var logs = await db.Set<TelegramCommandLog>().AsNoTracking().Where(l => l.ExecutedAt >= since).Select(l => new { l.Command, l.TelegramId }).ToListAsync(ct);
        var byType = logs.GroupBy(l => l.Command).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal);
        var names = await db.Set<TelegramUser>().AsNoTracking().Select(u => new { u.TelegramId, u.TelegramUsername }).ToListAsync(ct);
        var top = logs.Join(names, l => l.TelegramId, n => n.TelegramId, (l, n) => n.TelegramUsername).GroupBy(n => n).OrderByDescending(g => g.Count()).Take(10);
        return new JsonObject
        {
            ["total_commands"] = logs.Count, ["commands_by_type"] = new JsonObject(byType.Select(g => new KeyValuePair<string, JsonNode?>(g.Key, g.Count()))),
            ["active_users"] = logs.Select(l => l.TelegramId).Distinct().Count(), ["top_users"] = new JsonArray(top.Select(g => (JsonNode)new JsonArray(g.Key, g.Count())).ToArray()), ["period_days"] = days,
        };
    }

    public async Task<JsonObject> PreferencesAsync(int telegramId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var p = await db.Set<TelegramUserPreference>().AsNoTracking().FirstOrDefaultAsync(x => x.TelegramId == telegramId, ct);
        return new JsonObject
        {
            ["order_notifications"] = p?.OrderNotifications ?? true, ["trade_notifications"] = p?.TradeNotifications ?? true, ["pnl_notifications"] = p?.PnlNotifications ?? true,
            ["daily_summary"] = p?.DailySummary ?? true, ["summary_time"] = p?.SummaryTime ?? "18:00", ["language"] = p?.Language ?? "en", ["timezone"] = p?.Timezone ?? "Asia/Kolkata",
        };
    }

    public async Task UpdatePreferencesAsync(int telegramId, JsonObject changes, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var p = await db.Set<TelegramUserPreference>().FirstOrDefaultAsync(x => x.TelegramId == telegramId, ct);
        if (p is null) { p = new TelegramUserPreference { TelegramId = telegramId }; db.Add(p); }
        bool? Flag(string key) => changes[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
        if (changes.ContainsKey("order_notifications")) p.OrderNotifications = Flag("order_notifications");
        if (changes.ContainsKey("trade_notifications")) p.TradeNotifications = Flag("trade_notifications");
        if (changes.ContainsKey("pnl_notifications")) p.PnlNotifications = Flag("pnl_notifications");
        if (changes.ContainsKey("daily_summary")) p.DailySummary = Flag("daily_summary");
        if (changes.ContainsKey("summary_time")) p.SummaryTime = changes["summary_time"]?.GetValue<string>();
        if (changes.ContainsKey("language")) p.Language = changes["language"]?.GetValue<string>();
        if (changes.ContainsKey("timezone")) p.Timezone = changes["timezone"]?.GetValue<string>();
        p.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }
}
