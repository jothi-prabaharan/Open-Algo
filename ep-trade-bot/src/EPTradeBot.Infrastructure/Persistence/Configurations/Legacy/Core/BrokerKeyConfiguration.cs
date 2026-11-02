// Generated from OpenAlgo table 'broker_keys'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class BrokerKeyConfiguration : IEntityTypeConfiguration<BrokerKey>
{
    public void Configure(EntityTypeBuilder<BrokerKey> b)
    {
        b.ToTable("broker_keys", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.Broker).HasColumnName("broker").HasMaxLength(50).IsRequired();
        b.Property(x => x.ApiKey).HasColumnName("api_key").IsRequired();
        b.Property(x => x.ApiSecret).HasColumnName("api_secret").IsRequired();
        b.Property(x => x.ApiKeyMarket).HasColumnName("api_key_market").IsRequired();
        b.Property(x => x.ApiSecretMarket).HasColumnName("api_secret_market").IsRequired();
        b.Property(x => x.RedirectUrl).HasColumnName("redirect_url").HasMaxLength(500).IsRequired();
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.HasIndex(x => x.Broker).IsUnique().HasDatabaseName("ux_broker_keys_broker");
    }
}
