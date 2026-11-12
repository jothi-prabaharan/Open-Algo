using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Flow;

public sealed record FlowValidationError(string Path, string Code, string Message);

/// <summary>
/// Structural checks on a workflow before it is saved, activated or run. Port of the structural half of OpenAlgo's flow_workflow_validator: shape, ids, known node
/// types, edge endpoints and, when strict (activation and every run), one trigger, no cycles and nothing unreachable. Beyond OpenAlgo, a node type the editor knows but
/// this engine cannot run yet is refused by name: skipping it silently would let a workflow place the orders around a basket order it never sent.
/// </summary>
public static class FlowValidator
{
    public const int MaxNodes = 500;
    public const int MaxEdges = 1000;

    /// <summary>Every node type the editor can render.</summary>
    public static readonly IReadOnlySet<string> KnownTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "andGate", "barOffset", "basketOrder", "calendar", "cancelAllOrders", "cancelOrder", "closePositions", "delay", "expiry", "fundCheck", "funds", "getDepth",
        "getOrderStatus", "getQuote", "group", "history", "holdings", "holidays", "httpRequest", "indicator", "intervals", "log", "margin", "mathExpression", "modifyOrder",
        "multiQuotes", "notGate", "openPosition", "optionChain", "optionSymbol", "optionsMultiOrder", "optionsOrder", "orGate", "orderBook", "orderUpdateTrigger",
        "placeOrder", "positionBook", "positionCheck", "priceAlert", "priceCondition", "priorPeriodOhlc", "smartOrder", "splitOrder", "start", "strategyPnl",
        "subscribeDepth", "subscribeLtp", "subscribeQuote", "symbol", "syntheticFuture", "telegramAlert", "timeCondition", "timeWindow", "timings", "tradeBook",
        "unsubscribe", "varCondition", "variable", "waitUntil", "webhookTrigger", "whatsappAlert",
    };

    public static List<FlowValidationError> Validate(JsonNode? payload, bool requireName, bool strict, Func<string, bool>? supports = null)
    {
        var errors = new List<FlowValidationError>();
        if (payload is not JsonObject root) return [new("/", "invalid_type", "A workflow must be a JSON object.")];

        if (requireName && !(root["name"] is JsonValue n && n.TryGetValue<string>(out var name) && name.Trim().Length > 0))
            errors.Add(new("/name", "required", "A workflow needs a name."));

        var nodes = root["nodes"] as JsonArray;
        var edges = root["edges"] as JsonArray;
        if (nodes is null) errors.Add(new("/nodes", "required", "A workflow needs a list of nodes."));
        if (edges is null) errors.Add(new("/edges", "required", "A workflow needs a list of edges."));
        nodes ??= [];
        edges ??= [];
        if (nodes.Count > MaxNodes) errors.Add(new("/nodes", "too_large", $"A workflow can have at most {MaxNodes} nodes; this one has {nodes.Count}."));
        if (edges.Count > MaxEdges) errors.Add(new("/edges", "too_large", $"A workflow can have at most {MaxEdges} edges; this one has {edges.Count}."));

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var triggers = new List<string>();
        var unsupported = new SortedSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < nodes.Count; i++)
        {
            var path = $"/nodes/{i}";
            if (nodes[i] is not JsonObject node) { errors.Add(new(path, "invalid_type", "A node must be an object.")); continue; }
            var id = node["id"] is JsonValue iv && iv.TryGetValue<string>(out var idText) ? idText : null;
            if (string.IsNullOrWhiteSpace(id)) errors.Add(new($"{path}/id", "required", "Every node needs an id."));
            else if (!ids.Add(id)) errors.Add(new($"{path}/id", "duplicate", $"Two nodes share the id '{id}'."));

            var type = node["type"] is JsonValue tv && tv.TryGetValue<string>(out var typeText) ? typeText : null;
            if (string.IsNullOrEmpty(type)) errors.Add(new($"{path}/type", "required", "Every node needs a type."));
            else if (!KnownTypes.Contains(type)) errors.Add(new($"{path}/type", "unknown_type", $"'{type}' is not a node type this platform knows."));
            else
            {
                if (FlowGraph.TriggerTypes.Contains(type) && id is not null) triggers.Add(id);
                if (supports is not null && !supports(type)) unsupported.Add(type);
            }
        }

        var adjacency = ids.ToDictionary(x => x, _ => new List<string>(), StringComparer.Ordinal);
        for (var i = 0; i < edges.Count; i++)
        {
            var path = $"/edges/{i}";
            if (edges[i] is not JsonObject edge) { errors.Add(new(path, "invalid_type", "An edge must be an object.")); continue; }
            var source = edge["source"] is JsonValue sv && sv.TryGetValue<string>(out var s) ? s : null;
            var target = edge["target"] is JsonValue tv && tv.TryGetValue<string>(out var t) ? t : null;
            if (source is null || !ids.Contains(source)) errors.Add(new($"{path}/source", "dangling", $"This edge starts at '{source}', which is not a node in the workflow."));
            if (target is null || !ids.Contains(target)) errors.Add(new($"{path}/target", "dangling", $"This edge ends at '{target}', which is not a node in the workflow."));
            if (source is not null && target is not null && ids.Contains(source) && ids.Contains(target)) adjacency[source].Add(target);
        }

        if (!strict) return errors;

        if (unsupported.Count > 0)
            errors.Add(new("/nodes", "unsupported", $"This platform cannot run {string.Join(", ", unsupported)} nodes yet, so the workflow was not started. Remove them, or run this workflow on a platform that has them."));
        if (triggers.Count == 0) errors.Add(new("/nodes", "no_trigger", "The workflow needs a trigger node (start, webhook, price alert or order update)."));
        else if (triggers.Count > 1) errors.Add(new("/nodes", "multiple_triggers", $"A workflow can have only one trigger; this one has {triggers.Count}."));

        var cycle = FindCycle(adjacency);
        if (cycle is not null) errors.Add(new("/edges", "cycle", "The graph contains a cycle, which would run until the visit limit and then stop without saying why: " + string.Join(" -> ", cycle.Take(6))));

        if (triggers.Count > 0)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>(triggers);
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (!seen.Add(id)) continue;
                foreach (var next in adjacency[id]) stack.Push(next);
            }

            // A group is a drawn container with no behaviour: its nodes run through their own edges.
            var decorative = nodes.OfType<JsonObject>().Where(x => x["type"]?.GetValue<string>() == "group").Select(x => x["id"]?.GetValue<string>()).ToHashSet();
            var unreachable = ids.Except(seen).Where(x => !decorative.Contains(x)).Order(StringComparer.Ordinal).ToList();
            if (unreachable.Count > 0) errors.Add(new("/nodes", "unreachable", "These nodes cannot be reached from the trigger and would never run: " + string.Join(", ", unreachable.Take(8))));
        }

        return errors;
    }

    private static List<string>? FindCycle(Dictionary<string, List<string>> adjacency)
    {
        var colour = adjacency.Keys.ToDictionary(k => k, _ => 0, StringComparer.Ordinal);
        List<string>? found = null;

        bool Visit(string node, List<string> path)
        {
            colour[node] = 1;
            foreach (var next in adjacency[node])
            {
                if (colour[next] == 1)
                {
                    var at = path.IndexOf(next);
                    found = at >= 0 ? [.. path[at..], next] : [next];
                    return true;
                }

                if (colour[next] == 0 && Visit(next, [.. path, next])) return true;
            }

            colour[node] = 2;
            return false;
        }

        foreach (var id in adjacency.Keys.Order(StringComparer.Ordinal))
            if (colour[id] == 0 && Visit(id, [id])) return found;
        return null;
    }
}
