using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EPTradeBot.Core.Flow;

/// <summary>Reading a node's configuration the way OpenAlgo's executor does.</summary>
public static class FlowData
{
    /// <summary>A text field with its <c>{{}}</c> references substituted; the default when the field is missing or empty.</summary>
    public static string Str(this JsonObject data, FlowContext ctx, string key, string fallback = "")
    {
        var raw = data[key] is JsonValue v ? FlowContext.PyStr(v) : "";
        return raw.Length == 0 ? fallback : ctx.Interpolate(raw);
    }

    /// <summary>Python's float() on a number or on text.</summary>
    public static double ToDouble(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return b ? 1 : 0;
            if (TryNumber(v, out var d)) return d;
            if (v.TryGetValue<string>(out var s) && TryParseFloat(s, out var parsed)) return parsed;
        }

        throw new FormatException($"could not convert '{FlowContext.PyStr(node)}' to a number");
    }

    /// <summary>A JSON number as a double, whatever type it was created or read as (a number read from text and one made in code differ in what they convert to).</summary>
    public static bool TryNumber(JsonValue v, out double d)
    {
        if (v.TryGetValue<string>(out _) || v.TryGetValue<bool>(out _)) { d = 0; return false; }
        if (v.TryGetValue<double>(out d)) return true;
        if (v.TryGetValue<long>(out var l)) { d = l; return true; }
        if (v.TryGetValue<int>(out var i)) { d = i; return true; }
        if (v.TryGetValue<decimal>(out var m)) { d = (double)m; return true; }
        if (v.TryGetValue<float>(out var f)) { d = f; return true; }
        d = 0;
        return false;
    }

    public static bool TryParseFloat(string text, out double value)
    {
        var t = text.Trim().Replace("_", "");
        if (t.Length == 0) { value = 0; return false; }
        switch (t.ToLowerInvariant().TrimStart('+', '-'))
        {
            case "inf" or "infinity": value = t.StartsWith('-') ? double.NegativeInfinity : double.PositiveInfinity; return true;
            case "nan": value = double.NaN; return true;
        }

        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Hours, minutes and seconds from "HH:MM[:SS]", clamped; the defaults when it cannot be read.</summary>
    public static (int H, int M, int S) ParseTime(string? text, int defaultHour = 9, int defaultMinute = 15)
    {
        if (string.IsNullOrWhiteSpace(text)) return (defaultHour, defaultMinute, 0);
        var parts = text.Trim().Split(':');
        static bool Digits(string p) => p.Length > 0 && p.All(char.IsAsciiDigit);
        var h = Digits(parts[0]) ? int.Parse(parts[0], CultureInfo.InvariantCulture) : defaultHour;
        var m = parts.Length > 1 && Digits(parts[1]) ? int.Parse(parts[1], CultureInfo.InvariantCulture) : defaultMinute;
        var s = parts.Length > 2 && Digits(parts[2]) ? int.Parse(parts[2], CultureInfo.InvariantCulture) : 0;
        return (Math.Clamp(h, 0, 23), Math.Clamp(m, 0, 59), Math.Clamp(s, 0, 59));
    }

    public static JsonNode Number(double d) => JsonValue.Create(d);
}

/// <summary>The trigger nodes and the group container: nothing to do when reached, the trigger having already fired.</summary>
public sealed class PassThroughNodes : IFlowNode
{
    public IReadOnlyList<string> Types => ["start", "webhookTrigger", "priceAlert", "orderUpdateTrigger", "group"];

    public Task<FlowNodeResult?> ExecuteAsync(FlowNode node, FlowRun run, CancellationToken ct)
    {
        if (node.Type == "webhookTrigger") run.Log("Webhook trigger activated");
        if (node.Type == "orderUpdateTrigger") run.Log("Order-update trigger activated");
        return Task.FromResult<FlowNodeResult?>(null);
    }
}

public sealed class LogNode : IFlowNode
{
    public IReadOnlyList<string> Types => ["log"];

    public Task<FlowNodeResult?> ExecuteAsync(FlowNode node, FlowRun run, CancellationToken ct)
    {
        var message = run.Context.Interpolate(node.Data["message"] is JsonValue v ? FlowContext.PyStr(v) : "");
        var level = node.Data["level"] is JsonValue l && l.TryGetValue<string>(out var s) ? s : "info";
        run.Log($"[LOG] {message}", level);
        return Task.FromResult<FlowNodeResult?>(FlowNodeResult.Ok(new JsonObject { ["message"] = message }));
    }
}

