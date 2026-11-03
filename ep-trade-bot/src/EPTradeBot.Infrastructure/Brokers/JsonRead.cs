using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Brokers;

namespace EPTradeBot.Infrastructure.Brokers;

/// <summary>Python-style reads of broker JSON: str(), float(), truthiness and dict.get with a default.</summary>
internal static class JsonRead
{
    public static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<JsonElement>(out var e) ? e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.GetRawText(),
            _ => null,
        } : node is JsonValue s && s.TryGetValue<string>(out var str) ? str : null;

    public static double Float(JsonNode? node, double fallback = 0)
    {
        if (BookFormatting.Number(node) is { } n) return n;
        return Str(node) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
    }

    /// <summary>int(float(x)) for a quantity that may come as a number or text.</summary>
    public static long Int(JsonNode? node) => (long)Math.Truncate(Float(node));

    public static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonValue v when v.TryGetValue<JsonElement>(out var e) => e.ValueKind switch
        {
            JsonValueKind.String => e.GetString()!.Length > 0,
            JsonValueKind.Number => e.GetDouble() != 0,
            JsonValueKind.True => true,
            _ => false,
        },
        JsonArray a => a.Count > 0,
        JsonObject o => o.Count > 0,
        _ => true,
    };

    /// <summary>dict.get(key, default).</summary>
    public static JsonNode? Get(JsonObject? obj, string key, JsonNode? fallback) =>
        obj is not null && obj.TryGetPropertyValue(key, out var value) ? value?.DeepClone() : fallback;

    public static async Task<JsonNode?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
