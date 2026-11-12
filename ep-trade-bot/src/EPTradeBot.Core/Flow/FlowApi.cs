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
/// <summary>What a lot size lookup found: the lot, or why there is none.</summary>
public sealed record LotSizeAnswer(int? Lot, LotSizeProblem Problem = LotSizeProblem.None)
{
    public static LotSizeAnswer Found(int lot) => new(lot);
}

public enum LotSizeProblem
{
    None,

    /// <summary>The exchange has contracts loaded, but none for this underlying.</summary>
    NotListed,

    /// <summary>No contracts are loaded for the exchange at all.</summary>
    ExchangeEmpty,

    /// <summary>The master contract could not be read.</summary>
    Unreadable,
}

public delegate Task<LotSizeAnswer> LotSizeLookup(string underlying, string foExchange, CancellationToken ct);

public sealed record FlowHost(IFlowApi Api, string StrategyTag)
{
    /// <summary>Finds an underlying's lot size in the master contract. Absent in a host that has no master contract to read.</summary>
    public LotSizeLookup? LotSizes { get; init; }

    /// <summary>Sends the HTTP request node's request. Absent in a host that must not reach the network.</summary>
    public IFlowHttp? Http { get; init; }
}

/// <summary>What came back from an outbound request.</summary>
public sealed record FlowHttpResponse(int Status, bool IsSuccess, string Text);

/// <summary>The request could not be completed (no connection, timed out, ...); the message is shown to the author.</summary>
public sealed class FlowHttpException(string message) : Exception(message);

/// <summary>Outbound HTTP for a workflow, on its own connections so a slow endpoint cannot hold one an order needs.</summary>
public interface IFlowHttp
{
    /// <summary>Why the destination must not be reached (not a public address, bad scheme, cannot be resolved), or null when it may.</summary>
    Task<string?> CheckDestinationAsync(string url, CancellationToken ct);

    Task<FlowHttpResponse> SendAsync(string method, string url, IReadOnlyDictionary<string, string> headers, JsonNode? jsonBody, string? textBody, TimeSpan timeout, CancellationToken ct);
}

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
