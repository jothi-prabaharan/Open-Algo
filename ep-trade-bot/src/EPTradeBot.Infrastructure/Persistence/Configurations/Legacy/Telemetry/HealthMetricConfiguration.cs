// Generated from OpenAlgo table 'health_metrics'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Telemetry;

internal sealed class HealthMetricConfiguration : IEntityTypeConfiguration<HealthMetric>
{
    public void Configure(EntityTypeBuilder<HealthMetric> b)
    {
        b.ToTable("health_metrics", schema: "telemetry");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.Timestamp).HasColumnName("timestamp");
        b.Property(x => x.FdCount).HasColumnName("fd_count");
        b.Property(x => x.FdLimit).HasColumnName("fd_limit");
        b.Property(x => x.FdUsagePercent).HasColumnName("fd_usage_percent");
        b.Property(x => x.FdAvailable).HasColumnName("fd_available");
        b.Property(x => x.FdStatus).HasColumnName("fd_status").HasMaxLength(20);
        b.Property(x => x.MemoryRssMb).HasColumnName("memory_rss_mb");
        b.Property(x => x.MemoryVmsMb).HasColumnName("memory_vms_mb");
        b.Property(x => x.MemoryPercent).HasColumnName("memory_percent");
        b.Property(x => x.MemoryAvailableMb).HasColumnName("memory_available_mb");
        b.Property(x => x.MemorySwapMb).HasColumnName("memory_swap_mb");
        b.Property(x => x.MemoryStatus).HasColumnName("memory_status").HasMaxLength(20);
        b.Property(x => x.DbConnectionsTotal).HasColumnName("db_connections_total");
        b.Property(x => x.DbConnections).HasColumnName("db_connections").HasColumnType("jsonb");
        b.Property(x => x.DbStatus).HasColumnName("db_status").HasMaxLength(20);
        b.Property(x => x.WsConnectionsTotal).HasColumnName("ws_connections_total");
        b.Property(x => x.WsConnections).HasColumnName("ws_connections").HasColumnType("jsonb");
        b.Property(x => x.WsTotalSymbols).HasColumnName("ws_total_symbols");
        b.Property(x => x.WsStatus).HasColumnName("ws_status").HasMaxLength(20);
        b.Property(x => x.ThreadCount).HasColumnName("thread_count");
        b.Property(x => x.StuckThreads).HasColumnName("stuck_threads");
        b.Property(x => x.ThreadDetails).HasColumnName("thread_details").HasColumnType("jsonb");
        b.Property(x => x.ThreadStatus).HasColumnName("thread_status").HasMaxLength(20);
        b.Property(x => x.ProcessDetails).HasColumnName("process_details").HasColumnType("jsonb");
        b.Property(x => x.OverallStatus).HasColumnName("overall_status").HasMaxLength(20);
    }
}
