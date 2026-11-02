// Generated from OpenAlgo table 'session_revocations'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class SessionRevocationConfiguration : IEntityTypeConfiguration<SessionRevocation>
{
    public void Configure(EntityTypeBuilder<SessionRevocation> b)
    {
        b.ToTable("session_revocations", schema: "core");
        b.HasKey(x => x.Username);
        b.Property(x => x.Username).ValueGeneratedNever();
        b.Property(x => x.Username).HasColumnName("username").HasMaxLength(255).IsRequired();
        b.Property(x => x.RevokedAt).HasColumnName("revoked_at").IsRequired();
        b.Property(x => x.PreBrokerRevokedAt).HasColumnName("pre_broker_revoked_at");
    }
}
