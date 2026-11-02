// Generated from OpenAlgo table 'chartink_symbol_mappings'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class ChartinkSymbolMappingConfiguration : IEntityTypeConfiguration<ChartinkSymbolMapping>
{
    public void Configure(EntityTypeBuilder<ChartinkSymbolMapping> b)
    {
        b.ToTable("chartink_symbol_mappings", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.StrategyId).HasColumnName("strategy_id").IsRequired();
        b.Property(x => x.ChartinkSymbol).HasColumnName("chartink_symbol").HasMaxLength(50).IsRequired();
        b.Property(x => x.Exchange).HasColumnName("exchange").HasMaxLength(10).IsRequired();
        b.Property(x => x.Quantity).HasColumnName("quantity").IsRequired();
        b.Property(x => x.ProductType).HasColumnName("product_type").HasMaxLength(10).IsRequired();
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.HasOne<ChartinkStrategy>().WithMany().HasForeignKey(x => x.StrategyId).OnDelete(DeleteBehavior.Cascade);
    }
}
