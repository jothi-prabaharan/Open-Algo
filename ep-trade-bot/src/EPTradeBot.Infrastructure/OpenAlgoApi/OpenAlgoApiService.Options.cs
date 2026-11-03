using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EPTradeBot.Core.Brokers;
using EPTradeBot.Core.Contracts.V1;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Entities.Legacy.Core;
using EPTradeBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EPTradeBot.Infrastructure.OpenAlgoApi;

/// <summary>An option resolved from an underlying, an expiry and an offset from the money.</summary>
public sealed record ResolvedOption(string Symbol, string Exchange, int? LotSize, double? TickSize, int FreezeQty, JsonNode? UnderlyingLtp);

/// <summary>
/// Option symbol resolution (/optionsymbol), ported from services/option_symbol_service.py.
/// Without a strike interval the ATM strike is the listed strike nearest the underlying's
/// last price and offsets walk the listed strikes; with one, ATM is the price rounded to
/// that interval (half to even, as Python's round) and offsets are multiples of it.
/// </summary>
public sealed partial class OpenAlgoApiService
{
    private static readonly HashSet<string> NoSpotExchanges = ["MCX", "CDS", "BCD", "NCDEX", "NCO"];
    private static readonly string[] NseIndices = ["NIFTY", "BANKNIFTY", "FINNIFTY", "MIDCPNIFTY", "NIFTYNXT50", "INDIAVIX"];
    private static readonly string[] BseIndices = ["SENSEX", "BANKEX", "SENSEX50"];

    public async Task<V1Result> OptionSymbolAsync(BrokerAccount account, OptionSymbolRequest r, CancellationToken ct)
    {
        var (resolved, error) = await ResolveOptionAsync(account, r.Underlying, r.Exchange, r.ExpiryDate, r.StrikeInterval, r.Offset, r.OptionType, null, ct);
        if (error is not null) return error;
        return new V1Result(200, new JsonObject
        {
            ["status"] = "success", ["symbol"] = resolved!.Symbol, ["exchange"] = resolved.Exchange, ["lotsize"] = resolved.LotSize,
            ["tick_size"] = resolved.TickSize, ["freeze_qty"] = resolved.FreezeQty, ["underlying_ltp"] = resolved.UnderlyingLtp?.DeepClone(),
        });
    }

    /// <summary>Splits an expiry embedded in the underlying (NIFTY28OCT25FUT is NIFTY and 28OCT25).</summary>
    internal static (string Base, string? Expiry) ParseUnderlying(string underlying)
    {
        var m = Regex.Match(underlying.ToUpperInvariant(), @"^([A-Z]+)(\d{2}[A-Z]{3}\d{2})(?:FUT)?$");
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : (underlying.ToUpperInvariant(), null);
    }

    /// <summary>The exchange an underlying is quoted on: an index or the cash market for NFO and BFO.</summary>
    internal static string QuoteExchange(string baseSymbol, string exchange)
    {
        var upper = exchange.ToUpperInvariant();
        if (upper is not ("NFO" or "BFO")) return exchange;
        if (NseIndices.Contains(baseSymbol)) return "NSE_INDEX";
        if (BseIndices.Contains(baseSymbol)) return "BSE_INDEX";
        return upper == "NFO" ? "NSE" : "BSE";
    }

    internal static string OptionExchange(string underlyingExchange) => underlyingExchange.ToUpperInvariant() switch
    {
        "NSE" or "NSE_INDEX" => "NFO",
        "BSE" or "BSE_INDEX" => "BFO",
        "MCX" or "CDS" or "NCO" or "BCD" or "NCDEX" or "CRYPTO" => underlyingExchange.ToUpperInvariant(),
        _ => "NFO",
    };

    internal static double AtmByInterval(double ltp, double interval) => Math.Round(ltp / interval, MidpointRounding.ToEven) * interval;

    internal static double? OffsetByInterval(double atm, string offset, double interval, string optionType)
    {
        var o = offset.ToUpperInvariant();
        if (o == "ATM") return atm;
        if (!TryOffset(o, out var itm, out var n)) return null;
        var up = (optionType == "CE") != itm; // CE: ITM is lower, OTM higher. PE: the reverse.
        return up ? atm + n * interval : atm - n * interval;
    }

    internal static double? OffsetByListedStrikes(double atm, string offset, string optionType, IReadOnlyList<double> strikes)
    {
        var index = -1;
        for (var i = 0; i < strikes.Count; i++)
        {
            if (strikes[i] == atm) { index = i; break; }
        }

        if (index < 0) return null;
        var o = offset.ToUpperInvariant();
        if (o == "ATM") return atm;
        if (!TryOffset(o, out var itm, out var n)) return null;
        var up = (optionType == "CE") != itm;
        var target = up ? index + n : index - n;
        return target < 0 || target >= strikes.Count ? null : strikes[target];
    }

