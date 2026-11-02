// Generated from OpenAlgo table 'oauth_signing_keys'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class OauthSigningKeyConfiguration : IEntityTypeConfiguration<OauthSigningKey>
{
    public void Configure(EntityTypeBuilder<OauthSigningKey> b)
    {
        b.ToTable("oauth_signing_keys", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.Kid).HasColumnName("kid").HasMaxLength(64).IsRequired();
        b.Property(x => x.Algorithm).HasColumnName("algorithm").HasMaxLength(16).IsRequired();
        b.Property(x => x.PublicJwk).HasColumnName("public_jwk").IsRequired();
        b.Property(x => x.PrivatePath).HasColumnName("private_path").HasMaxLength(512).IsRequired();
        b.Property(x => x.IsActive).HasColumnName("is_active").IsRequired();
        b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        b.Property(x => x.RotatedAt).HasColumnName("rotated_at");
        b.HasIndex(x => x.Kid).IsUnique().HasDatabaseName("ux_oauth_signing_keys_kid");
    }
}
