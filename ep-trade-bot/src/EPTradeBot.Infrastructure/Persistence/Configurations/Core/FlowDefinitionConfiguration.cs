using EPTradeBot.Core.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Core;

internal sealed class FlowDefinitionConfiguration : IEntityTypeConfiguration<FlowDefinition>
{
    public void Configure(EntityTypeBuilder<FlowDefinition> b)
    {
        b.ToTable("flow_definitions", schema: Schemas.Core);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.NodesJson).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.EdgesJson).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.ExecutionMode).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.ScheduleDays).HasConversion<int>();
        b.Property(x => x.WebhookTokenHash).HasMaxLength(64);
        b.Property(x => x.Exchange).HasMaxLength(12).IsRequired().HasDefaultValue("NSE");
        b.Property(x => x.LastRunStatus).HasMaxLength(20);
        b.HasIndex(x => x.Name).IsUnique().HasDatabaseName("ux_flow_definitions_name");
        b.HasIndex(x => x.WebhookTokenHash).IsUnique().HasDatabaseName("ux_flow_definitions_webhook_token_hash");
        b.HasOne<BrokerAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class FlowRunRecordConfiguration : IEntityTypeConfiguration<FlowRunRecord>
{
    public void Configure(EntityTypeBuilder<FlowRunRecord> b)
    {
        b.ToTable("flow_runs", schema: Schemas.Core);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.Trigger).HasMaxLength(20).IsRequired();
        b.Property(x => x.Error).HasMaxLength(1000);
        b.Property(x => x.LogsJson).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.ErrorsJson).HasColumnType("jsonb").IsRequired();
        b.HasIndex(x => new { x.FlowId, x.StartedAtUtc }).HasDatabaseName("ix_flow_runs_flow_started");
        b.HasOne<FlowDefinition>().WithMany().HasForeignKey(x => x.FlowId).OnDelete(DeleteBehavior.Cascade);
    }
}
