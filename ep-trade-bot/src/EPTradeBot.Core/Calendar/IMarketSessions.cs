namespace EPTradeBot.Core.Calendar;

/// <summary>The exchanges' sessions on a date, from the stored calendar and timings (OpenAlgo's get_market_timings_for_date).</summary>
public interface IMarketSessions
{
    Task<IReadOnlyList<ExchangeSession>> SessionsAsync(DateOnly date, CancellationToken cancellationToken);
}
