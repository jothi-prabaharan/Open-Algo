using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Calendar;
using EPTradeBot.Core.Contracts.V1;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.Legacy.Core;
using EPTradeBot.Infrastructure.Persistence;
using EPTradeBot.Infrastructure.Sandbox;
using Microsoft.EntityFrameworkCore;

namespace EPTradeBot.Infrastructure.OpenAlgoApi;

/// <summary>
/// The /api/v1 endpoints that ask no broker anything: the market calendar
/// (services/market_calendar_service.py), chart preferences (services/chart_service.py and
/// restx_api/chart_api.py) and the sandbox day P&amp;L by symbol (restx_api/pnl_symbols.py).
/// </summary>
public sealed partial class OpenAlgoApiService
{
    // ---- market calendar -------------------------------------------------------------------------------------

    public async Task<V1Result> MarketHolidaysAsync(BrokerAccount account, MarketHolidaysRequest r, CancellationToken ct)
    {
        // The current year in India, where the exchanges are, whatever the server's own clock says.
        var year = r.Year ?? Core.Time.Ist.FromUtc(clock.GetUtcNow().UtcDateTime).Year;
        if (year is < 2020 or > 2050) return V1Result.Error(400, "Year must be between 2020 and 2050");

        await using var db = await contexts.CreateDbContextAsync(ct);
        var entries = await MarketSessionLookup.CalendarAsync(db, h => h.Year == year, ct);
        var rows = new JsonArray();
        foreach (var e in entries)
        {
            var open = new JsonArray();
            foreach (var x in e.Exchanges.Where(x => x.IsOpen))
                open.Add(new JsonObject { ["exchange"] = x.Exchange, ["start_time"] = x.StartTime, ["end_time"] = x.EndTime });
            rows.Add(new JsonObject
            {
                ["date"] = e.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["description"] = e.Description,
                ["holiday_type"] = e.HolidayType,
                ["closed_exchanges"] = new JsonArray(e.Exchanges.Where(x => !x.IsOpen).Select(x => (JsonNode?)x.Exchange).ToArray()),
                ["open_exchanges"] = open,
            });
        }

        return new V1Result(200, new JsonObject { ["status"] = "success", ["year"] = year, ["timezone"] = "Asia/Kolkata", ["data"] = rows });
    }

