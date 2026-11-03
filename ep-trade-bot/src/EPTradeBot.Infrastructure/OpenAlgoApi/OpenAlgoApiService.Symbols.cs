using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EPTradeBot.Core.Contracts.V1;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.Legacy.Core;
using EPTradeBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EPTradeBot.Infrastructure.OpenAlgoApi;

/// <summary>
/// The symbol master endpoints (/symbol, /search, /expiry, /instruments), ported from
/// services/symbol_service.py, search_service.py, expiry_service.py and instruments_service.py.
/// OpenAlgo held one broker's master contract; here each account reads its own broker's rows.
/// </summary>
public sealed partial class OpenAlgoApiService
{
    private IQueryable<SymToken> Master(EpTradeDbContext db, BrokerAccount account) =>
        db.Set<SymToken>().AsNoTracking().Where(s => s.Broker == account.Broker);

    public async Task<V1Result> SymbolAsync(BrokerAccount account, SymbolRequest r, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await Master(db, account).Where(s => s.Symbol == r.Symbol && s.Exchange == r.Exchange).OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
        if (row is null) return V1Result.Error(404, $"Symbol {r.Symbol} not found in exchange {r.Exchange}");
        var data = SymbolRow(row, withId: true);
        data["freeze_qty"] = await freeze.GetAsync(row.Exchange ?? "", row.Symbol, ct);
        return new V1Result(200, new JsonObject { ["data"] = data, ["status"] = "success" });
    }

    /// <summary>At most this many matches, as OpenAlgo's search_service asks of its cache.</summary>
    private const int SearchLimit = 500;

