// Generated from OpenAlgo table 'sandbox_gtt'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Sandbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Sandbox;

internal sealed class SandboxGttConfiguration : IEntityTypeConfiguration<SandboxGtt>
{
    public void Configure(EntityTypeBuilder<SandboxGtt> b)
    {
        b.ToTable("sandbox_gtt", schema: "sandbox");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.GttId).HasColumnName("gtt_id").HasMaxLength(50).IsRequired();
        b.Property(x => x.UserId).HasColumnName("user_id").HasMaxLength(50).IsRequired();
        b.Property(x => x.Strategy).HasColumnName("strategy").HasMaxLength(100);
        b.Property(x => x.TriggerType).HasColumnName("trigger_type").HasMaxLength(10).IsRequired();
        b.Property(x => x.Symbol).HasColumnName("symbol").HasMaxLength(50).IsRequired();
        b.Property(x => x.Exchange).HasColumnName("exchange").HasMaxLength(20).IsRequired();
        b.Property(x => x.LastPrice).HasColumnName("last_price").HasPrecision(10, 2).IsRequired();
        b.Property(x => x.GttStatus).HasColumnName("gtt_status").HasMaxLength(20).IsRequired();
        b.Property(x => x.MarginBlocked).HasColumnName("margin_blocked").HasPrecision(15, 2).IsRequired();
        b.Property(x => x.ExpiresAt).HasColumnName("expires_at");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        b.HasIndex(x => x.GttId).IsUnique().HasDatabaseName("ux_sandbox_gtt_gtt_id");
        b.HasIndex(x => new { x.UserId, x.GttStatus }).HasDatabaseName("ix_sandbox_gtt_user_id_gtt_status");
        b.HasIndex(x => x.Symbol).HasDatabaseName("ix_sandbox_gtt_symbol");
        b.HasIndex(x => x.GttStatus).HasDatabaseName("ix_sandbox_gtt_gtt_status");
        b.HasIndex(x => new { x.Symbol, x.Exchange }).HasDatabaseName("ix_sandbox_gtt_symbol_exchange");
        b.HasIndex(x => x.UserId).HasDatabaseName("ix_sandbox_gtt_user_id");
        b.HasIndex(x => x.Exchange).HasDatabaseName("ix_sandbox_gtt_exchange");
    }
}