public sealed class MathExpressionNode : IFlowNode
{
    public IReadOnlyList<string> Types => ["mathExpression"];

    public Task<FlowNodeResult?> ExecuteAsync(FlowNode node, FlowRun run, CancellationToken ct)
    {
        var expression = node.Data["expression"] is JsonValue v ? FlowContext.PyStr(v) : "";
        var output = node.Data["outputVariable"] is JsonValue o && o.TryGetValue<string>(out var os) && os.Length > 0 ? os : "result";
        if (expression.Length == 0)
        {
            run.Log("No expression provided", "error");
            return Task.FromResult<FlowNodeResult?>(FlowNodeResult.Fail("No expression provided"));
        }

        try
        {
            var interpolated = run.Context.Interpolate(expression);
            var result = MathEvaluator.Evaluate(interpolated);
            run.Context.Set(output, FlowData.Number(result));
            run.Log($"Result: {output} = {FlowContext.PyFloat(result)}");
            return Task.FromResult<FlowNodeResult?>(FlowNodeResult.Ok(new JsonObject { ["interpolated"] = interpolated, ["result"] = result, ["outputVariable"] = output }));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or DivideByZeroException or OverflowException)
        {
            run.Log($"Math expression failed: {ex.Message}", "error");
            return Task.FromResult<FlowNodeResult?>(FlowNodeResult.Fail(ex.Message));
        }
    }
}

public sealed class VariableNode : IFlowNode
{
    public IReadOnlyList<string> Types => ["variable"];

