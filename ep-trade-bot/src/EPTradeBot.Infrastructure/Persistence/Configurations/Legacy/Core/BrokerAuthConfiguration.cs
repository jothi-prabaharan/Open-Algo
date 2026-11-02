// Generated from OpenAlgo table 'auth'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class BrokerAuthConfiguration : IEntityTypeConfiguration<BrokerAuth>
{
    public void Configure(EntityTypeBuilder<BrokerAuth> b)
    {
        b.ToTable("auth", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(255).IsRequired();
        b.Property(x => x.Auth).HasColumnName("auth").IsRequired();
        b.Property(x => x.FeedToken).HasColumnName("feed_token");
        b.Property(x => x.Broker).HasColumnName("broker").HasMaxLength(20).IsRequired();
        b.Property(x => x.UserId).HasColumnName("user_id").HasMaxLength(255);
        b.Property(x => x.IsRevoked).HasColumnName("is_revoked");
        b.Property(x => x.SecretApiKey).HasColumnName("secret_api_key");
        b.Property(x => x.PrimaryIp).HasColumnName("primary_ip").HasMaxLength(45);
        b.Property(x => x.SecondaryIp).HasColumnName("secondary_ip").HasMaxLength(45);
        b.Property(x => x.IpUpdatedAt).HasColumnName("ip_updated_at");
        b.Property(x => x.AuxParam1).HasColumnName("aux_param1");
        b.Property(x => x.AuxParam2).HasColumnName("aux_param2");
        b.Property(x => x.AuxParam3).HasColumnName("aux_param3");
        b.Property(x => x.AuxParam4).HasColumnName("aux_param4");
        b.HasIndex(x => x.Name).IsUnique().HasDatabaseName("ux_auth_name");
        b.HasIndex(x => x.IsRevoked).HasDatabaseName("ix_auth_is_revoked");
        b.HasIndex(x => x.Broker).HasDatabaseName("ix_auth_broker");
        b.HasIndex(x => x.UserId).HasDatabaseName("ix_auth_user_id");
    }
}
