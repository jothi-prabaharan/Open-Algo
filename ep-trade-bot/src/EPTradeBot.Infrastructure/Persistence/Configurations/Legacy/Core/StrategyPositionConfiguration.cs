// Generated from OpenAlgo table 'strategy_positions'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class StrategyPositionConfiguration : IEntityTypeConfiguration<StrategyPosition>
{
    public void Configure(EntityTypeBuilder<StrategyPosition> b)
    {
        b.ToTable("strategy_positions", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.UserId).HasColumnName("user_id").HasMaxLength(64).IsRequired();
        b.Property(x => x.Strategy).HasColumnName("strategy").HasMaxLength(120).IsRequired();
        b.Property(x => x.Symbol).HasColumnName("symbol").HasMaxLength(64).IsRequired();
        b.Property(x => x.Exchange).HasColumnName("exchange").HasMaxLength(20).IsRequired();
        b.Property(x => x.Product).HasColumnName("product").HasMaxLength(20).IsRequired();
        b.Property(x => x.Quantity).HasColumnName("quantity").IsRequired();
        b.Property(x => x.AveragePrice).HasColumnName("average_price").IsRequired();
        b.Property(x => x.RealizedPnl).HasColumnName("realized_pnl").IsRequired();
        b.Property(x => x.TodayRealizedPnl).HasColumnName("today_realized_pnl").IsRequired();
        b.Property(x => x.TradeDate).HasColumnName("trade_date").HasMaxLength(10);
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        b.HasIndex(x => new { x.UserId, x.Strategy, x.Symbol, x.Exchange, x.Product }).IsUnique().HasDatabaseName("ux_strategy_positions_user_id_strategy_symbol_exchange_product");
        b.HasIndex(x => x.Strategy).HasDatabaseName("ix_strategy_positions_strategy");
        b.HasIndex(x => x.Symbol).HasDatabaseName("ix_strategy_positions_symbol");
        b.HasIndex(x => x.UserId).HasDatabaseName("ix_strategy_positions_user_id");
    }
}