    public Task<FlowNodeResult?> ExecuteAsync(FlowNode node, FlowRun run, CancellationToken ct)
    {
        var ctx = run.Context;
        var name = node.Data["variableName"] is JsonValue vn && vn.TryGetValue<string>(out var vns) && vns.Length > 0 ? vns
            : node.Data["name"] is JsonValue n && n.TryGetValue<string>(out var ns) ? ns : "";
        var operation = node.Data["operation"] is JsonValue op && op.TryGetValue<string>(out var ops) ? ops : "set";
        JsonNode? value = node.Data["value"]?.DeepClone();
        if (node.Data["value"] is null) value = JsonValue.Create("");
        if (value is JsonValue sv && sv.TryGetValue<string>(out var raw)) value = JsonValue.Create(ctx.Interpolate(raw));

        JsonNode? result;
        try
        {
            switch (operation)
            {
                case "set":
                    result = value;
                    if (value is JsonValue t && t.TryGetValue<string>(out var text) && (text.StartsWith('{') || text.StartsWith('[')))
                    {
                        try { result = JsonNode.Parse(text); } catch (JsonException) { }
                    }

                    break;
                case "get":
                    result = Lookup(ctx, node.Data["sourceVariable"], node.Data["jsonPath"]);
                    break;
                case "add" or "subtract" or "multiply" or "divide":
                    var current = FlowData.ToDouble(ctx.Get(name) ?? JsonValue.Create(0));
                    var operand = FlowData.ToDouble(value);
                    if (operation == "divide" && operand == 0) throw new ArgumentException("Cannot divide by zero");
                    result = FlowData.Number(operation switch { "add" => current + operand, "subtract" => current - operand, "multiply" => current * operand, _ => current / operand });
                    break;
                case "increment":
                    result = FlowData.Number(FlowData.ToDouble(ctx.Get(name) ?? JsonValue.Create(0)) + 1);
                    break;
                case "decrement":
                    result = FlowData.Number(FlowData.ToDouble(ctx.Get(name) ?? JsonValue.Create(0)) - 1);
                    break;
                case "parse_json":
                    result = JsonNode.Parse(FlowContext.PyStr(value));
                    break;
                case "stringify":
                    result = JsonValue.Create(FlowContext.PyJson(Lookup(ctx, node.Data["sourceVariable"], null)));
                    break;
                case "append":
                    result = JsonValue.Create(ctx.Variables.ContainsKey(name) ? FlowContext.PyStr(ctx.Get(name)) + FlowContext.PyStr(value) : FlowContext.PyStr(value));
                    break;
                default:
                    throw new ArgumentException($"Unknown variable operation: {operation}");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException or InvalidOperationException or JsonException or IndexOutOfRangeException)
        {
            var message = $"Variable operation '{operation}' failed: {ex.Message}";
            run.Log(message, "error");
            return Task.FromResult<FlowNodeResult?>(FlowNodeResult.Fail(message));
        }

        ctx.Set(name, result);
        run.Log($"Set variable {name} = {FlowContext.PyStr(result)}");
        return Task.FromResult<FlowNodeResult?>(FlowNodeResult.Ok(new JsonObject { ["variable"] = name, ["value"] = result?.DeepClone() }));
    }

    private static JsonNode? Lookup(FlowContext ctx, JsonNode? source, JsonNode? jsonPath)
    {
        if (source is not JsonValue sv || !sv.TryGetValue<string>(out var sname) || string.IsNullOrWhiteSpace(sname)) throw new ArgumentException("sourceVariable is required");
        sname = sname.Trim();
        if (!ctx.Variables.TryGetValue(sname, out var value)) throw new KeyNotFoundException($"Source variable not found: {sname}");
        var path = jsonPath is JsonValue pv && pv.TryGetValue<string>(out var ps) ? ps.Trim() : "";
        return path.Length == 0 ? value?.DeepClone() : Walk(value, path);
    }

    /// <summary>Dotted keys and bracketed indexes, read without evaluating anything.</summary>
    private static JsonNode? Walk(JsonNode? value, string path)
    {
        var pos = 0;
        while (pos < path.Length)
        {
            if (path[pos] == '[')
            {
                var close = path.IndexOf(']', pos + 1);
                if (close < 0) throw new ArgumentException($"Invalid jsonPath: {path}");
                var index = path[(pos + 1)..close];
                if (index.Length == 0 || !index.All(char.IsAsciiDigit)) throw new ArgumentException($"Invalid jsonPath index: {index}");
                if (value is not JsonArray a) throw new ArgumentException("jsonPath index requires a list");
                value = a[int.Parse(index, CultureInfo.InvariantCulture)];
                pos = close + 1;
                if (pos < path.Length && path[pos] is not ('.' or '[')) throw new ArgumentException($"Invalid jsonPath: {path}");
                continue;
            }

            if (pos > 0)
            {
                if (path[pos] != '.') throw new ArgumentException($"Invalid jsonPath: {path}");
                pos++;
            }

            var start = pos;
            while (pos < path.Length && path[pos] is not ('.' or '[' or ']')) pos++;
            var key = path[start..pos];
            if (key.Length == 0) throw new ArgumentException($"Invalid jsonPath: {path}");
            if (value is not JsonObject o) throw new ArgumentException("jsonPath key requires an object");
            if (!o.TryGetPropertyValue(key, out value)) throw new KeyNotFoundException(key);
            if (pos < path.Length && path[pos] == ']') throw new ArgumentException($"Invalid jsonPath: {path}");
        }

        return value?.DeepClone();
    }
}

public sealed class DelayNode : IFlowNode
{
    /// <summary>The longest a delay may wait; anything longer belongs in a schedule.</summary>
    public const int MaxSeconds = 300;

    public IReadOnlyList<string> Types => ["delay"];

    public async Task<FlowNodeResult?> ExecuteAsync(FlowNode node, FlowRun run, CancellationToken ct)
    {
        double seconds;
        if (node.Data["delayValue"] is { } dv)
        {
            var value = (long)FlowData.ToDouble(dv);
            var unit = node.Data["delayUnit"] is JsonValue u && u.TryGetValue<string>(out var us) ? us : "seconds";
            seconds = unit switch { "minutes" => value * 60, "hours" => value * 3600, _ => value };
        }
        else
        {
            seconds = (node.Data["delayMs"] is { } ms ? FlowData.ToDouble(ms) : 1000) / 1000;
        }

        if (seconds > MaxSeconds)
        {
            run.Log($"Delay of {FlowContext.PyFloat(seconds)}s exceeds the {MaxSeconds}s maximum; waiting the maximum instead.", "warning");
            seconds = MaxSeconds;
        }

        run.Log($"Waiting for {FlowContext.PyFloat(seconds)} seconds");
        await run.Sleep(TimeSpan.FromSeconds(Math.Max(0, seconds)), ct);
        return FlowNodeResult.Ok(new JsonObject { ["message"] = $"Waited {FlowContext.PyFloat(seconds)}s" });
    }
}

public sealed class WaitUntilNode : IFlowNode
{
    /// <summary>Looser than a delay, since waiting a few minutes for a square-off is the point; hours is a schedule.</summary>
    public const int MaxSeconds = 1800;

