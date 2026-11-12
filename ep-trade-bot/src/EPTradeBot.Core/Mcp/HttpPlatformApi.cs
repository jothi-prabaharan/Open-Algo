using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Flow;

namespace EPTradeBot.Core.Mcp;

/// <summary>
/// The platform as a separate process reaches it: its /api/v1 over HTTP with an API key. Answers have the same shape the in-process adapter gives, so the MCP tools do
/// not care which they run on. The key's own order mode applies: a semi-auto key's orders wait in the Action Center whoever sends them.
/// </summary>
public sealed class HttpPlatformApi(HttpClient http, string apiKey) : IFlowApi
{
    /// <summary>The path under /api/v1 for an endpoint name; most are the name itself.</summary>
    private static string PathOf(string api) => api switch { "holidays" => "market/holidays", "timings" => "market/timings", _ => api };

    public async Task<JsonObject> CallAsync(string api, JsonObject body, CancellationToken cancellationToken)
    {
        var payload = (JsonObject)body.DeepClone();
        payload["apikey"] = apiKey;
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync($"/api/v1/{PathOf(api)}", payload, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return Failure(503, "EP Trade bot could not be reached. Check that it is running and that the address is right.");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonObject? json;
            try
            {
                json = text.Length > 0 ? JsonNode.Parse(text) as JsonObject : null;
            }
            catch (System.Text.Json.JsonException)
            {
                json = null;
            }

            if (json is null) return Failure((int)response.StatusCode, "EP Trade bot gave an answer that could not be read.");
            var failed = !response.IsSuccessStatusCode || json["status"]?.GetValue<string>() == "error";
            if (failed) return Failure((int)response.StatusCode, json["message"]?.GetValue<string>() ?? json["detail"]?.GetValue<string>() ?? "The request was refused.");
            var shaped = new JsonObject { ["status"] = "success" };
            foreach (var (k, v) in json)
            {
                if (k != "status") shaped[k] = v?.DeepClone();
            }

            return shaped;
        }
    }

    private static JsonObject Failure(int code, string message) => new() { ["status"] = "error", ["error"] = message, ["code"] = code };
}
