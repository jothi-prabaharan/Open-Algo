using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Contracts.V1;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.Legacy.Core;
using EPTradeBot.Core.Risk;
using EPTradeBot.Core.Trading;
using EPTradeBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EPTradeBot.Infrastructure.OpenAlgoApi;

/// <summary>
/// The run engine of a batch strategy: it resolves the legs, places every entry (longs first), then watches the open legs on each pass of
/// <see cref="StrategyRunMonitor"/> with the shared risk core in Core/Risk, and closes them on a stop, a target, a combined limit, the exit time or a request.
/// A run's working state (legs, ratchets) lives in its one checkpoint row, so a restart carries on where it was.
/// </summary>
public sealed partial class OpenAlgoApiService
{
    private const string BrokerAccountRefused = "Sandbox mode runs on a sandbox account and live mode on a live account. Pick the account that matches the mode you are starting.";

    private sealed class RunLeg
    {
        [JsonPropertyName("leg_id")] public int LegId { get; set; }
        [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
        [JsonPropertyName("exchange")] public string Exchange { get; set; } = "";
        [JsonPropertyName("position")] public string Position { get; set; } = "B";
        [JsonPropertyName("qty")] public int Qty { get; set; }
        [JsonPropertyName("status")] public string Status { get; set; } = "pending";
        [JsonPropertyName("entry_order_id")] public string? EntryOrderId { get; set; }
        [JsonPropertyName("entry_avg")] public double EntryAvg { get; set; }
        [JsonPropertyName("sl_pts")] public double? SlPts { get; set; }
        [JsonPropertyName("target_pts")] public double? TargetPts { get; set; }
        [JsonPropertyName("trail_x")] public double? TrailX { get; set; }
        [JsonPropertyName("trail_y")] public double? TrailY { get; set; }
        [JsonPropertyName("risk_unit")] public string RiskUnit { get; set; } = "points";
        [JsonPropertyName("effective_sl")] public double? EffectiveSl { get; set; }
        [JsonPropertyName("effective_target")] public double? EffectiveTarget { get; set; }
        [JsonPropertyName("highest_price")] public double? Highest { get; set; }
        [JsonPropertyName("lowest_price")] public double? Lowest { get; set; }
        [JsonPropertyName("ltp")] public double? Ltp { get; set; }
        [JsonPropertyName("trail_active")] public bool TrailActive { get; set; }
        [JsonPropertyName("realized_pnl")] public double RealizedPnl { get; set; }
        [JsonPropertyName("exit_kind")] public string? ExitKind { get; set; }
        [JsonPropertyName("exit_order_id")] public string? ExitOrderId { get; set; }
        [JsonPropertyName("exit_price")] public double? ExitPrice { get; set; }
    }

    private sealed class RunState
    {
        [JsonPropertyName("legs")] public List<RunLeg> Legs { get; set; } = [];
        [JsonPropertyName("pnl_peak")] public double PnlPeak { get; set; }
        [JsonPropertyName("pnl_trough")] public double PnlTrough { get; set; }
        [JsonPropertyName("lock_armed")] public bool LockArmed { get; set; }
        [JsonPropertyName("lock_floor")] public double? LockFloor { get; set; }
        [JsonPropertyName("trail_to_entry_active")] public bool TrailToEntryActive { get; set; }
        [JsonPropertyName("pending_stop")] public string? PendingStop { get; set; }
    }

    private static double Scale(RunLeg leg) => !string.Equals(leg.RiskUnit, "percent", StringComparison.OrdinalIgnoreCase) ? 1.0 : leg.EntryAvg > 0 ? leg.EntryAvg / 100.0 : 0.0;

    private static double? InPoints(double? value, double scale) => value is { } v && v * scale is var p && p > 0 ? p : null;

    private static Side SideOf(RunLeg leg) => leg.Position.ToUpperInvariant() switch
    {
        "B" => Side.BUY,
        "S" => Side.SELL,
        _ => throw new InvalidOperationException($"Unusable leg position: {leg.Position}"),
    };

    /// <summary>One leg as the risk core's input, as services/strategy_module/risk_adapter.leg_to_position_risk maps it: the stop and target the leg is carrying, else those its points give.</summary>
    private static PositionRisk ToRisk(RunLeg leg)
    {
        var side = SideOf(leg);
        var scale = Scale(leg);
        var slPts = InPoints(leg.SlPts, scale);
        var targetPts = InPoints(leg.TargetPts, scale);
        var initial = slPts is { } sp ? RiskValues.StopFromPoints(side, leg.EntryAvg, sp) : null;
        var configuredTarget = targetPts is { } tp ? RiskValues.TargetFromPoints(side, leg.EntryAvg, tp) : null;
        var x = InPoints(leg.TrailX, scale) ?? 0.0;
        var y = InPoints(leg.TrailY, scale) ?? 0.0;
        return new PositionRisk
        {
            Identifier = leg.LegId.ToString(CultureInfo.InvariantCulture),
            Side = side,
            EntryPrice = leg.EntryAvg,
            Quantity = leg.Qty,
            StopPrice = leg.EffectiveSl ?? initial,
            InitialStopPrice = initial,
            TargetPrice = leg.EffectiveTarget ?? configuredTarget,
            TrailingEnabled = x > 0,
            TrailTrigger = x,
            TrailStep = y > 0 ? y : x,
            TrailMode = y > 0 ? TrailMode.Stepped : TrailMode.Continuous,
            HighestPrice = leg.Highest,
            LowestPrice = leg.Lowest,
        };
    }

