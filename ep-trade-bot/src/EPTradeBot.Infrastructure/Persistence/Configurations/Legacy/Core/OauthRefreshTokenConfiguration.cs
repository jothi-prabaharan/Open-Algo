// Generated from OpenAlgo table 'oauth_refresh_tokens'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class OauthRefreshTokenConfiguration : IEntityTypeConfiguration<OauthRefreshToken>
{
    public void Configure(EntityTypeBuilder<OauthRefreshToken> b)
    {
        b.ToTable("oauth_refresh_tokens", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.ClientId).HasColumnName("client_id").HasMaxLength(64).IsRequired();
        b.Property(x => x.TokenHash).HasColumnName("token_hash").IsRequired();
        b.Property(x => x.Scopes).HasColumnName("scopes").HasMaxLength(255).IsRequired();
        b.Property(x => x.FamilyId).HasColumnName("family_id").HasMaxLength(64).IsRequired();
        b.Property(x => x.ParentId).HasColumnName("parent_id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        b.Property(x => x.ExpiresAt).HasColumnName("expires_at").IsRequired();
        b.Property(x => x.LastUsedAt).HasColumnName("last_used_at");
        b.Property(x => x.RevokedAt).HasColumnName("revoked_at");
        b.Property(x => x.RevokeReason).HasColumnName("revoke_reason").HasMaxLength(64);
        b.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("ux_oauth_refresh_tokens_token_hash");
        b.HasIndex(x => x.ClientId).HasDatabaseName("ix_oauth_refresh_tokens_client_id");
        b.HasIndex(x => x.FamilyId).HasDatabaseName("ix_oauth_refresh_tokens_family_id");
        b.HasIndex(x => x.RevokedAt).HasDatabaseName("ix_oauth_refresh_tokens_revoked_at");
        b.HasOne<OauthRefreshToken>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Cascade);
    }
}
