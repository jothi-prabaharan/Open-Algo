namespace EPTradeBot.Core.Entities.MarketData;

/// <summary>Port of Historify <c>watchlist</c>.</summary>
public sealed class HistorifyWatchlistItem
{
    public int Id { get; set; }
    public string Symbol { get; set; } = "";
    public string Exchange { get; set; } = "";
    public string? DisplayName { get; set; }
    public DateTime AddedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Port of Historify <c>data_catalog</c>: what is stored per symbol and interval.</summary>
public sealed class DataCatalogEntry
{
    public int Id { get; set; }
    public string Symbol { get; set; } = "";
    public string Exchange { get; set; } = "";
    public string Interval { get; set; } = "";
    public DateTime? FirstTs { get; set; }
    public DateTime? LastTs { get; set; }
    public long RecordCount { get; set; }
    public DateTime? LastDownloadAtUtc { get; set; }
}

/// <summary>Port of Historify <c>download_jobs</c>.</summary>
public sealed class DownloadJob
{
    public string Id { get; set; } = "";
    public string JobType { get; set; } = "";
    public string Status { get; set; } = "";
    public int TotalSymbols { get; set; }
    public int CompletedSymbols { get; set; }
    public int FailedSymbols { get; set; }
    public string? Interval { get; set; }
    public string? StartDate { get; set; }
    public string? EndDate { get; set; }
    public string? ConfigJson { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>Port of Historify <c>job_items</c>.</summary>
public sealed class DownloadJobItem
{
    public int Id { get; set; }
    public string JobId { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Exchange { get; set; } = "";
    public string Status { get; set; } = "";
    public int RecordsDownloaded { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}

/// <summary>Port of Historify <c>symbol_metadata</c>.</summary>
public sealed class SymbolMetadata
{
    public string Symbol { get; set; } = "";
    public string Exchange { get; set; } = "";
    public string? Name { get; set; }
    public string? Expiry { get; set; }
    public double? Strike { get; set; }
    public int? LotSize { get; set; }
    public string? InstrumentType { get; set; }
    public double? TickSize { get; set; }
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Port of Historify <c>historify_schedules</c>.</summary>
public sealed class HistorifySchedule
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string ScheduleType { get; set; } = "";
    public int? IntervalValue { get; set; }
    public string? IntervalUnit { get; set; }
    public string? TimeOfDay { get; set; }
    public string DownloadSource { get; set; } = "watchlist";
    public string DataInterval { get; set; } = "";
    public int LookbackDays { get; set; } = 1;
    public bool IsEnabled { get; set; } = true;
    public bool IsPaused { get; set; }
    public string Status { get; set; } = "idle";
    public string? SchedulerJobId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastRunAtUtc { get; set; }
    public DateTime? NextRunAtUtc { get; set; }
    public string? LastRunStatus { get; set; }
    public int TotalRuns { get; set; }
    public int SuccessfulRuns { get; set; }
    public int FailedRuns { get; set; }
}

/// <summary>Port of Historify <c>historify_schedule_executions</c>.</summary>
public sealed class HistorifyScheduleExecution
{
    public int Id { get; set; }
    public string ScheduleId { get; set; } = "";
    public string? DownloadJobId { get; set; }
    public string Status { get; set; } = "";
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
    public int SymbolsProcessed { get; set; }
    public int SymbolsSuccess { get; set; }
    public int SymbolsFailed { get; set; }
    public int RecordsDownloaded { get; set; }
    public string? ErrorMessage { get; set; }
}
