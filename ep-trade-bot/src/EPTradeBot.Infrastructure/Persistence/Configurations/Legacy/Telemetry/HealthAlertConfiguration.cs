// Generated from OpenAlgo table 'health_alerts'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Telemetry;

internal sealed class HealthAlertConfiguration : IEntityTypeConfiguration<HealthAlert>
{
    public void Configure(EntityTypeBuilder<HealthAlert> b)
    {
        b.ToTable("health_alerts", schema: "telemetry");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.Timestamp).HasColumnName("timestamp");
        b.Property(x => x.AlertType).HasColumnName("alert_type").HasMaxLength(50);
        b.Property(x => x.Severity).HasColumnName("severity").HasMaxLength(20);
        b.Property(x => x.MetricName).HasColumnName("metric_name").HasMaxLength(50);
        b.Property(x => x.MetricValue).HasColumnName("metric_value");
        b.Property(x => x.ThresholdValue).HasColumnName("threshold_value");
        b.Property(x => x.Message).HasColumnName("message").HasMaxLength(500);
        b.Property(x => x.Acknowledged).HasColumnName("acknowledged");
        b.Property(x => x.AcknowledgedAt).HasColumnName("acknowledged_at");
        b.Property(x => x.Resolved).HasColumnName("resolved");
        b.Property(x => x.ResolvedAt).HasColumnName("resolved_at");
    }
}