    private static AggregateRisk ToAggregate(RunState state, SmStrategy strategy)
    {
        var lock_ = ParseJson(strategy.LockProfit) as JsonObject;
        var mode = Text(lock_?["mode"]);
        return new AggregateRisk
        {
            CombinedStopLoss = (double?)strategy.OverallSlMtm,
            CombinedTarget = (double?)strategy.OverallTargetMtm,
            LockProfitAt = BookFormatting.Number(lock_?["if_profit_reaches"]),
            LockProfitFloor = BookFormatting.Number(lock_?["lock_profit"]),
            LockTrailStep = mode == "lock_and_trail" ? BookFormatting.Number(lock_?["trail_step"]) : null,
            LockArmed = state.LockArmed,
            LockFloor = state.LockFloor,
            PeakPnl = state.PnlPeak,
            TroughPnl = state.PnlTrough,
            StopBypassed = state.TrailToEntryActive,
        };
    }

    private static (double Realized, double Unrealized) RunPnl(RunState state)
    {
        var summary = AggregateEvaluator.AggregatePnl(state.Legs.Where(l => l.Status == "open" || l.RealizedPnl != 0).Select(l => new PositionRisk_Pnl(l).ToPnl()));
        return (summary.Realized, summary.Unrealized);
    }

    private readonly record struct PositionRisk_Pnl(RunLeg Leg)
    {
        public PositionPnL ToPnl() => new()
        {
            Identifier = Leg.LegId.ToString(CultureInfo.InvariantCulture), Side = SideOf(Leg), EntryPrice = Leg.EntryAvg, Quantity = Leg.Qty, LastPrice = Leg.Ltp,
            Closed = Leg.Status != "open", RealizedPnl = Leg.RealizedPnl,
        };
    }

    // ---- storage ---------------------------------------------------------------------------------------------

    private static readonly JsonSerializerOptions StateJson = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    private static async Task<RunState> LoadStateAsync(EpTradeDbContext db, int runId, CancellationToken ct)
    {
        var row = await db.Set<SmStrategyCheckpoint>().AsNoTracking().Where(c => c.RunId == runId).OrderByDescending(c => c.Id).FirstOrDefaultAsync(ct);
        return row is null ? new RunState() : JsonSerializer.Deserialize<RunState>(row.LegState, StateJson) ?? new RunState();
    }

    private static async Task SaveStateAsync(EpTradeDbContext db, int runId, RunState state, double realized, double unrealized, CancellationToken ct)
    {
        var row = await db.Set<SmStrategyCheckpoint>().Where(c => c.RunId == runId).OrderByDescending(c => c.Id).FirstOrDefaultAsync(ct);
        if (row is null) db.Set<SmStrategyCheckpoint>().Add(row = new SmStrategyCheckpoint { RunId = runId });
        row.Ts = DateTime.UtcNow;
        row.PnlRealized = (decimal)realized;
        row.PnlUnrealized = (decimal)unrealized;
        row.PnlTotal = (decimal)(realized + unrealized);
        row.PnlPeak = (decimal)state.PnlPeak;
        row.PnlTrough = (decimal)state.PnlTrough;
        row.LockFloor = state.LockFloor is { } f ? (decimal)f : null;
        row.TrailToEntryActive = state.TrailToEntryActive;
        row.LegState = JsonSerializer.Serialize(state, StateJson);
        await db.SaveChangesAsync(ct);
    }

    private static void AddEvent(EpTradeDbContext db, SmStrategy strategy, int? runId, string kind, string severity, int? legId, string message) =>
        db.Set<SmStrategyEvent>().Add(new SmStrategyEvent { RunId = runId, StrategyId = strategy.Id, UserId = strategy.UserId, Kind = kind, Severity = severity, LegId = legId, Message = message });

    // ---- leg resolution --------------------------------------------------------------------------------------

    private sealed record LegPlan(RunLeg Leg, string? Fallback);

    private static string? LegText(JsonObject leg, params string[] names)
    {
        foreach (var n in names)
        {
            if (Text(leg[n]) is { Length: > 0 } t) return t;
        }

        return null;
    }

    private static double? LegNumber(JsonObject leg, params string[] names)
    {
        foreach (var n in names)
        {
            if (BookFormatting.Number(leg[n]) is { } v) return v;
        }

        return null;
    }

