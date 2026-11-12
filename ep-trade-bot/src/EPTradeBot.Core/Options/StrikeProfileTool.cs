using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Options;

/// <summary>
/// Gamma and delta exposure for every strike of every selected expiry, with the gamma flip and the call and put walls. OpenAlgo's get_strike_profile, with the chain fetch passed in.
/// GEX is gamma x open interest x spot squared x 1% in crore (calls positive, puts negative); DEX is delta-weighted open interest x spot in crore.
/// </summary>
public static class StrikeProfileTool
{
    public const int DefaultStrikeCount = 20;
    public const int MaxStrikeCount = 40;
    public const int FlipGridPoints = 241;
    private const double Crore = 1e7;
    private const double SecondsPerYear = 365.0 * 86400.0;
    private const double MinYears = 0.0001;

    private static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    /// <summary>Fetches an expiry's chain with Greeks for a number of strikes either side of ATM.</summary>
    public delegate (bool Ok, JsonObject Response, int Code) ChainFetch(string expiry, int strikeCount);

    private sealed record Leg(double Oi, double Gex, double Dex, double Iv, bool Priced);

    private static Leg Exposure(JsonNode? legNode, double spot, double sign)
    {
        if (legNode is not JsonObject leg || leg.Count == 0) return new Leg(0, 0.0, 0.0, 0.0, false);
        var oi = OptionChainReading.Num(leg, "oi");
        var hasGreeks = leg.ContainsKey("gamma") && leg.ContainsKey("delta");
        var gamma = OptionChainReading.Num(leg, "gamma");
        var delta = OptionChainReading.Num(leg, "delta");
        return new Leg(oi, sign * gamma * oi * spot * spot * 0.01 / Crore, delta * oi * spot / Crore, OptionChainReading.Num(leg, "implied_volatility"), hasGreeks);
    }

    private static double ExpiryYears(JsonObject chain, double nowUnix)
    {
        var expiry = OptionChainReading.Num(chain, "expiry_ts");
        if (expiry == 0) return 0.0;
        var server = OptionChainReading.NumOr(chain, "server_ts", nowUnix);
        return Math.Max((expiry - server) / SecondsPerYear, 0.0);
    }

    private static int? CalendarDte(double expiryTs, DateTime nowUtc)
    {
        if (expiryTs == 0) return null;
        var expiryDay = DateTimeOffset.FromUnixTimeSeconds((long)expiryTs).UtcDateTime.Add(IstOffset).Date;
        var today = nowUtc.Add(IstOffset).Date;
        return Math.Max((int)(expiryDay - today).TotalDays, 0);
    }

    /// <summary>The spot at which total GEX changes sign: every leg's gamma re-priced (its own volatility held) over a grid of hypothetical spots; the crossing nearest spot, or null.</summary>
    public static double? FindGammaFlip(IReadOnlyList<(double Strike, double Forward, double Years, double Sigma, double Oi, double Sign)> legs, double spot, double low, double high, int points = FlipGridPoints)
    {
        if (legs.Count == 0 || spot <= 0 || low <= 0 || high <= low) return null;
        var step = (high - low) / (points - 1);
        var totals = new double[points];
        for (var i = 0; i < points; i++)
        {
            var price = i == points - 1 ? high : low + i * step;
            double sum = 0;
            foreach (var l in legs)
            {
                var gamma = Black76.Gamma(l.Forward * (price / spot), l.Strike, l.Years, 0.0, l.Sigma);
                if (double.IsNaN(gamma) || double.IsInfinity(gamma)) gamma = 0;
                sum += gamma * (l.Oi * l.Sign);
            }

            totals[i] = sum * price * price;
        }

        double? best = null;
        for (var i = 0; i < points - 1; i++)
        {
            double a = totals[i], b = totals[i + 1];
            double crossing;
            var gridI = i == points - 1 ? high : low + i * step;
            var gridNext = i + 1 == points - 1 ? high : low + (i + 1) * step;
            if (a == 0) crossing = gridI;
            else if ((a < 0) != (b < 0) && b != 0) crossing = gridI + (gridNext - gridI) * (-a) / (b - a);
            else continue;
            if (best is null || Math.Abs(crossing - spot) < Math.Abs(best.Value - spot)) best = crossing;
        }

        return best;
    }

