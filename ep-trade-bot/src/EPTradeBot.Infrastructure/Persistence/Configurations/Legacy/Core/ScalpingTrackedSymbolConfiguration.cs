// Generated from OpenAlgo table 'scalping_tracked_symbol'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class ScalpingTrackedSymbolConfiguration : IEntityTypeConfiguration<ScalpingTrackedSymbol>
{
    public void Configure(EntityTypeBuilder<ScalpingTrackedSymbol> b)
    {
        b.ToTable("scalping_tracked_symbol", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.Symbol).HasColumnName("symbol").HasMaxLength(60).IsRequired();
        b.Property(x => x.Exchange).HasColumnName("exchange").HasMaxLength(10).IsRequired();
        b.Property(x => x.Product).HasColumnName("product").HasMaxLength(10).IsRequired();
        b.Property(x => x.Mode).HasColumnName("mode").HasMaxLength(10).IsRequired();
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.HasIndex(x => new { x.Symbol, x.Exchange, x.Product, x.Mode }).IsUnique().HasDatabaseName("ux_scalping_tracked_symbol_symbol_exchange_product_mode");
    }
}