    /// <summary>Turns the strategy's stored legs into legs with a real contract and quantity, or a sentence naming what is wrong.</summary>
    private async Task<(List<LegPlan>? Plans, string? Error)> ResolveLegsAsync(BrokerAccount account, SmStrategy strategy, CancellationToken ct)
    {
        if (ParseJson(strategy.Legs) is not JsonArray array || array.Count == 0) return (null, "This strategy has no legs to place. Add at least one leg on the strategy page.");
        var plans = new List<LegPlan>();
        var index = 0;
        double? underlyingLtp = null;
        foreach (var item in array)
        {
            index++;
            if (item is not JsonObject leg) return (null, $"Leg {index} is not readable. Open the strategy and save it again.");
            var legId = (int?)LegNumber(leg, "leg_id", "legId", "id") ?? index;
            var position = (LegText(leg, "position", "action", "side", "transaction_type") ?? "").ToUpperInvariant() switch { "B" or "BUY" => "B", "S" or "SELL" => "S", _ => "" };
            if (position.Length == 0) return (null, $"Leg {legId} has no buy or sell side. Set it on the strategy page.");
            var lots = (int?)LegNumber(leg, "lots", "num_lots", "numLots") ?? 0;
            if (lots < 1) return (null, $"Leg {legId} has no lots. Set a whole number of lots of at least 1.");
            var segment = (LegText(leg, "segment", "instrument", "leg_type", "legType") ?? "options").ToLowerInvariant();

            string symbol, exchange;
            int quantity;
            string? fallback = null;
            var upperExchange = strategy.UnderlyingExchange.ToUpperInvariant();
            if (segment is "cash" or "equity")
            {
                symbol = LegText(leg, "symbol") ?? strategy.Underlying;
                exchange = (LegText(leg, "exchange") ?? upperExchange).ToUpperInvariant();
                quantity = lots;
            }
            else
            {
                var optionsExchange = OptionExchange(upperExchange);
                var kind = segment is "futures" or "future" or "fut" ? "futures" : "options";
                var dates = await ExpiryAsync(account, new ExpiryRequest { Symbol = strategy.Underlying, Exchange = optionsExchange, InstrumentType = kind }, ct);
                var listed = (dates.Body["data"] as JsonArray ?? []).Select(d => Text(d)).Where(d => d is not null).Select(d => d!.Replace("-", "")).ToList();
                var wanted = LegText(leg, "expiry_date", "expiryDate");
                string expiry;
                if (wanted is not null)
                {
                    expiry = wanted.Replace("-", "").ToUpperInvariant();
                }
                else
                {
                    if (listed.Count == 0) return (null, $"No {kind} expiry is listed for {strategy.Underlying} on {optionsExchange}. Download the master contract and try again.");
                    var rankText = (LegText(leg, "expiry_rank", "expiryRank", "expiry") ?? "current").ToLowerInvariant();
                    var rank = rankText switch { "current" or "current_week" or "0" => 0, "next" or "next_week" or "1" => 1, _ => int.TryParse(rankText, out var r0) ? r0 : 0 };
                    if (rank >= listed.Count)
                    {
                        fallback = $"Leg {legId} asked for expiry number {rank + 1}; only {listed.Count} is listed, so {listed[^1]} was used";
                        rank = listed.Count - 1;
                    }

                    expiry = listed[rank];
                }

                if (kind == "futures")
                {
                    var contract = strategy.Underlying.ToUpperInvariant() + expiry + "FUT";
                    SymToken? row;
                    await using (var db = await contexts.CreateDbContextAsync(ct))
                        row = await Master(db, account).Where(s => s.Symbol == contract && s.Exchange == optionsExchange).OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
                    if (row is null) return (null, $"The futures contract {contract} is not in the master contract for {optionsExchange}. Download the master contract and try again.");
                    (symbol, exchange, quantity) = (row.Symbol, optionsExchange, lots * Math.Max(row.Lotsize ?? 1, 1));
                }
                else
                {
                    var optionType = (LegText(leg, "option_type", "optionType", "opt_type") ?? "").ToUpperInvariant();
                    if (optionType is not ("CE" or "PE")) return (null, $"Leg {legId} has no option type. Choose CE or PE on the strategy page.");
                    var offset = LegText(leg, "atm_offset", "atmOffset", "offset") ?? "ATM";
                    var (option, error) = await ResolveOptionAsync(account, strategy.Underlying, upperExchange, expiry, (int?)LegNumber(leg, "strike_interval", "strikeInterval", "strike_int"), offset, optionType,
                        underlyingLtp is { } known ? JsonValue.Create(known) : null, ct);
                    if (error is not null) return (null, $"Leg {legId}: {Text(error.Body["message"]) ?? "the option could not be resolved"}");
                    underlyingLtp ??= BookFormatting.Number(option!.UnderlyingLtp);
                    (symbol, exchange, quantity) = (option!.Symbol, option.Exchange, lots * Math.Max(option.LotSize ?? 1, 1));
                }
            }

            plans.Add(new LegPlan(new RunLeg
            {
                LegId = legId, Symbol = symbol, Exchange = exchange, Position = position, Qty = quantity,
                SlPts = LegNumber(leg, "sl_pts", "slPts"), TargetPts = LegNumber(leg, "target_pts", "targetPts"), TrailX = LegNumber(leg, "trail_x", "trailX"), TrailY = LegNumber(leg, "trail_y", "trailY"),
                RiskUnit = (LegText(leg, "risk_unit", "riskUnit") ?? "points").ToLowerInvariant(),
            }, fallback));
        }

        return (plans, null);
    }

    // ---- orders ----------------------------------------------------------------------------------------------

    private static string Action(string position) => position == "B" ? "BUY" : "SELL";

    /// <summary>Where an order stands: complete (with its average), rejected, or still open.</summary>
    private async Task<(string State, double Average)> SettleOrderAsync(BrokerAccount account, string orderId, CancellationToken ct)
    {
        var answer = await OrderStatusAsync(account, new OrderStatusRequest { OrderId = orderId }, ct);
        if (answer.Status != 200) return ("open", 0);
        var data = answer.Body["data"] as JsonObject;
        var status = (Text(data?["order_status"]) ?? "").ToLowerInvariant();
        return status switch
        {
            "complete" => ("complete", BookFormatting.Number(data?["average_price"]) ?? 0),
            "rejected" or "cancelled" => ("rejected", 0),
            _ => ("open", 0),
        };
    }

    private async Task<(DispatchResult Result, SmStrategyOrder Row)> SendAsync(EpTradeDbContext db, BrokerAccount account, SmStrategy strategy, int runId, RunLeg leg, string kind, string action, CancellationToken ct)
    {
        // The intent is stored before the broker is called, so a crash after the call never leaves a position with no row describing it.
        var row = new SmStrategyOrder
        {
            RunId = runId, LegId = leg.LegId, Kind = kind, Symbol = leg.Symbol, Exchange = leg.Exchange, Action = action, Qty = leg.Qty, Product = strategy.Product, Pricetype = "MARKET", Status = "pending",
        };
        db.Set<SmStrategyOrder>().Add(row);
        await db.SaveChangesAsync(ct);
        var result = await PlacePartAsync(account, strategy.Name, leg.Symbol, leg.Exchange, action, leg.Qty, "MARKET", strategy.Product, 0, 0, 0, ct);
        if (result.Status == DispatchStatus.Accepted) { row.BrokerOrderId = result.OrderId; row.Status = "open"; }
        else { row.Status = "rejected"; row.RejectReason = result.Message; }
        await db.SaveChangesAsync(ct);
        return (result, row);
    }

