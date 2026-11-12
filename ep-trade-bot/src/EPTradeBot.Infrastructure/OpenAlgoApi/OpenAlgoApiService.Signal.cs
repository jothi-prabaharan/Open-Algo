using System.Globalization;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.Legacy.Core;
using EPTradeBot.Core.Trading;
using EPTradeBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EPTradeBot.Infrastructure.OpenAlgoApi;

/// <summary>
/// Signal-mode strategies, ported from services/strategy_module/signals.py: one alert (long_entry, long_exit, short_entry, short_exit) moves one leg. A signal strategy has one run per trading day, opened by the
/// first signal of the day (sandbox unless the strategy is enabled for live) and closed at its exit time or on the first signal of a later day. Quantity is units, or lots when the leg says so. The run shares the batch
/// engine's state, orders and risk monitor; a leg that exits goes back to "flat" so it can be signalled again.
/// Three things are answered as accepted no-ops rather than errors, because an alert engine repeats itself and a refusal invites a retry that could become a second position: an exit on a flat leg, an entry on a leg
/// already held that way, and any signal outside the trading window. A signal that contradicts the strategy's direction or the leg's own side is a refusal.
/// </summary>
public sealed partial class OpenAlgoApiService
{
    public sealed record SignalResult(bool Ok, string? Note = null, string? Error = null, int? LegId = null, int? RunId = null, bool Flipped = false);

    private static readonly string[] SignalActions = ["long_entry", "long_exit", "short_entry", "short_exit"];

    private static JsonObject? FindSignalLeg(SmStrategy strategy, int? legId, string? symbol, string? exchange)
    {
        if (ParseJson(strategy.Legs) is not JsonArray legs) return null;
        foreach (var leg in legs.OfType<JsonObject>())
        {
            var id = (int?)LegNumber(leg, "id", "leg_id");
            if (legId is { } wanted)
            {
                if (id == wanted) return leg;
                continue;
            }

            if (symbol is null || !string.Equals(LegText(leg, "symbol"), symbol, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(exchange) && !string.Equals(LegText(leg, "exchange"), exchange, StringComparison.OrdinalIgnoreCase)) continue;
            return leg;
        }

        return null;
    }

    /// <summary>Why a signal is outside the trading window, or null. Exits are allowed before the entry window opens, because a carried position must always be closable.</summary>
    private string? WindowNote(SmStrategy strategy, string action)
    {
        if (strategy.StrategyType != "intraday") return null;
        var now = TimeOnly.FromDateTime(NowIst());
        if (strategy.ExitTime is { } exit && now >= exit) return "outside_trading_window";
        if (action.EndsWith("_entry", StringComparison.Ordinal) && strategy.EntryTime is { } entry && now < entry) return "outside_entry_window";
        return null;
    }

    public async Task<SignalResult> HandleSignalAsync(int strategyId, string action, int? legId, string? symbol, string? exchange, CancellationToken ct)
    {
        if (!SignalActions.Contains(action)) return new SignalResult(false, Error: $"Unknown signal action: '{action}'");
        var side = action.StartsWith("long", StringComparison.Ordinal) ? "long" : "short";
        var entry = action.EndsWith("_entry", StringComparison.Ordinal);
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.Set<SmStrategy>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == strategyId, ct) is not { } strategy) return new SignalResult(false, Error: StrategyNotFound);
        if (strategy.StrategyKind != "signal") return new SignalResult(false, Error: "This is a batch strategy. Use start and stop.");
        if (strategy.Direction == "long_only" && side == "short" || strategy.Direction == "short_only" && side == "long")
            return new SignalResult(false, Error: $"This strategy is {strategy.Direction}; a {side} signal is not accepted");
        if (FindSignalLeg(strategy, legId, symbol, exchange) is not { } leg) return new SignalResult(false, Error: "No leg matches this signal");
        var id = (int?)LegNumber(leg, "id", "leg_id") ?? 0;
        var legSide = (LegText(leg, "side") ?? "both").ToLowerInvariant();
        if (legSide != "both" && legSide != side) return new SignalResult(false, LegId: id, Error: $"Leg {id} only accepts {legSide} signals");
        if (WindowNote(strategy, action) is { } window) return new SignalResult(true, window, LegId: id);