    public IReadOnlyList<string> Types => ["waitUntil"];

    public async Task<FlowNodeResult?> ExecuteAsync(FlowNode node, FlowRun run, CancellationToken ct)
    {
        var text = node.Data["targetTime"] is JsonValue t && t.TryGetValue<string>(out var ts) ? ts : "09:30";
        var (h, m, s) = FlowData.ParseTime(text, 9, 30);
        var now = run.Context.IstNow.TimeOfDay;
        var nowSeconds = (int)now.TotalSeconds;
        var target = h * 3600 + m * 60 + s;
        if (nowSeconds >= target)
        {
            run.Log($"Target time {text} already passed");
            return FlowNodeResult.Ok(new JsonObject { ["waited"] = false });
        }

        var wait = target - nowSeconds;
        if (wait > MaxSeconds)
        {
            var message = $"Wait Until {text} is {wait}s away, over the {MaxSeconds}s limit. The wait holds this workflow's lock and the request that triggered it, so every trigger in between is answered 'already running'. Use a schedule trigger at {text} instead, or split the square-off into its own workflow.";
            run.Log($"Wait Until aborted: {message}", "error");
            return FlowNodeResult.Fail(message);
        }

        run.Log($"Waiting until {text} (~{wait}s)");
        await run.Sleep(TimeSpan.FromSeconds(wait), ct);
        return FlowNodeResult.Ok(new JsonObject { ["waited"] = true });
    }
}

public sealed class VarConditionNode : IFlowNode
{
    public static readonly IReadOnlyList<string> Operators = [">", "gt", "<", "lt", "==", "eq", "!=", "neq", ">=", "gte", "<=", "lte"];

    public IReadOnlyList<string> Types => ["varCondition"];

    public Task<FlowNodeResult?> ExecuteAsync(FlowNode node, FlowRun run, CancellationToken ct)
    {
        if (Operand(node, run, "leftValue", out var left) is { } e1) return Task.FromResult<FlowNodeResult?>(e1);
        if (Operand(node, run, "rightValue", out var right) is { } e2) return Task.FromResult<FlowNodeResult?>(e2);
        var op = node.Data.Str(run.Context, "operator", ">");
        if (!Operators.Contains(op))
        {
            var message = $"operator '{op}' is not one of {string.Join(", ", Operators.Order(StringComparer.Ordinal))}";
            run.Log($"Var check aborted: {message}", "error");
            return Task.FromResult<FlowNodeResult?>(FlowNodeResult.Fail(message, false));
        }

        var met = Compare(left, op, right);
        run.Log($"Var check: {FlowContext.PyFloat(left)} {op} {FlowContext.PyFloat(right)} = {(met ? "True" : "False")}");
        return Task.FromResult<FlowNodeResult?>(FlowNodeResult.Branch(met, new JsonObject { ["left"] = left, ["operator"] = op, ["right"] = right }));
    }

    /// <summary>One side of the comparison as a number, or the failure that makes the condition take neither branch.</summary>
    private static FlowNodeResult? Operand(FlowNode node, FlowRun run, string key, out double value)
    {
        value = 0;
        var raw = node.Data[key];
        // A literal 0 is falsy in a text default, so a number is taken as given.
        if (raw is JsonValue n && FlowData.TryNumber(n, out var d))
        {
            value = d;
            return null;
        }

        var text = run.Context.Interpolate(raw is null ? "" : FlowContext.PyStr(raw)).Trim();
        string message;
        if (text.Length == 0) message = $"{key} is empty";
        else if (FlowData.TryParseFloat(text, out value)) return null;
        else message = $"{key} did not resolve to a number: '{text}'";
        run.Log($"Var check aborted: {message}", "error");
        return FlowNodeResult.Fail(message, false);
    }

