// Generated from OpenAlgo table 'broker_login_states'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class BrokerLoginStateConfiguration : IEntityTypeConfiguration<BrokerLoginState>
{
    public void Configure(EntityTypeBuilder<BrokerLoginState> b)
    {
        b.ToTable("broker_login_states", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.NonceHash).HasColumnName("nonce_hash").HasMaxLength(64).IsRequired();
        b.Property(x => x.StateHash).HasColumnName("state_hash").HasMaxLength(64).IsRequired();
        b.Property(x => x.Broker).HasColumnName("broker").HasMaxLength(50).IsRequired();
        b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        b.Property(x => x.ExpiresAt).HasColumnName("expires_at").IsRequired();
        b.HasIndex(x => x.NonceHash).IsUnique().HasDatabaseName("ux_broker_login_states_nonce_hash");
        b.HasIndex(x => x.StateHash).HasDatabaseName("ix_broker_login_states_state_hash");
    }
}
