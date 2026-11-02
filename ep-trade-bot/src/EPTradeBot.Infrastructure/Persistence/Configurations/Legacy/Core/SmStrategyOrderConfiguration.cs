// Generated from OpenAlgo table 'sm_strategy_order'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class SmStrategyOrderConfiguration : IEntityTypeConfiguration<SmStrategyOrder>
{
    public void Configure(EntityTypeBuilder<SmStrategyOrder> b)
    {
        b.ToTable("sm_strategy_order", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.RunId).HasColumnName("run_id").IsRequired();
        b.Property(x => x.LegId).HasColumnName("leg_id").IsRequired();
        b.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(30).IsRequired();
        b.Property(x => x.PositionRef).HasColumnName("position_ref").HasMaxLength(32);
        b.Property(x => x.BrokerOrderId).HasColumnName("broker_order_id").HasMaxLength(100);
        b.Property(x => x.Symbol).HasColumnName("symbol").HasMaxLength(100).IsRequired();
        b.Property(x => x.Exchange).HasColumnName("exchange").HasMaxLength(20).IsRequired();
        b.Property(x => x.Action).HasColumnName("action").HasMaxLength(10).IsRequired();
        b.Property(x => x.Qty).HasColumnName("qty").IsRequired();
        b.Property(x => x.Product).HasColumnName("product").HasMaxLength(10);
        b.Property(x => x.Pricetype).HasColumnName("pricetype").HasMaxLength(10).IsRequired();
        b.Property(x => x.Price).HasColumnName("price").HasPrecision(18, 4).IsRequired();
        b.Property(x => x.TriggerPrice).HasColumnName("trigger_price").HasPrecision(18, 4).IsRequired();
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        b.Property(x => x.PlacedAt).HasColumnName("placed_at").IsRequired();
        b.Property(x => x.FilledAt).HasColumnName("filled_at");
        b.Property(x => x.AvgFillPrice).HasColumnName("avg_fill_price").HasPrecision(18, 4);
        b.Property(x => x.FilledQty).HasColumnName("filled_qty");
        b.Property(x => x.RejectReason).HasColumnName("reject_reason");
        b.HasIndex(x => x.BrokerOrderId).HasDatabaseName("ix_sm_strategy_order_broker_order_id");
        b.HasIndex(x => new { x.RunId, x.PlacedAt }).HasDatabaseName("ix_sm_strategy_order_run_id_placed_at");
        b.HasIndex(x => new { x.RunId, x.LegId, x.PositionRef }).HasDatabaseName("ix_sm_strategy_order_run_id_leg_id_position_ref");
        b.HasIndex(x => x.RunId).HasDatabaseName("ix_sm_strategy_order_run_id");
        b.HasOne<SmStrategyRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
    }
}
