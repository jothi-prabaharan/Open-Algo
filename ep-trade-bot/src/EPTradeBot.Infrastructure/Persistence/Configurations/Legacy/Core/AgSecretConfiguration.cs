// Generated from OpenAlgo table 'ag_secret'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class AgSecretConfiguration : IEntityTypeConfiguration<AgSecret>
{
    public void Configure(EntityTypeBuilder<AgSecret> b)
    {
        b.ToTable("ag_secret", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        b.Property(x => x.Ciphertext).HasColumnName("ciphertext").IsRequired();
        b.Property(x => x.Fingerprint).HasColumnName("fingerprint").HasMaxLength(80).IsRequired();
        b.Property(x => x.LastUsedAt).HasColumnName("last_used_at");
        b.HasIndex(x => x.Name).IsUnique().HasDatabaseName("ux_ag_secret_name");
    }
}
