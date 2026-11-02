// Generated from OpenAlgo table 'flow_workflows'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class FlowWorkflowConfiguration : IEntityTypeConfiguration<FlowWorkflow>
{
    public void Configure(EntityTypeBuilder<FlowWorkflow> b)
    {
        b.ToTable("flow_workflows", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(255).IsRequired();
        b.Property(x => x.Description).HasColumnName("description");
        b.Property(x => x.Nodes).HasColumnName("nodes").HasColumnType("jsonb");
        b.Property(x => x.Edges).HasColumnName("edges").HasColumnType("jsonb");
        b.Property(x => x.IsActive).HasColumnName("is_active");
        b.Property(x => x.ScheduleJobId).HasColumnName("schedule_job_id").HasMaxLength(255);
        b.Property(x => x.WebhookToken).HasColumnName("webhook_token").HasMaxLength(64);
        b.Property(x => x.WebhookSecret).HasColumnName("webhook_secret").HasMaxLength(64);
        b.Property(x => x.WebhookEnabled).HasColumnName("webhook_enabled");
        b.Property(x => x.WebhookAuthType).HasColumnName("webhook_auth_type").HasMaxLength(20);
        b.Property(x => x.ApiKey).HasColumnName("api_key").HasMaxLength(255);
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.HasIndex(x => x.WebhookToken).IsUnique().HasDatabaseName("ux_flow_workflows_webhook_token");
    }
}