        using var _ = await instrumentLocks.AcquireAsync($"signal-day|{strategyId}", ct);
        var (runId, error) = await DayRunAsync(strategyId, ct);
        if (runId is null) return new SignalResult(false, LegId: id, Error: error ?? "No run");
        return await ApplySignalAsync(strategyId, runId.Value, leg, id, side, entry, ct);
    }

    /// <summary>The run this signal belongs to, opening one if the day has none; a run from an earlier trading day is closed first.</summary>
    private async Task<(int? RunId, string? Error)> DayRunAsync(int strategyId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var strategy = await db.Set<SmStrategy>().AsNoTracking().SingleAsync(s => s.Id == strategyId, ct);
        if (strategy.CurrentRunId is { } current && await db.Set<SmStrategyRun>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == current && r.StoppedAt == null, ct) is { } run)
        {
            var started = EPTradeBot.Core.Time.Ist.FromUtc(DateTime.SpecifyKind(run.StartedAt, DateTimeKind.Utc)).Date;
            if (started >= NowIst().Date) return (current, null);
            var closed = await CloseRunAsync(strategyId, "eod", "eod_squareoff", ct);
            if (closed.Status != 200) return (current, null);
        }

        var mode = strategy.LiveEnabled ? "live" : "sandbox";
        var account = await AccountForModeAsync(mode, ct);
        if (account is null)
            return (null, $"There is no enabled {(mode == "live" ? "live" : "sandbox")} account to trade on, so the signal was not acted on. Add or enable one.");
        var claimed = await db.Set<SmStrategy>().Where(s => s.Id == strategyId && s.CurrentRunId == null && s.Status != "running")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "running").SetProperty(x => x.UpdatedAt, DateTime.UtcNow), ct);
        if (claimed == 0)
        {
            var refreshed = await db.Set<SmStrategy>().AsNoTracking().SingleAsync(s => s.Id == strategyId, ct);
            return refreshed.CurrentRunId is { } existing ? (existing, null) : (null, "This strategy is already running");
        }

        var tracked = await db.Set<SmStrategy>().SingleAsync(s => s.Id == strategyId, ct);
        var opened = new SmStrategyRun { StrategyId = strategyId, Mode = mode, Broker = account.Broker, BrokerAccount = account.Id.ToString("N"), TriggerSource = "webhook", StartedAt = DateTime.UtcNow };
        db.Set<SmStrategyRun>().Add(opened);
        await db.SaveChangesAsync(ct);
        tracked.CurrentRunId = opened.Id;
        AddEvent(db, tracked, opened.Id, "run_started", "info", null, $"Signal run opened in {mode} mode.");
        await db.SaveChangesAsync(ct);
        await SaveStateAsync(db, opened.Id, new RunState(), 0, 0, ct);
        return (opened.Id, null);
    }

    private async Task<SignalResult> ApplySignalAsync(int strategyId, int runId, JsonObject config, int legId, string side, bool entry, CancellationToken ct)
    {
        using var _ = await instrumentLocks.AcquireAsync($"strategy-run|{runId}", ct);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var strategy = await db.Set<SmStrategy>().SingleAsync(s => s.Id == strategyId, ct);
        if (strategy.CurrentRunId != runId || await db.Set<SmStrategyRun>().FirstOrDefaultAsync(r => r.Id == runId && r.StoppedAt == null, ct) is not { } run)
            return new SignalResult(false, LegId: legId, Error: "No active run");
        if (entry && run.StopRequestedReason is not null) return new SignalResult(true, "run_stopping", LegId: legId, RunId: runId);
        var account = await AccountOfRunAsync(db, run, ct);
        if (account is null) return new SignalResult(false, LegId: legId, RunId: runId, Error: "The account this run trades on is not available. Reconnect it, then send the signal again.");
        var state = await LoadStateAsync(db, runId, ct);
        var leg = state.Legs.FirstOrDefault(l => l.LegId == legId);
        var position = side == "long" ? "B" : "S";
        var result = entry
            ? await SignalEntryAsync(db, account, strategy, run, state, leg, config, legId, position, ct)
            : await SignalExitAsync(db, account, strategy, run, state, leg, legId, position, ct);
        var (realized, unrealized) = RunPnl(state);
        await SaveStateAsync(db, runId, state, realized, unrealized, ct);
        return result;
    }

    private async Task<SignalResult> SignalExitAsync(EpTradeDbContext db, BrokerAccount account, SmStrategy strategy, SmStrategyRun run, RunState state, RunLeg? leg, int legId, string position, CancellationToken ct)
    {
        if (leg is null || leg.Status != "open" || leg.Position != position) return new SignalResult(true, "no_matching_position", LegId: legId, RunId: run.Id);
        if (leg.ExitKind is not null) return new SignalResult(true, "exit_in_progress", LegId: legId, RunId: run.Id);
        if (await ExitLegAsync(db, account, strategy, run.Id, leg, "exit_signal", $"Signal exit for leg {legId}.", ct)) return new SignalResult(true, LegId: legId, RunId: run.Id);
        return new SignalResult(false, LegId: legId, RunId: run.Id, Error: $"The order closing leg {legId} was refused, so it is still open and its stops are still being watched. Try the signal again in a moment.");
    }

    private async Task<SignalResult> SignalEntryAsync(EpTradeDbContext db, BrokerAccount account, SmStrategy strategy, SmStrategyRun run, RunState state, RunLeg? leg, JsonObject config, int legId, string position, CancellationToken ct)
    {
        if (leg is { Status: "open" } held && held.Position == position) return new SignalResult(true, position == "B" ? "already_long" : "already_short", LegId: legId, RunId: run.Id);
        if (leg is { Status: "pending" }) return new SignalResult(true, "entry_in_progress", LegId: legId, RunId: run.Id);

        var symbol = LegText(config, "symbol")?.ToUpperInvariant();
        var exchange = LegText(config, "exchange")?.ToUpperInvariant();
        var raw = LegNumber(config, "qty", "quantity");
        if (symbol is null || exchange is null || raw is not > 0) return new SignalResult(false, LegId: legId, Error: $"Leg {legId}: symbol, exchange and quantity are all required");
        if (position == "S" && !strategy.Product.Equals("MIS", StringComparison.OrdinalIgnoreCase) && !DerivativeExchanges.Contains(exchange))
            return new SignalResult(false, LegId: legId, Error: $"Leg {legId}: cash cannot be held short overnight, and product {strategy.Product} carries the position. Use MIS for an intraday short.");
        SymToken? contract;
        await using (var master = await contexts.CreateDbContextAsync(ct))
            contract = await Master(master, account).Where(s => s.Symbol == symbol && s.Exchange == exchange).OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
        if (contract is null && !SandboxWithoutMaster(account)) return new SignalResult(false, LegId: legId, Error: $"Leg {legId}: {symbol} is not a contract on {exchange}");
        var quantity = (LegText(config, "qty_mode") ?? "units") == "lots" ? (int)raw.Value * Math.Max(contract?.Lotsize ?? 1, 1) : (int)raw.Value;

        // A leg held the other way is closed first, and the new side opens only once that close has filled.
        var flipped = false;
        if (leg is { Status: "open" })
        {
            if (!await ExitLegAsync(db, account, strategy, run.Id, leg, "exit_signal", $"Leg {legId} is being reversed by a signal.", ct) || leg.Status != "closed" && leg.Status != "flat")
            {
                AddEvent(db, strategy, run.Id, "flip_outgoing_exit_rejected", "critical", legId, $"The close of leg {legId} before its reversal was not completed, so the new side was not opened.");
                await db.SaveChangesAsync(ct);
                return new SignalResult(false, LegId: legId, RunId: run.Id, Error: $"The position on leg {legId} could not be closed first, so the reversal was not placed and it is still open. Try the signal again in a moment.");
            }

            flipped = true;
        }

        if (leg is null)
        {
            leg = new RunLeg { LegId = legId, Signal = true };
            state.Legs.Add(leg);
        }

        leg.Symbol = symbol; leg.Exchange = exchange; leg.Position = position; leg.Qty = quantity; leg.Status = "pending"; leg.Signal = true;
        leg.EntryOrderId = null; leg.EntryAvg = 0; leg.ExitKind = null; leg.ExitOrderId = null; leg.ExitPrice = null; leg.EffectiveSl = null; leg.EffectiveTarget = null; leg.Highest = null; leg.Lowest = null; leg.TrailActive = false;
        leg.SlPts = LegNumber(config, "sl_pts"); leg.TargetPts = LegNumber(config, "target_pts"); leg.RiskUnit = (LegText(config, "risk_unit") ?? "points").ToLowerInvariant();
        if (config["trail"] is JsonObject trail) { leg.TrailX = LegNumber(trail, "x", "trail_x"); leg.TrailY = LegNumber(trail, "y", "trail_y"); }
        else { leg.TrailX = LegNumber(config, "trail_x"); leg.TrailY = LegNumber(config, "trail_y"); }

        var (sent, row) = await SendAsync(db, account, strategy, run.Id, leg, "entry", Action(position), ct);
        if (sent.Status != DispatchStatus.Accepted)
        {
            leg.Status = "flat";
            AddEvent(db, strategy, run.Id, "leg_entry_rejected", "critical", legId, $"The entry for leg {legId} was refused: {sent.Message}");
            await db.SaveChangesAsync(ct);
            return new SignalResult(false, LegId: legId, RunId: run.Id, Error: $"The entry for leg {legId} was refused: {sent.Message}");
        }

        leg.EntryOrderId = sent.OrderId;
        AddEvent(db, strategy, run.Id, "leg_entry_placed", "info", legId, $"Signal entry placed for leg {legId}: {Action(position)} {quantity} {symbol}.");
        var (settled, average) = await SettleOrderAsync(account, sent.OrderId!, ct);
        if (settled == "complete") MarkEntryFilled(db, strategy, run.Id, leg, row, average);
        else if (settled == "rejected")
        {
            row.Status = "rejected";
            leg.Status = "flat";
            AddEvent(db, strategy, run.Id, "leg_entry_rejected", "critical", legId, $"The broker rejected the entry for leg {legId}.");
        }

        await db.SaveChangesAsync(ct);
        return settled == "rejected" ? new SignalResult(false, LegId: legId, RunId: run.Id, Error: $"The broker rejected the entry for leg {legId}.") : new SignalResult(true, LegId: legId, RunId: run.Id, Flipped: flipped);
    }

    private static readonly string[] DerivativeExchanges = ["NFO", "BFO", "CDS", "BCD", "MCX", "NCDEX", "NCO", "CRYPTO"];

    /// <summary>A sandbox account trades what the quote book prices; a missing master contract does not stop it, as with the order endpoints.</summary>
    private static bool SandboxWithoutMaster(BrokerAccount account) => account.AccountType == AccountType.PAPER;
}
