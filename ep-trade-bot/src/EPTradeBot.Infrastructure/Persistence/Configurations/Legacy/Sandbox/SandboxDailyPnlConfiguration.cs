// Generated from OpenAlgo table 'sandbox_daily_pnl'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Sandbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Sandbox;

internal sealed class SandboxDailyPnlConfiguration : IEntityTypeConfiguration<SandboxDailyPnl>
{
    public void Configure(EntityTypeBuilder<SandboxDailyPnl> b)
    {
        b.ToTable("sandbox_daily_pnl", schema: "sandbox");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.UserId).HasColumnName("user_id").HasMaxLength(50).IsRequired();
        b.Property(x => x.Date).HasColumnName("date").IsRequired();
        b.Property(x => x.RealizedPnl).HasColumnName("realized_pnl").HasPrecision(15, 2);
        b.Property(x => x.PositionsUnrealizedPnl).HasColumnName("positions_unrealized_pnl").HasPrecision(15, 2);
        b.Property(x => x.HoldingsUnrealizedPnl).HasColumnName("holdings_unrealized_pnl").HasPrecision(15, 2);
        b.Property(x => x.TotalMtm).HasColumnName("total_mtm").HasPrecision(15, 2);
        b.Property(x => x.AvailableBalance).HasColumnName("available_balance").HasPrecision(15, 2);
        b.Property(x => x.UsedMargin).HasColumnName("used_margin").HasPrecision(15, 2);
        b.Property(x => x.PortfolioValue).HasColumnName("portfolio_value").HasPrecision(15, 2);
        b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        b.HasIndex(x => new { x.UserId, x.Date }).IsUnique().HasDatabaseName("ux_sandbox_daily_pnl_user_id_date");
        b.HasIndex(x => x.UserId).HasDatabaseName("ix_sandbox_daily_pnl_user_id");
        b.HasIndex(x => x.Date).HasDatabaseName("ix_sandbox_daily_pnl_date");
    }
}
