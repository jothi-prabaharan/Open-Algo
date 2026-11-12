using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Flow;

/// <summary>One node type's behaviour. Returns null for a pass-through node (a trigger, a group).</summary>
public interface IFlowNode
{
    IReadOnlyList<string> Types { get; }

    Task<FlowNodeResult?> ExecuteAsync(FlowNode node, FlowRun run, CancellationToken cancellationToken);
}

/// <summary>Everything one run carries: its variables, its log, its errors, the clock and the way to wait.</summary>
public sealed class FlowRun(Func<DateTime> utcNow, Func<TimeSpan, CancellationToken, Task>? sleep = null, object? host = null)
{
    public FlowContext Context { get; } = new(utcNow);

    public Func<DateTime> UtcNow { get; } = utcNow;

    public Func<TimeSpan, CancellationToken, Task> Sleep { get; } = sleep ?? ((d, ct) => Task.Delay(d, ct));

    /// <summary>What node types that reach outside the engine need (the account, the services); the engine itself never looks inside.</summary>
    public object? Host { get; } = host;

    public List<FlowLogLine> Logs { get; } = [];

    public List<FlowError> Errors { get; } = [];

    public void Log(string message, string level = "info") => Logs.Add(new FlowLogLine(UtcNow(), level, message));
}

/// <summary>A run hit one of the engine's own bounds (depth or visits), whose wording is meant to be read.</summary>
public sealed class FlowLimitException(string message) : InvalidOperationException(message);

/// <summary>How a run ended. <see cref="Fault"/> is an unexpected failure whose detail belongs in the log, never on a trader's screen.</summary>
public sealed record FlowOutcome(bool Succeeded, string Message, IReadOnlyList<FlowError> Errors, Exception? Fault = null);

/// <summary>
/// Walks a workflow graph from its trigger node. Port of OpenAlgo's execute_node_chain with its protections kept:
/// a bound on depth and visits; a condition or gate answers once per run however many paths reach it; a gate waits for every input and for every
/// declared input to be wired; a condition that could not be evaluated takes neither branch; a node that failed stops its branch; an order node with an
/// unresolved reference in a field that decides what is sent is refused rather than sent with a default.
/// </summary>
public sealed class FlowRunner(IEnumerable<IFlowNode> nodes)
{
    public const int MaxDepth = 100;
    public const int MaxVisits = 500;

    public static readonly IReadOnlyList<string> GateTypes = ["andGate", "orGate", "notGate"];

    public static readonly IReadOnlyList<string> BranchingTypes = ["priceCondition", "timeCondition", "timeWindow", "positionCheck", "fundCheck", "varCondition"];

    public static readonly IReadOnlyList<string> OrderTypes =
        ["placeOrder", "smartOrder", "optionsOrder", "optionsMultiOrder", "basketOrder", "splitOrder", "modifyOrder", "cancelOrder", "closePositions"];

    /// <summary>The fields on an order node that decide what reaches the broker. Labels (strategy, outputVariable) are absent on purpose.</summary>
    public static readonly IReadOnlyList<string> OrderCriticalFields =
    [
        "symbol", "exchange", "action", "quantity", "product", "priceType", "pricetype", "price", "triggerPrice", "splitSize", "positionSize", "underlying",
        "strike", "optionType", "expiryDate", "orderId", "newQuantity", "newPrice", "newTriggerPrice", "orders", "legs",
    ];

    private static readonly string[] TrueHandles = ["yes", "true"];
    private static readonly string[] FalseHandles = ["no", "false"];

    private readonly Dictionary<string, IFlowNode> handlers = nodes.SelectMany(n => n.Types.Select(t => (t, n))).ToDictionary(x => x.t, x => x.n, StringComparer.Ordinal);

    /// <summary>Whether this runner can execute a node type (a gate, or one with a handler).</summary>
    public bool Supports(string type) => GateTypes.Contains(type) || handlers.ContainsKey(type);

    public async Task<FlowOutcome> RunAsync(FlowGraph graph, FlowRun run, CancellationToken ct)
    {
        var trigger = graph.Trigger;
        if (trigger is null) return new FlowOutcome(false, "No trigger node found", []);
        // A node the editor knows and this runner cannot execute must stop the run before anything is sent, never be skipped.
        var cannot = graph.Nodes.Where(n => FlowValidator.KnownTypes.Contains(n.Type) && !Supports(n.Type)).Select(n => n.Type).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (cannot.Count > 0)
        {
            var message = $"This platform cannot run {string.Join(", ", cannot)} nodes yet, so the workflow was not started.";
            run.Log(message, "error");
            return new FlowOutcome(false, message, []);
        }

        var visits = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            await WalkAsync(trigger.Id, graph, run, visits, 0, ct);
        }
        catch (FlowLimitException ex)
        {
            run.Log($"Error: {ex.Message}", "error");
            return new FlowOutcome(false, ex.Message, run.Errors);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            const string message = "The workflow stopped because of an unexpected problem. The server log has the detail.";
            run.Log(message, "error");
            return new FlowOutcome(false, message, run.Errors, ex);
        }

        if (run.Errors.Count > 0)
        {
            var summary = string.Join("; ", run.Errors.Select(e => $"{e.Type}: {e.Message}"));
            return new FlowOutcome(false, $"{run.Errors.Count} node(s) failed: {summary}", run.Errors);
        }