    private static void CloseLeg(RunLeg leg, double exitPrice)
    {
        leg.ExitPrice = exitPrice;
        leg.RealizedPnl = AggregateEvaluator.PositionPnl(SideOf(leg), leg.EntryAvg, leg.Qty, exitPrice);
        leg.Status = "closed";
    }

    private async Task<SmStrategyOrder?> OrderRowAsync(EpTradeDbContext db, int runId, string? orderId, CancellationToken ct) =>
        orderId is null ? null : await db.Set<SmStrategyOrder>().FirstOrDefaultAsync(o => o.RunId == runId && o.BrokerOrderId == orderId, ct);

    /// <summary>
    /// Sends the order that closes a leg. The leg is claimed (<c>exit_kind</c>) before the call, so two rules firing on one pass send one order, and a refusal releases the claim:
    /// the leg stays open and managed, because the position is still there.
    /// </summary>
    private async Task<bool> ExitLegAsync(EpTradeDbContext db, BrokerAccount account, SmStrategy strategy, int runId, RunLeg leg, string kind, string message, CancellationToken ct)
    {
        if (leg.Status != "open" || leg.ExitKind is not null) return leg.ExitKind is not null;
        leg.ExitKind = kind;
        var (result, row) = await SendAsync(db, account, strategy, runId, leg, kind, Action(leg.Position == "B" ? "S" : "B"), ct);
        if (result.Status != DispatchStatus.Accepted)
        {
            leg.ExitKind = null;
            AddEvent(db, strategy, runId, "leg_exit_rejected", "critical", leg.LegId, $"The order closing leg {leg.LegId} was refused: {result.Message} The leg is still open and being watched.");
            return false;
        }

        leg.ExitOrderId = result.OrderId;
        AddEvent(db, strategy, runId, "leg_exit_placed", "info", leg.LegId, message);
        var (state, average) = await SettleOrderAsync(account, result.OrderId!, ct);
        if (state == "complete") { FinishExit(db, strategy, runId, leg, row, average); }
        else if (state == "rejected")
        {
            row.Status = "rejected";
            leg.ExitKind = null;
            leg.ExitOrderId = null;
            AddEvent(db, strategy, runId, "leg_exit_rejected", "critical", leg.LegId, $"The broker rejected the order closing leg {leg.LegId}. The leg is still open and being watched.");
        }

        return true;
    }

    private static void FinishExit(EpTradeDbContext db, SmStrategy strategy, int runId, RunLeg leg, SmStrategyOrder? row, double average)
    {
        if (row is not null) { row.Status = "complete"; row.AvgFillPrice = (decimal)average; row.FilledQty = leg.Qty; row.FilledAt = DateTime.UtcNow; }
        CloseLeg(leg, average);
        AddEvent(db, strategy, runId, "leg_exit_filled", "info", leg.LegId, $"Leg {leg.LegId} closed at {RiskValues.FormatPrice(average)}.");
    }

    // ---- start -----------------------------------------------------------------------------------------------

    public Task<V1Result> StrategyStartAsync(BrokerAccount account, StrategyStartRequest r, CancellationToken ct) => StartRunAsync(account, r, "manual", ct);

    /// <summary>Starts a run; <paramref name="source"/> records who asked ("manual" from the API, "scheduler" from the strategy's own schedule).</summary>
    internal async Task<V1Result> StartRunAsync(BrokerAccount account, StrategyStartRequest r, string source, CancellationToken ct, int? webhookEventId = null)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await FindStrategyAsync(db, r.StrategyId!.Value, ct) is not { } found) return V1Result.Error(404, StrategyNotFound);
        if (r.Mode == "live" && !found.LiveEnabled)
            return V1Result.Error(409, "This strategy is not enabled for live trading. Enable it on the strategy page, or start it with mode 'sandbox'.");
        if (found.StrategyKind == "signal")
            return V1Result.Error(400, "A signal strategy has no start. Its run opens on the first long_entry or short_entry signal after the session boundary.");
        if (found.CurrentRunId is not null || found.Status == "running") return V1Result.Error(409, "This strategy is already running");
        if ((r.Mode == "sandbox") != (account.AccountType == AccountType.PAPER) || r.Mode == "live" && account.AccountType != AccountType.LIVE)
            return V1Result.Error(409, BrokerAccountRefused);
        if (!string.Equals(found.Pricetype, "MARKET", StringComparison.OrdinalIgnoreCase))
            return V1Result.Error(400, "Strategy runs place market orders. Set the strategy's price type to MARKET.");

