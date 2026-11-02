using EPTradeBot.Core.Entities.Audit;
using EPTradeBot.Core.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Audit;

internal static class AuditColumns
{
    /// <summary>UTC instant plus the IST wall clock it corresponds to, stored without a zone.</summary>
    public static void Times<T>(EntityTypeBuilder<T> b, string table) where T : class, IImmutableAuditRecord
    {
        b.Property(x => x.OccurredAtUtc).IsRequired();
        b.Property(x => x.OccurredAtIst).HasColumnType("timestamp without time zone").IsRequired();
        b.HasIndex(x => x.OccurredAtUtc).HasMethod("brin").HasDatabaseName($"ix_{table}_occurred_at_brin");
    }
}

internal sealed class OrderAuditLogConfiguration : IEntityTypeConfiguration<OrderAuditLog>
{
    public void Configure(EntityTypeBuilder<OrderAuditLog> b)
    {
        b.ToTable("order_audit_log", schema: Schemas.Audit);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        AuditColumns.Times(b, "order_audit_log");
        b.Property(x => x.AccountType).HasMaxLength(8);
        b.Property(x => x.Environment).HasMaxLength(32).IsRequired();
        b.Property(x => x.Source).HasConversion<string>().HasMaxLength(24);
        b.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Action).HasMaxLength(32).IsRequired();
        b.Property(x => x.Symbol).HasMaxLength(64);
        b.Property(x => x.Exchange).HasMaxLength(16);
        b.Property(x => x.Quantity).HasPrecision(18, 4);
        b.Property(x => x.Price).HasPrecision(18, 4);
        b.Property(x => x.Product).HasMaxLength(8);
        b.Property(x => x.PriceType).HasMaxLength(8);
        b.Property(x => x.BrokerOrderId).HasMaxLength(64);
        b.Property(x => x.Reason).HasMaxLength(1000);
        b.Property(x => x.RequestJson).HasColumnType("jsonb");
        b.Property(x => x.CorrelationId).HasMaxLength(64);
        b.Property(x => x.Actor).HasMaxLength(80);
        b.HasIndex(x => new { x.AccountId, x.OccurredAtUtc }).HasDatabaseName("ix_order_audit_log_account_time");
        b.HasOne<BrokerAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class KillSwitchEventConfiguration : IEntityTypeConfiguration<KillSwitchEvent>
{
    public void Configure(EntityTypeBuilder<KillSwitchEvent> b)
    {
        b.ToTable("kill_switch_events", schema: Schemas.Audit);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        AuditColumns.Times(b, "kill_switch_events");
        b.Property(x => x.Scope).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Actor).HasMaxLength(80).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.ResultJson).HasColumnType("jsonb").IsRequired();
        b.HasOne<BrokerAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ActionCenterDecisionConfiguration : IEntityTypeConfiguration<ActionCenterDecision>
{
    public void Configure(EntityTypeBuilder<ActionCenterDecision> b)
    {
        b.ToTable("action_center_decisions", schema: Schemas.Audit);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        AuditColumns.Times(b, "action_center_decisions");
        b.Property(x => x.Decision).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Actor).HasMaxLength(80).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(500);
        b.HasIndex(x => x.PendingOrderId).HasDatabaseName("ix_action_center_decisions_pending_order_id");
        b.HasOne<EPTradeBot.Core.Entities.Legacy.Audit.ActionCenterPendingOrder>().WithMany().HasForeignKey(x => x.PendingOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<BrokerAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
    }
}
