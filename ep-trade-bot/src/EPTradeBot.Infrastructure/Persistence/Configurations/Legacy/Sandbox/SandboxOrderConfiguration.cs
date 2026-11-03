// Generated from OpenAlgo table 'sandbox_orders'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Sandbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Sandbox;

internal sealed class SandboxOrderConfiguration : IEntityTypeConfiguration<SandboxOrder>
{
    public void Configure(EntityTypeBuilder<SandboxOrder> b)
    {
        b.ToTable("sandbox_orders", schema: "sandbox");
        b.HasKey(x => x.Id);
        b.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.Orderid).HasColumnName("orderid").HasMaxLength(50).IsRequired();
        b.Property(x => x.UserId).HasColumnName("user_id").HasMaxLength(50).IsRequired();
        b.Property(x => x.Strategy).HasColumnName("strategy").HasMaxLength(100);
        b.Property(x => x.Symbol).HasColumnName("symbol").HasMaxLength(50).IsRequired();
        b.Property(x => x.Exchange).HasColumnName("exchange").HasMaxLength(20).IsRequired();
        b.Property(x => x.Action).HasColumnName("action").HasMaxLength(10).IsRequired();
        b.Property(x => x.Quantity).HasColumnName("quantity").IsRequired();
        b.Property(x => x.Price).HasColumnName("price").HasPrecision(10, 2);
        b.Property(x => x.TriggerPrice).HasColumnName("trigger_price").HasPrecision(10, 2);
        b.Property(x => x.PriceType).HasColumnName("price_type").HasMaxLength(20).IsRequired();
        b.Property(x => x.Product).HasColumnName("product").HasMaxLength(20).IsRequired();
        b.Property(x => x.OrderStatus).HasColumnName("order_status").HasMaxLength(20).IsRequired();
        b.Property(x => x.AveragePrice).HasColumnName("average_price").HasPrecision(10, 2);
        b.Property(x => x.FilledQuantity).HasColumnName("filled_quantity");
        b.Property(x => x.PendingQuantity).HasColumnName("pending_quantity").IsRequired();
        b.Property(x => x.RejectionReason).HasColumnName("rejection_reason");
        b.Property(x => x.MarginBlocked).HasColumnName("margin_blocked").HasPrecision(10, 2);
        b.Property(x => x.GttLegId).HasColumnName("gtt_leg_id");
        b.Property(x => x.OrderTimestamp).HasColumnName("order_timestamp").IsRequired();
        b.Property(x => x.UpdateTimestamp).HasColumnName("update_timestamp").IsRequired();
        b.HasIndex(x => x.Orderid).IsUnique().HasDatabaseName("ux_sandbox_orders_orderid");
        b.HasIndex(x => x.GttLegId).IsUnique().HasDatabaseName("ux_sandbox_orders_gtt_leg_id");
        b.HasIndex(x => x.OrderStatus).HasDatabaseName("ix_sandbox_orders_order_status");
        b.HasIndex(x => x.UserId).HasDatabaseName("ix_sandbox_orders_user_id");
        b.HasIndex(x => x.Exchange).HasDatabaseName("ix_sandbox_orders_exchange");
        b.HasIndex(x => new { x.UserId, x.OrderStatus }).HasDatabaseName("ix_sandbox_orders_user_id_order_status");
        b.HasIndex(x => new { x.Symbol, x.Exchange }).HasDatabaseName("ix_sandbox_orders_symbol_exchange");
        b.HasIndex(x => x.Symbol).HasDatabaseName("ix_sandbox_orders_symbol");
    }
}
