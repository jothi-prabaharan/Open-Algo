using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Options;

/// <summary>
/// Implied volatility and Greeks for a whole option ladder in one pass, and the forward the ladder is priced off. OpenAlgo's calculate_chain_greeks and _forward_from_chain: a leg with no time value or
/// whose volatility cannot be found takes theoretical Greeks (volatility 0, delta +/-1 only when in the money), and an unpriced leg gets none.
/// </summary>
public static class ChainGreeks
{
    /// <summary>The forward from the at-the-money pair by put-call parity, K + C - P (4 places); the underlying's price when either leg is unpriced.</summary>
    public static double ForwardFromChain(JsonArray chain, double atmStrike, double underlyingLtp)
    {
        foreach (var row in chain)
        {
            if (OptionChainReading.Num(row, "strike") != atmStrike) continue;
            var ce = OptionChainReading.Num(row?["ce"], "ltp");
            var pe = OptionChainReading.Num(row?["pe"], "ltp");
            if (ce > 0 && pe > 0) return PyNumber.Round(atmStrike + ce - pe, 4);
            break;
        }

        return underlyingLtp;
    }

    public static (List<JsonObject?> Ce, List<JsonObject?> Pe) Compute(IReadOnlyList<double> strikes, IReadOnlyList<double> cePrices, IReadOnlyList<double> pePrices, double forward, double years, double? ratePercent)
    {
        var count = strikes.Count;
        if (count == 0) return ([], []);
        if (forward <= 0 || years <= 0) return (Enumerable.Repeat<JsonObject?>(null, count).ToList(), Enumerable.Repeat<JsonObject?>(null, count).ToList());
        var rate = (ratePercent ?? 0) / 100.0;

        List<JsonObject?> Side(IReadOnlyList<double> prices, bool call)
        {
            var result = new List<JsonObject?>();
            for (var i = 0; i < count; i++)
            {
                var price = prices[i] > 0 ? prices[i] : 0.0;
                var strike = strikes[i];
                var intrinsic = Math.Max(call ? forward - strike : strike - forward, 0.0);
                var timeValue = price - intrinsic;
                var priced = price > 0 && strike > 0;
                if (!priced) { result.Add(null); continue; }
                var noTimeValue = timeValue <= 0 || (intrinsic > 0 && timeValue < 0.01);
                double? sigma = noTimeValue ? null : Black76.ImpliedVolatility(price, forward, strike, rate, years, call, out _);
                if (sigma is { } s && double.IsFinite(s) && s > 0)
                {
                    result.Add(new JsonObject
                    {
                        ["iv"] = PyNumber.Round(s * 100.0, 2), ["delta"] = PyNumber.Round(Black76.Delta(call, forward, strike, years, rate, s), 4), ["gamma"] = PyNumber.Round(Black76.Gamma(forward, strike, years, rate, s), 6),
                        ["theta"] = PyNumber.Round(Black76.Theta(call, forward, strike, years, rate, s), 4), ["vega"] = PyNumber.Round(Black76.Vega(forward, strike, years, rate, s), 4),
                    });
                }
                else
                {
                    var delta = intrinsic > 0 ? (call ? 1.0 : -1.0) : 0.0;
                    result.Add(new JsonObject { ["iv"] = 0, ["delta"] = delta, ["gamma"] = 0, ["theta"] = 0, ["vega"] = 0 });
                }
            }

            return result;
        }

        return (Side(cePrices, true), Side(pePrices, false));
    }

    /// <summary>Attaches implied volatility and Greeks to every leg of the chain, in place; a leg that has none is left without the fields so "no data" differs from "zero".</summary>
    public static void Attach(JsonArray chain, DateTime? expiryIst, double forward, double? ratePercent, DateTime nowIst)
    {
        if (expiryIst is null) return;
        var (years, _) = OptionGreeks.TimeToExpiry(expiryIst.Value, nowIst);
        if (years <= 0) return;
        var strikes = chain.Select(r => OptionChainReading.Num(r, "strike")).ToList();
        var ce = chain.Select(r => OptionChainReading.Num(r?["ce"], "ltp")).ToList();
        var pe = chain.Select(r => OptionChainReading.Num(r?["pe"], "ltp")).ToList();
        var (ceGreeks, peGreeks) = Compute(strikes, ce, pe, forward, years, ratePercent);
        for (var i = 0; i < chain.Count; i++)
            foreach (var (key, g) in new[] { ("ce", ceGreeks[i]), ("pe", peGreeks[i]) })
            {
                if (chain[i]?[key] is not JsonObject leg || g is null) continue;
                leg["implied_volatility"] = g["iv"]!.DeepClone();
                leg["delta"] = g["delta"]!.DeepClone();
                leg["gamma"] = g["gamma"]!.DeepClone();
                leg["theta"] = g["theta"]!.DeepClone();
                leg["vega"] = g["vega"]!.DeepClone();
            }
    }
}
