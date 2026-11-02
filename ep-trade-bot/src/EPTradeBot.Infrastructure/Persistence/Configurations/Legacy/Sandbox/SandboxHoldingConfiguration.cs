// Generated from OpenAlgo table 'sandbox_holdings'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Sandbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Sandbox;

internal sealed class SandboxHoldingConfiguration : IEntityTypeConfiguration<SandboxHolding>
{
    public void Configure(EntityTypeBuilder<SandboxHolding> b)
    {
        b.ToTable("sandbox_holdings", schema: "sandbox");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.UserId).HasColumnName("user_id").HasMaxLength(50).IsRequired();
        b.Property(x => x.Symbol).HasColumnName("symbol").HasMaxLength(50).IsRequired();
        b.Property(x => x.Exchange).HasColumnName("exchange").HasMaxLength(20).IsRequired();
        b.Property(x => x.Quantity).HasColumnName("quantity").IsRequired();
        b.Property(x => x.AveragePrice).HasColumnName("average_price").HasPrecision(10, 2).IsRequired();
        b.Property(x => x.Ltp).HasColumnName("ltp").HasPrecision(10, 2);
        b.Property(x => x.Pnl).HasColumnName("pnl").HasPrecision(10, 2);
        b.Property(x => x.PnlPercent).HasColumnName("pnl_percent").HasPrecision(10, 4);
        b.Property(x => x.SettlementDate).HasColumnName("settlement_date").IsRequired();
        b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        b.HasIndex(x => new { x.UserId, x.Symbol, x.Exchange }).IsUnique().HasDatabaseName("ux_sandbox_holdings_user_id_symbol_exchange");
        b.HasIndex(x => x.Symbol).HasDatabaseName("ix_sandbox_holdings_symbol");
        b.HasIndex(x => x.Exchange).HasDatabaseName("ix_sandbox_holdings_exchange");
        b.HasIndex(x => x.UserId).HasDatabaseName("ix_sandbox_holdings_user_id");
    }
}
