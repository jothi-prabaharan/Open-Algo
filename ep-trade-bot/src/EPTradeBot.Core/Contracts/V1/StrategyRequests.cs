using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace EPTradeBot.Core.Contracts.V1;

public static class StrategyVocabulary
{
    public const string Statuses = "^(stopped|running|paused|errored)$";

    public const string EventSeverities = "^(info|warn|critical)$";

    public const string EventKinds = "^(strategy_created|strategy_updated|webhook_token_rotated|live_enabled|live_disabled|webhook_locked|webhook_unlocked|run_started|run_paused|run_resumed|run_stop_requested|run_stopped|run_stop_failed|flip_outgoing_exit_rejected|close_all_manual|leg_entry_placed|leg_entry_filled|leg_entry_rejected|leg_exit_placed|leg_exit_filled|leg_exit_rejected|leg_close_manual|leg_expiry_fallback|order_ack_unrecorded|leg_sl_hit|leg_target_hit|leg_trail_armed|leg_trail_advanced|overall_sl_hit|overall_target_hit|lock_profit_armed|lock_profit_floor_advanced|lock_profit_triggered|trail_to_entry_activated|eod_squareoff|expiry_squareoff|tick_source_switched_to_polling|tick_source_switched_to_ws|tick_source_stale|recovery_succeeded|recovery_failed)$";
}

/// <summary>POST /api/v1/strategy/list</summary>
public sealed class StrategyListRequest : ApiKeyRequest
{
    [RegularExpression(StrategyVocabulary.Statuses)] [JsonPropertyName("status")] public string? Status { get; set; }
    [StringLength(100)] [JsonPropertyName("q")] public string? Q { get; set; }
}

/// <summary>Names one strategy and nothing else: the whole body of /status, /stop and /close_all.</summary>
public class StrategyRefRequest : ApiKeyRequest
{
    [Required, Range(1, int.MaxValue)] [JsonPropertyName("strategy_id")] public int? StrategyId { get; set; }
}

/// <summary>POST /api/v1/strategy/start. The mode is required and never defaulted: the default a hurried reader would reach for is the one that places real orders.</summary>
public sealed class StrategyStartRequest : StrategyRefRequest
{
    [Required, RegularExpression("^(live|sandbox)$")] [JsonPropertyName("mode")] public string? Mode { get; set; }
}

/// <summary>POST /api/v1/strategy/close_leg</summary>
public sealed class StrategyCloseLegRequest : StrategyRefRequest
{
    [Required, Range(1, int.MaxValue)] [JsonPropertyName("leg_id")] public int? LegId { get; set; }
}

/// <summary>POST /api/v1/strategy/runs</summary>
public sealed class StrategyRunsRequest : StrategyRefRequest
{
    [Range(1, 500)] [JsonPropertyName("limit")] public int Limit { get; set; } = 100;
}

/// <summary>POST /api/v1/strategy/orders</summary>
public sealed class StrategyOrdersRequest : StrategyRefRequest
{
    [Range(1, int.MaxValue)] [JsonPropertyName("run_id")] public int? RunId { get; set; }
}

/// <summary>POST /api/v1/strategy/events. The limit is bounded rather than clamped so a caller learns the value was refused.</summary>
public sealed class StrategyEventsRequest : StrategyRefRequest
{
    [Range(1, int.MaxValue)] [JsonPropertyName("run_id")] public int? RunId { get; set; }
    [RegularExpression(StrategyVocabulary.EventKinds)] [JsonPropertyName("kind")] public string? Kind { get; set; }
    [RegularExpression(StrategyVocabulary.EventSeverities)] [JsonPropertyName("severity")] public string? Severity { get; set; }
    [Range(1, 1000)] [JsonPropertyName("limit")] public int Limit { get; set; } = 500;
}
