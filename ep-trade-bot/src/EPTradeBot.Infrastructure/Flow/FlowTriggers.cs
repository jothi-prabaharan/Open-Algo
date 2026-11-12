using System.Security.Cryptography;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Flow;
using EPTradeBot.Infrastructure.Persistence;
using EPTradeBot.Infrastructure.Scheduling;
using EPTradeBot.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EPTradeBot.Infrastructure.Flow;

/// <summary>
/// Keeps the live triggers equal to the active workflows: a schedule trigger in Quartz, a price alert in the monitor, a webhook token that can be checked. The database
/// is the source of truth and everything here is rebuilt from it at start.
/// </summary>
public sealed class FlowTriggers(
    IDbContextFactory<EpTradeDbContext> contexts,
    FlowPriceMonitor monitor,
    ScheduleRegistry schedules,
    ApiKeyService keys,
    ILogger<FlowTriggers> logger) : IHostedService
{
    /// <summary>Makes a new webhook token for a workflow and returns it; only its hash is kept, so it cannot be shown again.</summary>
    public async Task<string> IssueWebhookTokenAsync(Guid flowId, CancellationToken ct)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.FlowDefinitions.Where(f => f.Id == flowId).ExecuteUpdateAsync(u => u.SetProperty(f => f.WebhookTokenHash, keys.Hash(token)), ct);
        return token;
    }

    public bool TokenMatches(string? stored, string presented) =>
        stored is not null && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(stored), System.Text.Encoding.ASCII.GetBytes(keys.Hash(presented)));

    /// <summary>Puts the triggers of one workflow in line with its row. Returns a warning when a trigger could not be fully set up, null when all is well.</summary>
    public async Task<string?> SyncAsync(Guid flowId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var flow = await db.FlowDefinitions.AsNoTracking().SingleOrDefaultAsync(f => f.Id == flowId, ct);
        await schedules.SyncFlowAsync(flow, ct);
        if (flow is null || !flow.IsActive)
        {
            await monitor.UnwatchAsync(flowId, ct);
            return null;
        }

        var graph = FlowGraph.Parse(JsonNode.Parse(flow.NodesJson) as JsonArray, JsonNode.Parse(flow.EdgesJson) as JsonArray);
        if (graph.Trigger is not { Type: "priceAlert" } trigger)
        {
            await monitor.UnwatchAsync(flowId, ct);
            return null;
        }

        var (alert, problem) = PriceAlertRules.FromNode(trigger.Data);
        if (alert is null) return problem;
        var account = flow.AccountId is { } id ? await db.BrokerAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct) : null;
        if (account is null) return "Choose the account this workflow trades on, so its price alert has a feed to watch.";
        return await monitor.WatchAsync(flowId, account.Id, account.Broker, alert, ct);
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        foreach (var id in await db.FlowDefinitions.AsNoTracking().Where(f => f.IsActive).Select(f => f.Id).ToListAsync(ct))
        {
            try
            {
                await SyncAsync(id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The triggers of workflow {FlowId} could not be restored.", id);
            }
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
