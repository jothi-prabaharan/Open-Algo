// Generated from OpenAlgo table 'sm_strategy'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class SmStrategyConfiguration : IEntityTypeConfiguration<SmStrategy>
{
    public void Configure(EntityTypeBuilder<SmStrategy> b)
    {
        b.ToTable("sm_strategy", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.UserId).HasColumnName("user_id").HasMaxLength(80).IsRequired();
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        b.Property(x => x.StrategyKind).HasColumnName("strategy_kind").HasMaxLength(20).IsRequired();
        b.Property(x => x.Direction).HasColumnName("direction").HasMaxLength(20).IsRequired();
        b.Property(x => x.UniverseTab).HasColumnName("universe_tab").HasMaxLength(30).IsRequired();
        b.Property(x => x.Underlying).HasColumnName("underlying").HasMaxLength(50).IsRequired();
        b.Property(x => x.UnderlyingExchange).HasColumnName("underlying_exchange").HasMaxLength(20).IsRequired();
        b.Property(x => x.StrategyType).HasColumnName("strategy_type").HasMaxLength(20).IsRequired();
        b.Property(x => x.EntryTime).HasColumnName("entry_time");
        b.Property(x => x.ExitTime).HasColumnName("exit_time");
        b.Property(x => x.Product).HasColumnName("product").HasMaxLength(10).IsRequired();
        b.Property(x => x.Pricetype).HasColumnName("pricetype").HasMaxLength(10).IsRequired();
        b.Property(x => x.Legs).HasColumnName("legs").HasColumnType("jsonb").IsRequired();
        b.Property(x => x.OverallSlMtm).HasColumnName("overall_sl_mtm").HasPrecision(18, 2);
        b.Property(x => x.OverallTargetMtm).HasColumnName("overall_target_mtm").HasPrecision(18, 2);
        b.Property(x => x.LockProfit).HasColumnName("lock_profit").HasColumnType("jsonb");
        b.Property(x => x.TrailSlToEntry).HasColumnName("trail_sl_to_entry").IsRequired();
        b.Property(x => x.Scheduler).HasColumnName("scheduler").HasColumnType("jsonb");
        b.Property(x => x.LiveEnabled).HasColumnName("live_enabled").IsRequired();
        b.Property(x => x.WebhookTokenHash).HasColumnName("webhook_token_hash").HasMaxLength(64).IsRequired();
        b.Property(x => x.WebhookIpAllowlist).HasColumnName("webhook_ip_allowlist").HasColumnType("jsonb");
        b.Property(x => x.WebhookLocked).HasColumnName("webhook_locked").IsRequired();
        b.Property(x => x.DailyLossLimitInr).HasColumnName("daily_loss_limit_inr").HasPrecision(18, 2);
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        b.Property(x => x.CurrentRunId).HasColumnName("current_run_id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        b.HasIndex(x => x.WebhookTokenHash).IsUnique().HasDatabaseName("ux_sm_strategy_webhook_token_hash");
        b.HasIndex(x => new { x.UserId, x.Name }).IsUnique().HasDatabaseName("ux_sm_strategy_user_id_name");
        b.HasIndex(x => x.UserId).HasDatabaseName("ix_sm_strategy_user_id");
        b.HasIndex(x => new { x.UserId, x.Status }).HasDatabaseName("ix_sm_strategy_user_id_status");
        b.HasIndex(x => x.Status).HasDatabaseName("ix_sm_strategy_status");
    }
}
