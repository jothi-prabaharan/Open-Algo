using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EPTradeBot.Core.Time;

namespace EPTradeBot.Core.Flow;

/// <summary>
/// The variables of one run, and the <c>{{path}}</c> substitution over them. Port of OpenAlgo's WorkflowContext. An unknown path is left as the
/// literal <c>{{...}}</c> text, never an empty string: callers that must not send a guess (orders) look for exactly that.
/// </summary>
public sealed partial class FlowContext(Func<DateTime> utcNow)
{
    private static readonly object Unresolved = new();

    public Dictionary<string, JsonNode?> Variables { get; } = new(StringComparer.Ordinal);

    private readonly Dictionary<string, bool> conditions = new(StringComparer.Ordinal);

    public DateTime IstNow => Ist.FromUtc(utcNow());

    public void Set(string name, JsonNode? value) => Variables[name] = value;

    public JsonNode? Get(string name) => Variables.GetValueOrDefault(name);

    public void SetCondition(string nodeId, bool result) => conditions[nodeId] = result;

    public bool? GetCondition(string nodeId) => conditions.TryGetValue(nodeId, out var v) ? v : null;

    /// <summary>The system variables a flow may reference, from the India clock.</summary>
    private string? Builtin(string name)
    {
        var now = IstNow;
        var iso = (int)now.DayOfWeek == 0 ? 7 : (int)now.DayOfWeek;
        return name switch
        {
            "timestamp" => now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            "date" => now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "time" => now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            "year" => now.ToString("yyyy", CultureInfo.InvariantCulture),
            "month" => now.ToString("MM", CultureInfo.InvariantCulture),
            "day" => now.ToString("dd", CultureInfo.InvariantCulture),
            "hour" => now.ToString("HH", CultureInfo.InvariantCulture),
            "minute" => now.ToString("mm", CultureInfo.InvariantCulture),
            "second" => now.ToString("ss", CultureInfo.InvariantCulture),
            "weekday" => now.DayOfWeek.ToString(),
            "iso_timestamp" => now.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
            // Numeric weekday, because a comparison needs a number: 1 is Monday.
            "weekday_num" => iso.ToString(CultureInfo.InvariantCulture),
            "quarter" => ((now.Month - 1) / 3 + 1).ToString(CultureInfo.InvariantCulture),
            "week_of_year" => ISOWeek.GetWeekOfYear(now).ToString(CultureInfo.InvariantCulture),
            "day_of_year" => now.DayOfYear.ToString(CultureInfo.InvariantCulture),
            // The trading session date differs from "date" between midnight and the 03:00 rollover.
            "session_date" => (now.TimeOfDay < TimeSpan.FromHours(3) ? now.Date.AddDays(-1) : now.Date).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    [GeneratedRegex(@"([^\.\[\]]+)|\[(\d+)\]")]
    private static partial Regex SegmentPattern();

    [GeneratedRegex(@"^\{\{\s*([^}]+?)\s*\}\}$")]
    private static partial Regex WholeTokenPattern();

    [GeneratedRegex(@"\{\{([^}]+)\}\}")]
    private static partial Regex TokenPattern();

    /// <summary>Whether text still holds a <c>{{...}}</c> reference after substitution.</summary>
    public static bool HasUnresolved(string text) => TokenPattern().IsMatch(text);

    /// <summary>Walks a dotted and bracketed path through the variables; <see cref="Unresolved"/> when any step is missing.</summary>
    private object WalkPath(string path)
    {
        JsonNode? value = new JsonObject(Variables.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value?.DeepClone())));
        foreach (Match seg in SegmentPattern().Matches(path))
        {
            if (seg.Groups[1].Success)
            {
                if (value is not JsonObject o || !o.TryGetPropertyValue(seg.Groups[1].Value, out value)) return Unresolved;
            }
            else
            {
                var i = int.Parse(seg.Groups[2].Value, CultureInfo.InvariantCulture);
                if (value is not JsonArray a || i >= a.Count) return Unresolved;
                value = a[i];
            }

            if (value is null) return Unresolved;
        }

        return value!;
    }

    /// <summary>A string that is exactly one <c>{{path}}</c> token resolves to the stored value itself; anything else is substituted as text.</summary>
    public JsonNode? ResolveRaw(string text)
    {
        var m = WholeTokenPattern().Match(text);
        if (m.Success)
        {
            var path = m.Groups[1].Value.Trim();
            if (Builtin(path) is null && WalkPath(path) is JsonNode found) return found;
        }

        return JsonValue.Create(Interpolate(text));
    }

    public string Interpolate(string text) => TokenPattern().Replace(text, m =>
    {
        var path = m.Groups[1].Value.Trim();
        if (Builtin(path) is { } builtin) return builtin;
        return WalkPath(path) is JsonNode v ? PyStr(v) : m.Value;
    });

