using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Contracts.V1;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.Legacy.Core;
using EPTradeBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EPTradeBot.Infrastructure.OpenAlgoApi;

/// <summary>
/// /strategy/*: the multi-leg strategies, their runs, orders and risk events, as OpenAlgo's strategy API answers them. The platform has one operator, so every strategy is theirs and an unknown id is
/// "not found". Listing and history read the stored strategies, runs, orders and events. Starting, stopping and closing legs are the run engine, in OpenAlgoApiService.StrategyRun.cs.
/// </summary>
public sealed partial class OpenAlgoApiService
{
    public const string StrategyNotFound = "Strategy not found";
    public const string StrategyNotRunning = "This strategy is not running";

    private static JsonNode? Num(decimal? value) => value is null ? null : (double)value.Value;

    /// <summary>A stored UTC timestamp as an explicit UTC ISO string, as OpenAlgo writes it.</summary>
    private static JsonNode? Utc(DateTime? value)
    {
        if (value is not { } v) return null;
        var utc = DateTime.SpecifyKind(v, DateTimeKind.Utc);
        var micro = utc.Ticks % TimeSpan.TicksPerSecond / 10;
        // Python's isoformat leaves the fraction out when it is zero.
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + (micro == 0 ? "" : "." + micro.ToString("D6", CultureInfo.InvariantCulture)) + "+00:00";
    }

