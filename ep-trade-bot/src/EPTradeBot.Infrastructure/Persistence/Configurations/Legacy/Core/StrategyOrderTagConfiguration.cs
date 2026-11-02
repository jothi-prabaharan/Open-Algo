// Generated from OpenAlgo table 'strategy_order_tags'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class StrategyOrderTagConfiguration : IEntityTypeConfiguration<StrategyOrderTag>
{
    public void Configure(EntityTypeBuilder<StrategyOrderTag> b)
    {
        b.ToTable("strategy_order_tags", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.Orderid).HasColumnName("orderid").HasMaxLength(64).IsRequired();
        b.Property(x => x.UserId).HasColumnName("user_id").HasMaxLength(64).IsRequired();
        b.Property(x => x.Strategy).HasColumnName("strategy").HasMaxLength(120).IsRequired();
        b.Property(x => x.Symbol).HasColumnName("symbol").HasMaxLength(64).IsRequired();
        b.Property(x => x.Exchange).HasColumnName("exchange").HasMaxLength(20).IsRequired();
        b.Property(x => x.Product).HasColumnName("product").HasMaxLength(20).IsRequired();
        b.Property(x => x.AppliedQuantity).HasColumnName("applied_quantity").IsRequired();
        b.Property(x => x.AppliedNotional).HasColumnName("applied_notional").IsRequired();
        b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        b.HasIndex(x => x.Orderid).IsUnique().HasDatabaseName("ux_strategy_order_tags_orderid");
        b.HasIndex(x => x.UserId).HasDatabaseName("ix_strategy_order_tags_user_id");
        b.HasIndex(x => x.Strategy).HasDatabaseName("ix_strategy_order_tags_strategy");
    }
}
