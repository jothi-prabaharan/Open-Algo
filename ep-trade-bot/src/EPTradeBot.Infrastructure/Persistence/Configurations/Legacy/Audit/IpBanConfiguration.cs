// Generated from OpenAlgo table 'ip_bans'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Audit;

internal sealed class IpBanConfiguration : IEntityTypeConfiguration<IpBan>
{
    public void Configure(EntityTypeBuilder<IpBan> b)
    {
        b.ToTable("ip_bans", schema: "audit");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.IpAddress).HasColumnName("ip_address").HasMaxLength(50).IsRequired();
        b.Property(x => x.BanReason).HasColumnName("ban_reason").HasMaxLength(200);
        b.Property(x => x.BanCount).HasColumnName("ban_count");
        b.Property(x => x.BannedAt).HasColumnName("banned_at");
        b.Property(x => x.ExpiresAt).HasColumnName("expires_at");
        b.Property(x => x.IsPermanent).HasColumnName("is_permanent");
        b.Property(x => x.CreatedBy).HasColumnName("created_by").HasMaxLength(50);
        b.HasIndex(x => x.IpAddress).IsUnique().HasDatabaseName("ux_ip_bans_ip_address");
    }
}