    private static bool TryOffset(string offset, out bool itm, out int n)
    {
        itm = offset.StartsWith("ITM", StringComparison.Ordinal);
        n = 0;
        return (itm || offset.StartsWith("OTM", StringComparison.Ordinal)) && int.TryParse(offset.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out n);
    }

    /// <summary>[Base][Expiry][Strike][CE/PE]: a whole strike without decimals, otherwise as Python prints it (292.5).</summary>
    internal static string OptionSymbol(string baseSymbol, string expiry, double strike, string optionType) =>
        baseSymbol + expiry + (strike == Math.Truncate(strike) ? ((long)strike).ToString(CultureInfo.InvariantCulture) : strike.ToString("R", CultureInfo.InvariantCulture)) + optionType.ToUpperInvariant();

    /// <summary>
    /// Resolves an option for an account, quoting the underlying unless its last price is given.
    /// Returns the option or OpenAlgo's error answer for the request.
    /// </summary>
    internal async Task<(ResolvedOption? Option, V1Result? Error)> ResolveOptionAsync(BrokerAccount account, string underlying, string exchange, string? expiryDate,
        int? strikeInterval, string offset, string optionTypeIn, JsonNode? underlyingLtp, CancellationToken ct)
    {
        var optionType = optionTypeIn.Trim().ToUpperInvariant();
        if (optionType is not ("CE" or "PE")) return (null, V1Result.Error(400, $"Invalid option_type: '{optionTypeIn}'. Supported option types are CE and PE."));
        var (baseSymbol, embedded) = ParseUnderlying(underlying);
        var expiry = string.IsNullOrEmpty(expiryDate) ? embedded : expiryDate;
        if (string.IsNullOrEmpty(expiry))
            return (null, V1Result.Error(400, "Expiry date required. Provide via expiry_date parameter or embed in underlying (e.g., NIFTY28OCT25FUT)."));

        var upperExchange = exchange.ToUpperInvariant();
        var quoteExchange = QuoteExchange(baseSymbol, exchange);
        string quoteSymbol;
        await using (var db = await contexts.CreateDbContextAsync(ct))
        {
            if (upperExchange == "CRYPTO")
            {
                var perp = await PerpetualAsync(db, account, baseSymbol, upperExchange, ct);
                if (perp is null) return (null, V1Result.Error(404, $"No perpetual futures found for {baseSymbol} on {exchange}"));
                (quoteSymbol, quoteExchange) = (perp, upperExchange);
            }
            else if (embedded is not null)
            {
                quoteSymbol = upperExchange is "MCX" or "CDS" ? underlying.ToUpperInvariant() : baseSymbol;
            }
            else if (NoSpotExchanges.Contains(upperExchange))
            {
                var future = await NearMonthFutureAsync(db, account, baseSymbol, upperExchange, ct);
                if (future is null)
                {
                    return (null, V1Result.Error(404, $"No unexpired futures contract for {baseSymbol} on {upperExchange}, so the ATM reference price cannot be determined. "
                        + "Check the symbol, or re-download the master contract."));
                }

                (quoteSymbol, quoteExchange) = (future, upperExchange);
            }
            else
            {
                quoteSymbol = underlying;
            }
        }

        if (underlyingLtp is null)
        {
            var quote = await QuoteAsync(account, quoteSymbol, quoteExchange, ct);
            if (quote.Status != 200)
                return (null, V1Result.Error(quote.Status, $"Failed to fetch LTP for {quoteSymbol}. {Text(quote.Body["message"]) ?? "Unknown error"}"));
            underlyingLtp = quote.Body["data"]?["ltp"];
            if (underlyingLtp is null) return (null, V1Result.Error(500, $"Could not determine LTP for {quoteSymbol}."));
        }

        if (BookFormatting.Number(underlyingLtp) is not { } ltp || !double.IsFinite(ltp))
            return (null, V1Result.Error(500, $"Could not determine a usable LTP for {quoteSymbol}."));

        var optionsExchange = OptionExchange(quoteExchange);
        double target;
        if (strikeInterval is null)
        {
            var strikes = await ListedStrikesAsync(account, baseSymbol, expiry, optionType, optionsExchange, ct);
            if (strikes.Count == 0)
                return (null, V1Result.Error(404, $"No strikes found for {baseSymbol} expiring {expiry}. Please check expiry date or update master contract."));
            var atm = strikes.MinBy(s => Math.Abs(s - ltp));
            if (OffsetByListedStrikes(atm, offset, optionType, strikes) is not { } t)
                return (null, V1Result.Error(400, $"Offset {offset} is out of range for available strikes. Please use a smaller offset."));
            target = t;
        }
        else
        {
            var atm = AtmByInterval(ltp, strikeInterval.Value);
            if (OffsetByInterval(atm, offset, strikeInterval.Value, optionType) is not { } t)
                return (null, V1Result.Error(400, $"Invalid offset: {offset.ToUpperInvariant()}"));
            target = t;
        }

        var symbol = OptionSymbol(baseSymbol, expiry, target, optionType);
        SymToken? row;
        await using (var db = await contexts.CreateDbContextAsync(ct))
            row = await Master(db, account).Where(s => s.Symbol == symbol && s.Exchange == optionsExchange).OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
        if (row is null)
            return (null, V1Result.Error(404, $"Option symbol {symbol} not found in {optionsExchange}. Symbol may not exist or master contract needs update."));

        var freezeQty = await freeze.GetAsync(row.Exchange ?? "", row.Symbol, ct);
        return (new ResolvedOption(row.Symbol, row.Exchange ?? optionsExchange, row.Lotsize, row.TickSize, freezeQty, underlyingLtp.DeepClone()), null);
    }

