// Generated from OpenAlgo table 'sm_strategy_checkpoint'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class SmStrategyCheckpointConfiguration : IEntityTypeConfiguration<SmStrategyCheckpoint>
{
    public void Configure(EntityTypeBuilder<SmStrategyCheckpoint> b)
    {
        b.ToTable("sm_strategy_checkpoint", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.RunId).HasColumnName("run_id").IsRequired();
        b.Property(x => x.Ts).HasColumnName("ts").IsRequired();
        b.Property(x => x.PnlRealized).HasColumnName("pnl_realized").HasPrecision(18, 2).IsRequired();
        b.Property(x => x.PnlUnrealized).HasColumnName("pnl_unrealized").HasPrecision(18, 2).IsRequired();
        b.Property(x => x.PnlTotal).HasColumnName("pnl_total").HasPrecision(18, 2).IsRequired();
        b.Property(x => x.PnlPeak).HasColumnName("pnl_peak").HasPrecision(18, 2).IsRequired();
        b.Property(x => x.PnlTrough).HasColumnName("pnl_trough").HasPrecision(18, 2).IsRequired();
        b.Property(x => x.LockFloor).HasColumnName("lock_floor").HasPrecision(18, 2);
        b.Property(x => x.TrailToEntryActive).HasColumnName("trail_to_entry_active").IsRequired();
        b.Property(x => x.LegState).HasColumnName("leg_state").HasColumnType("jsonb").IsRequired();
        b.HasIndex(x => new { x.RunId, x.Ts }).HasDatabaseName("ix_sm_strategy_checkpoint_run_id_ts");
        b.HasIndex(x => x.RunId).HasDatabaseName("ix_sm_strategy_checkpoint_run_id");
        b.HasOne<SmStrategyRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
    }
}