    public async Task<V1Result> SearchAsync(BrokerAccount account, SearchRequest r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Query)) return V1Result.Error(400, "Query parameter is required and cannot be empty");
        var query = r.Query.Trim();
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(t => t.ToUpperInvariant()).ToList();

        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = Master(db, account);
        if (!string.IsNullOrEmpty(r.Exchange)) rows = rows.Where(s => s.Exchange == r.Exchange);
        foreach (var term in terms)
        {
            // Each term must appear in the symbol, broker symbol, name or token, or equal the strike.
            var pattern = "%" + EscapeLike(term) + "%";
            if (double.TryParse(term, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                rows = rows.Where(s => EF.Functions.ILike(s.Symbol, pattern, "\\") || EF.Functions.ILike(s.Brsymbol, pattern, "\\")
                    || EF.Functions.ILike(s.Name ?? "", pattern, "\\") || EF.Functions.ILike(s.Token ?? "", pattern, "\\") || s.Strike == number);
            }
            else
            {
                rows = rows.Where(s => EF.Functions.ILike(s.Symbol, pattern, "\\") || EF.Functions.ILike(s.Brsymbol, pattern, "\\")
                    || EF.Functions.ILike(s.Name ?? "", pattern, "\\") || EF.Functions.ILike(s.Token ?? "", pattern, "\\"));
            }
        }

        var matches = await rows.OrderBy(s => s.Id).ToListAsync(ct);
        if (matches.Count == 0)
            return new V1Result(200, new JsonObject { ["status"] = "success", ["message"] = "No matching symbols found", ["data"] = new JsonArray() });

        // Ranked as OpenAlgo's symbol cache ranks: exact symbol, then prefix, then a symbol that
        // holds every term, then the rest; shorter symbols first; then the symbol; then load order.
        var upperQuery = query.ToUpperInvariant();
        var ranked = matches
            .Select((s, i) => (Row: s, Score: SearchScore(s.Symbol.ToUpperInvariant(), upperQuery, terms), Seq: i))
            .OrderBy(x => x.Score).ThenBy(x => x.Row.Symbol.Length).ThenBy(x => x.Row.Symbol.ToUpperInvariant(), StringComparer.Ordinal).ThenBy(x => x.Seq)
            .Take(SearchLimit)
            .Select(x => x.Row)
            .ToList();

        var data = new JsonArray();
        foreach (var row in ranked)
        {
            var item = SymbolRow(row, withId: false);
            item["freeze_qty"] = await freeze.GetAsync(row.Exchange ?? "", row.Symbol, ct);
            data.Add(item);
        }

        return new V1Result(200, new JsonObject { ["status"] = "success", ["message"] = $"Found {data.Count} matching symbols", ["data"] = data });
    }

    private static int SearchScore(string symbol, string query, List<string> terms) =>
        symbol == query ? 0
        : symbol.StartsWith(query, StringComparison.Ordinal) ? 1
        : terms.All(t => symbol.Contains(t, StringComparison.Ordinal)) ? 2
        : 3;

    private static string EscapeLike(string term) => term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static readonly Dictionary<(string Kind, string Exchange), string[]> ExpiryTypes = new()
    {
        [("futures", "NFO")] = ["FUTSTK", "FUTIDX", "FUT"], [("futures", "BFO")] = ["FUTSTK", "FUTIDX", "FUT"],
        [("futures", "MCX")] = ["FUTCOM", "FUTENR", "FUT"], [("futures", "NCDEX")] = ["FUTCOM", "FUTENR", "FUT"],
        [("futures", "CDS")] = ["FUTCUR", "FUTIRC", "FUT"], [("futures", "BCD")] = ["FUTCUR", "FUTIRC", "FUT"],
        [("futures", "CRYPTO")] = ["FUT", "PERPFUT"],
        [("options", "NFO")] = ["OPTSTK", "OPTIDX", "CE", "PE"], [("options", "BFO")] = ["OPTSTK", "OPTIDX", "CE", "PE"],
        [("options", "MCX")] = ["OPTFUT", "CE", "PE"], [("options", "NCDEX")] = ["OPTFUT", "CE", "PE"],
        [("options", "CDS")] = ["OPTCUR", "OPTIRC", "CE", "PE"], [("options", "BCD")] = ["OPTCUR", "OPTIRC", "CE", "PE"],
        [("options", "CRYPTO")] = ["CE", "PE"],
    };

    public async Task<V1Result> ExpiryAsync(BrokerAccount account, ExpiryRequest r, CancellationToken ct)
    {
        var symbol = r.Symbol.Trim().ToUpperInvariant();
        var exchange = r.Exchange.Trim().ToUpperInvariant();
        var kind = r.InstrumentType.Trim().ToLowerInvariant();
        if (symbol.Length == 0) return V1Result.Error(400, "Symbol parameter is required and cannot be empty");

        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = Master(db, account).Where(s => s.Symbol.StartsWith(symbol) && s.Exchange == exchange && s.Expiry != null && s.Expiry != "");
        if (ExpiryTypes.TryGetValue((kind, exchange), out var types)) rows = rows.Where(s => types.Contains(s.Instrumenttype!));
        var found = await rows.OrderBy(s => s.Id).Select(s => new { s.Symbol, s.Expiry }).ToListAsync(ct);
        if (found.Count == 0)
        {
            return new V1Result(200, new JsonObject
            {
                ["status"] = "success", ["message"] = $"No expiry dates found for {symbol} {kind} in {exchange}", ["data"] = new JsonArray(),
            });
        }

        var s0 = Regex.Escape(symbol);
        string[] patterns = kind == "futures"
            ? [$"^{s0}[0-9]{{2}}[A-Z]{{3}}[0-9]{{2}}(FUT)?", $"^{s0}[0-9]{{2}}[A-Z]{{3}}[0-9]{{2}}FUT", $"^{s0}[0-9]{{2}}[A-Z]{{3}}[0-9]{{2}}",
                $"^{s0}[0-9]{{2}}[A-Z]{{3}}FUT", $"^{s0}[0-9]{{4}}[A-Z]{{3}}FUT", $"^{s0}[A-Z]{{3}}[0-9]{{2}}FUT", $"^{s0}[A-Z]{{3}}[0-9]{{4}}FUT"]
            : [$"^{s0}[0-9]{{2}}[A-Z]{{3}}[0-9]{{2}}", $"^{s0}[0-9]{{2}}[A-Z]{{3}}[0-9]{{2}}", $"^{s0}[0-9]{{2}}[A-Z]{{3}}",
                $"^{s0}[0-9]{{4}}[A-Z]{{3}}", $"^{s0}[A-Z]{{3}}[0-9]{{2}}", $"^{s0}[A-Z]{{3}}[0-9]{{4}}"];

        // The first pattern is the usual one; the others are tried in turn only when it matches nothing.
        var dates = new List<string>();
        foreach (var pattern in patterns)
        {
            dates = found.Where(f => Regex.IsMatch(f.Symbol, pattern)).Select(f => f.Expiry!).Distinct().ToList();
            if (dates.Count > 0) break;
        }

        var today = DateOnly.FromDateTime(Ist(clock.GetUtcNow()));
        var live = dates.Select(d => (Text: d, Date: ParseExpiry(d))).Where(d => d.Date is null || d.Date >= today)
            .OrderBy(d => d.Date ?? DateOnly.MaxValue).Select(d => (JsonNode?)d.Text).ToArray();
        return new V1Result(200, new JsonObject
        {
            ["status"] = "success", ["message"] = $"Found {live.Length} expiry dates for {symbol} {kind} in {exchange}", ["data"] = new JsonArray(live),
        });
    }

    /// <summary>An expiry as the master contract writes it (DD-MMM-YY, or DD-MMM-YYYY); null when it is neither.</summary>
    internal static DateOnly? ParseExpiry(string? text) =>
        DateOnly.TryParseExact(text, ["dd-MMM-yy", "d-MMM-yy", "dd-MMM-yyyy", "d-MMM-yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>OpenAlgo runs on Indian time, so "today" for an expiry is the IST date.</summary>
    private static DateTime Ist(DateTimeOffset utc) => utc.ToOffset(TimeSpan.FromHours(5.5)).DateTime;

    private static readonly string[] InstrumentColumns = ["symbol", "brsymbol", "name", "exchange", "brexchange", "token", "expiry", "strike", "lotsize", "instrumenttype", "tick_size"];

    /// <summary>GET /instruments. A CSV answer is returned as text in <see cref="V1Result"/>'s <c>csv</c> field.</summary>
    public async Task<V1Result> InstrumentsAsync(BrokerAccount account, InstrumentsRequest r, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = Master(db, account);
        if (!string.IsNullOrEmpty(r.Exchange)) rows = rows.Where(s => s.Exchange == r.Exchange);
        var list = await rows.OrderBy(s => s.Id).ToListAsync(ct);
        var csv = string.Equals(r.Format, "csv", StringComparison.OrdinalIgnoreCase);
        if (list.Count == 0 && !csv)
            return new V1Result(200, new JsonObject { ["status"] = "success", ["message"] = "No instruments found", ["data"] = new JsonArray() });

        if (csv)
        {
            var text = new StringBuilder();
            if (list.Count > 0)
            {
                text.Append(string.Join(',', InstrumentColumns)).Append("\r\n");
                foreach (var row in list)
                {
                    string[] fields =
                    [
                        row.Symbol, row.Brsymbol, row.Name ?? "", row.Exchange ?? "", row.Brexchange ?? "", row.Token ?? "", row.Expiry ?? "",
                        row.Strike is { } k ? PyFloat(k) : "", row.Lotsize?.ToString(CultureInfo.InvariantCulture) ?? "", row.Instrumenttype ?? "",
                        row.TickSize is { } t ? PyFloat(t) : "",
                    ];
                    text.Append(string.Join(',', fields.Select(CsvField))).Append("\r\n");
                }
            }

            return new V1Result(200, new JsonObject { ["csv"] = text.ToString(), ["filename"] = $"instruments_{r.Exchange ?? "all"}.csv" });
        }

        var data = new JsonArray();
        foreach (var row in list) data.Add(SymbolRow(row, withId: false));
        return new V1Result(200, new JsonObject { ["status"] = "success", ["message"] = $"Found {data.Count} instruments", ["data"] = data });
    }

    /// <summary>One CSV field as Python's csv module writes it: quoted only when it holds a comma, quote or line break.</summary>
    private static string CsvField(string text) =>
        text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;

    /// <summary>A float as Python's str() writes it (24000.0, 0.05).</summary>
    internal static string PyFloat(double value) =>
        value == Math.Truncate(value) && Math.Abs(value) < 1e16 ? value.ToString("0.0", CultureInfo.InvariantCulture) : value.ToString("R", CultureInfo.InvariantCulture);

    private static JsonObject SymbolRow(SymToken r, bool withId)
    {
        var o = new JsonObject();
        if (withId) o["id"] = r.Id;
        o["symbol"] = r.Symbol;
        o["brsymbol"] = r.Brsymbol;
        o["name"] = r.Name;
        o["exchange"] = r.Exchange;
        o["brexchange"] = r.Brexchange;
        o["token"] = r.Token;
        o["expiry"] = r.Expiry;
        o["strike"] = r.Strike is { } k ? JsonValue.Create(k) : null;
        o["lotsize"] = r.Lotsize;
        o["instrumenttype"] = r.Instrumenttype;
        o["tick_size"] = r.TickSize is { } t ? JsonValue.Create(t) : null;
        return o;
    }
}
