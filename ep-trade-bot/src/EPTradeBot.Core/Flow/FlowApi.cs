using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Flow;

/// <summary>
/// The platform's own API as a workflow reaches it: one call by endpoint name with the request body, answered in the shape OpenAlgo's Flow client gives:
/// on success <c>{"status":"success", ...the response fields}</c>, otherwise <c>{"status":"error","error":message,"code":status}</c>.
/// The account, the guard and the risk limits are behind it; an order sent through it is an order like any other.
/// </summary>
public interface IFlowApi
{
    Task<JsonObject> CallAsync(string api, JsonObject body, CancellationToken cancellationToken);
}

/// <summary>What a run is working for: how to reach the API, and the label its orders carry.</summary>
public sealed record FlowHost(IFlowApi Api, string StrategyTag);

public static class FlowApiResults
{
    public static bool IsSuccess(JsonObject r) => r["status"] is JsonValue v && v.TryGetValue<string>(out var s) && s == "success";

    /// <summary>The reason a call failed, as OpenAlgo reads it: the message, else the error, else a stock phrase.</summary>
    public static string Reason(JsonObject r, string fallback) =>
        (r["message"] is JsonValue m && m.TryGetValue<string>(out var ms) && ms.Length > 0 ? ms : null)
        ?? (r["error"] is JsonValue e && e.TryGetValue<string>(out var es) && es.Length > 0 ? es : null)
        ?? fallback;

    /// <summary>The node result for an API answer: failure keeps the reason; success keeps every field for the run record.</summary>
    public static FlowNodeResult ToResult(JsonObject r, string fallback = "node failed") =>
        IsSuccess(r) ? FlowNodeResult.Ok((JsonObject)r.DeepClone()) : new FlowNodeResult { Status = FlowNodeResult.Error, Message = Reason(r, fallback), Values = (JsonObject)r.DeepClone() };
}
