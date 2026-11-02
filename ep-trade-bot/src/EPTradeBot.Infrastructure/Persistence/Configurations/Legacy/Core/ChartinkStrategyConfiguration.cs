// Generated from OpenAlgo table 'chartink_strategies'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class ChartinkStrategyConfiguration : IEntityTypeConfiguration<ChartinkStrategy>
{
    public void Configure(EntityTypeBuilder<ChartinkStrategy> b)
    {
        b.ToTable("chartink_strategies", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(255).IsRequired();
        b.Property(x => x.WebhookId).HasColumnName("webhook_id").HasMaxLength(36).IsRequired();
        b.Property(x => x.UserId).HasColumnName("user_id").HasMaxLength(255).IsRequired();
        b.Property(x => x.IsActive).HasColumnName("is_active");
        b.Property(x => x.IsIntraday).HasColumnName("is_intraday");
        b.Property(x => x.StartTime).HasColumnName("start_time").HasMaxLength(5);
        b.Property(x => x.EndTime).HasColumnName("end_time").HasMaxLength(5);
        b.Property(x => x.SquareoffTime).HasColumnName("squareoff_time").HasMaxLength(5);
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.HasIndex(x => x.WebhookId).IsUnique().HasDatabaseName("ux_chartink_strategies_webhook_id");
    }
}