        // Only one caller wins the claim, so two starts never open two runs.
        var claimed = await db.Set<SmStrategy>().Where(s => s.Id == found.Id && s.CurrentRunId == null && s.Status != "running")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "running").SetProperty(x => x.UpdatedAt, DateTime.UtcNow), ct);
        if (claimed == 0) return V1Result.Error(409, "This strategy is already running");

        var strategy = await db.Set<SmStrategy>().SingleAsync(s => s.Id == found.Id, ct);
        try
        {
            if (await DailyLossReachedAsync(db, strategy, 0, ct))
            {
                await ReleaseAsync(db, strategy, ct);
                return V1Result.Error(409, "This strategy has reached its daily loss limit, so it cannot start again today.");
            }

            var (plans, error) = await ResolveLegsAsync(account, strategy, ct);
            if (plans is null)
            {
                await ReleaseAsync(db, strategy, ct);
                return V1Result.Error(400, error!);
            }

            var run = new SmStrategyRun
            {
                StrategyId = strategy.Id, Mode = r.Mode!, Broker = account.Broker, BrokerAccount = account.Id.ToString("N"), TriggerSource = source, WebhookEventId = webhookEventId, StartedAt = DateTime.UtcNow,
                ResolvedExpiries = new JsonArray(plans.Select(p => (JsonNode?)JsonValue.Create(p.Leg.Symbol)).ToArray()).ToJsonString(),
            };
            db.Set<SmStrategyRun>().Add(run);
            await db.SaveChangesAsync(ct);
            strategy.CurrentRunId = run.Id;
            AddEvent(db, strategy, run.Id, "run_started", "info", null, $"Run {run.Id} started in {r.Mode} mode with {plans.Count} leg(s).");
            foreach (var p in plans.Where(p => p.Fallback is not null)) AddEvent(db, strategy, run.Id, "leg_expiry_fallback", "warn", p.Leg.LegId, p.Fallback!);
            await db.SaveChangesAsync(ct);

            using var _ = await instrumentLocks.AcquireAsync($"strategy-run|{run.Id}", ct);
            var state = new RunState { Legs = [.. plans.Select(p => p.Leg)] };
            var failure = await PlaceEntriesAsync(db, account, strategy, run, state, ct);
            if (failure is not null)
            {
                // A run that could not open every leg does not trade half a structure: what did fill is closed, and the run ends.
                // Legs that were never sent are not part of the run any more.
                foreach (var unsent in state.Legs.Where(l => l.Status == "pending" && l.EntryOrderId is null)) unsent.Status = "failed";
                foreach (var leg in state.Legs.Where(l => l.Status == "open")) await ExitLegAsync(db, account, strategy, run.Id, leg, "exit_manual", $"Closing leg {leg.LegId} because the strategy could not open every leg.", ct);
                state.PendingStop = "entry_failed";
                await FinishIfDoneAsync(db, strategy, run, state, ct);
                var (re, un) = RunPnl(state);
                await SaveStateAsync(db, run.Id, state, re, un, ct);
                return Mark(account, V1Result.Error(502, failure));
            }

            var (realized, unrealized) = RunPnl(state);
            await SaveStateAsync(db, run.Id, state, realized, unrealized, ct);
            var body = new JsonObject { ["status"] = "success", ["message"] = $"Strategy started in {r.Mode} mode", ["run_id"] = run.Id, ["data"] = RunRow(run) };
            return Mark(account, new V1Result(200, body));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Starting strategy {StrategyId} failed.", strategy.Id);
            await ReleaseIfNoRunAsync(db, strategy, ct);
            return V1Result.Error(500, "The strategy could not be started. Nothing was placed unless it appears on the orders page. Try again in a moment.");
        }
    }

    private static async Task ReleaseAsync(EpTradeDbContext db, SmStrategy strategy, CancellationToken ct)
    {
        strategy.Status = "stopped";
        strategy.CurrentRunId = null;
        strategy.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static async Task ReleaseIfNoRunAsync(EpTradeDbContext db, SmStrategy strategy, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var hasRun = await db.Set<SmStrategyRun>().AnyAsync(x => x.StrategyId == strategy.Id && x.StoppedAt == null, ct);
        if (!hasRun) await db.Set<SmStrategy>().Where(s => s.Id == strategy.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "stopped").SetProperty(x => x.CurrentRunId, (int?)null), ct);
    }

    /// <summary>Places each entry, buys first (a short leg can be refused for margin it would have had once the long leg existed). Returns a sentence when a leg did not open.</summary>
    private async Task<string?> PlaceEntriesAsync(EpTradeDbContext db, BrokerAccount account, SmStrategy strategy, SmStrategyRun run, RunState state, CancellationToken ct)
    {
        foreach (var leg in state.Legs.OrderBy(l => l.Position == "B" ? 0 : 1).ThenBy(l => l.LegId))
        {
            var (result, row) = await SendAsync(db, account, strategy, run.Id, leg, "entry", Action(leg.Position), ct);
            if (result.Status != DispatchStatus.Accepted)
            {
                leg.Status = "failed";
                AddEvent(db, strategy, run.Id, "leg_entry_rejected", "critical", leg.LegId, $"The entry for leg {leg.LegId} was refused: {result.Message}");
                await db.SaveChangesAsync(ct);
                return $"The entry for leg {leg.LegId} ({leg.Symbol}) was refused: {result.Message} Any leg already opened was closed and the run has ended.";
            }

            leg.EntryOrderId = result.OrderId;
            AddEvent(db, strategy, run.Id, "leg_entry_placed", "info", leg.LegId, $"Entry placed for leg {leg.LegId}: {Action(leg.Position)} {leg.Qty} {leg.Symbol}.");
            var (state0, average) = await SettleOrderAsync(account, result.OrderId!, ct);
            if (state0 == "complete") MarkEntryFilled(db, strategy, run.Id, leg, row, average);
            else if (state0 == "rejected")
            {
                row.Status = "rejected";
                leg.Status = "failed";
                AddEvent(db, strategy, run.Id, "leg_entry_rejected", "critical", leg.LegId, $"The broker rejected the entry for leg {leg.LegId}.");
                await db.SaveChangesAsync(ct);
                return $"The broker rejected the entry for leg {leg.LegId} ({leg.Symbol}). Any leg already opened was closed and the run has ended.";
            }

            await db.SaveChangesAsync(ct);
        }

        return null;
    }

    private static void MarkEntryFilled(EpTradeDbContext db, SmStrategy strategy, int runId, RunLeg leg, SmStrategyOrder? row, double average)
    {
        if (row is not null) { row.Status = "complete"; row.AvgFillPrice = (decimal)average; row.FilledQty = leg.Qty; row.FilledAt = DateTime.UtcNow; }
        leg.Status = "open";
        leg.EntryAvg = average;
        leg.Ltp = average;
        AddEvent(db, strategy, runId, "leg_entry_filled", "info", leg.LegId, $"Leg {leg.LegId} opened at {RiskValues.FormatPrice(average)}.");
    }

    // ---- stop and close --------------------------------------------------------------------------------------

    public async Task<V1Result> StrategyStopAsync(BrokerAccount account, StrategyRefRequest r, CancellationToken ct) =>
        await CloseRunAsync(r.StrategyId!.Value, "manual_stop", "run_stop_requested", ct);

    /// <summary>Squares a run off at its schedule's auto-stop time; the run ends with reason "scheduled_stop".</summary>
    internal Task<V1Result> ScheduledStopAsync(int strategyId, CancellationToken ct) =>
        CloseRunAsync(strategyId, "scheduled_stop", "eod_squareoff", ct);

    /// <summary>A stop asked for by an alert on the strategy's webhook.</summary>
    internal Task<V1Result> WebhookStopAsync(int strategyId, CancellationToken ct) => CloseRunAsync(strategyId, "manual_stop", "run_stop_requested", ct);

    /// <summary>Closes every open leg of the strategy's run, then ends the run. A leg the broker refuses to close leaves the run open and watched.</summary>
    public async Task<V1Result> StrategyCloseAllAsync(BrokerAccount account, StrategyRefRequest r, CancellationToken ct) =>
        await CloseRunAsync(r.StrategyId!.Value, "close_all", "close_all_manual", ct);

    private async Task<V1Result> CloseRunAsync(int strategyId, string reason, string eventKind, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.Set<SmStrategy>().FirstOrDefaultAsync(s => s.Id == strategyId, ct) is not { } strategy) return V1Result.Error(404, StrategyNotFound);
        if (strategy.CurrentRunId is not { } runId) return V1Result.Error(409, StrategyNotRunning);
        using var _ = await instrumentLocks.AcquireAsync($"strategy-run|{runId}", ct);
        db.ChangeTracker.Clear();
        strategy = await db.Set<SmStrategy>().SingleAsync(s => s.Id == strategyId, ct);
        if (strategy.CurrentRunId != runId) return V1Result.Error(409, StrategyNotRunning);
        var run = await db.Set<SmStrategyRun>().SingleAsync(x => x.Id == runId, ct);
        var runAccount = await AccountOfRunAsync(db, run, ct);
        if (runAccount is null) return V1Result.Error(409, "The account this run trades on is not available. Reconnect it, then try again.");
        var state = await LoadStateAsync(db, runId, ct);
        run.StopRequestedAt ??= DateTime.UtcNow;
        run.StopRequestedReason = reason;
        AddEvent(db, strategy, runId, eventKind, "info", null, reason == "close_all" ? "Close all requested." : "Stop requested.");
        var failed = await ExitAllAsync(db, runAccount, strategy, runId, state, "exit_manual", reason, ct);
        state.PendingStop = reason;
        var finished = await FinishIfDoneAsync(db, strategy, run, state, ct);
        var (realized, unrealized) = RunPnl(state);
        await SaveStateAsync(db, runId, state, realized, unrealized, ct);
        if (!finished)
        {
            AddEvent(db, strategy, runId, "run_stop_failed", "critical", null, $"{failed} leg(s) could not be closed. The run is still open and being watched.");
            await db.SaveChangesAsync(ct);
            return Mark(runAccount, V1Result.Error(502, $"{failed} leg(s) could not be closed, so the run is still open and its stops are still being watched. Check the broker connection and try again."));
        }

        return Mark(runAccount, new V1Result(200, new JsonObject { ["status"] = "success", ["message"] = reason == "close_all" ? "All legs closed" : "Strategy stopped", ["data"] = RunRow(run) }));
    }

    public async Task<V1Result> StrategyCloseLegAsync(BrokerAccount account, StrategyCloseLegRequest r, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.Set<SmStrategy>().FirstOrDefaultAsync(s => s.Id == r.StrategyId, ct) is not { } strategy) return V1Result.Error(404, StrategyNotFound);
        if (strategy.CurrentRunId is not { } runId) return V1Result.Error(409, StrategyNotRunning);
        using var _ = await instrumentLocks.AcquireAsync($"strategy-run|{runId}", ct);
        db.ChangeTracker.Clear();
        strategy = await db.Set<SmStrategy>().SingleAsync(s => s.Id == r.StrategyId, ct);
        if (strategy.CurrentRunId != runId) return V1Result.Error(409, StrategyNotRunning);
        var run = await db.Set<SmStrategyRun>().SingleAsync(x => x.Id == runId, ct);
        var runAccount = await AccountOfRunAsync(db, run, ct);
        if (runAccount is null) return V1Result.Error(409, "The account this run trades on is not available. Reconnect it, then try again.");
        var state = await LoadStateAsync(db, runId, ct);
        if (state.Legs.FirstOrDefault(l => l.LegId == r.LegId) is not { } leg) return V1Result.Error(404, $"Leg {r.LegId} is not part of this run.");
        if (leg.Status != "open") return V1Result.Error(409, $"Leg {r.LegId} is not open.");
        if (!await ExitLegAsync(db, runAccount, strategy, runId, leg, "exit_manual", $"Leg {leg.LegId} closed by hand.", ct))
        {
            await SaveStateAsync(db, runId, state, RunPnl(state).Realized, RunPnl(state).Unrealized, ct);
            return Mark(runAccount, V1Result.Error(502, $"The order closing leg {leg.LegId} was refused, so it is still open and its stops are still being watched. Try again in a moment."));
        }

        AddEvent(db, strategy, runId, "leg_close_manual", "info", leg.LegId, $"Leg {leg.LegId} closed by hand.");
        await FinishIfDoneAsync(db, strategy, run, state, ct);
        var (realized, unrealized) = RunPnl(state);
        await SaveStateAsync(db, runId, state, realized, unrealized, ct);
        return Mark(runAccount, new V1Result(200, new JsonObject { ["status"] = "success", ["message"] = $"Leg {leg.LegId} closed" }));
    }

    /// <summary>The account a scheduled or alert-driven start trades on: the first enabled sandbox account for sandbox mode, the first enabled live account for live mode.</summary>
    internal async Task<BrokerAccount?> AccountForModeAsync(string mode, CancellationToken ct)
    {
        var type = mode == "live" ? AccountType.LIVE : AccountType.PAPER;
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Set<BrokerAccount>().AsNoTracking().Where(a => a.AccountType == type && a.IsEnabled).OrderBy(a => a.Id).FirstOrDefaultAsync(ct);
    }

    private async Task<BrokerAccount?> AccountOfRunAsync(EpTradeDbContext db, SmStrategyRun run, CancellationToken ct)
    {
        if (!Guid.TryParseExact(run.BrokerAccount, "N", out var id)) return null;
        return await db.Set<BrokerAccount>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
    }

    /// <summary>Closes every open leg; returns how many could not be closed.</summary>
    private async Task<int> ExitAllAsync(EpTradeDbContext db, BrokerAccount account, SmStrategy strategy, int runId, RunState state, string kind, string why, CancellationToken ct)
    {
        var failed = 0;
        foreach (var leg in state.Legs.Where(l => l.Status == "open" && l.ExitKind is null).ToList())
        {
            if (!await ExitLegAsync(db, account, strategy, runId, leg, kind, $"Closing leg {leg.LegId}: {why}.", ct)) failed++;
        }

        await db.SaveChangesAsync(ct);
        return failed;
    }

    /// <summary>Ends the run when no leg is open or waiting on an order. A leg that closed on its own ends a run with no stop requested as "all_legs_closed".</summary>
    private async Task<bool> FinishIfDoneAsync(EpTradeDbContext db, SmStrategy strategy, SmStrategyRun run, RunState state, CancellationToken ct)
    {
        if (state.Legs.Any(l => l.Status is "open" or "pending")) return false;
        var (realized, unrealized) = RunPnl(state);
        var total = realized + unrealized;
        state.PnlPeak = Math.Max(state.PnlPeak, total);
        state.PnlTrough = Math.Min(state.PnlTrough, total);
        run.StoppedAt = DateTime.UtcNow;
        run.StopReason = state.PendingStop ?? "all_legs_closed";
        run.PnlRealized = (decimal)realized;
        run.PnlPeak = (decimal)state.PnlPeak;
        run.PnlTrough = (decimal)state.PnlTrough;
        strategy.Status = "stopped";
        strategy.CurrentRunId = null;
        strategy.UpdatedAt = DateTime.UtcNow;
        AddEvent(db, strategy, run.Id, "run_stopped", "info", null, $"Run {run.Id} ended ({run.StopReason}) with realised P&L {RiskValues.FormatPrice(realized)}.");
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<bool> DailyLossReachedAsync(EpTradeDbContext db, SmStrategy strategy, double openTotal, CancellationToken ct)
    {
        if (strategy.DailyLossLimitInr is not { } limit || limit <= 0) return false;
        var dayStart = EPTradeBot.Core.Time.Ist.ToUtc(DateOnly.FromDateTime(EPTradeBot.Core.Time.Ist.FromUtc(clock.GetUtcNow().UtcDateTime)).ToDateTime(TimeOnly.MinValue));
        var today = await db.Set<SmStrategyRun>().AsNoTracking().Where(x => x.StrategyId == strategy.Id && x.StartedAt >= dayStart && x.StoppedAt != null).Select(x => x.PnlRealized).ToListAsync(ct);
        return (double)today.Sum() + openTotal <= -(double)limit;
    }

    // ---- the monitor pass ------------------------------------------------------------------------------------

    /// <summary>The runs still open, for the monitor to visit.</summary>
    public async Task<List<int>> OpenRunIdsAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Set<SmStrategyRun>().AsNoTracking().Where(x => x.StoppedAt == null).Select(x => x.Id).ToListAsync(ct);
    }

    /// <summary>
    /// One pass over one run: settle orders still open, mark the legs to market, apply each leg's stop, target and trail, then the combined limits, the exit time and the daily loss limit.
    /// Everything is decided with the shared risk core; this only translates a leg to it and acts on the answer.
    /// </summary>
    public async Task RunTickAsync(int runId, CancellationToken ct)
    {
        using var _ = await instrumentLocks.AcquireAsync($"strategy-run|{runId}", ct);
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.Set<SmStrategyRun>().FirstOrDefaultAsync(x => x.Id == runId && x.StoppedAt == null, ct) is not { } run) return;
        if (await db.Set<SmStrategy>().FirstOrDefaultAsync(s => s.Id == run.StrategyId, ct) is not { } strategy) return;
        var account = await AccountOfRunAsync(db, run, ct);
        if (account is null) return;
        var state = await LoadStateAsync(db, runId, ct);

        // Orders that were still open at the last pass.
        foreach (var leg in state.Legs.Where(l => l.Status == "pending" && l.EntryOrderId is not null))
        {
            var (s, avg) = await SettleOrderAsync(account, leg.EntryOrderId!, ct);
            var row = await OrderRowAsync(db, runId, leg.EntryOrderId, ct);
            if (s == "complete") MarkEntryFilled(db, strategy, runId, leg, row, avg);
            else if (s == "rejected") { leg.Status = "failed"; if (row is not null) row.Status = "rejected"; AddEvent(db, strategy, runId, "leg_entry_rejected", "critical", leg.LegId, $"The broker rejected the entry for leg {leg.LegId}."); }
        }

        foreach (var leg in state.Legs.Where(l => l.Status == "open" && l.ExitKind is not null && l.ExitOrderId is not null))
        {
            var (s, avg) = await SettleOrderAsync(account, leg.ExitOrderId!, ct);
            var row = await OrderRowAsync(db, runId, leg.ExitOrderId, ct);
            if (s == "complete") FinishExit(db, strategy, runId, leg, row, avg);
            else if (s == "rejected")
            {
                if (row is not null) row.Status = "rejected";
                leg.ExitKind = null;
                leg.ExitOrderId = null;
                AddEvent(db, strategy, runId, "leg_exit_rejected", "critical", leg.LegId, $"The broker rejected the order closing leg {leg.LegId}. The leg is still open and being watched.");
            }
        }

        // Marks.
        foreach (var leg in state.Legs.Where(l => l.Status == "open"))
        {
            try
            {
                // A sandbox account is marked from the platform's own quote book, the price its orders fill at; any other account from the broker.
                if (account.AccountType == AccountType.PAPER && sandbox.LastPrice(leg.Exchange, leg.Symbol) is { } held and > 0) { leg.Ltp = (double)held; continue; }
                var quote = await QuoteAsync(account, leg.Symbol, leg.Exchange, ct);
                if (quote.Status == 200 && BookFormatting.Number(quote.Body["data"]?["ltp"]) is { } ltp && ltp > 0) leg.Ltp = ltp;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not read the price of {Symbol} for run {RunId}.", leg.Symbol, runId);
            }
        }

        var stopReason = state.PendingStop;
        var exitKind = "exit_manual";

        if (stopReason is null)
        {
            // Each leg against its own stop, target and trail.
            foreach (var leg in state.Legs.Where(l => l.Status == "open" && l.ExitKind is null && l.Ltp is not null).ToList())
            {
                var decision = PositionEvaluator.Evaluate(ToRisk(leg), leg.Ltp);
                var wasActive = leg.TrailActive;
                leg.EffectiveSl = decision.StopPrice;
                leg.EffectiveTarget = decision.TargetPrice;
                leg.Highest = decision.HighestPrice;
                leg.Lowest = decision.LowestPrice;
                if (decision.TrailArmed) leg.TrailActive = true;
                if (decision.TrailArmed && !wasActive) AddEvent(db, strategy, runId, "leg_trail_armed", "info", leg.LegId, decision.Detail);
                else if (decision.StopMoved) AddEvent(db, strategy, runId, "leg_trail_advanced", "info", leg.LegId, decision.Detail);
                if (!decision.Breached) continue;
                var stop = decision.Reason == BreachReason.Stop;
                AddEvent(db, strategy, runId, stop ? "leg_sl_hit" : "leg_target_hit", "warn", leg.LegId, decision.Detail);
                await ExitLegAsync(db, account, strategy, runId, leg, stop ? "exit_sl" : "exit_target", $"Leg {leg.LegId} {(stop ? "hit its stop" : "reached its target")}.", ct);
                if (stop && strategy.TrailSlToEntry) TrailOthersToEntry(db, strategy, runId, state, leg);
            }

            // The combined limits.
            var (realized, unrealized) = RunPnl(state);
            var aggregate = AggregateEvaluator.Evaluate(ToAggregate(state, strategy), realized, unrealized);
            state.PnlPeak = aggregate.PeakPnl;
            state.PnlTrough = aggregate.TroughPnl;
            if (aggregate.LockArmedNow) AddEvent(db, strategy, runId, "lock_profit_armed", "info", null, aggregate.Detail);
            if (aggregate.LockFloorRaised) AddEvent(db, strategy, runId, "lock_profit_floor_advanced", "info", null, aggregate.Detail);
            state.LockArmed = aggregate.LockArmed;
            state.LockFloor = aggregate.LockFloor;
            if (aggregate.Breached)
            {
                (stopReason, var kind) = aggregate.Reason switch
                {
                    BreachReason.CombinedStop => ("overall_sl", "overall_sl_hit"),
                    BreachReason.CombinedTarget => ("overall_target", "overall_target_hit"),
                    _ => ("lock_profit", "lock_profit_triggered"),
                };
                AddEvent(db, strategy, runId, kind, "warn", null, aggregate.Detail);
                exitKind = "exit_overall";
            }
            else if (await DailyLossReachedAsync(db, strategy, aggregate.TotalPnl, ct))
            {
                stopReason = "daily_loss_limit";
                AddEvent(db, strategy, runId, "overall_sl_hit", "critical", null, "The strategy's daily loss limit was reached.");
                exitKind = "exit_overall";
            }
            else if (strategy.ExitTime is { } exit && TimeOnly.FromDateTime(EPTradeBot.Core.Time.Ist.FromUtc(clock.GetUtcNow().UtcDateTime)) >= exit)
            {
                stopReason = "eod_squareoff";
                AddEvent(db, strategy, runId, "eod_squareoff", "info", null, $"The exit time {exit:HH:mm} was reached, so the run is being closed.");
                exitKind = "exit_eod";
            }
        }

        if (stopReason is not null)
        {
            state.PendingStop = stopReason;
            await ExitAllAsync(db, account, strategy, runId, state, exitKind, stopReason, ct);
        }

        await FinishIfDoneAsync(db, strategy, run, state, ct);
        var (re, un) = RunPnl(state);
        await SaveStateAsync(db, runId, state, re, un, ct);
    }

    /// <summary>When one stop fires and the strategy asks for it, every other open leg's stop moves to its own entry (the shared core says which moves are safe).</summary>
    private static void TrailOthersToEntry(EpTradeDbContext db, SmStrategy strategy, int runId, RunState state, RunLeg trigger)
    {
        var open = state.Legs.Where(l => l.Status == "open").ToList();
        var prices = open.Where(l => l.Ltp is not null).ToDictionary(l => l.LegId.ToString(CultureInfo.InvariantCulture), l => l.Ltp!.Value);
        var decision = AggregateEvaluator.TrailStopsToEntry(open.Select(ToRisk), [trigger.LegId.ToString(CultureInfo.InvariantCulture)], prices);
        var moved = false;
        foreach (var move in decision.Moves)
        {
            if (open.FirstOrDefault(l => l.LegId.ToString(CultureInfo.InvariantCulture) == move.Identifier) is not { } leg) continue;
            leg.EffectiveSl = move.NewStop;
            moved = true;
        }

        if (!moved) return;
        state.TrailToEntryActive = true;
        AddEvent(db, strategy, runId, "trail_to_entry_activated", "info", trigger.LegId, decision.Detail);
    }
}
