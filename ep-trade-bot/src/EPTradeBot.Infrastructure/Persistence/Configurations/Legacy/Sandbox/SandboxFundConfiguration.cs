// Generated from OpenAlgo table 'sandbox_funds'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Sandbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Sandbox;

internal sealed class SandboxFundConfiguration : IEntityTypeConfiguration<SandboxFund>
{
    public void Configure(EntityTypeBuilder<SandboxFund> b)
    {
        b.ToTable("sandbox_funds", schema: "sandbox");
        b.HasKey(x => x.Id);
        b.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.UserId).HasColumnName("user_id").HasMaxLength(50).IsRequired();
        b.Property(x => x.TotalCapital).HasColumnName("total_capital").HasPrecision(15, 2);
        b.Property(x => x.AvailableBalance).HasColumnName("available_balance").HasPrecision(15, 2);
        b.Property(x => x.UsedMargin).HasColumnName("used_margin").HasPrecision(15, 2);
        b.Property(x => x.RealizedPnl).HasColumnName("realized_pnl").HasPrecision(15, 2);
        b.Property(x => x.TodayRealizedPnl).HasColumnName("today_realized_pnl").HasPrecision(15, 2);
        b.Property(x => x.UnrealizedPnl).HasColumnName("unrealized_pnl").HasPrecision(15, 2);
        b.Property(x => x.TotalPnl).HasColumnName("total_pnl").HasPrecision(15, 2);
        b.Property(x => x.LastResetDate).HasColumnName("last_reset_date").IsRequired();
        b.Property(x => x.ResetCount).HasColumnName("reset_count");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        b.HasIndex(x => x.UserId).IsUnique().HasDatabaseName("ux_sandbox_funds_user_id");
    }
}
