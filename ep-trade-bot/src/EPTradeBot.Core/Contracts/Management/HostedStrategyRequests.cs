using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace EPTradeBot.Core.Contracts.Management;

/// <summary>POST /api/v1/hosted-strategies: register a strategy script to be hosted, optionally on an IST schedule.</summary>
public sealed class CreateHostedStrategyRequest : IScheduleSettings
{
    [Required, StringLength(100, MinimumLength = 1)] [JsonPropertyName("name")] public string Name { get; set; } = "";
    [Required, StringLength(260, MinimumLength = 1)] [JsonPropertyName("script_path")] public string ScriptPath { get; set; } = "";
    [JsonPropertyName("account_id")] public Guid? AccountId { get; set; }
    [RegularExpression("^(auto|semi_auto)$", ErrorMessage = "Execution mode must be auto or semi_auto.")]
    [JsonPropertyName("execution_mode")] public string? ExecutionMode { get; set; }
    [RegularExpression("^[A-Za-z_]{2,12}$", ErrorMessage = "Exchange must be a code such as NSE, NFO, MCX or CRYPTO.")]
    [JsonPropertyName("exchange")] public string? Exchange { get; set; }
    [JsonPropertyName("is_enabled")] public bool? IsEnabled { get; set; }
    [JsonPropertyName("is_scheduled")] public bool? IsScheduled { get; set; }
    [RegularExpression(ScheduleFormat.Time, ErrorMessage = "Start time must be HH:mm, India time.")]
    [JsonPropertyName("schedule_start")] public string? ScheduleStart { get; set; }
    [RegularExpression(ScheduleFormat.Time, ErrorMessage = "Stop time must be HH:mm, India time.")]
    [JsonPropertyName("schedule_stop")] public string? ScheduleStop { get; set; }
    [JsonPropertyName("schedule_days")] public List<string>? ScheduleDays { get; set; }
}

/// <summary>PUT /api/v1/hosted-strategies/{id}/schedule: replace the schedule.</summary>
public sealed class UpdateScheduleRequest : IScheduleSettings
{
    [RegularExpression("^[A-Za-z_]{2,12}$", ErrorMessage = "Exchange must be a code such as NSE, NFO, MCX or CRYPTO.")]
    [JsonPropertyName("exchange")] public string? Exchange { get; set; }
    [JsonPropertyName("is_enabled")] public bool? IsEnabled { get; set; }
    [JsonPropertyName("is_scheduled")] public bool? IsScheduled { get; set; }
    [RegularExpression(ScheduleFormat.Time, ErrorMessage = "Start time must be HH:mm, India time.")]
    [JsonPropertyName("schedule_start")] public string? ScheduleStart { get; set; }
    [RegularExpression(ScheduleFormat.Time, ErrorMessage = "Stop time must be HH:mm, India time.")]
    [JsonPropertyName("schedule_stop")] public string? ScheduleStop { get; set; }
    [JsonPropertyName("schedule_days")] public List<string>? ScheduleDays { get; set; }
}

public interface IScheduleSettings
{
    string? ScheduleStart { get; }

    string? ScheduleStop { get; }

    List<string>? ScheduleDays { get; }
}

public static class ScheduleFormat
{
    public const string Time = "^([01][0-9]|2[0-3]):[0-5][0-9]$";

    public static readonly IReadOnlyList<string> DayNames = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];
}
