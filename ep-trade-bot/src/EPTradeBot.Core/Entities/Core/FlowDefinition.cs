using System.ComponentModel.DataAnnotations;

namespace EPTradeBot.Core.Entities.Core;

/// <summary>A Flow workflow: a graph of nodes the platform runs on a trigger, trading on one account.</summary>
public sealed class FlowDefinition
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    [Required, StringLength(100, MinimumLength = 1)]
    public string Name { get; set; } = "";

    [StringLength(500)]
    public string? Description { get; set; }

    /// <summary>The nodes, as the editor writes them (a JSON array).</summary>
    [Required]
    public string NodesJson { get; set; } = "[]";

    /// <summary>The edges, as the editor writes them (a JSON array).</summary>
    [Required]
    public string EdgesJson { get; set; } = "[]";

    /// <summary>The account the workflow's orders go to. A workflow without one can be saved but not run.</summary>
    public Guid? AccountId { get; set; }

    /// <summary>Whether its orders are sent at once or wait in the Action Center.</summary>
    public ExecutionMode ExecutionMode { get; set; } = ExecutionMode.SemiAuto;

    /// <summary>Whether its triggers (schedule, webhook, price alert) are live.</summary>
    public bool IsActive { get; set; }

    /// <summary>Hash of the webhook token; the token itself is shown once, when it is made.</summary>
    [StringLength(64)]
    public string? WebhookTokenHash { get; set; }

    public bool IsScheduled { get; set; }

    /// <summary>Start time, Asia/Kolkata wall clock.</summary>
    public TimeOnly? ScheduleTimeIst { get; set; }

    public TradingDays ScheduleDays { get; set; } = TradingDays.Weekdays;

    /// <summary>The exchange whose calendar decides which days a schedule runs.</summary>
    [Required, RegularExpression("^[A-Z_]{2,12}$")]
    public string Exchange { get; set; } = "NSE";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastRunAtUtc { get; set; }

    [StringLength(20)]
    public string? LastRunStatus { get; set; }
}

/// <summary>One run of a workflow and what it did.</summary>
public sealed class FlowRunRecord
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid FlowId { get; set; }

    /// <summary>running, completed or failed.</summary>
    [Required, StringLength(20)]
    public string Status { get; set; } = "running";

    /// <summary>manual, schedule, webhook or price_alert.</summary>
    [Required, StringLength(20)]
    public string Trigger { get; set; } = "manual";

    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? FinishedAtUtc { get; set; }

    [StringLength(1000)]
    public string? Error { get; set; }

    [Required]
    public string LogsJson { get; set; } = "[]";

    [Required]
    public string ErrorsJson { get; set; } = "[]";
}
