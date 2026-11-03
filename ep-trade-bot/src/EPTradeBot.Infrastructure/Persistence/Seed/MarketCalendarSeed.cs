using System.Text.Json;
using System.Text.Json.Serialization;
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EPTradeBot.Infrastructure.Persistence.Seed;

/// <summary>
/// Fills the market calendar (core.market_holidays with their exchanges, and core.market_timings)
/// with what a fresh OpenAlgo install holds: its 2025 and 2026 calendars after the 2026 correction
/// and the published Muhurat session, and the session timings with the 15:40 F&amp;O close.
/// market_calendar.json is exported from OpenAlgo's own startup path
/// (tools/seed/export_market_calendar.py). Each table is filled only while it is empty, as
/// OpenAlgo seeds them, so a holiday or timing an administrator changed is never replaced.
/// </summary>
public static class MarketCalendarSeed
{
    private const string Resource = "EPTradeBot.Infrastructure.Persistence.Seed.market_calendar.json";

    public static async Task SeedAsync(EpTradeDbContext db, ILogger logger, CancellationToken cancellationToken)
    {
        var seed = Read();
        if (!await db.Set<MarketHoliday>().AnyAsync(cancellationToken))
        {
            // One transaction: a seed stopped half way would leave a table that is no longer
            // empty, and so is never completed.
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            foreach (var h in seed.Holidays)
            {
                var holiday = new MarketHoliday { HolidayDate = DateOnly.Parse(h.Date, System.Globalization.CultureInfo.InvariantCulture), Description = h.Description, HolidayType = h.HolidayType, Year = h.Year };
                db.Set<MarketHoliday>().Add(holiday);
                await db.SaveChangesAsync(cancellationToken);
                db.Set<MarketHolidayExchange>().AddRange(h.Exchanges.Select(e => new MarketHolidayExchange
                {
                    HolidayId = holiday.Id, ExchangeCode = e.Exchange, IsOpen = e.IsOpen, StartTime = e.StartTime, EndTime = e.EndTime,
                }));
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Loaded {Count} market calendar entries.", seed.Holidays.Count);
        }

        if (!await db.Set<MarketTiming>().AnyAsync(cancellationToken))
        {
            db.Set<MarketTiming>().AddRange(seed.Timings.Select(t => new MarketTiming
            {
                ExchangeCode = t.Exchange, StartTime = t.StartTime, EndTime = t.EndTime, StartOffset = t.StartOffset, EndOffset = t.EndOffset,
            }));
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public static SeedFile Read()
    {
        using var stream = typeof(MarketCalendarSeed).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException("The market calendar seed is missing from the build.");
        return JsonSerializer.Deserialize<SeedFile>(stream) ?? throw new InvalidOperationException("The market calendar seed is empty.");
    }

    public sealed record SeedFile(
        [property: JsonPropertyName("holidays")] List<SeedHoliday> Holidays,
        [property: JsonPropertyName("timings")] List<SeedTiming> Timings);

    public sealed record SeedHoliday(
        [property: JsonPropertyName("date")] string Date,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("holiday_type")] string HolidayType,
        [property: JsonPropertyName("year")] int Year,
        [property: JsonPropertyName("exchanges")] List<SeedExchange> Exchanges);

    public sealed record SeedExchange(
        [property: JsonPropertyName("exchange")] string Exchange,
        [property: JsonPropertyName("is_open")] bool IsOpen,
        [property: JsonPropertyName("start_time")] long? StartTime,
        [property: JsonPropertyName("end_time")] long? EndTime);

    public sealed record SeedTiming(
        [property: JsonPropertyName("exchange")] string Exchange,
        [property: JsonPropertyName("start_time")] string StartTime,
        [property: JsonPropertyName("end_time")] string EndTime,
        [property: JsonPropertyName("start_offset")] long StartOffset,
        [property: JsonPropertyName("end_offset")] long EndOffset);
}
