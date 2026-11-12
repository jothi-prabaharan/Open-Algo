using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Options;

/// <summary>Open interest by strike with put-call ratios, and the strike where option writers lose least (max pain). OpenAlgo's get_oi_data and calculate_max_pain over a chain already fetched.</summary>
public static class OiTools
{
    private static JsonObject? Present(JsonNode? row, string side) => row is JsonObject o && o[side] is JsonObject leg && leg.Count > 0 ? leg : null;

    public static (bool Ok, JsonObject Response, int Code) OiData(JsonObject chainResponse, double? futuresPrice, string underlying, string expiryDate)
    {
        var chain = chainResponse["chain"] as JsonArray ?? [];
        double ceOi = 0, peOi = 0, ceVolume = 0, peVolume = 0;
        double? lot = null;
        var rows = new JsonArray();
        foreach (var item in chain)
        {
            var ce = Present(item, "ce");
            var pe = Present(item, "pe");
            var rowCe = 0.0;
            var rowPe = 0.0;
            if (ce is not null)
            {
                rowCe = OptionChainReading.Num(ce, "oi");
                ceOi += rowCe;
                ceVolume += OptionChainReading.Num(ce, "volume");
                if (lot is null && OptionChainReading.Num(ce, "lotsize") != 0) lot = OptionChainReading.Num(ce, "lotsize");
            }

            if (pe is not null)
            {
                rowPe = OptionChainReading.Num(pe, "oi");
                peOi += rowPe;
                peVolume += OptionChainReading.Num(pe, "volume");
                if (lot is null && OptionChainReading.Num(pe, "lotsize") != 0) lot = OptionChainReading.Num(pe, "lotsize");
            }

            rows.Add(new JsonObject { ["strike"] = item?["strike"]?.DeepClone(), ["ce_oi"] = rowCe, ["pe_oi"] = rowPe });
        }

        return (true, new JsonObject
        {
            ["status"] = "success", ["underlying"] = chainResponse["underlying"]?.DeepClone() ?? underlying, ["spot_price"] = chainResponse["underlying_ltp"]?.DeepClone(), ["futures_price"] = futuresPrice,
            ["lot_size"] = lot is null or 0 ? 1 : lot, ["pcr_oi"] = ceOi > 0 ? PyNumber.Round(peOi / ceOi, 2) : 0, ["pcr_volume"] = ceVolume > 0 ? PyNumber.Round(peVolume / ceVolume, 2) : 0,
            ["total_ce_oi"] = ceOi, ["total_pe_oi"] = peOi, ["atm_strike"] = chainResponse["atm_strike"]?.DeepClone(), ["expiry_date"] = expiryDate, ["chain"] = rows,
        }, 200);
    }

    public static (bool Ok, JsonObject Response, int Code) MaxPain(JsonObject chainResponse, double? futuresPrice, string underlying, string expiryDate)
    {
        var (_, oi, _) = OiData(chainResponse, futuresPrice, underlying, expiryDate);
        var all = oi["chain"] as JsonArray ?? [];
        if (all.Count == 0) return (false, new JsonObject { ["status"] = "error", ["message"] = "No OI data available" }, 404);
        var strikes = all.Where(i => i?["strike"] is JsonValue v && !(v.TryGetValue<string>(out _)) && OptionChainReading.Num(i, "strike") > 0 && i["strike"] is not null && IsNumber(i["strike"]!)).ToList();
        if (strikes.Count == 0) return (false, new JsonObject { ["status"] = "error", ["message"] = "No valid strike data available" }, 404);

        var pain = new List<(JsonNode? Strike, double Value, double Ce, double Pe, double Total)>();
        var cr = new List<double>();
        foreach (var candidate in strikes)
        {
            var cs = OptionChainReading.Num(candidate, "strike");
            double cePain = 0, pePain = 0;
            foreach (var item in strikes)
            {
                var s = OptionChainReading.Num(item, "strike");
                var ceOi = OptionChainReading.Num(item, "ce_oi");
                var peOi = OptionChainReading.Num(item, "pe_oi");
                if (cs > s && ceOi > 0) cePain += (cs - s) * ceOi;
                if (cs < s && peOi > 0) pePain += (s - cs) * peOi;
            }

            pain.Add((candidate!["strike"], cs, PyNumber.Round(cePain, 2), PyNumber.Round(pePain, 2), PyNumber.Round(cePain + pePain, 2)));
            // total_pain_cr is derived from the unrounded total, as OpenAlgo does
            cr.Add(PyNumber.Round((cePain + pePain) / 10000000, 2));
        }

        var best = pain.Select((p, i) => (p, i)).MinBy(x => x.p.Total);
        var array = new JsonArray();
        for (var i = 0; i < pain.Count; i++)
            array.Add(new JsonObject { ["strike"] = pain[i].Strike?.DeepClone(), ["ce_pain"] = pain[i].Ce, ["pe_pain"] = pain[i].Pe, ["total_pain"] = pain[i].Total, ["total_pain_cr"] = cr[i] });
        var response = new JsonObject
        {
            ["status"] = "success", ["underlying"] = oi["underlying"]?.DeepClone(), ["spot_price"] = oi["spot_price"]?.DeepClone(), ["futures_price"] = oi["futures_price"]?.DeepClone(),
            ["atm_strike"] = oi["atm_strike"]?.DeepClone(), ["max_pain_strike"] = best.p.Strike?.DeepClone(), ["lot_size"] = oi["lot_size"]?.DeepClone(), ["pcr_oi"] = oi["pcr_oi"]?.DeepClone(),
            ["pcr_volume"] = oi["pcr_volume"]?.DeepClone(), ["expiry_date"] = expiryDate, ["pain_data"] = array,
        };
        return (true, response, 200);
    }

    private static bool IsNumber(JsonNode n) => n is JsonValue v && (v.TryGetValue<double>(out _) || v.TryGetValue<long>(out _) || v.TryGetValue<int>(out _));
}
