using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Flow;
using EPTradeBot.Infrastructure.OpenAlgoApi;
using EPTradeBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EPTradeBot.Infrastructure.Flow;

public sealed record FlowRunResult(bool Started, bool Succeeded, string Message, Guid? RunId = null, IReadOnlyList<FlowError>? Errors = null, bool AlreadyRunning = false);

/// <summary>
/// Runs workflows. Every trigger (a person, a schedule, a webhook, a price alert) arrives here, so this is the one place that can promise an incomplete graph never
/// reaches the broker: the workflow is validated as finished before a run is recorded, and a workflow that is already running is not run again.
/// </summary>
public sealed class FlowService(IDbContextFactory<EpTradeDbContext> contexts, OpenAlgoApiService api, TimeProvider clock, ILogger<FlowService> logger)
{
    private const int MaxLogLines = 500;

    private static readonly FlowRunner Runner = new(CoreFlowNodes.All().Concat(ApiFlowNodes.All()));

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> locks = new();

    public static bool Supports(string nodeType) => Runner.Supports(nodeType);

    public static string ModeName(ExecutionMode mode) => mode == ExecutionMode.Auto ? "auto" : "semi_auto";

    /// <summary>The problems with a workflow that stop it being run, worded for a trader; empty when it can run.</summary>
    public static List<FlowValidationError> Validate(string name, string nodesJson, string edgesJson, bool strict)
    {
        JsonNode? nodes, edges;
        try
        {
            nodes = JsonNode.Parse(nodesJson);
            edges = JsonNode.Parse(edgesJson);
        }
        catch (JsonException)
        {
            return [new FlowValidationError("/", "invalid_type", "The workflow's nodes or edges could not be read.")];
        }

        return FlowValidator.Validate(new JsonObject { ["name"] = name, ["nodes"] = nodes, ["edges"] = edges }, true, strict, Supports);
    }

