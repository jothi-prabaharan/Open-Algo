// Generated from OpenAlgo table 'alert_log'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Audit;

internal sealed class AlertLogConfiguration : IEntityTypeConfiguration<AlertLog>
{
    public void Configure(EntityTypeBuilder<AlertLog> b)
    {
        b.ToTable("alert_log", schema: "audit");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.UserId).HasColumnName("user_id").HasMaxLength(80).IsRequired();
        b.Property(x => x.AlertId).HasColumnName("alert_id").HasMaxLength(64).IsRequired();
        b.Property(x => x.Title).HasColumnName("title").HasMaxLength(200).IsRequired();
        b.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(24).IsRequired();
        b.Property(x => x.Condition).HasColumnName("condition").HasMaxLength(24).IsRequired();
        b.Property(x => x.Symbol).HasColumnName("symbol").HasMaxLength(60).IsRequired();
        b.Property(x => x.Exchange).HasColumnName("exchange").HasMaxLength(20).IsRequired();
        b.Property(x => x.Interval).HasColumnName("interval").HasMaxLength(16).IsRequired();
        b.Property(x => x.Price).HasColumnName("price");
        b.Property(x => x.Message).HasColumnName("message");
        b.Property(x => x.Delivered).HasColumnName("delivered").HasMaxLength(64).IsRequired();
        b.Property(x => x.FiredAt).HasColumnName("fired_at").IsRequired();
        b.HasIndex(x => new { x.UserId, x.FiredAt }).HasDatabaseName("ix_alert_log_user_id_fired_at");
    }
}
