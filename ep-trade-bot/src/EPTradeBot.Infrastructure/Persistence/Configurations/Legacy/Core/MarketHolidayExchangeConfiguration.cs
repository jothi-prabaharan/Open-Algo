// Generated from OpenAlgo table 'market_holiday_exchanges'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class MarketHolidayExchangeConfiguration : IEntityTypeConfiguration<MarketHolidayExchange>
{
    public void Configure(EntityTypeBuilder<MarketHolidayExchange> b)
    {
        b.ToTable("market_holiday_exchanges", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.HolidayId).HasColumnName("holiday_id").IsRequired();
        b.Property(x => x.ExchangeCode).HasColumnName("exchange_code").HasMaxLength(10).IsRequired();
        b.Property(x => x.IsOpen).HasColumnName("is_open").IsRequired();
        b.Property(x => x.StartTime).HasColumnName("start_time");
        b.Property(x => x.EndTime).HasColumnName("end_time");
        b.HasIndex(x => x.ExchangeCode).HasDatabaseName("ix_market_holiday_exchanges_exchange_code");
        b.HasIndex(x => new { x.HolidayId, x.ExchangeCode }).HasDatabaseName("ix_market_holiday_exchanges_holiday_id_exchange_code");
        b.HasIndex(x => x.HolidayId).HasDatabaseName("ix_market_holiday_exchanges_holiday_id");
    }
}