        return new FlowOutcome(true, "Workflow executed successfully", []);
    }

    private async Task WalkAsync(string nodeId, FlowGraph graph, FlowRun run, Dictionary<string, int> visits, int depth, CancellationToken ct)
    {
        if (depth > MaxDepth) throw new FlowLimitException($"Maximum node depth ({MaxDepth}) exceeded");
        if (visits.Values.Sum() >= MaxVisits) throw new FlowLimitException($"Maximum node visits ({MaxVisits}) exceeded");
        visits[nodeId] = visits.GetValueOrDefault(nodeId) + 1;
        if (!graph.ById.TryGetValue(nodeId, out var node)) return;
        var type = node.Type;

        // A condition reachable by more than one path answers once: its first traversal already followed the branch.
        if (BranchingTypes.Contains(type) && run.Context.GetCondition(nodeId) is not null) return;

        FlowNodeResult? result = null;
        var blocked = OrderTypes.Contains(type) ? UnresolvedOrderFields(node, run.Context) : [];
        if (blocked.Count > 0)
        {
            var detail = string.Join(", ", blocked.Select(b => $"{b.Field}='{b.Text}'"));
            var message = $"{type} has unresolved variable(s) in {detail}. Refusing to send it with substituted values.";
            run.Log(message, "error");
            result = FlowNodeResult.Fail(message);
        }
        else if (GateTypes.Contains(type))
        {
            if (run.Context.GetCondition(nodeId) is not null) return;
            var incoming = graph.Incoming.GetValueOrDefault(nodeId) ?? [];
            var inputs = new List<bool>();
            var pending = 0;
            foreach (var edge in incoming)
            {
                if (run.Context.GetCondition(edge.Source) is { } r) inputs.Add(r);
                else pending++;
            }

            // The graph is walked depth first, so the first input to finish reaches the gate while the others are still unevaluated.
            // Waiting is safe: the later input's own traversal arrives here again with every result stored.
            if (pending > 0)
            {
                run.Log($"{type}: waiting for {pending} more input(s) before evaluating");
                return;
            }

            if (node.Data["inputCount"] is JsonValue declaredValue && declaredValue.TryGetValue<int>(out var declared) && declared > incoming.Count)
            {
                var message = $"{type} is configured for {declared} inputs but only {incoming.Count} are wired. Wire the rest, or lower the input count, rather than evaluating on part of the condition.";
                run.Log(message, "error");
                run.Errors.Add(new FlowError(nodeId, type, message));
                return;
            }

            result = EvaluateGate(type, inputs, run);
        }
        else if (handlers.TryGetValue(type, out var handler))
        {
            result = await handler.ExecuteAsync(node, run, ct);
        }
        else
        {
            run.Log($"Unknown node type: {type}", "warning");
        }

        if (result is { IsError: true })
        {
            var message = result.Message ?? "node failed";
            run.Errors.Add(new FlowError(nodeId, type, message));
            if (result.Condition is null)
            {
                run.Log($"Stopping this branch: {type} node failed ({message})", "error");
                return;
            }

            run.Log($"{type} could not be evaluated ({message})", "error");
        }

        var edges = graph.Outgoing.GetValueOrDefault(nodeId) ?? [];
        if (result?.Condition is { } met)
        {
            var errored = result.IsError;
            // Only a condition that evaluated is recorded: a gate treats a stored result as "this input is settled".
            if (!errored) run.Context.SetCondition(nodeId, met);
            var gateIds = graph.Nodes.Where(n => GateTypes.Contains(n.Type)).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            var followed = new List<FlowEdge>();
            foreach (var edge in edges)
            {
                var isBranch = TrueHandles.Contains(edge.SourceHandle) || FalseHandles.Contains(edge.SourceHandle);
                // An errored condition takes neither branch, so a typo cannot fire the else path; plain pass-through edges (logging) still run.
                if (errored) { if (!isBranch) followed.Add(edge); }
                // A wire into a gate carries the source's value, not control flow, so it is always delivered.
                else if (gateIds.Contains(edge.Target)) followed.Add(edge);
                else if (met && TrueHandles.Contains(edge.SourceHandle)) followed.Add(edge);
                else if (!met && FalseHandles.Contains(edge.SourceHandle)) followed.Add(edge);
                else if (!isBranch) followed.Add(edge);
            }

            edges = followed;
        }

        foreach (var edge in edges)
            await WalkAsync(edge.Target, graph, run, visits, depth + 1, ct);
    }

    private static FlowNodeResult EvaluateGate(string type, List<bool> inputs, FlowRun run)
    {
        bool met;
        switch (type)
        {
            case "andGate":
                met = inputs.Count > 0 && inputs.All(x => x);
                run.Log($"AND Gate: [{string.Join(", ", inputs)}] -> {met}");
                break;
            case "orGate":
                met = inputs.Count > 0 && inputs.Any(x => x);
                run.Log($"OR Gate: [{string.Join(", ", inputs)}] -> {met}");
                break;
            default:
                var first = inputs.Count > 0 && inputs[0];
                met = !first;
                run.Log($"NOT Gate: {first} -> {met}");
                break;
        }

        return FlowNodeResult.Branch(met);
    }

    /// <summary>Order-defining fields still holding an unresolved reference after substitution.</summary>
    public static List<(string Field, string Text)> UnresolvedOrderFields(FlowNode node, FlowContext context)
    {
        var found = new List<(string, string)>();
        foreach (var key in OrderCriticalFields)
        {
            if (node.Data[key] is not JsonValue v || !v.TryGetValue<string>(out var raw) || !raw.Contains("{{", StringComparison.Ordinal)) continue;
            if (context.ResolveRaw(raw) is JsonValue r && r.TryGetValue<string>(out var text) && FlowContext.HasUnresolved(text)) found.Add((key, text));
        }

        found.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return found;
    }
}
