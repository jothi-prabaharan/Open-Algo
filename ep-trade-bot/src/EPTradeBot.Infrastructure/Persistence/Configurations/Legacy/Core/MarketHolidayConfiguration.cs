// Generated from OpenAlgo table 'market_holidays'. Maintained by hand from here on.
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.Core;

internal sealed class MarketHolidayConfiguration : IEntityTypeConfiguration<MarketHoliday>
{
    public void Configure(EntityTypeBuilder<MarketHoliday> b)
    {
        b.ToTable("market_holidays", schema: "core");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Id).HasColumnName("id").IsRequired();
        b.Property(x => x.HolidayDate).HasColumnName("holiday_date").IsRequired();
        b.Property(x => x.Description).HasColumnName("description").HasMaxLength(150).IsRequired();
        b.Property(x => x.HolidayType).HasColumnName("holiday_type").HasMaxLength(30).IsRequired();
        b.Property(x => x.Year).HasColumnName("year").IsRequired();
        b.HasIndex(x => x.HolidayDate).HasDatabaseName("ix_market_holidays_holiday_date");
        b.HasIndex(x => new { x.HolidayDate, x.Year }).HasDatabaseName("ix_market_holidays_holiday_date_year");
        b.HasIndex(x => x.Year).HasDatabaseName("ix_market_holidays_year");
    }
}
