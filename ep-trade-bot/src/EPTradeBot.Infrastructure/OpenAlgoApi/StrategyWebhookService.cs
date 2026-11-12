using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EPTradeBot.Core.Contracts.V1;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.Legacy.Core;
using EPTradeBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EPTradeBot.Infrastructure.OpenAlgoApi;

/// <summary>
/// The strategy webhook, ported from services/strategy_module/webhook.py: an alert (TradingView cannot set a header) starts or stops a batch strategy by token. The token is the credential, so it is looked up by
/// its SHA-256 digest only, never logged or stored, an unknown and a malformed token answer identically, and every outcome is audited. The stages run in a fixed order: token, lock, allowlist, payload, action,
/// mode, live opt-in, repeat and cooling off, then the engine. Repeats and cooling off are read from the audit and run rows, so they survive a restart and need no memory of their own.
/// </summary>
public sealed partial class StrategyWebhookService(OpenAlgoApiService api, IDbContextFactory<EpTradeDbContext> contexts, TimeProvider clock, ILogger<StrategyWebhookService> logger)
{
    public const string TokenPrefix = "oaws_";
    public const int MaxPayloadBytes = 16384;
    public const int DedupeSeconds = 60;
    public const int CoolingOffSeconds = 30;
    private const string UnknownToken = "Unknown or expired webhook token";

    [GeneratedRegex("^[A-Za-z0-9_-]{16,128}$")]
    private static partial Regex TokenBody();

    private static readonly string[] SecretHints = ["token", "secret", "password", "passwd", "apikey", "api_key", "auth", "signature"];

    public sealed record Outcome(bool Ok, string Result, int Status, string Message, int? StrategyId = null, int? RunId = null)
    {
        public JsonObject Body()
        {
            var body = new JsonObject { ["status"] = Ok ? "success" : "error", ["result"] = Result, ["message"] = Message };
            if (StrategyId is { } s) body["strategy_id"] = s;
            if (RunId is { } r) body["run_id"] = r;
            return body;
        }
    }

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static int StatusOf(string result) => result switch
    {
        "ok" or "rejected_dedupe" => 200,
        "rejected_token" => 404,
        "rejected_locked" or "rejected_ip" or "rejected_live_disabled" => 403,
        "rejected_payload" or "rejected_invalid_action" => 400,
        "rejected_cooling_off" => 409,
        _ => 500,
    };

    private static Outcome Out(string result, string message, bool ok = false, int? strategy = null, int? run = null) => new(ok, result, StatusOf(result), message, strategy, run);

    /// <summary>Whether <paramref name="ip"/> is inside the allowlist; an empty list allows everyone, a listed one is closed (an unreadable address is refused, a malformed entry skipped).</summary>
    public static bool IpAllowed(string? ip, string? allowlist)
    {
        var entries = new List<string>();
        if (!string.IsNullOrWhiteSpace(allowlist))
        {
            try
            {
                if (JsonNode.Parse(allowlist) is JsonArray array) entries = array.Select(a => a?.GetValue<string>()?.Trim()).OfType<string>().Where(a => a.Length > 0).ToList();
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return true; }
        }

        if (entries.Count == 0) return true;
        if (!IPAddress.TryParse(ip?.Trim(), out var address)) return false;
        var candidates = new List<IPAddress> { address };
        if (address.IsIPv4MappedToIPv6) candidates.Add(address.MapToIPv4());
        foreach (var entry in entries)
        {
            var parts = entry.Split('/', 2);
            if (!IPAddress.TryParse(parts[0], out var network)) continue;
            var bits = network.GetAddressBytes().Length * 8;
            var prefix = bits;
            if (parts.Length == 2 && (!int.TryParse(parts[1], out prefix) || prefix < 0 || prefix > bits)) continue;
            foreach (var candidate in candidates)
            {
                if (candidate.AddressFamily == network.AddressFamily && Within(candidate.GetAddressBytes(), network.GetAddressBytes(), prefix)) return true;
            }
        }

        return false;
    }

    private static bool Within(byte[] address, byte[] network, int prefix)
    {
        var full = prefix / 8;
        for (var i = 0; i < full; i++)
        {
            if (address[i] != network[i]) return false;
        }

        var rest = prefix % 8;
        if (rest == 0) return true;
        var mask = (byte)(0xFF << (8 - rest));
        return (address[full] & mask) == (network[full] & mask);
    }

    /// <summary>A copy of the payload that is safe to store: credential-named keys lose their value and anything carrying the token is replaced; depth, count and length are capped.</summary>
    internal static JsonNode? Redact(JsonNode? value, string token, int depth = 0)
    {
        if (depth > 4) return "[truncated]";
        switch (value)
        {
            case JsonObject o:
                var copy = new JsonObject();
                foreach (var (k, v) in o.Take(50))
                {
                    var name = k.Length > 500 ? k[..500] : k;
                    copy[name] = SecretHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase)) ? "[redacted]" : Redact(v, token, depth + 1);
                }