    /// <summary>The text a value takes when substituted: numbers, booleans and null as Python prints them.</summary>
    public static string PyStr(JsonNode? node)
    {
        switch (node)
        {
            case null: return "None";
            case JsonValue v when v.TryGetValue<string>(out var s): return s;
            case JsonValue v when v.TryGetValue<bool>(out var b): return b ? "True" : "False";
            case JsonValue v when v.TryGetValue<long>(out var l): return l.ToString(CultureInfo.InvariantCulture);
            case JsonValue v when v.TryGetValue<int>(out var i): return i.ToString(CultureInfo.InvariantCulture);
            case JsonValue v when v.TryGetValue<double>(out var d): return PyFloat(d);
            case JsonValue v when v.TryGetValue<decimal>(out var m): return PyFloat((double)m);
            default: return Repr(node);
        }
    }

    /// <summary>Python's json.dumps: a space after commas and colons, and non-ASCII text written as \\uXXXX.</summary>
    public static string PyJson(JsonNode? node)
    {
        switch (node)
        {
            case null: return "null";
            case JsonArray a: return "[" + string.Join(", ", a.Select(PyJson)) + "]";
            case JsonObject o: return "{" + string.Join(", ", o.Select(kv => $"{JsonText(kv.Key)}: {PyJson(kv.Value)}")) + "}";
            case JsonValue v when v.TryGetValue<string>(out var s): return JsonText(s);
            case JsonValue v when v.TryGetValue<bool>(out var b): return b ? "true" : "false";
            case JsonValue v when v.TryGetValue<long>(out var l): return l.ToString(CultureInfo.InvariantCulture);
            case JsonValue v when v.TryGetValue<int>(out var i): return i.ToString(CultureInfo.InvariantCulture);
            case JsonValue v when v.TryGetValue<double>(out var d): return double.IsFinite(d) ? PyFloat(d) : double.IsNaN(d) ? "NaN" : d > 0 ? "Infinity" : "-Infinity";
            default: return node.ToJsonString();
        }
    }

    private static string JsonText(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }

        return sb.Append('"').ToString();
    }

    /// <summary>Python's repr of a list or dict, which is what substituting one into text gives.</summary>
    private static string Repr(JsonNode? node) => node switch
    {
        null => "None",
        JsonArray a => "[" + string.Join(", ", a.Select(Repr)) + "]",
        JsonObject o => "{" + string.Join(", ", o.Select(kv => $"{Quote(kv.Key)}: {Repr(kv.Value)}")) + "}",
        JsonValue v when v.TryGetValue<string>(out var s) => Quote(s),
        _ => PyStr(node),
    };

    private static string Quote(string s) =>
        s.Contains('\'') && !s.Contains('"') ? "\"" + s.Replace("\\", "\\\\") + "\"" : "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    /// <summary>Python's repr of a float: the shortest text that reads back, a ".0" on whole numbers, an exponent outside 1e-4 to 1e16.</summary>
    public static string PyFloat(double d)
    {
        if (double.IsNaN(d)) return "nan";
        if (double.IsInfinity(d)) return d > 0 ? "inf" : "-inf";
        if (d == 0) return 1 / d < 0 ? "-0.0" : "0.0";
        // The shortest digits that round-trip, from "R".
        var shortest = d.ToString("R", CultureInfo.InvariantCulture);
        var neg = shortest.StartsWith('-');
        if (neg) shortest = shortest[1..];
        var exp = 0;
        var e = shortest.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            exp = int.Parse(shortest[(e + 1)..], CultureInfo.InvariantCulture);
            shortest = shortest[..e];
        }

        var dot = shortest.IndexOf('.');
        var digits = shortest.Replace(".", "");
        var pointAt = (dot < 0 ? shortest.Length : dot) + exp;
        var lead = digits.Length - digits.TrimStart('0').Length;
        digits = digits.TrimStart('0').TrimEnd('0');
        pointAt -= lead;
        if (digits.Length == 0) digits = "0";
        var sci = pointAt - 1;
        var sb = new StringBuilder();
        if (neg) sb.Append('-');
        if (sci < -4 || sci >= 16)
        {
            sb.Append(digits[0]);
            if (digits.Length > 1) sb.Append('.').Append(digits, 1, digits.Length - 1);
            sb.Append('e').Append(sci < 0 ? '-' : '+').Append(Math.Abs(sci).ToString("00", CultureInfo.InvariantCulture));
        }
        else if (pointAt <= 0)
        {
            sb.Append("0.").Append('0', -pointAt).Append(digits);
        }
        else if (pointAt >= digits.Length)
        {
            sb.Append(digits).Append('0', pointAt - digits.Length).Append(".0");
        }
        else
        {
            sb.Append(digits, 0, pointAt).Append('.').Append(digits, pointAt, digits.Length - pointAt);
        }

        return sb.ToString();
    }
}
