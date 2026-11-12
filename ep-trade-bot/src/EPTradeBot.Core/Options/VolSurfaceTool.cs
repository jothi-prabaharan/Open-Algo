using System.Globalization;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Options;

/// <summary>An implied volatility surface across strikes and expiries, from the out-of-the-money option at each strike (calls from the money upward, puts below). OpenAlgo's get_vol_surface_data with its fetching taken out.</summary>
public static class VolSurfaceTool
{
    private static readonly HashSet<string> NseIndices = ["NIFTY", "BANKNIFTY", "FINNIFTY", "MIDCPNIFTY", "NIFTYNXT50", "NIFTYIT", "NIFTYPHARMA", "NIFTYBANK"];
    private static readonly HashSet<string> BseIndices = ["SENSEX", "BANKEX", "SENSEX50"];

    /// <summary>The exchange the underlying is quoted on.</summary>
    public static string QuoteExchange(string baseSymbol, string exchange)
    {
        if (NseIndices.Contains(baseSymbol)) return "NSE_INDEX";
        if (BseIndices.Contains(baseSymbol)) return "BSE_INDEX";
        var e = exchange.ToUpperInvariant();
        return e switch { "NFO" => "NSE", "BFO" => "BSE", _ => e };
    }

    /// <summary>The exchange options are listed on for an underlying's exchange.</summary>
    public static string OptionExchange(string underlyingExchange) => underlyingExchange.ToUpperInvariant() switch
    {
        "NSE" or "NSE_INDEX" => "NFO",
        "BSE" or "BSE_INDEX" => "BFO",
        "MCX" => "MCX",
        "CDS" => "CDS",
        var e and ("NCO" or "BCD" or "NCDEX") => e,
        var e => OptionChainReading.OptionsExchange(e) == e && e is "NFO" or "BFO" ? e : "NFO",
    };

    /// <summary>NIFTY26NOV2624000CE: a whole strike without its decimal point, any other with it.</summary>
    public static string OptionSymbol(string baseSymbol, string expiry, double strike, string type) =>
        $"{baseSymbol}{expiry}{(strike == Math.Truncate(strike) ? ((long)strike).ToString(CultureInfo.InvariantCulture) : strike.ToString("R", CultureInfo.InvariantCulture))}{type.ToUpperInvariant()}";

    /// <summary>The strike nearest the price; the first of equals.</summary>
    public static double? AtmStrike(double ltp, IReadOnlyList<double> strikes) => strikes.Count == 0 || double.IsNaN(ltp) || double.IsInfinity(ltp) ? null : strikes.MinBy(s => Math.Abs(s - ltp));

    public sealed record Inputs(double? UnderlyingLtp, (int Code, string Message)? QuoteFailure, Func<string, IReadOnlyList<double>> StrikesFor, Func<string, double?> OptionLtp);

    public static (bool Ok, JsonObject Response, int Code) Compute(string underlying, string exchange, IReadOnlyList<string> expiries, int strikeCount, Inputs inputs, DateTime nowIst)
    {
        JsonObject Fail(string message) => new() { ["status"] = "error", ["message"] = message };
        if (expiries.Count == 0) return (false, Fail("At least one expiry is required"), 400);

        var baseSymbol = underlying.ToUpperInvariant();
        var optionsExchange = OptionExchange(QuoteExchange(baseSymbol, exchange));
        if (inputs.QuoteFailure is { } failure) return (false, Fail($"Failed to fetch LTP for {baseSymbol}: {failure.Message}"), failure.Code);
        if (inputs.UnderlyingLtp is not { } ltp || ltp == 0) return (false, Fail($"No LTP for {baseSymbol}"), 500);

        var chosen = new List<(string Expiry, List<double> Strikes, double Atm)>();
        foreach (var expiry in expiries)
        {
            var strikes = inputs.StrikesFor(expiry);
            if (strikes.Count == 0) continue;
            if (AtmStrike(ltp, strikes) is not { } atm) continue;
            var index = strikes.ToList().IndexOf(atm);
            var start = Math.Max(0, index - strikeCount);
            var end = Math.Min(strikes.Count, index + strikeCount + 1);
            chosen.Add((expiry, strikes.Skip(start).Take(end - start).ToList(), atm));
        }

        if (chosen.Count == 0) return (false, Fail("No valid expiry data found"), 404);

        var common = chosen.Select(c => c.Strikes.ToHashSet()).Aggregate((a, b) => { a.IntersectWith(b); return a; }).OrderBy(s => s).ToList();
        if (common.Count < 3) common = chosen[0].Strikes.OrderBy(s => s).ToList();
        var atmStrike = chosen[0].Atm;

        var surface = new JsonArray();
        var info = new JsonArray();
        foreach (var (expiry, _, _) in chosen)
        {
            var row = new JsonArray();
            foreach (var strike in common)
            {
                var symbol = OptionSymbol(baseSymbol, expiry, strike, strike >= atmStrike ? "CE" : "PE");
                var price = inputs.OptionLtp(symbol) ?? 0;
                double? iv = null;
                if (price > 0)
                {
                    var (ok, greeks, _) = OptionGreeks.Calculate(symbol, optionsExchange, ltp, price, null, null, nowIst);
                    if (ok && greeks["status"]?.GetValue<string>() == "success")
                    {
                        var value = OptionChainReading.Num(greeks, "implied_volatility");
                        iv = value > 0 ? PyNumber.Round(value, 2) : null;
                    }
                }

                row.Add(iv);
            }

            surface.Add(row);
            try
            {
                var (_, expiryAt, _, _) = OptionSymbols.Parse(OptionSymbol(baseSymbol, expiry, common[0], "CE"), optionsExchange);
                info.Add(new JsonObject { ["date"] = expiry, ["dte"] = PyNumber.Round(Math.Max(0, (expiryAt - nowIst).TotalSeconds / 86400), 1) });
            }
            catch (OptionSymbols.RefusedException)
            {
                info.Add(new JsonObject { ["date"] = expiry, ["dte"] = 0 });
            }
        }

        var strikesArray = new JsonArray();
        foreach (var s in common) strikesArray.Add(s);
        return (true, new JsonObject
        {
            ["status"] = "success",
            ["data"] = new JsonObject { ["underlying"] = baseSymbol, ["underlying_ltp"] = ltp, ["atm_strike"] = atmStrike, ["strikes"] = strikesArray, ["expiries"] = info, ["surface"] = surface },
        }, 200);
    }
}
