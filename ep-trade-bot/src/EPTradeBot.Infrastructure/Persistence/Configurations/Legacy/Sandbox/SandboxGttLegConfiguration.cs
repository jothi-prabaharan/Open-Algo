// Generated from OpenAlgo table 'sandbox_gtt_legs'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Sandbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Sandbox;

internal sealed class SandboxGttLegConfiguration : IEntityTypeConfiguration<SandboxGttLeg>
{
    public void Configure(EntityTypeBuilder<SandboxGttLeg> b)
    {
        b.ToTable("sandbox_gtt_legs", schema: "sandbox");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.GttId).HasColumnName("gtt_id").HasMaxLength(50).IsRequired();
        b.Property(x => x.LegNumber).HasColumnName("leg_number").IsRequired();
        b.Property(x => x.TriggerPrice).HasColumnName("trigger_price").HasPrecision(10, 2).IsRequired();
        b.Property(x => x.TriggerDirection).HasColumnName("trigger_direction").HasMaxLength(5).IsRequired();
        b.Property(x => x.Action).HasColumnName("action").HasMaxLength(10).IsRequired();
        b.Property(x => x.Quantity).HasColumnName("quantity").IsRequired();
        b.Property(x => x.Price).HasColumnName("price").HasPrecision(10, 2).IsRequired();
        b.Property(x => x.Pricetype).HasColumnName("pricetype").HasMaxLength(10).IsRequired();
        b.Property(x => x.Product).HasColumnName("product").HasMaxLength(10).IsRequired();
        b.Property(x => x.LegStatus).HasColumnName("leg_status").HasMaxLength(20).IsRequired();
        b.Property(x => x.TriggeredOrderId).HasColumnName("triggered_order_id").HasMaxLength(50);
        b.Property(x => x.LegMargin).HasColumnName("leg_margin").HasPrecision(15, 2).IsRequired();
        b.Property(x => x.ClaimedAt).HasColumnName("claimed_at");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        b.HasIndex(x => x.GttId).HasDatabaseName("ix_sandbox_gtt_legs_gtt_id");
        b.HasIndex(x => new { x.LegStatus, x.ClaimedAt }).HasDatabaseName("ix_sandbox_gtt_legs_leg_status_claimed_at");
        b.HasOne<SandboxGtt>().WithMany().HasPrincipalKey(p => p.GttId).HasForeignKey(x => x.GttId).OnDelete(DeleteBehavior.Cascade);
    }
}
