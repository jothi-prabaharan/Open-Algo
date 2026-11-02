using System.ComponentModel.DataAnnotations;

namespace EPTradeBot.Core.Entities.Core;

/// <summary>How an automated order reaches the broker.</summary>
public enum ExecutionMode
{
    /// <summary>Dispatched immediately after the guard and risk checks.</summary>
    Auto = 0,

    /// <summary>Held in the Action Center until approved.</summary>
    SemiAuto = 1,
}

[Flags]
public enum TradingDays
{
    None = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 4,
    Thursday = 8,
    Friday = 16,
    Saturday = 32,
    Sunday = 64,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
}

/// <summary>A strategy hosted by the platform (/python), with its IST schedule.</summary>
public sealed class HostedStrategy
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    [Required, StringLength(100, MinimumLength = 1)]
    public string Name { get; set; } = "";

    /// <summary>Path of the script, relative to the strategies directory.</summary>
    [Required, StringLength(260, MinimumLength = 1)]
    public string ScriptPath { get; set; } = "";

    public Guid? AccountId { get; set; }

    public ExecutionMode ExecutionMode { get; set; } = ExecutionMode.SemiAuto;

    public bool IsEnabled { get; set; }

    public bool IsScheduled { get; set; }

    /// <summary>Start time, Asia/Kolkata wall clock.</summary>
    public TimeOnly? ScheduleStartIst { get; set; }

    /// <summary>Stop time, Asia/Kolkata wall clock.</summary>
    public TimeOnly? ScheduleStopIst { get; set; }

    public TradingDays ScheduleDays { get; set; } = TradingDays.Weekdays;

    public bool IsRunning { get; set; }

    public int? ProcessId { get; set; }

    public DateTime? LastStartedAtUtc { get; set; }

    public DateTime? LastStoppedAtUtc { get; set; }

    [StringLength(500)]
    public string? LastError { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
