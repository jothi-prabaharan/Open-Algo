using EPTradeBot.Core.Entities.MarketData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.MarketData;

internal sealed class CandleConfiguration : IEntityTypeConfiguration<Candle>
{
    public void Configure(EntityTypeBuilder<Candle> b)
    {
        b.ToTable("candles", schema: Schemas.MarketData);

        // The primary key's B-tree serves point and range reads per instrument and interval.
        b.HasKey(x => new { x.Symbol, x.Exchange, x.Interval, x.Ts });
        b.Property(x => x.Symbol).HasMaxLength(64);
        b.Property(x => x.Exchange).HasMaxLength(16);
        b.Property(x => x.Interval).HasMaxLength(8);

        // Candles arrive in time order, so physical order tracks Ts and a BRIN index
        // answers time-range scans across all instruments at a fraction of a B-tree's size.
        b.HasIndex(x => x.Ts).HasMethod("brin").HasDatabaseName("ix_candles_ts_brin");
    }
}

internal sealed class HistorifyWatchlistItemConfiguration : IEntityTypeConfiguration<HistorifyWatchlistItem>
{
    public void Configure(EntityTypeBuilder<HistorifyWatchlistItem> b)
    {
        b.ToTable("watchlist", schema: Schemas.MarketData);
        b.HasKey(x => x.Id);
        b.Property(x => x.Symbol).HasMaxLength(64).IsRequired();
        b.Property(x => x.Exchange).HasMaxLength(16).IsRequired();
        b.Property(x => x.DisplayName).HasMaxLength(120);
        b.HasIndex(x => new { x.Symbol, x.Exchange }).IsUnique().HasDatabaseName("ux_market_data_watchlist_symbol_exchange");
    }
}

internal sealed class DataCatalogEntryConfiguration : IEntityTypeConfiguration<DataCatalogEntry>
{
    public void Configure(EntityTypeBuilder<DataCatalogEntry> b)
    {
        b.ToTable("data_catalog", schema: Schemas.MarketData);
        b.HasKey(x => x.Id);
        b.Property(x => x.Symbol).HasMaxLength(64).IsRequired();
        b.Property(x => x.Exchange).HasMaxLength(16).IsRequired();
        b.Property(x => x.Interval).HasMaxLength(8).IsRequired();
        b.HasIndex(x => new { x.Symbol, x.Exchange, x.Interval }).IsUnique().HasDatabaseName("ux_data_catalog_symbol_exchange_interval");
    }
}

internal sealed class DownloadJobConfiguration : IEntityTypeConfiguration<DownloadJob>
{
    public void Configure(EntityTypeBuilder<DownloadJob> b)
    {
        b.ToTable("download_jobs", schema: Schemas.MarketData);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasMaxLength(64).ValueGeneratedNever();
        b.Property(x => x.JobType).HasMaxLength(32).IsRequired();
        b.Property(x => x.Status).HasMaxLength(16).IsRequired();
        b.Property(x => x.Interval).HasMaxLength(8);
        b.Property(x => x.StartDate).HasMaxLength(10);
        b.Property(x => x.EndDate).HasMaxLength(10);
        b.Property(x => x.ConfigJson).HasColumnType("jsonb");
        b.HasIndex(x => x.Status).HasDatabaseName("ix_download_jobs_status");
    }
}

internal sealed class DownloadJobItemConfiguration : IEntityTypeConfiguration<DownloadJobItem>
{
    public void Configure(EntityTypeBuilder<DownloadJobItem> b)
    {
        b.ToTable("job_items", schema: Schemas.MarketData);
        b.HasKey(x => x.Id);
        b.Property(x => x.JobId).HasMaxLength(64).IsRequired();
        b.Property(x => x.Symbol).HasMaxLength(64).IsRequired();
        b.Property(x => x.Exchange).HasMaxLength(16).IsRequired();
        b.Property(x => x.Status).HasMaxLength(16).IsRequired();
        b.HasIndex(x => x.JobId).HasDatabaseName("ix_job_items_job_id");
        b.HasOne<DownloadJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SymbolMetadataConfiguration : IEntityTypeConfiguration<SymbolMetadata>
{
    public void Configure(EntityTypeBuilder<SymbolMetadata> b)
    {
        b.ToTable("symbol_metadata", schema: Schemas.MarketData);
        b.HasKey(x => new { x.Symbol, x.Exchange });
        b.Property(x => x.Symbol).HasMaxLength(64);
        b.Property(x => x.Exchange).HasMaxLength(16);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Expiry).HasMaxLength(16);
        b.Property(x => x.InstrumentType).HasMaxLength(16);
    }
}

internal sealed class HistorifyScheduleConfiguration : IEntityTypeConfiguration<HistorifySchedule>
{
    public void Configure(EntityTypeBuilder<HistorifySchedule> b)
    {
        b.ToTable("historify_schedules", schema: Schemas.MarketData);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasMaxLength(64).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.ScheduleType).HasMaxLength(16).IsRequired();
        b.Property(x => x.IntervalUnit).HasMaxLength(16);
        b.Property(x => x.TimeOfDay).HasMaxLength(5);
        b.Property(x => x.DownloadSource).HasMaxLength(16).IsRequired();
        b.Property(x => x.DataInterval).HasMaxLength(8).IsRequired();
        b.Property(x => x.Status).HasMaxLength(16).IsRequired();
        b.Property(x => x.SchedulerJobId).HasMaxLength(100);
        b.Property(x => x.LastRunStatus).HasMaxLength(16);
    }
}

internal sealed class HistorifyScheduleExecutionConfiguration : IEntityTypeConfiguration<HistorifyScheduleExecution>
{
    public void Configure(EntityTypeBuilder<HistorifyScheduleExecution> b)
    {
        b.ToTable("historify_schedule_executions", schema: Schemas.MarketData);
        b.HasKey(x => x.Id);
        b.Property(x => x.ScheduleId).HasMaxLength(64).IsRequired();
        b.Property(x => x.DownloadJobId).HasMaxLength(64);
        b.Property(x => x.Status).HasMaxLength(16).IsRequired();
        b.HasIndex(x => x.ScheduleId).HasDatabaseName("ix_historify_schedule_executions_schedule_id");
        b.HasOne<HistorifySchedule>().WithMany().HasForeignKey(x => x.ScheduleId).OnDelete(DeleteBehavior.Cascade);
    }
}