    public async Task<FlowRunResult> RunAsync(Guid flowId, string trigger, JsonObject? triggerData, CancellationToken ct)
    {
        var gate = locks.GetOrAdd(flowId, _ => new SemaphoreSlim(1, 1));
        // Try, never wait: a second trigger while one run is in progress is answered "already running", not queued behind it to run the whole workflow again.
        if (!await gate.WaitAsync(0, ct)) return new FlowRunResult(false, false, "This workflow is already running.", AlreadyRunning: true);
        try
        {
            return await RunLockedAsync(flowId, trigger, triggerData, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<FlowRunResult> RunLockedAsync(Guid flowId, string trigger, JsonObject? triggerData, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var flow = await db.FlowDefinitions.SingleOrDefaultAsync(f => f.Id == flowId, ct);
        if (flow is null) return new FlowRunResult(false, false, "This workflow does not exist.");

        var problems = Validate(flow.Name, flow.NodesJson, flow.EdgesJson, strict: true);
        if (problems.Count > 0) return new FlowRunResult(false, false, $"This workflow cannot be run: {problems[0].Message}");
        if (flow.AccountId is null) return new FlowRunResult(false, false, "Choose the account this workflow trades on before running it.");
        var account = await db.BrokerAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == flow.AccountId, ct);
        if (account is null) return new FlowRunResult(false, false, "The account this workflow trades on no longer exists.");
        if (!account.IsEnabled) return new FlowRunResult(false, false, "The account this workflow trades on is switched off.");
        if (account.KillSwitchEngaged) return new FlowRunResult(false, false, "The kill switch is on for this workflow's account. Release it before running the workflow.");

        var started = clock.GetUtcNow().UtcDateTime;
        var record = new FlowRunRecord { FlowId = flow.Id, Trigger = trigger, StartedAtUtc = started };
        db.FlowRuns.Add(record);
        await db.SaveChangesAsync(ct);

        var graph = FlowGraph.Parse(JsonNode.Parse(flow.NodesJson) as JsonArray, JsonNode.Parse(flow.EdgesJson) as JsonArray);
        var broker = account.Broker;
        var host = new FlowHost(new FlowApi(api, account, ModeName(flow.ExecutionMode)), flow.Name) { LotSizes = (underlying, exchange, token) => LotSizeAsync(broker, underlying, exchange, token), Http = new FlowHttp() };
        var run = new FlowRun(() => clock.GetUtcNow().UtcDateTime, host: host);
        if (triggerData is not null) run.Context.Set(trigger == "price_alert" ? "alert" : "webhook", triggerData.DeepClone());
        run.Log($"Starting workflow: {flow.Name}");

        FlowOutcome outcome;
        try
        {
            outcome = await Runner.RunAsync(graph, run, ct);
        }
        catch (OperationCanceledException)
        {
            outcome = new FlowOutcome(false, "The run was interrupted because the platform is stopping.", run.Errors);
        }

        if (outcome.Fault is not null) logger.LogError(outcome.Fault, "Workflow {Flow} ({FlowId}) stopped on an unexpected problem.", flow.Name, flow.Id);

        var finished = clock.GetUtcNow().UtcDateTime;
        record.Status = outcome.Succeeded ? "completed" : "failed";
        record.FinishedAtUtc = finished;
        record.Error = outcome.Succeeded ? null : Trim(outcome.Message, 1000);
        record.LogsJson = new JsonArray([.. run.Logs.TakeLast(MaxLogLines).Select(l => (JsonNode?)new JsonObject { ["time"] = l.AtUtc.ToString("O"), ["level"] = l.Level, ["message"] = l.Message })]).ToJsonString();
        record.ErrorsJson = new JsonArray([.. run.Errors.Select(e => (JsonNode?)new JsonObject { ["node"] = e.Node, ["type"] = e.Type, ["message"] = e.Message })]).ToJsonString();
        flow.LastRunAtUtc = finished;
        flow.LastRunStatus = record.Status;
        await db.SaveChangesAsync(CancellationToken.None);
        return new FlowRunResult(true, outcome.Succeeded, outcome.Message, record.Id, outcome.Errors);
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>An underlying's lot size from the symbol master of the account's broker: by the name column where the broker fills it, otherwise by the symbol's own underlying.</summary>
    private async Task<LotSizeAnswer> LotSizeAsync(string broker, string underlying, string exchange, CancellationToken ct)
    {
        try
        {
            await using var db = await contexts.CreateDbContextAsync(ct);
            var contracts = db.Set<EPTradeBot.Core.Entities.Legacy.Core.SymToken>().AsNoTracking().Where(t => t.Broker == broker && t.Exchange == exchange);
            var named = await contracts.Where(t => t.Name == underlying && t.Lotsize > 0).Select(t => t.Lotsize).FirstOrDefaultAsync(ct);
            if (named is > 0) return LotSizeAnswer.Found(named.Value);
            var candidates = await contracts.Where(t => t.Symbol.StartsWith(underlying) && t.Lotsize > 0).Select(t => new { t.Symbol, t.Lotsize }).Take(2000).ToListAsync(ct);
            foreach (var c in candidates)
                if (UnderlyingOf(c.Symbol) == underlying) return LotSizeAnswer.Found(c.Lotsize!.Value);
            return await contracts.AnyAsync(ct) ? new LotSizeAnswer(null, LotSizeProblem.NotListed) : new LotSizeAnswer(null, LotSizeProblem.ExchangeEmpty);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Lot size lookup failed for {Underlying} on {Exchange}", underlying, exchange);
            return new LotSizeAnswer(null, LotSizeProblem.Unreadable);
        }
    }

    /// <summary>NIFTY26NOV2624000CE and NIFTYNXT5026NOVFUT both belong to their own roots: everything before the expiry.</summary>
    internal static string UnderlyingOf(string symbol)
    {
        var m = System.Text.RegularExpressions.Regex.Match(symbol, @"^(.*?)\d{2}[A-Z]{3}\d{2}");
        return m.Success ? m.Groups[1].Value : symbol;
    }
}
