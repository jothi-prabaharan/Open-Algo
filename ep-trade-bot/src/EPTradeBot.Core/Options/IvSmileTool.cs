using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Options;

/// <summary>Implied volatility across strikes with the ATM level and a skew read at about five percent either side of ATM. OpenAlgo's get_iv_smile_data over a chain already fetched.</summary>
public static class IvSmileTool
{
    private static double? LegIv(JsonNode? row, string side, double spot, string optionsExchange, DateTime nowIst)
    {
        var leg = OptionChainReading.Leg(row, side);
        if (leg is null) return null;
        var ltp = OptionChainReading.Num(leg, "ltp");
        if (ltp <= 0) return null;
        var (ok, greeks, _) = OptionGreeks.Calculate(OptionChainReading.Text(leg, "symbol")!, optionsExchange, spot, ltp, null, null, nowIst);
        if (!ok || greeks["status"]?.GetValue<string>() != "success") return null;
        var iv = OptionChainReading.Num(greeks, "implied_volatility");
        return iv > 0 ? PyNumber.Round(iv, 2) : null;
    }

    public static (bool Ok, JsonObject Response, int Code) Compute(JsonObject chainResponse, string underlying, string exchange, string expiryDate, DateTime nowIst)
    {
        var chain = chainResponse["chain"] as JsonArray ?? [];
        var spot = OptionChainReading.Num(chainResponse, "underlying_ltp");
        if (spot <= 0) return (false, new JsonObject { ["status"] = "error", ["message"] = "Could not determine spot price" }, 500);

        var atm = chainResponse["atm_strike"] is JsonValue av && av.TryGetValue<double>(out var atmValue) ? (double?)atmValue : null;
        var optionsExchange = OptionChainReading.OptionsExchange(exchange);
        var rows = new List<(double Strike, JsonNode? Raw, double? Ce, double? Pe)>();
        double? atmCe = null, atmPe = null;
        foreach (var item in chain)
        {
            var strike = OptionChainReading.Num(item, "strike");
            var ce = LegIv(item, "ce", spot, optionsExchange, nowIst);
            var pe = LegIv(item, "pe", spot, optionsExchange, nowIst);
            if (atm is not null && strike == atm) { atmCe = ce; atmPe = pe; }
            rows.Add((strike, item?["strike"], ce, pe));
        }

        double? atmIv = null;
        if (atmCe is not null && atmPe is not null) atmIv = PyNumber.Round((atmCe.Value + atmPe.Value) / 2, 2);
        else if (atmCe is not null) atmIv = atmCe;
        else if (atmPe is not null) atmIv = atmPe;

        double? skew = null;
        if (atm is { } a && a != 0 && rows.Count > 0)
        {
            var distance = a * 0.05;
            double? putIv = rows.OrderBy(r => Math.Abs(r.Strike - (a - distance))).FirstOrDefault(r => r.Strike < a && r.Pe is not null).Pe;
            double? callIv = rows.OrderBy(r => Math.Abs(r.Strike - (a + distance))).FirstOrDefault(r => r.Strike > a && r.Ce is not null).Ce;
            if (putIv is not null && callIv is not null) skew = PyNumber.Round(putIv.Value - callIv.Value, 2);
        }

        var array = new JsonArray();
        foreach (var r in rows) array.Add(new JsonObject { ["strike"] = r.Raw?.DeepClone(), ["ce_iv"] = r.Ce, ["pe_iv"] = r.Pe });
        return (true, new JsonObject
        {
            ["status"] = "success", ["underlying"] = chainResponse["underlying"]?.DeepClone() ?? underlying, ["spot_price"] = spot, ["atm_strike"] = chainResponse["atm_strike"]?.DeepClone(),
            ["atm_iv"] = atmIv, ["skew"] = skew, ["expiry_date"] = expiryDate, ["chain"] = array,
        }, 200);
    }
}
