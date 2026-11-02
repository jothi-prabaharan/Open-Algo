// Generated from OpenAlgo table 'sm_strategy_run'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class SmStrategyRunConfiguration : IEntityTypeConfiguration<SmStrategyRun>
{
    public void Configure(EntityTypeBuilder<SmStrategyRun> b)
    {
        b.ToTable("sm_strategy_run", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.StrategyId).HasColumnName("strategy_id").IsRequired();
        b.Property(x => x.Mode).HasColumnName("mode").HasMaxLength(10).IsRequired();
        b.Property(x => x.Broker).HasColumnName("broker").HasMaxLength(50).IsRequired();
        b.Property(x => x.BrokerAccount).HasColumnName("broker_account").HasMaxLength(255);
        b.Property(x => x.StartedAt).HasColumnName("started_at").IsRequired();
        b.Property(x => x.StoppedAt).HasColumnName("stopped_at");
        b.Property(x => x.StopReason).HasColumnName("stop_reason").HasMaxLength(30);
        b.Property(x => x.StopRequestedAt).HasColumnName("stop_requested_at");
        b.Property(x => x.StopRequestedReason).HasColumnName("stop_requested_reason").HasMaxLength(30);
        b.Property(x => x.PnlRealized).HasColumnName("pnl_realized").HasPrecision(18, 2).IsRequired();
        b.Property(x => x.PnlPeak).HasColumnName("pnl_peak").HasPrecision(18, 2).IsRequired();
        b.Property(x => x.PnlTrough).HasColumnName("pnl_trough").HasPrecision(18, 2).IsRequired();
        b.Property(x => x.TriggerSource).HasColumnName("trigger_source").HasMaxLength(20).IsRequired();
        b.Property(x => x.WebhookEventId).HasColumnName("webhook_event_id");
        b.Property(x => x.ResolvedExpiries).HasColumnName("resolved_expiries").HasColumnType("jsonb");
        b.HasIndex(x => new { x.StrategyId, x.StartedAt }).HasDatabaseName("ix_sm_strategy_run_strategy_id_started_at");
        b.HasIndex(x => x.StrategyId).HasDatabaseName("ix_sm_strategy_run_strategy_id");
        b.HasOne<SmStrategy>().WithMany().HasForeignKey(x => x.StrategyId).OnDelete(DeleteBehavior.Cascade);
    }
}
