// Generated from OpenAlgo table 'sm_strategy_event'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class SmStrategyEventConfiguration : IEntityTypeConfiguration<SmStrategyEvent>
{
    public void Configure(EntityTypeBuilder<SmStrategyEvent> b)
    {
        b.ToTable("sm_strategy_event", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.RunId).HasColumnName("run_id");
        b.Property(x => x.StrategyId).HasColumnName("strategy_id").IsRequired();
        b.Property(x => x.UserId).HasColumnName("user_id").HasMaxLength(80).IsRequired();
        b.Property(x => x.Ts).HasColumnName("ts").IsRequired();
        b.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(40).IsRequired();
        b.Property(x => x.Severity).HasColumnName("severity").HasMaxLength(10).IsRequired();
        b.Property(x => x.LegId).HasColumnName("leg_id");
        b.Property(x => x.Message).HasColumnName("message").IsRequired();
        b.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
        b.HasIndex(x => new { x.StrategyId, x.Ts }).HasDatabaseName("ix_sm_strategy_event_strategy_id_ts");
        b.HasIndex(x => x.UserId).HasDatabaseName("ix_sm_strategy_event_user_id");
        b.HasIndex(x => new { x.RunId, x.Ts }).HasDatabaseName("ix_sm_strategy_event_run_id_ts");
        b.HasIndex(x => x.RunId).HasDatabaseName("ix_sm_strategy_event_run_id");
        b.HasIndex(x => x.StrategyId).HasDatabaseName("ix_sm_strategy_event_strategy_id");
        b.HasOne<SmStrategyRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<SmStrategy>().WithMany().HasForeignKey(x => x.StrategyId).OnDelete(DeleteBehavior.Cascade);
    }
}
