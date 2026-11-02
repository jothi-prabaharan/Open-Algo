// Generated from OpenAlgo table 'ag_audit'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Audit;

internal sealed class AgAuditConfiguration : IEntityTypeConfiguration<AgAudit>
{
    public void Configure(EntityTypeBuilder<AgAudit> b)
    {
        b.ToTable("ag_audit", schema: "audit");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.Ts).HasColumnName("ts").IsRequired();
        b.Property(x => x.Phase).HasColumnName("phase").HasMaxLength(16).IsRequired();
        b.Property(x => x.Tool).HasColumnName("tool").HasMaxLength(120).IsRequired();
        b.Property(x => x.ConversationId).HasColumnName("conversation_id");
        b.Property(x => x.RunId).HasColumnName("run_id").HasMaxLength(120);
        b.Property(x => x.Args).HasColumnName("args").HasColumnType("jsonb");
        b.Property(x => x.RiskVerdict).HasColumnName("risk_verdict").HasMaxLength(60);
        b.Property(x => x.Ok).HasColumnName("ok");
        b.Property(x => x.Response).HasColumnName("response").HasColumnType("jsonb");
        b.Property(x => x.OrderIds).HasColumnName("order_ids").HasColumnType("jsonb");
        b.HasIndex(x => x.ConversationId).HasDatabaseName("ix_ag_audit_conversation_id");
        b.HasIndex(x => x.RunId).HasDatabaseName("ix_ag_audit_run_id");
        b.HasIndex(x => new { x.ConversationId, x.Ts }).HasDatabaseName("ix_ag_audit_conversation_id_ts");
        b.HasIndex(x => new { x.RunId, x.Ts }).HasDatabaseName("ix_ag_audit_run_id_ts");
    }
}
