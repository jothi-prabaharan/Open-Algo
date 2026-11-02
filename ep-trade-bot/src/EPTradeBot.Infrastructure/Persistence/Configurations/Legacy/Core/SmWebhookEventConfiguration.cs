// Generated from OpenAlgo table 'sm_webhook_event'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class SmWebhookEventConfiguration : IEntityTypeConfiguration<SmWebhookEvent>
{
    public void Configure(EntityTypeBuilder<SmWebhookEvent> b)
    {
        b.ToTable("sm_webhook_event", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.StrategyId).HasColumnName("strategy_id");
        b.Property(x => x.Action).HasColumnName("action").HasMaxLength(20);
        b.Property(x => x.Mode).HasColumnName("mode").HasMaxLength(10);
        b.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
        b.Property(x => x.Ip).HasColumnName("ip").HasMaxLength(45);
        b.Property(x => x.UserAgent).HasColumnName("user_agent").HasMaxLength(255);
        b.Property(x => x.ReceivedAt).HasColumnName("received_at").IsRequired();
        b.Property(x => x.Result).HasColumnName("result").HasMaxLength(50).IsRequired();
        b.Property(x => x.Error).HasColumnName("error");
        b.HasIndex(x => new { x.StrategyId, x.ReceivedAt }).HasDatabaseName("ix_sm_webhook_event_strategy_id_received_at");
        b.HasIndex(x => x.StrategyId).HasDatabaseName("ix_sm_webhook_event_strategy_id");
        b.HasOne<SmStrategy>().WithMany().HasForeignKey(x => x.StrategyId).OnDelete(DeleteBehavior.Cascade);
    }
}
