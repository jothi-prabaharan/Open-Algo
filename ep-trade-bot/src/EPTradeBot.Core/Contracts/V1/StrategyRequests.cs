using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace EPTradeBot.Core.Contracts.V1;

/// <summary>POST /api/v1/strategy/list</summary>
public sealed class StrategyListRequest : ApiKeyRequest
{
    [RegularExpression(ApiPatterns.StrategyStatus)] [JsonPropertyName("status")] public string? Status { get; set; }
    [StringLength(100)] [JsonPropertyName("q")] public string? Query { get; set; }
}

/// <summary>Body of /strategy/status, /stop and /close_all; the id travels in the body for external callers.</summary>
public class StrategyRefRequest : ApiKeyRequest
{
    [Range(1, int.MaxValue)] [JsonPropertyName("strategy_id")] public int StrategyId { get; set; }
}

/// <summary>
/// POST /api/v1/strategy/start. <see cref="Mode"/> is required with no default:
/// a caller that forgets it must be refused, never defaulted to live.
/// </summary>
public sealed class StrategyStartRequest : StrategyRefRequest
{
    [Required(AllowEmptyStrings = false), RegularExpression(ApiPatterns.RunMode)] [JsonPropertyName("mode")] public string? Mode { get; set; }
}

/// <summary>POST /api/v1/strategy/close_leg</summary>
public sealed class StrategyCloseLegRequest : StrategyRefRequest
{
    [Range(1, int.MaxValue)] [JsonPropertyName("leg_id")] public int LegId { get; set; }
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

/// <summary>POST /api/v1/strategy/events. The limit is bounded, never clamped.</summary>
public sealed class StrategyEventsRequest : StrategyRefRequest
{
    [Range(1, int.MaxValue)] [JsonPropertyName("run_id")] public int? RunId { get; set; }
    [RegularExpression(ApiPatterns.EventKind)] [JsonPropertyName("kind")] public string? Kind { get; set; }
    [RegularExpression(ApiPatterns.EventSeverity)] [JsonPropertyName("severity")] public string? Severity { get; set; }
    [Range(1, 1000)] [JsonPropertyName("limit")] public int Limit { get; set; } = 500;
}
