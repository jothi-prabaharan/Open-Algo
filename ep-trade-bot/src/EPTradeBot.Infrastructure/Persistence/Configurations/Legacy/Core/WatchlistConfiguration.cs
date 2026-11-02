// Generated from OpenAlgo table 'watchlists'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class WatchlistConfiguration : IEntityTypeConfiguration<Watchlist>
{
    public void Configure(EntityTypeBuilder<Watchlist> b)
    {
        b.ToTable("watchlists", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.UserId).HasColumnName("user_id").HasMaxLength(80).IsRequired();
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(64).IsRequired();
        b.Property(x => x.Position).HasColumnName("position").IsRequired();
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.HasIndex(x => new { x.UserId, x.Name }).IsUnique().HasDatabaseName("ux_watchlists_user_id_name");
        b.HasIndex(x => x.UserId).HasDatabaseName("ix_watchlists_user_id");
    }
}
