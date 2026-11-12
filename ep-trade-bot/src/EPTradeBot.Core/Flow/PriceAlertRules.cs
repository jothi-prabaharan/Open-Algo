using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Flow;

/// <summary>What a price-alert trigger node asks for.</summary>
public sealed record PriceAlert(string Symbol, string Exchange, string Condition, double Target, double? Lower, double? Upper, double Percentage, bool EveryTime);

/// <summary>What an alert remembers between prices: the last price it saw, and whether a once-only alert has already fired.</summary>
public sealed class PriceAlertState
{
    public double? LastPrice { get; set; }

    public bool Triggered { get; set; }
}

/// <summary>
/// When a price alert fires. Port of OpenAlgo's FlowPriceMonitor (condition vocabulary, evaluation and the once-only latch), fed by ticks rather than polled
/// quotes. Pure: the alert, its memory and one price in; whether to fire out.
/// </summary>
public static class PriceAlertRules
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["above"] = "greater_than", ["price_above"] = "greater_than", ["below"] = "less_than", ["price_below"] = "less_than",
        ["crosses_above"] = "crossing_up", ["cross_above"] = "crossing_up", ["crosses_below"] = "crossing_down", ["cross_below"] = "crossing_down", ["crosses"] = "crossing",
    };

    public static readonly IReadOnlyList<string> Conditions =
    [
        "greater_than", "less_than", "crossing", "crossing_up", "crossing_down", "entering_channel", "inside_channel", "exiting_channel", "outside_channel",
        "moving_up", "moving_down", "moving_up_percent", "moving_down_percent",
    ];

    /// <summary>The conditions whose level or channel must be given.</summary>
    private static readonly IReadOnlyList<string> NeedsPrice = ["greater_than", "less_than", "crossing", "crossing_up", "crossing_down"];

    private static readonly IReadOnlyList<string> NeedsChannel = ["entering_channel", "inside_channel", "exiting_channel", "outside_channel"];

    /// <summary>The condition's canonical name; the editor and the monitor each grew a vocabulary for the same conditions, and both are accepted.</summary>
    public static string Normalize(string? value)
    {
        var raw = (value ?? "").Trim().ToLowerInvariant().Replace('-', '_');
        return Aliases.GetValueOrDefault(raw, raw);
    }

    /// <summary>Reads an alert from a trigger node, or says what is wrong with it.</summary>
    public static (PriceAlert? Alert, string? Problem) FromNode(JsonObject data)
    {
        string Text(string key) => data[key] is JsonValue v ? FlowContext.PyStr(v).Trim() : "";
        double? Num(string key) => data[key] is JsonValue v && FlowData.TryParseOrNumber(v, out var d) && double.IsFinite(d) ? d : null;

        var symbol = Text("symbol").ToUpperInvariant();
        if (symbol.Length == 0) return (null, "The price alert has no symbol.");
        var exchange = Text("exchange").ToUpperInvariant();
        if (exchange.Length == 0) exchange = "NSE";
        var condition = Normalize(Text("condition").Length == 0 ? "greater_than" : Text("condition"));
        if (!Conditions.Contains(condition)) return (null, $"The price alert condition '{Text("condition")}' is not one this platform knows.");

        var price = Num("price");
        var lower = Num("priceLower");
        var upper = Num("priceUpper");
        if (NeedsPrice.Contains(condition) && !(price > 0)) return (null, $"A '{condition}' price alert needs a price above zero; it could never fire without one.");
        if (NeedsChannel.Contains(condition))
        {
            if (!(lower > 0) || !(upper > 0)) return (null, $"A '{condition}' price alert needs a lower and an upper price above zero.");
            if (lower >= upper) return (null, $"The lower price ({FlowContext.PyFloat(lower.Value)}) must be below the upper price ({FlowContext.PyFloat(upper.Value)}); an inverted channel contains no prices.");
        }

        var percentage = Num("percentage") ?? 0;
        if (condition is "moving_up_percent" or "moving_down_percent" && percentage <= 0) return (null, $"A '{condition}' price alert needs a percentage above zero.");

        var everyTime = Text("trigger").Replace('-', '_').Equals("every_time", StringComparison.OrdinalIgnoreCase);
        return (new PriceAlert(symbol, exchange, condition, price ?? 0, lower, upper, percentage, everyTime), null);
    }

    /// <summary>Whether the condition holds for this price, given the last price seen.</summary>
    public static bool Holds(PriceAlert a, double price, double? last)
    {
        var tolerance = price * 0.001;
        switch (a.Condition)
        {
            case "greater_than": return price > a.Target;
            case "less_than": return price < a.Target;
            case "crossing": return Math.Abs(price - a.Target) <= tolerance;
            case "crossing_up": return last is null ? price > a.Target : last <= a.Target && price > a.Target;
            case "crossing_down": return last is null ? price < a.Target : last >= a.Target && price < a.Target;
            case "entering_channel" or "inside_channel": return (a.Lower ?? a.Target) <= price && price <= (a.Upper ?? a.Target);
            case "exiting_channel" or "outside_channel": return price < (a.Lower ?? a.Target) || price > (a.Upper ?? a.Target);
            case "moving_up": return last is not null && price > last;
            case "moving_down": return last is not null && price < last;
            case "moving_up_percent": return last is { } up && up != 0 && (price - up) / up * 100 >= a.Percentage;
            case "moving_down_percent": return last is { } down && down != 0 && (down - price) / down * 100 >= a.Percentage;
            default: return false;
        }
    }

    /// <summary>
    /// One price arriving. Returns true when the alert fires. A price that is not above zero is no price. A once-only alert fires once and is spent; an every-time alert
    /// keeps watching, and remembers the price it fired at so an edge-triggered crossing needs a fresh cross rather than firing again.
    /// </summary>
    public static bool Check(PriceAlert alert, PriceAlertState state, double price)
    {
        if (!(price > 0) || !double.IsFinite(price)) return false;
        var met = Holds(alert, price, state.LastPrice);
        if (met && !state.Triggered)
        {
            if (!alert.EveryTime) state.Triggered = true;
            state.LastPrice = price;
            return true;
        }

        state.LastPrice = price;
        return false;
    }
}
