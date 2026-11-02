// Generated from OpenAlgo table 'strategy_pending_fills'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class StrategyPendingFillConfiguration : IEntityTypeConfiguration<StrategyPendingFill>
{
    public void Configure(EntityTypeBuilder<StrategyPendingFill> b)
    {
        b.ToTable("strategy_pending_fills", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.Orderid).HasColumnName("orderid").HasMaxLength(64).IsRequired();
        b.Property(x => x.FilledQuantity).HasColumnName("filled_quantity").IsRequired();
        b.Property(x => x.AveragePrice).HasColumnName("average_price").IsRequired();
        b.Property(x => x.Action).HasColumnName("action").HasMaxLength(10).IsRequired();
        b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        b.HasIndex(x => x.Orderid).HasDatabaseName("ix_strategy_pending_fills_orderid");
    }
}
