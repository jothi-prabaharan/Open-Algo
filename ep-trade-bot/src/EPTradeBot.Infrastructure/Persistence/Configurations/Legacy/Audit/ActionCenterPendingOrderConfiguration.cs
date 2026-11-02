// Generated from OpenAlgo table 'pending_orders'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Audit;

internal sealed class ActionCenterPendingOrderConfiguration : IEntityTypeConfiguration<ActionCenterPendingOrder>
{
    public void Configure(EntityTypeBuilder<ActionCenterPendingOrder> b)
    {
        b.ToTable("pending_orders", schema: "audit");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.UserId).HasColumnName("user_id").HasMaxLength(255).IsRequired();
        b.Property(x => x.ApiType).HasColumnName("api_type").HasMaxLength(50).IsRequired();
        b.Property(x => x.OrderData).HasColumnName("order_data").IsRequired();
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CreatedAtIst).HasColumnName("created_at_ist").HasMaxLength(50);
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(20);
        b.Property(x => x.ApprovedAt).HasColumnName("approved_at");
        b.Property(x => x.ApprovedAtIst).HasColumnName("approved_at_ist").HasMaxLength(50);
        b.Property(x => x.ApprovedBy).HasColumnName("approved_by").HasMaxLength(255);
        b.Property(x => x.RejectedAt).HasColumnName("rejected_at");
        b.Property(x => x.RejectedAtIst).HasColumnName("rejected_at_ist").HasMaxLength(50);
        b.Property(x => x.RejectedBy).HasColumnName("rejected_by").HasMaxLength(255);
        b.Property(x => x.RejectedReason).HasColumnName("rejected_reason");
        b.Property(x => x.BrokerOrderId).HasColumnName("broker_order_id").HasMaxLength(255);
        b.Property(x => x.BrokerStatus).HasColumnName("broker_status").HasMaxLength(20);
        b.HasIndex(x => new { x.UserId, x.Status }).HasDatabaseName("ix_pending_orders_user_id_status");
        b.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_pending_orders_created_at");
    }
}
