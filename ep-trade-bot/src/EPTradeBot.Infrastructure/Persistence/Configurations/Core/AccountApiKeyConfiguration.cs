using EPTradeBot.Core.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Core;

internal sealed class AccountApiKeyConfiguration : IEntityTypeConfiguration<AccountApiKey>
{
    public void Configure(EntityTypeBuilder<AccountApiKey> b)
    {
        b.ToTable("account_api_keys", schema: Schemas.Core);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.KeyHash).HasMaxLength(64).IsFixedLength().IsRequired();
        b.Property(x => x.Label).HasMaxLength(64).IsRequired();
        b.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
        b.HasIndex(x => x.KeyHash).IsUnique().HasDatabaseName("ux_account_api_keys_key_hash");
        b.HasIndex(x => x.AccountId).HasDatabaseName("ix_account_api_keys_account_id");
        b.HasOne<BrokerAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
    }
}
