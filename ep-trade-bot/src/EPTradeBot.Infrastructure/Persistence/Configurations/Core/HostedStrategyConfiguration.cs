using EPTradeBot.Core.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Core;

internal sealed class HostedStrategyConfiguration : IEntityTypeConfiguration<HostedStrategy>
{
    public void Configure(EntityTypeBuilder<HostedStrategy> b)
    {
        b.ToTable("hosted_strategies", schema: Schemas.Core);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.ScriptPath).HasMaxLength(260).IsRequired();
        b.Property(x => x.ExecutionMode).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.ScheduleDays).HasConversion<int>();
        b.Property(x => x.LastError).HasMaxLength(500);
        b.HasIndex(x => x.Name).IsUnique().HasDatabaseName("ux_hosted_strategies_name");
        b.HasOne<BrokerAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.SetNull);
    }
}
