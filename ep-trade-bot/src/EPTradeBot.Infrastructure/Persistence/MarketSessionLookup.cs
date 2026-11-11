using EPTradeBot.Core.Calendar;
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;

namespace EPTradeBot.Infrastructure.Persistence;

/// <summary>Reads the market calendar tables and applies <see cref="MarketCalendar"/>. The one place that does, for /market/timings and for history that trims bars after the close.</summary>
public sealed class MarketSessionLookup(IDbContextFactory<EpTradeDbContext> contexts) : IMarketSessions
{
    public async Task<IReadOnlyList<ExchangeSession>> SessionsAsync(DateOnly date, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var offsets = (await db.Set<MarketTiming>().AsNoTracking().ToListAsync(cancellationToken))
            .GroupBy(t => t.ExchangeCode)
            .ToDictionary(g => g.Key, g => (g.First().StartOffset, g.First().EndOffset));
        // The first entry stored for the date, as OpenAlgo's .first() reads it.
        var entry = (await CalendarAsync(db, h => h.HolidayDate == date, cancellationToken)).FirstOrDefault();
        return MarketCalendar.Sessions(date, entry, offsets);
    }

    /// <summary>Calendar entries with their exchange rows, by date and then in stored order.</summary>
    public static async Task<List<CalendarDay>> CalendarAsync(EpTradeDbContext db, System.Linq.Expressions.Expression<Func<MarketHoliday, bool>> filter, CancellationToken cancellationToken)
    {
        var holidays = await db.Set<MarketHoliday>().AsNoTracking().Where(filter).OrderBy(h => h.HolidayDate).ThenBy(h => h.Id).ToListAsync(cancellationToken);
        var ids = holidays.Select(h => h.Id).ToList();
        var exchanges = (await db.Set<MarketHolidayExchange>().AsNoTracking().Where(x => ids.Contains(x.HolidayId)).OrderBy(x => x.Id).ToListAsync(cancellationToken))
            .ToLookup(x => x.HolidayId);
        return holidays.Select(h => new CalendarDay(h.HolidayDate, h.Description, h.HolidayType,
            exchanges[h.Id].Select(x => new CalendarExchange(x.ExchangeCode, x.IsOpen, x.StartTime, x.EndTime)).ToList())).ToList();
    }
}
