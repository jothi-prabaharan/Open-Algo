using EPTradeBot.Core.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Core;

internal sealed class BrokerAccountConfiguration : IEntityTypeConfiguration<BrokerAccount>
{
    public void Configure(EntityTypeBuilder<BrokerAccount> b)
    {
        b.ToTable("broker_accounts", schema: Schemas.Core);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.DisplayName).HasMaxLength(64).IsRequired();
        b.Property(x => x.Broker).HasMaxLength(32).IsRequired();
        b.Property(x => x.ClientId).HasMaxLength(64);
        b.Property(x => x.AccountType).HasConversion<string>().HasMaxLength(8).IsRequired();
        b.Property(x => x.KillSwitchReason).HasMaxLength(500);
        b.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
        b.HasIndex(x => new { x.Broker, x.ClientId }).IsUnique().HasDatabaseName("ux_broker_accounts_broker_client_id");
        b.HasIndex(x => x.AccountType).HasDatabaseName("ix_broker_accounts_account_type");
        b.HasOne(x => x.RiskSetting).WithOne().HasForeignKey<AccountRiskSetting>(r => r.AccountId).OnDelete(DeleteBehavior.Cascade);
    }
}
