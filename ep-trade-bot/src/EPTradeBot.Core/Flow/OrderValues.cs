using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Flow;

/// <summary>The words an order is written in.</summary>
public static class OrderVocabulary
{
    public static readonly IReadOnlyList<string> Actions = ["BUY", "SELL"];
    public static readonly IReadOnlyList<string> PriceTypes = ["MARKET", "LIMIT", "SL", "SL-M"];
    public static readonly IReadOnlyList<string> Products = ["CNC", "NRML", "MIS"];
    public static readonly IReadOnlyList<string> Exchanges = ["NSE", "NFO", "CDS", "BSE", "BFO", "BCD", "MCX", "NCDEX", "NCO", "NSE_INDEX", "BSE_INDEX", "MCX_INDEX", "GLOBAL_INDEX", "CRYPTO"];

    /// <summary>Segments that carry contracts on margin; a position there is normally taken NRML.</summary>
    private static readonly IReadOnlyList<string> Derivatives = ["NFO", "BFO", "CDS", "BCD", "MCX", "NCDEX", "NCO"];

    /// <summary>The product a node uses when its author picked none: NRML on a derivatives segment, MIS elsewhere. A product the author chose always wins.</summary>
    public static string DefaultProduct(string? exchange) => Derivatives.Contains((exchange ?? "").Trim().ToUpperInvariant()) ? "NRML" : "MIS";

    /// <summary>Why this price type cannot be sent, or null when it can: a limit or stop order whose price never arrived would execute at an unintended level.</summary>
    public static string? InvalidPriceReason(string priceType, double price, double triggerPrice)
    {
        var t = string.IsNullOrEmpty(priceType) ? "MARKET" : priceType.ToUpperInvariant();
        if (!double.IsFinite(price)) return $"{t} order needs a finite price, got {FlowContext.PyFloat(price)}";
        if (!double.IsFinite(triggerPrice)) return $"{t} order needs a finite trigger price, got {FlowContext.PyFloat(triggerPrice)}";
        if (t is "LIMIT" or "SL" && price <= 0) return $"{t} order needs a positive price, got {FlowContext.PyFloat(price)}";
        if (t is "SL" or "SL-M" && triggerPrice <= 0) return $"{t} order needs a positive trigger price, got {FlowContext.PyFloat(triggerPrice)}";
        return null;
    }
}

/// <summary>
/// Reads one order's fields without turning bad data into defaults. A default is used only when no supported key is present; once a value is supplied it must
/// resolve and be valid on its own merits, so a typo cannot become quantity 1 or a broker's MARKET fallback. Port of OpenAlgo's RuntimeOrderResolver.
/// A null fallback means the field is required.
/// </summary>
public sealed class RuntimeOrderResolver(FlowContext context, JsonObject data)
{
    private JsonNode? Find(string key, string[] aliases, JsonNode? fallback, bool required)
    {
        foreach (var candidate in new[] { key }.Concat(aliases))
        {
            if (!data.ContainsKey(candidate)) continue;
            var raw = data[candidate];
            var resolved = raw is JsonValue v && v.TryGetValue<string>(out var text) ? context.ResolveRaw(text) : raw;
            if (resolved is JsonValue r && r.TryGetValue<string>(out var s) && FlowContext.HasUnresolved(s))
                throw new ArgumentException($"{candidate} has unresolved value '{s}'");
            return resolved;
        }

        if (required) throw new ArgumentException($"{key} is required");
        return fallback;
    }

    /// <summary>A text field that may be absent or blank, but must be text when given.</summary>
    public string OptionalText(string key)
    {
        var v = Find(key, [], JsonValue.Create(""), false);
        if (v is null) return "";
        if (v is not JsonValue jv || !jv.TryGetValue<string>(out var text)) throw new ArgumentException($"{key} must resolve to text, got {FlowContext.PyStr(v)}");
        return text.Trim();
    }

    public string Text(string key, string? fallback = null, params string[] aliases)
    {
        var v = Find(key, aliases, fallback is null ? null : JsonValue.Create(fallback), fallback is null);
        if (v is not JsonValue jv || !jv.TryGetValue<string>(out var text)) throw new ArgumentException($"{key} must resolve to text, got {FlowContext.PyStr(v)}");
        text = text.Trim();
        if (text.Length == 0) throw new ArgumentException($"{key} must not be blank");
        return text;
    }

    public string Enum(string key, IReadOnlyList<string> allowed, string? fallback = null, params string[] aliases)
    {
        var canonical = Text(key, fallback, aliases).ToUpperInvariant();
        if (!allowed.Contains(canonical)) throw new ArgumentException($"{key} must be one of {string.Join(", ", allowed.Order(StringComparer.Ordinal))}, got '{canonical}'");
        return canonical;
    }

    public double Number(string key, double? fallback = null, params string[] aliases)
    {
        var v = Find(key, aliases, fallback is null ? null : JsonValue.Create(fallback.Value), fallback is null);
        double number;
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<bool>(out _)) throw new ArgumentException($"{key} must be numeric, got boolean {FlowContext.PyStr(jv)}");
            if (jv.TryGetValue<string>(out var s))
            {
                if (s.Trim().Length == 0) throw new ArgumentException($"{key} must not be blank");
                if (!FlowData.TryParseFloat(s, out number)) throw new ArgumentException($"{key} must be numeric, got '{s}'");
            }
            else if (!FlowData.TryNumber(jv, out number)) throw new ArgumentException($"{key} must be numeric, got {FlowContext.PyStr(jv)}");
        }
        else throw new ArgumentException($"{key} must be numeric, got {FlowContext.PyStr(v)}");

        if (!double.IsFinite(number)) throw new ArgumentException($"{key} must be finite, got {FlowContext.PyFloat(number)}");
        return number;
    }

    public long Integer(string key, long? fallback = null, long? minimum = null, params string[] aliases)
    {
        var n = Number(key, fallback, aliases);
        if (minimum is { } min && n < min)
            throw new ArgumentException($"{key} must be {(min == 0 ? "non-negative" : $"at least {min}")}, got {FlowContext.PyFloat(n)}");
        return (long)n;
    }
}
