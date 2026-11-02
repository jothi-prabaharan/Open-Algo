// Generated from OpenAlgo table 'watchlist_items'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class WatchlistItemConfiguration : IEntityTypeConfiguration<WatchlistItem>
{
    public void Configure(EntityTypeBuilder<WatchlistItem> b)
    {
        b.ToTable("watchlist_items", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.WatchlistId).HasColumnName("watchlist_id").IsRequired();
        b.Property(x => x.Symbol).HasColumnName("symbol").HasMaxLength(64).IsRequired();
        b.Property(x => x.Exchange).HasColumnName("exchange").HasMaxLength(16).IsRequired();
        b.Property(x => x.Position).HasColumnName("position").IsRequired();
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.HasIndex(x => new { x.WatchlistId, x.Symbol, x.Exchange }).IsUnique().HasDatabaseName("ux_watchlist_items_watchlist_id_symbol_exchange");
        b.HasIndex(x => x.WatchlistId).HasDatabaseName("ix_watchlist_items_watchlist_id");
        b.HasOne<Watchlist>().WithMany().HasForeignKey(x => x.WatchlistId).OnDelete(DeleteBehavior.Cascade);
    }
}
