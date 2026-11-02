// Generated from OpenAlgo table 'sandbox_positions'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Sandbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Sandbox;

internal sealed class SandboxPositionConfiguration : IEntityTypeConfiguration<SandboxPosition>
{
    public void Configure(EntityTypeBuilder<SandboxPosition> b)
    {
        b.ToTable("sandbox_positions", schema: "sandbox");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.UserId).HasColumnName("user_id").HasMaxLength(50).IsRequired();
        b.Property(x => x.Symbol).HasColumnName("symbol").HasMaxLength(50).IsRequired();
        b.Property(x => x.Exchange).HasColumnName("exchange").HasMaxLength(20).IsRequired();
        b.Property(x => x.Product).HasColumnName("product").HasMaxLength(20).IsRequired();
        b.Property(x => x.Quantity).HasColumnName("quantity").IsRequired();
        b.Property(x => x.AveragePrice).HasColumnName("average_price").HasPrecision(10, 2).IsRequired();
        b.Property(x => x.Ltp).HasColumnName("ltp").HasPrecision(10, 2);
        b.Property(x => x.Pnl).HasColumnName("pnl").HasPrecision(10, 2);
        b.Property(x => x.PnlPercent).HasColumnName("pnl_percent").HasPrecision(10, 4);
        b.Property(x => x.AccumulatedRealizedPnl).HasColumnName("accumulated_realized_pnl").HasPrecision(10, 2);
        b.Property(x => x.TodayRealizedPnl).HasColumnName("today_realized_pnl").HasPrecision(10, 2);
        b.Property(x => x.MarginBlocked).HasColumnName("margin_blocked").HasPrecision(15, 2);
        b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        b.HasIndex(x => new { x.UserId, x.Symbol, x.Exchange, x.Product }).IsUnique().HasDatabaseName("ux_sandbox_positions_user_id_symbol_exchange_product");
        b.HasIndex(x => x.UserId).HasDatabaseName("ix_sandbox_positions_user_id");
        b.HasIndex(x => new { x.UserId, x.Product }).HasDatabaseName("ix_sandbox_positions_user_id_product");
        b.HasIndex(x => x.Symbol).HasDatabaseName("ix_sandbox_positions_symbol");
        b.HasIndex(x => x.Exchange).HasDatabaseName("ix_sandbox_positions_exchange");
    }
}
