using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Options;

/// <summary>
/// Gamma x open interest per strike at two horizons (one day, and to expiry) with the one- and two-sigma expected-move bands from the ATM volatility. OpenAlgo's calculate_gamma_density
/// over a chain and a forward price that have already been fetched.
/// </summary>
public static class GammaDensityTool
{
    private const double IntradayYears = 1.0 / 365.0;
    private const double FallbackIv = 0.15;

    private static double? SafeIv(double price, double f, double k, double r, double t, bool call)
    {
        if (price == 0 || price <= 0 || f <= 0 || k <= 0 || t <= 0) return null;
        var iv = Black76.ImpliedVolatility(price, f, k, r, t, call, out _);
        if (iv is null || !double.IsFinite(iv.Value) || iv.Value <= 0 || iv.Value > 5) return null;
        return iv;
    }

    private static double SafeGamma(double f, double k, double t, double r, double sigma)
    {
        if (sigma == 0 || sigma <= 0 || f <= 0 || k <= 0 || t <= 0) return 0.0;
        var g = Black76.Gamma(f, k, t, r, sigma);
        return !double.IsFinite(g) || g < 0 ? 0.0 : g;
    }

    private static JsonObject Band(double spot, double sigma) => new()
    {
        ["sigma_move"] = PyNumber.Round(sigma, 2), ["one_sigma_low"] = PyNumber.Round(spot - sigma, 2), ["one_sigma_high"] = PyNumber.Round(spot + sigma, 2),
        ["two_sigma_low"] = PyNumber.Round(spot - 2 * sigma, 2), ["two_sigma_high"] = PyNumber.Round(spot + 2 * sigma, 2),
    };