    public async Task<V1Result> MarketTimingsAsync(BrokerAccount account, MarketTimingsRequest r, CancellationToken ct)
    {
        if (!DateOnly.TryParseExact(r.Date ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return V1Result.Error(400, "Invalid date format. Use YYYY-MM-DD");
        if (date < MarketCalendar.FirstDate || date > MarketCalendar.LastDate)
            return V1Result.Error(400, "Date must be between 2020-01-01 and 2050-12-31");

        var rows = new JsonArray();
        foreach (var s in await marketSessions.SessionsAsync(date, ct))
            rows.Add(new JsonObject { ["exchange"] = s.Exchange, ["start_time"] = s.StartTime, ["end_time"] = s.EndTime });
        return Success(rows);
    }

    // ---- chart preferences -----------------------------------------------------------------------------------

    /// <summary>OpenAlgo's limits on one save: 50 keys, 50 characters a key, 1 MB a value.</summary>
    public const int MaxChartKeys = 50, MaxChartKeyLength = 50, MaxChartValueLength = 1_048_576;

    public async Task<V1Result> ChartPreferencesAsync(BrokerAccount account, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var user = SandboxEngine.UserId(account.Id);
        var data = new JsonObject();
        foreach (var p in await db.Set<ChartPreference>().AsNoTracking().Where(p => p.UserId == user).OrderBy(p => p.Key).ToListAsync(ct))
            data[p.Key] = p.Value;
        return Success(data);
    }

    /// <summary>
    /// The limits OpenAlgo's chart endpoint checks before it saves anything, in its order.
    /// Run before the API key is checked, as OpenAlgo runs them.
    /// </summary>
    public static V1Result? ChartLimits(IReadOnlyDictionary<string, JsonElement> preferences)
    {
        if (preferences.Count > MaxChartKeys) return V1Result.Error(400, $"Too many preference keys (max {MaxChartKeys})");
        foreach (var (key, value) in preferences)
        {
            var runes = key.EnumerateRunes().ToList();
            if (runes.Count > MaxChartKeyLength)
                return V1Result.Error(400, $"Preference key too long: {string.Concat(runes.Take(20).Select(x => x.ToString()))}... (max {MaxChartKeyLength} chars)");
            if (PythonJsonLength(value) > MaxChartValueLength)
                return V1Result.Error(400, $"Preference value too large for key: {key} (max 1MB)");
        }

        return preferences.Count == 0 ? V1Result.Error(400, "No preferences provided to update") : null;
    }

    public async Task<V1Result> SaveChartPreferencesAsync(BrokerAccount account, ChartRequest r, CancellationToken ct)
    {
        if (ChartLimits(r.Preferences) is { } refused) return refused;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var user = SandboxEngine.UserId(account.Id);
        var keys = r.Preferences.Keys.ToList();
        var stored = await db.Set<ChartPreference>().Where(p => p.UserId == user && keys.Contains(p.Key)).ToDictionaryAsync(p => p.Key, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var (key, value) in r.Preferences)
        {
            var text = StoredText(value);
            if (stored.TryGetValue(key, out var row))
            {
                row.Value = text;
                row.UpdatedAt = now;
            }
            else
            {
                db.Set<ChartPreference>().Add(new ChartPreference { UserId = user, Key = key, Value = text, UpdatedAt = now });
            }
        }

        await db.SaveChangesAsync(ct);
        return new V1Result(200, new JsonObject { ["status"] = "success", ["message"] = "Preferences updated successfully" });
    }

    /// <summary>
    /// A preference is stored as text. Text is kept as sent and null as null; a number, a
    /// true/false or an object is kept as its JSON, so the chart reads back what it saved.
    /// </summary>
    private static string? StoredText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => value.GetRawText(),
    };

    /// <summary>
    /// The length of Python's json.dumps of a value, which is what OpenAlgo's 1 MB limit counts:
    /// quotes around text, two characters for each escaped quote, backslash and control
    /// character (six for the rare ones), and six (\uXXXX) for every UTF-16 unit outside ASCII.
    /// Numbers, true/false and null count as written; objects and lists count their JSON text,
    /// which json.dumps spaces a little more widely.
    /// </summary>
    public static long PythonJsonLength(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) return value.GetRawText().Length;
        long length = 2;
        foreach (var c in value.GetString()!)
        {
            length += c switch
            {
                '"' or '\\' or '\b' or '\f' or '\n' or '\r' or '\t' => 2,
                < ' ' => 6,
                > '\u007f' => 6,
                _ => 1,
            };
        }

        return length;
    }

    // ---- sandbox day P&L by symbol ---------------------------------------------------------------------------

    public async Task<V1Result> PnlSymbolsAsync(BrokerAccount account, CancellationToken ct)
    {
        if (account.AccountType != AccountType.PAPER)
            return V1Result.Error(400, $"P&L by symbol is only kept for sandbox accounts, and {account.DisplayName} is a live account. Use the API key of a sandbox account, or read the positions from /positionbook.");

        var rows = await sandbox.GetDayPnlAsync(account.Id, account.Broker, ct);
        var data = new JsonArray();
        foreach (var p in rows)
        {
            data.Add(new JsonObject
            {
                ["symbol"] = p.Symbol, ["exchange"] = p.Exchange, ["product"] = p.Product, ["quantity"] = p.Quantity,
                ["pnl"] = Money(p.Total), ["unrealized_pnl"] = Money(p.Unrealized), ["today_realized_pnl"] = Money(p.TodayRealized), ["total_pnl_today"] = Money(p.Total),
            });
        }

        var total = rows.Sum(p => p.Total);
        return new V1Result(200, new JsonObject
        {
            ["status"] = "success",
            ["data"] = data,
            ["total_pnl"] = Money(total),
            ["total_unrealized_pnl"] = Money(rows.Sum(p => p.Unrealized)),
            ["total_today_realized_pnl"] = Money(rows.Sum(p => p.TodayRealized)),
            ["total_pnl_today"] = Money(total),
            ["mode"] = "analyze",
        });
    }

    private static double Money(decimal value) => (double)Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
