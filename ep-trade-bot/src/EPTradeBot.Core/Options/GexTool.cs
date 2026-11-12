using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Options;

/// <summary>Gamma exposure by strike: gamma x open interest x lot size, call minus put. OpenAlgo's get_gex_data over a chain that has already been fetched.</summary>
public static class GexTool
{
    public static (bool Ok, JsonObject Response, int Code) Compute(JsonObject chainResponse, double? futuresPrice, string underlying, string exchange, string expiryDate, DateTime nowIst)
    {
        var chain = chainResponse["chain"] as JsonArray ?? [];
        var spot = OptionChainReading.Num(chainResponse, "underlying_ltp");
        if (spot <= 0) return (false, new JsonObject { ["status"] = "error", ["message"] = "Could not determine spot price" }, 500);

        var optionsExchange = OptionChainReading.OptionsExchange(exchange);
        double? lotSize = null;
        var rows = new JsonArray();
        double totalCeOi = 0, totalPeOi = 0, totalCeGex = 0, totalPeGex = 0, totalNet = 0;
        foreach (var item in chain)
        {
            var (ceOi, ceGamma, ceGex, ceLot) = Leg(item, "ce", spot, optionsExchange, nowIst);
            var (peOi, peGamma, peGex, peLot) = Leg(item, "pe", spot, optionsExchange, nowIst);
            if (ceLot is not null) lotSize ??= ceLot;
            if (peLot is not null) lotSize ??= peLot;
            var net = ceGex - peGex;
            var rCe = PyNumber.Round(ceGex, 2);
            var rPe = PyNumber.Round(peGex, 2);
            var rNet = PyNumber.Round(net, 2);
            rows.Add(new JsonObject
            {
                ["strike"] = item?["strike"]?.DeepClone(), ["ce_oi"] = ceOi, ["pe_oi"] = peOi, ["ce_gamma"] = PyNumber.Round(ceGamma, 6), ["pe_gamma"] = PyNumber.Round(peGamma, 6),
                ["ce_gex"] = rCe, ["pe_gex"] = rPe, ["net_gex"] = rNet,
            });
            totalCeOi += ceOi; totalPeOi += peOi; totalCeGex += rCe; totalPeGex += rPe; totalNet += rNet;
        }

        return (true, new JsonObject
        {
            ["status"] = "success",
            ["underlying"] = chainResponse["underlying"]?.DeepClone() ?? underlying,
            ["spot_price"] = spot,
            ["futures_price"] = futuresPrice,
            ["lot_size"] = lotSize is null or 0 ? 1 : lotSize,
            ["atm_strike"] = chainResponse["atm_strike"]?.DeepClone(),
            ["expiry_date"] = expiryDate,
            ["pcr_oi"] = totalCeOi > 0 ? PyNumber.Round(totalPeOi / totalCeOi, 2) : 0,
            ["total_ce_oi"] = totalCeOi,
            ["total_pe_oi"] = totalPeOi,
            ["total_ce_gex"] = PyNumber.Round(totalCeGex, 2),
            ["total_pe_gex"] = PyNumber.Round(totalPeGex, 2),
            ["total_net_gex"] = PyNumber.Round(totalNet, 2),
            ["chain"] = rows,
        }, 200);
    }

    private static (double Oi, double Gamma, double Gex, double? Lot) Leg(JsonNode? row, string side, double spot, string optionsExchange, DateTime nowIst)
    {
        var leg = OptionChainReading.Leg(row, side);
        if (leg is null) return (0, 0, 0, null);
        var oi = OptionChainReading.Num(leg, "oi");
        var ltp = OptionChainReading.Num(leg, "ltp");
        var lot = OptionChainReading.NumOr(leg, "lotsize", 1);
        var gamma = 0.0;
        if (ltp > 0 && oi > 0)
        {
            var (ok, greeks, _) = OptionGreeks.Calculate(OptionChainReading.Text(leg, "symbol")!, optionsExchange, spot, ltp, null, null, nowIst);
            if (ok && greeks["status"]?.GetValue<string>() == "success") gamma = OptionChainReading.Num(greeks["greeks"], "gamma");
        }

        return (oi, gamma, gamma * oi * lot, lot);
    }
}
