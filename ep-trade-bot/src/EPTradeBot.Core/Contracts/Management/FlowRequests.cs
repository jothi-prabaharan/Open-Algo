using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace EPTradeBot.Core.Contracts.Management;

/// <summary>POST /api/v1/flows: create (or import) a workflow.</summary>
public sealed class CreateFlowRequest : IScheduleSettings
{
    [Required, StringLength(100, MinimumLength = 1)] [JsonPropertyName("name")] public string Name { get; set; } = "";
    [StringLength(500)] [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("nodes")] public JsonArray? Nodes { get; set; }
    [JsonPropertyName("edges")] public JsonArray? Edges { get; set; }
    [JsonPropertyName("account_id")] public Guid? AccountId { get; set; }
    [RegularExpression("^(auto|semi_auto)$", ErrorMessage = "Execution mode must be auto or semi_auto.")]
    [JsonPropertyName("execution_mode")] public string? ExecutionMode { get; set; }
    [RegularExpression("^[A-Za-z_]{2,12}$", ErrorMessage = "Exchange must be a code such as NSE, NFO, MCX or CRYPTO.")]
    [JsonPropertyName("exchange")] public string? Exchange { get; set; }
    [JsonPropertyName("is_scheduled")] public bool? IsScheduled { get; set; }
    [RegularExpression(ScheduleFormat.Time, ErrorMessage = "Schedule time must be HH:mm, India time.")]
    [JsonPropertyName("schedule_time")] public string? ScheduleTime { get; set; }
    [JsonPropertyName("schedule_days")] public List<string>? ScheduleDays { get; set; }

    string? IScheduleSettings.ScheduleStart => ScheduleTime;

    string? IScheduleSettings.ScheduleStop => null;
}

/// <summary>PUT /api/v1/flows/{id}: change what is sent; what is left out stays as it was.</summary>
public sealed class UpdateFlowRequest : IScheduleSettings
{
    [StringLength(100, MinimumLength = 1)] [JsonPropertyName("name")] public string? Name { get; set; }
    [StringLength(500)] [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("nodes")] public JsonArray? Nodes { get; set; }
    [JsonPropertyName("edges")] public JsonArray? Edges { get; set; }
    [JsonPropertyName("account_id")] public Guid? AccountId { get; set; }
    [RegularExpression("^(auto|semi_auto)$", ErrorMessage = "Execution mode must be auto or semi_auto.")]
    [JsonPropertyName("execution_mode")] public string? ExecutionMode { get; set; }
    [RegularExpression("^[A-Za-z_]{2,12}$", ErrorMessage = "Exchange must be a code such as NSE, NFO, MCX or CRYPTO.")]
    [JsonPropertyName("exchange")] public string? Exchange { get; set; }
    [JsonPropertyName("is_scheduled")] public bool? IsScheduled { get; set; }
    [RegularExpression(ScheduleFormat.Time, ErrorMessage = "Schedule time must be HH:mm, India time.")]
    [JsonPropertyName("schedule_time")] public string? ScheduleTime { get; set; }
    [JsonPropertyName("schedule_days")] public List<string>? ScheduleDays { get; set; }

    string? IScheduleSettings.ScheduleStart => ScheduleTime;

    string? IScheduleSettings.ScheduleStop => null;
}
