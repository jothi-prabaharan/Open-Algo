using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Flow;

public sealed record FlowNode(string Id, string Type, JsonObject Data);

public sealed record FlowEdge(string Source, string Target, string SourceHandle);

public sealed record FlowError(string Node, string Type, string Message);

public sealed record FlowLogLine(DateTime AtUtc, string Level, string Message);

/// <summary>What a node reports. <see cref="Condition"/> is set only by nodes that fork into a true and a false branch.</summary>
public sealed class FlowNodeResult
{
    public const string Success = "success";
    public const string Error = "error";

    public string Status { get; init; } = Success;

    public string? Message { get; init; }

    public bool? Condition { get; init; }

    /// <summary>Extra fields of the result, kept for the run record.</summary>
    public JsonObject Values { get; init; } = [];

    public bool IsError => Status == Error;

    public static FlowNodeResult Ok(JsonObject? values = null) => new() { Values = values ?? [] };

    public static FlowNodeResult Fail(string message, bool? condition = null) => new() { Status = Error, Message = message, Condition = condition };

    public static FlowNodeResult Branch(bool condition, JsonObject? values = null) => new() { Condition = condition, Values = values ?? [] };
}

/// <summary>A workflow graph: nodes joined by edges whose source handle names the branch they leave from.</summary>
public sealed class FlowGraph
{
    public FlowGraph(IReadOnlyList<FlowNode> nodes, IReadOnlyList<FlowEdge> edges)
    {
        Nodes = nodes;
        Edges = edges;
        var outgoing = new Dictionary<string, List<FlowEdge>>(StringComparer.Ordinal);
        var incoming = new Dictionary<string, List<FlowEdge>>(StringComparer.Ordinal);
        foreach (var e in edges)
        {
            if (!outgoing.TryGetValue(e.Source, out var o)) outgoing[e.Source] = o = [];
            o.Add(e);
            if (!incoming.TryGetValue(e.Target, out var i)) incoming[e.Target] = i = [];
            i.Add(e);
        }

        Outgoing = outgoing.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<FlowEdge>)kv.Value, StringComparer.Ordinal);
        Incoming = incoming.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<FlowEdge>)kv.Value, StringComparer.Ordinal);
        ById = nodes.GroupBy(n => n.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    public IReadOnlyList<FlowNode> Nodes { get; }

    public IReadOnlyList<FlowEdge> Edges { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<FlowEdge>> Outgoing { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<FlowEdge>> Incoming { get; }

    public IReadOnlyDictionary<string, FlowNode> ById { get; }

    public static readonly IReadOnlyList<string> TriggerTypes = ["start", "webhookTrigger", "priceAlert", "orderUpdateTrigger"];

    public FlowNode? Trigger => Nodes.FirstOrDefault(n => TriggerTypes.Contains(n.Type));

    /// <summary>Builds a graph from the editor's JSON. A node or edge that is not an object with the needed fields is skipped.</summary>
    public static FlowGraph Parse(JsonArray? nodes, JsonArray? edges)
    {
        var ns = new List<FlowNode>();
        foreach (var n in nodes ?? [])
        {
            if (n is JsonObject o && o["id"]?.GetValue<string>() is { Length: > 0 } id && o["type"] is JsonValue t && t.TryGetValue<string>(out var type))
                ns.Add(new FlowNode(id, type, o["data"] as JsonObject ?? []));
        }

        var es = new List<FlowEdge>();
        foreach (var e in edges ?? [])
        {
            if (e is JsonObject o && o["source"]?.GetValue<string>() is { } s && o["target"]?.GetValue<string>() is { } t)
                es.Add(new FlowEdge(s, t, o["sourceHandle"] is JsonValue h && h.TryGetValue<string>(out var hs) ? hs : ""));
        }

        return new FlowGraph(ns, es);
    }
}