    public static (bool Ok, JsonObject Response, int Code) Compute(JsonObject chainResponse, string underlying, string exchange, string expiryDate, double? interestRate, double? forward, DateTime nowIst)
    {
        try
        {
            var chain = chainResponse["chain"] as JsonArray ?? [];
            var spot = OptionChainReading.Num(chainResponse, "underlying_ltp");
            var atm = chainResponse["atm_strike"] is JsonValue av && av.TryGetValue<double>(out var atmValue) ? (double?)atmValue : null;
            if (spot <= 0 || chain.Count == 0) return (false, new JsonObject { ["status"] = "error", ["message"] = "Spot price or option chain unavailable" }, 404);

            var (hour, minute) = OptionSymbols.ExpiryTime(exchange.ToUpperInvariant(), null);
            var expiry = ParseExpiry(expiryDate, hour, minute);
            var (years, days) = OptionGreeks.TimeToExpiry(expiry, nowIst);
            var intraday = years > 0 ? Math.Min(years, IntradayYears) : IntradayYears;
            var rate = interestRate ?? 0.0;
            var r = rate / 100.0;
            var f = forward is { } fw && fw != 0 ? fw : spot;

            var strikes = new List<(double K, double CeOi, double PeOi, double? CeIv, double? PeIv, double? Iv)>();
            var valid = new List<double>();
            double? atmIv = null;
            foreach (var item in chain)
            {
                if (item?["strike"] is not JsonValue sv || (!sv.TryGetValue<double>(out _) && !sv.TryGetValue<long>(out _) && !sv.TryGetValue<int>(out _))) continue;
                var k = OptionChainReading.Num(item, "strike");
                if (k <= 0) continue;
                var ce = item["ce"] as JsonObject;
                var pe = item["pe"] as JsonObject;
                var ceIv = SafeIv(OptionChainReading.Num(ce, "ltp"), f, k, r, years, true);
                var peIv = SafeIv(OptionChainReading.Num(pe, "ltp"), f, k, r, years, false);
                var sides = new[] { ceIv, peIv }.Where(v => v is not null).Select(v => v!.Value).ToList();
                double? strikeIv = sides.Count > 0 ? sides.Sum() / sides.Count : null;
                if (strikeIv is not null) valid.Add(strikeIv.Value);
                if (atm is not null && k == atm && strikeIv is not null) atmIv = strikeIv;
                strikes.Add((k, OptionChainReading.Num(ce, "oi"), OptionChainReading.Num(pe, "oi"), ceIv, peIv, strikeIv));
            }

            if (atmIv is null)
            {
                if (valid.Count > 0) { var sorted = valid.OrderBy(v => v).ToList(); atmIv = sorted[sorted.Count / 2]; }
                else atmIv = FallbackIv;
            }

            var rows = new JsonArray();
            double maxIntraday = 0, maxExpiry = 0;
            double? peakIntraday = null, peakExpiry = null;
            foreach (var s in strikes)
            {
                var ceSigma = s.CeIv is { } c && c != 0 ? c : atmIv.Value;
                var peSigma = s.PeIv is { } p && p != 0 ? p : atmIv.Value;
                var ceExp = SafeGamma(f, s.K, years, r, ceSigma);
                var peExp = SafeGamma(f, s.K, years, r, peSigma);
                var ceIntra = SafeGamma(f, s.K, intraday, r, ceSigma);
                var peIntra = SafeGamma(f, s.K, intraday, r, peSigma);
                var densityExpiry = ceExp * s.CeOi + peExp * s.PeOi;
                var densityIntraday = ceIntra * s.CeOi + peIntra * s.PeOi;
                if (densityExpiry > maxExpiry) { maxExpiry = densityExpiry; peakExpiry = s.K; }
                if (densityIntraday > maxIntraday) { maxIntraday = densityIntraday; peakIntraday = s.K; }
                rows.Add(new JsonObject
                {
                    ["strike"] = s.K, ["ce_oi"] = s.CeOi, ["pe_oi"] = s.PeOi, ["iv"] = s.Iv is { } iv ? PyNumber.Round(iv * 100, 2) : null,
                    ["density_intraday"] = densityIntraday, ["density_expiry"] = densityExpiry,
                });
            }

            var sigmaIntraday = spot * atmIv.Value * Math.Sqrt(IntradayYears);
            var sigmaExpiry = spot * atmIv.Value * Math.Sqrt(Math.Max(years, 1e-9));
            var intradayBand = Band(spot, sigmaIntraday);
            var expiryBand = Band(spot, sigmaExpiry);
            return (true, new JsonObject
            {
                ["status"] = "success", ["underlying"] = chainResponse["underlying"]?.DeepClone() ?? underlying, ["exchange"] = exchange, ["expiry_date"] = expiryDate,
                ["spot_price"] = PyNumber.Round(spot, 2), ["forward_price"] = PyNumber.Round(f, 2), ["atm_strike"] = chainResponse["atm_strike"]?.DeepClone(), ["atm_iv"] = PyNumber.Round(atmIv.Value * 100, 2),
                ["dte_days"] = PyNumber.Round(days, 2), ["interest_rate"] = PyNumber.Round(rate, 2), ["peak_intraday_strike"] = peakIntraday, ["peak_expiry_strike"] = peakExpiry,
                ["sigma_move"] = intradayBand["sigma_move"]!.DeepClone(), ["one_sigma_low"] = intradayBand["one_sigma_low"]!.DeepClone(), ["one_sigma_high"] = intradayBand["one_sigma_high"]!.DeepClone(),
                ["two_sigma_low"] = intradayBand["two_sigma_low"]!.DeepClone(), ["two_sigma_high"] = intradayBand["two_sigma_high"]!.DeepClone(),
                ["intraday_band"] = intradayBand, ["expiry_band"] = expiryBand, ["chain"] = rows,
            }, 200);
        }
        catch (Exception e) when (e is FormatException or KeyNotFoundException or ArgumentException or OverflowException)
        {
            return (false, new JsonObject { ["status"] = "error", ["message"] = "Error computing gamma density" }, 500);
        }
    }

    private static DateTime ParseExpiry(string code, int hour, int minute)
    {
        var months = new[] { "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC" };
        var month = Array.IndexOf(months, code.Substring(2, 3).ToUpperInvariant()) + 1;
        if (month == 0) throw new KeyNotFoundException(code);
        return new DateTime(2000 + int.Parse(code.Substring(5, 2), System.Globalization.CultureInfo.InvariantCulture), month, int.Parse(code.Substring(0, 2), System.Globalization.CultureInfo.InvariantCulture), hour, minute, 0);
    }
}