    private static JsonNode? ParseJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text); } catch (JsonException) { return null; }
    }

    private static JsonObject StrategyRow(SmStrategy s, bool includeLegs)
    {
        var row = new JsonObject
        {
            ["id"] = s.Id, ["name"] = s.Name, ["strategy_kind"] = s.StrategyKind, ["direction"] = s.Direction, ["universe_tab"] = s.UniverseTab, ["underlying"] = s.Underlying, ["underlying_exchange"] = s.UnderlyingExchange,
            ["strategy_type"] = s.StrategyType, ["entry_time"] = s.EntryTime?.ToString("HH:mm", CultureInfo.InvariantCulture), ["exit_time"] = s.ExitTime?.ToString("HH:mm", CultureInfo.InvariantCulture), ["product"] = s.Product,
            ["pricetype"] = s.Pricetype, ["overall_sl_mtm"] = Num(s.OverallSlMtm), ["overall_target_mtm"] = Num(s.OverallTargetMtm), ["lock_profit"] = s.LockProfit, ["trail_sl_to_entry"] = s.TrailSlToEntry,
            ["scheduler"] = s.Scheduler, ["live_enabled"] = s.LiveEnabled, ["webhook_locked"] = s.WebhookLocked, ["webhook_ip_allowlist"] = s.WebhookIpAllowlist, ["daily_loss_limit_inr"] = Num(s.DailyLossLimitInr),
            ["status"] = s.Status, ["current_run_id"] = s.CurrentRunId, ["created_at"] = Utc(s.CreatedAt), ["updated_at"] = Utc(s.UpdatedAt),
        };
        if (includeLegs) row["legs"] = ParseJson(s.Legs) ?? new JsonArray();
        return row;
    }

    private static JsonObject RunRow(SmStrategyRun r) => new()
    {
        ["id"] = r.Id, ["strategy_id"] = r.StrategyId, ["mode"] = r.Mode, ["broker"] = r.Broker, ["started_at"] = Utc(r.StartedAt), ["stopped_at"] = Utc(r.StoppedAt), ["stop_reason"] = r.StopReason,
        ["stop_requested_at"] = Utc(r.StopRequestedAt), ["stop_requested_reason"] = r.StopRequestedReason, ["pnl_realized"] = Num(r.PnlRealized), ["pnl_peak"] = Num(r.PnlPeak), ["pnl_trough"] = Num(r.PnlTrough),
        ["trigger_source"] = r.TriggerSource, ["webhook_event_id"] = r.WebhookEventId, ["resolved_expiries"] = ParseJson(r.ResolvedExpiries),
    };

    private static JsonObject OrderRow(SmStrategyOrder o) => new()
    {
        ["id"] = o.Id, ["run_id"] = o.RunId, ["leg_id"] = o.LegId, ["kind"] = o.Kind, ["position_ref"] = o.PositionRef, ["broker_order_id"] = o.BrokerOrderId, ["symbol"] = o.Symbol, ["exchange"] = o.Exchange,
        ["action"] = o.Action, ["qty"] = o.Qty, ["product"] = o.Product, ["pricetype"] = o.Pricetype, ["price"] = Num(o.Price), ["trigger_price"] = Num(o.TriggerPrice), ["status"] = o.Status,
        ["placed_at"] = Utc(o.PlacedAt), ["filled_at"] = Utc(o.FilledAt), ["avg_fill_price"] = Num(o.AvgFillPrice), ["filled_qty"] = o.FilledQty, ["reject_reason"] = o.RejectReason,
    };

    private static JsonObject EventRow(SmStrategyEvent e) => new()
    {
        ["id"] = e.Id, ["run_id"] = e.RunId, ["strategy_id"] = e.StrategyId, ["ts"] = Utc(e.Ts), ["kind"] = e.Kind, ["severity"] = e.Severity, ["leg_id"] = e.LegId, ["message"] = e.Message, ["payload"] = ParseJson(e.Payload),
    };

    private static V1Result StrategyOk(JsonObject extra)
    {
        var body = new JsonObject { ["status"] = "success" };
        foreach (var (k, v) in extra) body[k] = v?.DeepClone();
        return new V1Result(200, body);
    }

    public async Task<V1Result> StrategyListAsync(BrokerAccount account, StrategyListRequest r, CancellationToken ct)
    {
        _ = account;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var query = db.Set<SmStrategy>().AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(r.Status)) query = query.Where(s => s.Status == r.Status);
        var q = (r.Q ?? "").Trim();
        if (q.Length > 0) query = query.Where(s => EF.Functions.ILike(s.Name, "%" + q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"));
        var strategies = await query.OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id).ToListAsync(ct);
        var ids = strategies.Select(s => s.Id).ToList();
        var finished = await db.Set<SmStrategyRun>().AsNoTracking().Where(x => ids.Contains(x.StrategyId) && x.StoppedAt != null).ToListAsync(ct);
        var rows = new JsonArray();
        foreach (var s in strategies)
        {
            var row = StrategyRow(s, false);
            var last = finished.Where(x => x.StrategyId == s.Id).OrderByDescending(x => x.StoppedAt).ThenByDescending(x => x.Id).FirstOrDefault();
            row["last_finalized_run"] = last is null ? null : new JsonObject { ["id"] = last.Id, ["pnl_realized"] = Num(last.PnlRealized), ["stopped_at"] = Utc(last.StoppedAt) };
            rows.Add(row);
        }

        return StrategyOk(new JsonObject { ["data"] = rows });
    }

    private async Task<SmStrategy?> FindStrategyAsync(EpTradeDbContext db, int id, CancellationToken ct) => await db.Set<SmStrategy>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<V1Result> StrategyStatusAsync(BrokerAccount account, StrategyRefRequest r, CancellationToken ct)
    {
        _ = account;
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await FindStrategyAsync(db, r.StrategyId!.Value, ct) is not { } strategy) return V1Result.Error(404, StrategyNotFound);
        JsonNode? run = null;
        if (strategy.CurrentRunId is { } runId && await db.Set<SmStrategyRun>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == runId, ct) is { } current) run = RunRow(current);
        return StrategyOk(new JsonObject { ["data"] = StrategyRow(strategy, true), ["run"] = run });
    }

    public async Task<V1Result> StrategyRunsAsync(BrokerAccount account, StrategyRunsRequest r, CancellationToken ct)
    {
        _ = account;
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await FindStrategyAsync(db, r.StrategyId!.Value, ct) is null) return V1Result.Error(404, StrategyNotFound);
        var runs = await db.Set<SmStrategyRun>().AsNoTracking().Where(x => x.StrategyId == r.StrategyId).OrderByDescending(x => x.StartedAt).ThenByDescending(x => x.Id).Take(r.Limit).ToListAsync(ct);
        return StrategyOk(new JsonObject { ["data"] = new JsonArray(runs.Select(x => (JsonNode)RunRow(x)).ToArray()) });
    }

    public async Task<V1Result> StrategyOrdersAsync(BrokerAccount account, StrategyOrdersRequest r, CancellationToken ct)
    {
        _ = account;
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await FindStrategyAsync(db, r.StrategyId!.Value, ct) is null) return V1Result.Error(404, StrategyNotFound);
        var runIds = db.Set<SmStrategyRun>().Where(x => x.StrategyId == r.StrategyId).Select(x => x.Id);
        var orders = db.Set<SmStrategyOrder>().AsNoTracking().Where(o => runIds.Contains(o.RunId));
        if (r.RunId is { } run) orders = orders.Where(o => o.RunId == run);
        var rows = await orders.OrderBy(o => o.PlacedAt).ThenBy(o => o.Id).ToListAsync(ct);
        return StrategyOk(new JsonObject { ["data"] = new JsonArray(rows.Select(o => (JsonNode)OrderRow(o)).ToArray()) });
    }

    public async Task<V1Result> StrategyEventsAsync(BrokerAccount account, StrategyEventsRequest r, CancellationToken ct)
    {
        _ = account;
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await FindStrategyAsync(db, r.StrategyId!.Value, ct) is null) return V1Result.Error(404, StrategyNotFound);
        var events = db.Set<SmStrategyEvent>().AsNoTracking().Where(e => e.StrategyId == r.StrategyId);
        if (r.RunId is { } run) events = events.Where(e => e.RunId == run);
        if (!string.IsNullOrEmpty(r.Kind)) events = events.Where(e => e.Kind == r.Kind);
        if (!string.IsNullOrEmpty(r.Severity)) events = events.Where(e => e.Severity == r.Severity);
        var rows = await events.OrderByDescending(e => e.Ts).ThenByDescending(e => e.Id).Take(r.Limit).ToListAsync(ct);
        return StrategyOk(new JsonObject { ["data"] = new JsonArray(rows.Select(e => (JsonNode)EventRow(e)).ToArray()) });
    }
}
