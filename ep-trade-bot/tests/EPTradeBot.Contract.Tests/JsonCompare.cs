using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EPTradeBot.Contract.Tests;

/// <summary>
/// Structural JSON comparison. Object key order is ignored (Flask sorts keys); a
/// number equals a number of the same value (Python writes 0.0 where .NET writes 0);
/// a string must match exactly, including formatted amounts such as "801.12".
/// </summary>
public static class JsonCompare
{
    /// <param name="numbersMayBeText">For a broker that takes a number and its text form alike (OpenAlgo itself sends both), "10" equals 10.</param>
    public static List<string> Differences(JsonNode? expected, JsonNode? actual, string path = "$", bool numbersMayBeText = false)
    {
        var diffs = new List<string>();
        Walk(expected, actual, path, diffs, numbersMayBeText);
        return diffs;
    }

    private static void Walk(JsonNode? e, JsonNode? a, string path, List<string> diffs, bool loose)
    {
        switch (e)
        {
            case null:
                if (a is not null) diffs.Add($"{path}: expected null, got {a.ToJsonString()}");
                return;
            case JsonObject eo:
                if (a is not JsonObject ao)
                {
                    diffs.Add($"{path}: expected an object, got {a?.ToJsonString() ?? "null"}");
                    return;
                }

                foreach (var key in eo.Select(kv => kv.Key).Union(ao.Select(kv => kv.Key)))
                {
                    if (!eo.ContainsKey(key)) diffs.Add($"{path}.{key}: not expected, got {ao[key]?.ToJsonString() ?? "null"}");
                    else if (!ao.ContainsKey(key)) diffs.Add($"{path}.{key}: missing, expected {eo[key]?.ToJsonString() ?? "null"}");
                    else Walk(eo[key], ao[key], $"{path}.{key}", diffs, loose);
                }

                return;
            case JsonArray ea:
                if (a is not JsonArray aa)
                {
                    diffs.Add($"{path}: expected an array, got {a?.ToJsonString() ?? "null"}");
                    return;
                }

                if (ea.Count != aa.Count) diffs.Add($"{path}: expected {ea.Count} items, got {aa.Count}");
                for (var i = 0; i < Math.Min(ea.Count, aa.Count); i++) Walk(ea[i], aa[i], $"{path}[{i}]", diffs, loose);
                return;
            default:
                var ee = JsonSerializer.SerializeToElement(e);
                if (a is not JsonValue av)
                {
                    diffs.Add($"{path}: expected {e.ToJsonString()}, got {a?.ToJsonString() ?? "null"}");
                    return;
                }

                var ae = JsonSerializer.SerializeToElement(av);
                if (ee.ValueKind == JsonValueKind.Number && ae.ValueKind == JsonValueKind.Number)
                {
                    if (ee.GetDecimal() != ae.GetDecimal()) diffs.Add($"{path}: expected {ee.GetRawText()}, got {ae.GetRawText()}");
                }
                else if (loose && ee.ValueKind is JsonValueKind.Number or JsonValueKind.String && ae.ValueKind is JsonValueKind.Number or JsonValueKind.String
                    && SameValue(ee.ValueKind == JsonValueKind.String ? ee.GetString()! : ee.GetRawText(), ae.ValueKind == JsonValueKind.String ? ae.GetString()! : ae.GetRawText()))
                {
                }
                else if (ee.ValueKind != ae.ValueKind || ee.GetRawText() != ae.GetRawText())
                {
                    diffs.Add($"{path}: expected {ee.GetRawText()}, got {ae.GetRawText()}");
                }

                return;
        }
    }

    /// <summary>Two form or query values agree when equal as text, or equal as numbers ("24010" and "24010.0").</summary>
    public static bool SameValue(string expected, string actual) =>
        expected == actual
        || (decimal.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var e)
            && decimal.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && e == a);
}