                return copy;
            case JsonArray a:
                return new JsonArray(a.Take(50).Select(v => Redact(v, token, depth + 1)).ToArray());
            case JsonValue v when v.TryGetValue<string>(out var text):
                return text.Contains(token, StringComparison.Ordinal) || text.Contains(TokenPrefix, StringComparison.Ordinal) ? "[redacted]" : text.Length > 500 ? text[..500] : text;
            default:
                return value?.DeepClone();
        }
    }

    private async Task<int?> AuditAsync(string result, int? strategyId, string? action, string? mode, JsonNode? payload, string? ip, string? userAgent, string? error)
    {
        try
        {
            await using var db = await contexts.CreateDbContextAsync();
            var row = new SmWebhookEvent
            {
                StrategyId = strategyId, Action = Cut(action, 20), Mode = Cut(mode, 10), Payload = payload?.ToJsonString(), Ip = Cut(ip, 45), UserAgent = Cut(userAgent, 255), Result = result, Error = error,
                ReceivedAt = clock.GetUtcNow().UtcDateTime,
            };
            db.Set<SmWebhookEvent>().Add(row);
            await db.SaveChangesAsync();
            return row.Id;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not audit a strategy webhook ({Result}).", result);
            return null;
        }
    }

    private static string? Cut(string? text, int max) => text is null ? null : text.Length > max ? text[..max] : text;

    /// <summary>The answer to a token that resolves to nothing, audited, and the same for a malformed and an unregistered token.</summary>
    public async Task<Outcome> UnknownTokenAsync(string? ip, string? userAgent)
    {
        await AuditAsync("rejected_token", null, null, null, null, ip, userAgent, null);
        return Out("rejected_token", UnknownToken);
    }

    public async Task<Outcome> HandleAsync(string? token, byte[] body, string? ip, string? userAgent, CancellationToken ct)
    {
        try
        {
            if (token is null || !token.StartsWith(TokenPrefix, StringComparison.Ordinal) || !TokenBody().IsMatch(token[TokenPrefix.Length..])) return await UnknownTokenAsync(ip, userAgent);
            var hash = Hash(token);
            SmStrategy? strategy;
            await using (var db = await contexts.CreateDbContextAsync(ct))
                strategy = await db.Set<SmStrategy>().AsNoTracking().FirstOrDefaultAsync(s => s.WebhookTokenHash == hash, ct);
            if (strategy is null) return await UnknownTokenAsync(ip, userAgent);
            var id = strategy.Id;

            if (strategy.WebhookLocked)
            {
                await AuditAsync("rejected_locked", id, null, null, null, ip, userAgent, "The webhook kill switch is engaged");
                return Out("rejected_locked", "This strategy's webhook is locked", strategy: id);
            }

            if (!IpAllowed(ip, strategy.WebhookIpAllowlist))
            {
                await AuditAsync("rejected_ip", id, null, null, null, ip, userAgent, "The caller address is outside the allowlist");
                return Out("rejected_ip", "This address is not allowed to trigger this strategy", strategy: id);
            }

            var (payload, parseError) = Parse(body);
            if (payload is null)
            {
                await AuditAsync("rejected_payload", id, null, null, null, ip, userAgent, parseError);
                return Out("rejected_payload", parseError!, strategy: id);
            }

            var safe = Redact(payload, token);
            var rawAction = payload["action"] is JsonValue av && av.TryGetValue<string>(out var a0) ? a0.Trim().ToLowerInvariant() : null;
            string[] allowed = strategy.StrategyKind == "signal" ? ["long_entry", "short_entry", "long_exit", "short_exit", "stop"] : ["start", "stop"];
            if (rawAction is null || !allowed.Contains(rawAction))
            {
                await AuditAsync("rejected_invalid_action", id, rawAction ?? payload["action"]?.ToJsonString(), null, safe, ip, userAgent, "Unrecognised action");
                return Out("rejected_invalid_action", $"'action' must be one of {string.Join(", ", allowed)}", strategy: id);
            }

            if (strategy.StrategyKind == "signal")
            {
                // Signals bypass the repeat and cooling-off windows: an alert engine repeats itself, and the engine answers a repeat as an accepted no-op.
                var legId = payload["leg_id"] is JsonValue lv && lv.TryGetValue<int>(out var lid) ? lid : (int?)null;
                var signal = await api.HandleSignalAsync(id, rawAction, legId, Text(payload["symbol"]), Text(payload["exchange"]), ct);
                if (!signal.Ok)
                {
                    await AuditAsync("rejected_invalid_action", id, rawAction, null, safe, ip, userAgent, signal.Error);
                    return Out("rejected_invalid_action", signal.Error ?? "The signal was refused", strategy: id);
                }

                await AuditAsync("ok", id, rawAction, null, safe, ip, userAgent, null);
                return Out("ok", signal.Note is { } note ? $"Signal accepted ({note})" : "Signal accepted", ok: true, strategy: id, run: signal.RunId);
            }

            string? mode = null;
            if (rawAction == "start")
            {
                mode = payload["mode"] is JsonValue mv && mv.TryGetValue<string>(out var m0) ? m0.Trim().ToLowerInvariant() : null;
                if (mode is not ("live" or "sandbox"))
                {
                    await AuditAsync("rejected_invalid_action", id, rawAction, mode ?? payload["mode"]?.ToJsonString(), safe, ip, userAgent, "Unrecognised mode");
                    return Out("rejected_invalid_action", "'mode' must be one of live, sandbox when starting", strategy: id);
                }

                if (mode == "live" && !strategy.LiveEnabled)
                {
                    await AuditAsync("rejected_live_disabled", id, rawAction, mode, safe, ip, userAgent, "Live trading is not enabled for this strategy");
                    return Out("rejected_live_disabled", "Live trading is not enabled for this strategy", strategy: id);
                }
            }

            var now = clock.GetUtcNow().UtcDateTime;
            await using (var db = await contexts.CreateDbContextAsync(ct))
            {
                var since = now.AddSeconds(-DedupeSeconds);
                var repeat = await db.Set<SmWebhookEvent>().AsNoTracking().AnyAsync(e => e.StrategyId == id && e.Result == "ok" && e.Action == rawAction && e.Mode == mode && e.ReceivedAt >= since, ct);
                if (repeat)
                {
                    await AuditAsync("rejected_dedupe", id, rawAction, mode, safe, ip, userAgent, $"Duplicate signal inside {DedupeSeconds}s");
                    return Out("rejected_dedupe", $"Duplicate signal ignored, already handled within {DedupeSeconds}s", ok: true, strategy: id);
                }

                if (rawAction == "start")
                {
                    var last = await db.Set<SmStrategyRun>().AsNoTracking().Where(r => r.StrategyId == id && r.StoppedAt != null).OrderByDescending(r => r.StoppedAt).Select(r => r.StoppedAt).FirstOrDefaultAsync(ct);
                    if (last is { } stopped && (now - DateTime.SpecifyKind(stopped, DateTimeKind.Utc)).TotalSeconds < CoolingOffSeconds)
                    {
                        var remaining = Math.Max(1, (int)(CoolingOffSeconds - (now - DateTime.SpecifyKind(stopped, DateTimeKind.Utc)).TotalSeconds));
                        await AuditAsync("rejected_cooling_off", id, rawAction, mode, safe, ip, userAgent, $"Stopped within the last {CoolingOffSeconds}s");
                        return Out("rejected_cooling_off", $"This strategy stopped recently, try again in {remaining}s", strategy: id);
                    }
                }
            }

            var eventId = await AuditAsync("ok", id, rawAction, mode, safe, ip, userAgent, null);
            return await DispatchAsync(strategy, rawAction, mode, eventId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "A strategy webhook failed.");
            await AuditAsync("rejected_engine_error", null, null, null, null, ip, userAgent, "The webhook could not be handled");
            return Out("rejected_engine_error", "The platform could not act on the signal. Nothing was changed; check the strategy's events.");
        }
    }

    private static (JsonObject? Payload, string? Error) Parse(byte[] body)
    {
        if (body.Length == 0) return (null, "The request body is empty");
        if (body.Length > MaxPayloadBytes) return (null, $"The request body is larger than {MaxPayloadBytes} bytes");
        try
        {
            var text = new UTF8Encoding(false, true).GetString(body).Trim();
            if (text.Length == 0) return (null, "The request body is empty");
            return JsonNode.Parse(text) is JsonObject o ? (o, null) : (null, "The request body must be a JSON object");
        }
        catch (DecoderFallbackException) { return (null, "The request body is not valid UTF-8"); }
        catch (JsonException) { return (null, "The request body is not valid JSON"); }
    }

    private async Task<Outcome> DispatchAsync(SmStrategy strategy, string action, string? mode, int? eventId, CancellationToken ct)
    {
        V1Result result;
        if (action == "start")
        {
            var account = await api.AccountForModeAsync(mode!, ct);
            if (account is null)
            {
                return Out("rejected_engine_error", $"There is no enabled {(mode == "live" ? "live" : "sandbox")} account to trade on, so the strategy was not started. Add or enable one.", strategy: strategy.Id);
            }

            result = await api.StartRunAsync(account, new StrategyStartRequest { StrategyId = strategy.Id, Mode = mode }, "webhook", ct, eventId);
        }
        else
        {
            result = await api.WebhookStopAsync(strategy.Id, ct);
        }

        if (result.Status == 200)
        {
            var run = result.Body["run_id"] is JsonValue rv && rv.TryGetValue<int>(out var rid) ? rid : (int?)null;
            return Out("ok", action == "start" ? "Strategy started" : "Strategy stopped", ok: true, strategy: strategy.Id, run: run);
        }

        // The engine's own sentence is already written for a trader; a stop of a strategy that is not running is not a fault.
        var message = Text(result.Body["message"]) ?? "The engine refused the signal";
        return Out(result.Status == 409 && action == "stop" ? "rejected_invalid_action" : "rejected_engine_error", message, strategy: strategy.Id);
    }

    private static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