    /// <summary>The listed strikes for one underlying, expiry and type, ascending.</summary>
    private async Task<List<double>> ListedStrikesAsync(BrokerAccount account, string baseSymbol, string expiry, string optionType, string exchange, CancellationToken ct)
    {
        var dashed = expiry.Length >= 7 ? $"{expiry[..2]}-{expiry[2..5]}-{expiry[5..]}".ToUpperInvariant() : expiry.ToUpperInvariant();
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = Master(db, account).Where(s => s.Expiry == dashed && s.Instrumenttype == optionType && s.Strike != null);
        if (exchange == "CRYPTO")
        {
            var prefix = baseSymbol.ToUpperInvariant();
            rows = rows.Where(s => s.Symbol.StartsWith(prefix) && s.Exchange == "CRYPTO" && s.Strike > 0);
        }
        else
        {
            var prefix = baseSymbol + expiry.ToUpperInvariant();
            rows = rows.Where(s => s.Symbol.StartsWith(prefix) && s.Symbol.EndsWith(optionType) && s.Exchange == exchange);
        }

        return await rows.Select(s => s.Strike!.Value).Distinct().OrderBy(s => s).ToListAsync(ct);
    }

    /// <summary>
    /// The nearest unexpired future of exactly this product (GOLD, never GOLDM or GOLDPETAL),
    /// whose price stands in for the spot on exchanges that have none.
    /// </summary>
    private async Task<string?> NearMonthFutureAsync(EpTradeDbContext db, BrokerAccount account, string baseSymbol, string exchange, CancellationToken ct)
    {
        var rows = await Master(db, account)
            .Where(s => s.Symbol.StartsWith(baseSymbol) && s.Symbol.EndsWith("FUT") && s.Exchange == exchange && s.Instrumenttype == "FUT" && s.Expiry != null && s.Expiry != "")
            .OrderBy(s => s.Id).Select(s => new { s.Symbol, s.Expiry }).ToListAsync(ct);
        var exact = new Regex($@"^{Regex.Escape(baseSymbol)}\d{{2}}[A-Z]{{3}}\d{{2}}FUT$");
        var today = DateOnly.FromDateTime(Ist(clock.GetUtcNow()));
        return rows
            .Where(r => exact.IsMatch(r.Symbol))
            .Select(r => (r.Symbol, Date: DateOnly.TryParseExact(r.Expiry, ["dd-MMM-yy", "d-MMM-yy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateOnly?)null))
            .Where(r => r.Date is { } d && d >= today)
            .OrderBy(r => r.Date)
            .Select(r => r.Symbol)
            .FirstOrDefault();
    }

    /// <summary>The perpetual future that prices a crypto underlying (BTC is quoted as BTCUSDFUT).</summary>
    private async Task<string?> PerpetualAsync(EpTradeDbContext db, BrokerAccount account, string baseSymbol, string exchange, CancellationToken ct)
    {
        var query = baseSymbol + "USDFUT";
        var rows = await Master(db, account).Where(s => s.Exchange == exchange && s.Instrumenttype != null && s.Instrumenttype.ToUpper() == "PERPFUT")
            .Select(s => new { s.Symbol, s.Brsymbol, s.Name, s.Token }).ToListAsync(ct);
        return rows
            .Where(r => r.Symbol.Contains(query, StringComparison.OrdinalIgnoreCase) || r.Brsymbol.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (r.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) || (r.Token?.Contains(query, StringComparison.Ordinal) ?? false))
            .OrderBy(r => r.Symbol.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(r => r.Symbol, StringComparer.Ordinal)
            .Select(r => r.Symbol)
            .FirstOrDefault();
    }
}
