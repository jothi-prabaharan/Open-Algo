using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Options;

/// <summary>Reads the fields of an option chain row the way OpenAlgo's tools do: a missing or null value counts as nothing, and a leg with no symbol is not a leg.</summary>
public static class OptionChainReading
{
    public static double Num(JsonNode? node, string key, double fallback = 0)
    {
        if (node is not JsonObject o || o[key] is not JsonValue v) return fallback;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        return fallback;
    }

    /// <summary>The value, or the fallback when it is missing, null or zero (Python's <c>x or fallback</c>).</summary>
    public static double NumOr(JsonNode? node, string key, double fallback)
    {
        var v = Num(node, key, 0);
        return v == 0 ? fallback : v;
    }

    public static string? Text(JsonNode? node, string key) => node is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>A leg that exists: an object with a non-empty symbol.</summary>
    public static JsonObject? Leg(JsonNode? row, string side)
    {
        if (row is not JsonObject o || o[side] is not JsonObject leg) return null;
        return string.IsNullOrEmpty(Text(leg, "symbol")) ? null : leg;
    }

    /// <summary>The exchange options are listed on for an underlying's exchange (indices and cash map to F&amp;O).</summary>
    public static string OptionsExchange(string exchange)
    {
        var e = exchange.ToUpperInvariant();
        return e is "NSE_INDEX" or "NSE" ? "NFO" : e is "BSE_INDEX" or "BSE" ? "BFO" : e;
    }
}