    private static int StrikeCountForWindow(JsonObject chain, double spot, double? rangePct)
    {
        if (rangePct is null or 0) return MaxStrikeCount;
        var strikes = (chain["chain"] as JsonArray ?? []).Select(r => OptionChainReading.Num(r, "strike")).Distinct().OrderBy(s => s).ToList();
        if (strikes.Count < 2) return DefaultStrikeCount;
        var steps = strikes.Zip(strikes.Skip(1), (a, b) => b - a).Where(d => d > 0).ToList();
        var step = steps.Count > 0 ? steps.Min() : 0;
        if (step <= 0) return DefaultStrikeCount;
        var needed = (int)Math.Ceiling(spot * rangePct.Value / 100.0 / step) + 1;
        return Math.Max(5, Math.Min(needed, MaxStrikeCount));
    }

    public static (bool Ok, JsonObject Response, int Code) Compute(string underlying, string exchange, IReadOnlyList<string> expiries, double? rangePct, ChainFetch fetch, DateTime nowUtc)
    {
        static JsonObject Error(string message) => new() { ["status"] = "error", ["message"] = message };
        if (expiries.Count == 0) return (false, Error("Select at least one expiry."), 400);

        var nowUnix = (nowUtc - DateTime.UnixEpoch).TotalSeconds;
        double? spot = null;
        var metas = new List<JsonObject>();
        var rowsByExpiry = new List<Dictionary<double, JsonObject>>();
        var flipLegs = new List<(double, double, double, double, double, double)>();
        var allStrikes = new SortedSet<double>();
        var strikeCount = DefaultStrikeCount;
        JsonNode? serverTs = null;

        for (var index = 0; index < expiries.Count; index++)
        {
            var expiry = expiries[index];
            var (ok, chain, code) = fetch(expiry, strikeCount);
            if (!ok)
            {
                if (code == 429 || index == 0) return (false, chain, code);
                metas.Add(new JsonObject { ["expiry"] = expiry, ["expiry_ts"] = null, ["dte"] = null, ["available"] = false, ["message"] = "No option data for this expiry." });
                rowsByExpiry.Add([]);
                continue;
            }

            if (spot is null)
            {
                var ltp = OptionChainReading.Num(chain, "underlying_ltp");
                if (ltp <= 0) return (false, Error("The underlying price is not available."), 404);
                spot = ltp;
                serverTs = chain["server_ts"]?.DeepClone();
                var wanted = StrikeCountForWindow(chain, ltp, rangePct);
                if (wanted > strikeCount)
                {
                    strikeCount = wanted;
                    var (okAgain, refetched, codeAgain) = fetch(expiry, strikeCount);
                    if (!okAgain) return (false, refetched, codeAgain);
                    chain = refetched;
                }
                else
                {
                    strikeCount = wanted;
                }
            }

            var spotValue = spot.Value;
            var years = ExpiryYears(chain, nowUnix);
            var forward = OptionChainReading.NumOr(chain, "forward_price", spotValue);
            var expiryTs = OptionChainReading.Num(chain, "expiry_ts");
            var rows = new Dictionary<double, JsonObject>();
            double totalGex = 0, totalDex = 0;
            var chainRows = chain["chain"] as JsonArray ?? [];
            foreach (var row in chainRows)
            {
                if (row?["strike"] is not JsonValue sv || sv.TryGetValue<string>(out _)) continue;
                var strike = OptionChainReading.Num(row, "strike");
                if (strike <= 0) continue;
                var ce = Exposure(row["ce"], spotValue, 1.0);
                var pe = Exposure(row["pe"], spotValue, -1.0);
                var gex = ce.Gex + pe.Gex;
                var dex = ce.Dex + pe.Dex;
                var unpriced = (ce.Oi > 0 && !ce.Priced) || (pe.Oi > 0 && !pe.Priced);
                rows[strike] = new JsonObject
                {
                    ["ce_oi"] = ce.Oi, ["pe_oi"] = pe.Oi, ["ce_gex"] = PyNumber.Round(ce.Gex, 4), ["pe_gex"] = PyNumber.Round(pe.Gex, 4), ["ce_dex"] = PyNumber.Round(ce.Dex, 4),
                    ["pe_dex"] = PyNumber.Round(pe.Dex, 4), ["gex"] = PyNumber.Round(gex, 4), ["dex"] = PyNumber.Round(dex, 4), ["unpriced"] = unpriced,
                };
                allStrikes.Add(strike);
                totalGex += gex;
                totalDex += dex;
                if (years > 0)
                    foreach (var (leg, sign) in new[] { (ce, 1.0), (pe, -1.0) })
                        if (leg.Oi > 0 && leg.Priced && leg.Iv > 0)
                            flipLegs.Add((strike, forward, Math.Max(years, MinYears), leg.Iv / 100.0, leg.Oi, sign));
            }

            double? lot = null;
            foreach (var row in chainRows)
            {
                var leg = Present(row, "ce") ?? Present(row, "pe");
                if (leg is not null && OptionChainReading.Num(leg, "lotsize") != 0) { lot = OptionChainReading.Num(leg, "lotsize"); break; }
            }

            metas.Add(new JsonObject
            {
                ["expiry"] = expiry, ["expiry_ts"] = chain["expiry_ts"]?.DeepClone(), ["dte"] = CalendarDte(expiryTs, nowUtc), ["years"] = PyNumber.Round(years, 6), ["forward"] = PyNumber.Round(forward, 2),
                ["lot_size"] = lot, ["available"] = true, ["net_gex"] = PyNumber.Round(totalGex, 4), ["net_dex"] = PyNumber.Round(totalDex, 4),
            });
            rowsByExpiry.Add(rows);
        }

        if (spot is null || allStrikes.Count == 0) return (false, Error("No option data for these expiries."), 404);

        var strikes = allStrikes.ToList();
        for (var i = 0; i < metas.Count; i++)
        {
            var cells = new JsonArray();
            foreach (var s in strikes) cells.Add(rowsByExpiry[i].TryGetValue(s, out var cell) ? cell.DeepClone() : null);
            metas[i]["cells"] = cells;
        }

        double? callWall = null, putWall = null;
        double callBest = double.NegativeInfinity, putBest = double.NegativeInfinity;
        double? callWallRaw = null, putWallRaw = null;
        foreach (var s in strikes)
        {
            var call = rowsByExpiry.Sum(r => r.TryGetValue(s, out var c) ? OptionChainReading.Num(c, "ce_gex") : 0.0);
            var put = rowsByExpiry.Sum(r => r.TryGetValue(s, out var c) ? Math.Abs(OptionChainReading.Num(c, "pe_gex")) : 0.0);
            if (call > callBest) { callBest = call; callWallRaw = s; }
            if (put > putBest) { putBest = put; putWallRaw = s; }
        }

        if (callBest > 0) callWall = callWallRaw;
        if (putBest > 0) putWall = putWallRaw;

        var flip = FindGammaFlip(flipLegs, spot.Value, strikes[0], strikes[^1]);
        var strikesArray = new JsonArray();
        foreach (var s in strikes) strikesArray.Add(s);
        var expiriesArray = new JsonArray();
        foreach (var m in metas) expiriesArray.Add(m);
        return (true, new JsonObject
        {
            ["status"] = "success", ["underlying"] = underlying, ["exchange"] = exchange, ["spot_price"] = PyNumber.Round(spot.Value, 2),
            ["as_of"] = serverTs is not null && OptionChainReading.Num(new JsonObject { ["v"] = serverTs.DeepClone() }, "v") != 0 ? serverTs : (long)nowUnix,
            ["range_pct"] = rangePct, ["strikes"] = strikesArray, ["flip"] = flip is null ? null : PyNumber.Round(flip.Value, 2), ["call_wall"] = callWall, ["put_wall"] = putWall, ["expiries"] = expiriesArray,
        }, 200);
    }

    private static JsonObject? Present(JsonNode? row, string side) => row is JsonObject o && o[side] is JsonObject leg && leg.Count > 0 ? leg : null;
}
