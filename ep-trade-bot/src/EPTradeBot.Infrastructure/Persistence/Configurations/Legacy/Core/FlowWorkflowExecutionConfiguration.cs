// Generated from OpenAlgo table 'flow_workflow_executions'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class FlowWorkflowExecutionConfiguration : IEntityTypeConfiguration<FlowWorkflowExecution>
{
    public void Configure(EntityTypeBuilder<FlowWorkflowExecution> b)
    {
        b.ToTable("flow_workflow_executions", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.WorkflowId).HasColumnName("workflow_id").IsRequired();
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(50);
        b.Property(x => x.StartedAt).HasColumnName("started_at");
        b.Property(x => x.CompletedAt).HasColumnName("completed_at");
        b.Property(x => x.Logs).HasColumnName("logs").HasColumnType("jsonb");
        b.Property(x => x.Error).HasColumnName("error");
        b.HasOne<FlowWorkflow>().WithMany().HasForeignKey(x => x.WorkflowId).OnDelete(DeleteBehavior.Cascade);
    }
}
