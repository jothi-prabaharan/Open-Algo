// Generated from OpenAlgo table 'traffic_logs'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Audit;

internal sealed class TrafficLogConfiguration : IEntityTypeConfiguration<TrafficLog>
{
    public void Configure(EntityTypeBuilder<TrafficLog> b)
    {
        b.ToTable("traffic_logs", schema: "audit");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.Timestamp).HasColumnName("timestamp");
        b.Property(x => x.ClientIp).HasColumnName("client_ip").HasMaxLength(50).IsRequired();
        b.Property(x => x.Method).HasColumnName("method").HasMaxLength(10).IsRequired();
        b.Property(x => x.Path).HasColumnName("path").HasMaxLength(500).IsRequired();
        b.Property(x => x.StatusCode).HasColumnName("status_code").IsRequired();
        b.Property(x => x.DurationMs).HasColumnName("duration_ms").IsRequired();
        b.Property(x => x.Host).HasColumnName("host").HasMaxLength(500);
        b.Property(x => x.Error).HasColumnName("error").HasMaxLength(500);
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.HasIndex(x => x.ClientIp).HasDatabaseName("ix_traffic_logs_client_ip");
        b.HasIndex(x => new { x.ClientIp, x.Timestamp }).HasDatabaseName("ix_traffic_logs_client_ip_timestamp");
        b.HasIndex(x => x.StatusCode).HasDatabaseName("ix_traffic_logs_status_code");
        b.HasIndex(x => x.UserId).HasDatabaseName("ix_traffic_logs_user_id");
        b.HasIndex(x => x.Timestamp).HasDatabaseName("ix_traffic_logs_timestamp");
    }
}
