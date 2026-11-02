// Generated from OpenAlgo table 'oauth_clients'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class OauthClientConfiguration : IEntityTypeConfiguration<OauthClient>
{
    public void Configure(EntityTypeBuilder<OauthClient> b)
    {
        b.ToTable("oauth_clients", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.ClientId).HasColumnName("client_id").HasMaxLength(64).IsRequired();
        b.Property(x => x.ClientName).HasColumnName("client_name").HasMaxLength(255).IsRequired();
        b.Property(x => x.RedirectUris).HasColumnName("redirect_uris").IsRequired();
        b.Property(x => x.ClientSecretHash).HasColumnName("client_secret_hash");
        b.Property(x => x.ScopesRequested).HasColumnName("scopes_requested").HasMaxLength(255);
        b.Property(x => x.Approved).HasColumnName("approved").IsRequired();
        b.Property(x => x.ApprovedAt).HasColumnName("approved_at");
        b.Property(x => x.RevokedAt).HasColumnName("revoked_at");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        b.Property(x => x.LastUsedAt).HasColumnName("last_used_at");
        b.HasIndex(x => x.ClientId).IsUnique().HasDatabaseName("ux_oauth_clients_client_id");
        b.HasIndex(x => new { x.Approved, x.RevokedAt }).HasDatabaseName("ix_oauth_clients_approved_revoked_at");
    }
}