    public static bool Compare(double l, string op, double r) => op switch
    {
        ">" or "gt" => l > r,
        "<" or "lt" => l < r,
        "==" or "eq" => l == r,
        "!=" or "neq" => l != r,
        ">=" or "gte" => l >= r,
        "<=" or "lte" => l <= r,
        _ => false,
    };
}

public sealed class TimeWindowNode : IFlowNode
{
    public IReadOnlyList<string> Types => ["timeWindow"];

    public Task<FlowNodeResult?> ExecuteAsync(FlowNode node, FlowRun run, CancellationToken ct)
    {
        var startText = node.Data["startTime"] is JsonValue s && s.TryGetValue<string>(out var ss) ? ss : "09:15";
        var endText = node.Data["endTime"] is JsonValue e && e.TryGetValue<string>(out var es) ? es : "15:30";
        var invert = node.Data["invertCondition"] is JsonValue i && i.TryGetValue<bool>(out var ib) && ib;
        var now = TimeOnly.FromTimeSpan(run.Context.IstNow.TimeOfDay);
        var (sh, sm, _) = FlowData.ParseTime(startText, 9, 15);
        var (eh, em, _) = FlowData.ParseTime(endText, 15, 30);
        var start = new TimeOnly(sh, sm);
        var end = new TimeOnly(eh, em);
        // A window that ends before it starts crosses midnight, so its two halves are tested apart.
        var inWindow = start <= end ? start <= now && now <= end : now >= start || now <= end;
        var met = invert ? !inWindow : inWindow;
        run.Log($"Time window: {startText}-{endText}, in_window={(inWindow ? "True" : "False")}, invert={(invert ? "True" : "False")}, result={(met ? "True" : "False")}");
        return Task.FromResult<FlowNodeResult?>(FlowNodeResult.Branch(met));
    }
}

public sealed class TimeConditionNode : IFlowNode
{
    public IReadOnlyList<string> Types => ["timeCondition"];

    public Task<FlowNodeResult?> ExecuteAsync(FlowNode node, FlowRun run, CancellationToken ct)
    {
        var targetText = node.Data["targetTime"] is JsonValue t && t.TryGetValue<string>(out var ts) ? ts : "09:30";
        var op = node.Data["operator"] is JsonValue o && o.TryGetValue<string>(out var os) ? os : ">=";
        var kind = node.Data["conditionType"] is JsonValue c && c.TryGetValue<string>(out var cs) ? cs : "entry";
        var now = run.Context.IstNow.TimeOfDay;
        var (th, tm, tsec) = FlowData.ParseTime(targetText, 9, 30);
        var nowSeconds = (int)now.TotalSeconds;
        var target = th * 3600 + tm * 60 + tsec;
        bool met;
        switch (op)
        {
            case ">=": met = nowSeconds >= target; break;
            case "<=": met = nowSeconds <= target; break;
            case ">": met = nowSeconds > target; break;
            case "<": met = nowSeconds < target; break;
            // Minute precision on purpose: a second-exact equality would almost never be hit by a poll.
            case "==": met = now.Hours == th && now.Minutes == tm; break;
            default:
                var message = $"operator '{op}' is not one of >=, <=, >, <, ==";
                run.Log($"Time condition aborted: {message}", "error");
                return Task.FromResult<FlowNodeResult?>(FlowNodeResult.Fail(message, false));
        }

        run.Log($"Time condition ({kind}): {now.Hours:00}:{now.Minutes:00} {op} {targetText} = {(met ? "True" : "False")}");
        return Task.FromResult<FlowNodeResult?>(FlowNodeResult.Branch(met, new JsonObject
        {
            ["condition_type"] = kind, ["current_time"] = $"{now.Hours:00}:{now.Minutes:00}:{now.Seconds:00}", ["target_time"] = targetText, ["operator"] = op,
        }));
    }
}

/// <summary>The node types that need nothing from outside the engine.</summary>
public static class CoreFlowNodes
{
    public static IEnumerable<IFlowNode> All() =>
    [
        new PassThroughNodes(), new LogNode(), new MathExpressionNode(), new VariableNode(), new DelayNode(), new WaitUntilNode(),
        new VarConditionNode(), new TimeWindowNode(), new TimeConditionNode(),
    ];
}
