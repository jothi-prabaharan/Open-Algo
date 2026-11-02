// Generated from OpenAlgo table 'scalping_sl_state'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class ScalpingSlStateConfiguration : IEntityTypeConfiguration<ScalpingSlState>
{
    public void Configure(EntityTypeBuilder<ScalpingSlState> b)
    {
        b.ToTable("scalping_sl_state", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.Symbol).HasColumnName("symbol").HasMaxLength(60).IsRequired();
        b.Property(x => x.Exchange).HasColumnName("exchange").HasMaxLength(10).IsRequired();
        b.Property(x => x.Product).HasColumnName("product").HasMaxLength(10).IsRequired();
        b.Property(x => x.Side).HasColumnName("side").HasMaxLength(4).IsRequired();
        b.Property(x => x.Mode).HasColumnName("mode").HasMaxLength(10).IsRequired();
        b.Property(x => x.EntryPrice).HasColumnName("entry_price").IsRequired();
        b.Property(x => x.Quantity).HasColumnName("quantity").IsRequired();
        b.Property(x => x.InitialSl).HasColumnName("initial_sl");
        b.Property(x => x.TrailingEnabled).HasColumnName("trailing_enabled").IsRequired();
        b.Property(x => x.TrailingStep).HasColumnName("trailing_step");
        b.Property(x => x.HighestPrice).HasColumnName("highest_price");
        b.Property(x => x.LowestPrice).HasColumnName("lowest_price");
        b.Property(x => x.CurrentSl).HasColumnName("current_sl");
        b.Property(x => x.Target).HasColumnName("target");
        b.Property(x => x.IsActive).HasColumnName("is_active").IsRequired();
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.HasIndex(x => new { x.Symbol, x.Exchange, x.Product, x.Mode }).IsUnique().HasDatabaseName("ux_scalping_sl_state_symbol_exchange_product_mode");
    }
}
