// Generated from OpenAlgo table 'market_timings'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class MarketTimingConfiguration : IEntityTypeConfiguration<MarketTiming>
{
    public void Configure(EntityTypeBuilder<MarketTiming> b)
    {
        b.ToTable("market_timings", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.ExchangeCode).HasColumnName("exchange_code").HasMaxLength(10).IsRequired();
        b.Property(x => x.StartTime).HasColumnName("start_time").HasMaxLength(5).IsRequired();
        b.Property(x => x.EndTime).HasColumnName("end_time").HasMaxLength(5).IsRequired();
        b.Property(x => x.StartOffset).HasColumnName("start_offset").IsRequired();
        b.Property(x => x.EndOffset).HasColumnName("end_offset").IsRequired();
        b.HasIndex(x => x.ExchangeCode).IsUnique().HasDatabaseName("ux_market_timings_exchange_code");
    }
}
