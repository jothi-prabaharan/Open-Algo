using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using EPTradeBot.Core.Flow;
using EPTradeBot.Core.Streaming;
using EPTradeBot.Infrastructure.Persistence;
using EPTradeBot.Infrastructure.Streaming;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EPTradeBot.Infrastructure.Flow;

/// <summary>
/// Watches ticks for the workflows whose trigger is a price alert and starts a run when one fires. It never blocks the bus: a tick is checked in memory and a
/// firing is handed to a worker. A once-only alert is spent when it fires: the watch is dropped and the workflow switched off, so a restart cannot bring it back.
/// </summary>
public sealed class FlowPriceMonitor(
    IFeedSubscriptions feed,
    FlowService flows,
    IDbContextFactory<EpTradeDbContext> contexts,
    TimeProvider clock,
    ILogger<FlowPriceMonitor> logger) : BackgroundService, IMarketTickSink
{
    private sealed class Watch(Guid flowId, Guid accountId, string broker, PriceAlert alert)
    {
        public Guid FlowId { get; } = flowId;

        public Guid AccountId { get; } = accountId;

        public string Broker { get; } = broker;

        public PriceAlert Alert { get; } = alert;

        public PriceAlertState State { get; } = new();
    }

    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Watch> watches = [];
    private readonly Channel<(Watch Watch, JsonObject Data)> fired = Channel.CreateUnbounded<(Watch, JsonObject)>(new UnboundedChannelOptions { SingleReader = true });

    public int WatchCount
    {
        get
        {
            lock (gate) return watches.Count;
        }
    }

    /// <summary>Starts watching for a workflow (replacing any earlier watch of it) and asks the feed for the instrument. Returns a warning when the feed would not take it.</summary>
    public async Task<string?> WatchAsync(Guid flowId, Guid accountId, string broker, PriceAlert alert, CancellationToken ct)
    {
        await UnwatchAsync(flowId, ct);
        var watch = new Watch(flowId, accountId, broker, alert);
        lock (gate) watches[flowId] = watch;
        var answer = await feed.SubscribeAsync(accountId, broker, alert.Exchange, alert.Symbol, StreamMode.Ltp, ct);
        return answer.Ok ? null : answer.Message;
    }

    public async Task UnwatchAsync(Guid flowId, CancellationToken ct)
    {
        Watch? old;
        lock (gate)
        {
            watches.Remove(flowId, out old);
        }

        if (old is not null) await feed.UnsubscribeAsync(old.AccountId, old.Alert.Exchange, old.Alert.Symbol, StreamMode.Ltp, ct);
    }

    public void OnTick(MarketTick tick)
    {
        if (tick.Data["ltp"] is not JsonValue v || !(v.TryGetValue<double>(out var price) || (v.TryGetValue<string>(out var s) && double.TryParse(s, CultureInfo.InvariantCulture, out price)))) return;
        List<Watch> hits = [];
        lock (gate)
        {
            foreach (var w in watches.Values)
            {
                if (w.Alert.Exchange == tick.Topic.Exchange && w.Alert.Symbol == tick.Topic.Symbol && PriceAlertRules.Check(w.Alert, w.State, price)) hits.Add(w);
            }
        }

        foreach (var w in hits)
        {
            var data = new JsonObject
            {
                ["symbol"] = w.Alert.Symbol, ["exchange"] = w.Alert.Exchange, ["ltp"] = price, ["condition"] = w.Alert.Condition, ["target"] = w.Alert.Target,
                ["at"] = clock.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            };
            fired.Writer.TryWrite((w, data));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (watch, data) in fired.Reader.ReadAllAsync(stoppingToken))
        {
            // Each firing runs on its own, so a slow workflow does not hold up the next alert. A second firing of the same workflow is answered "already running".
            _ = Task.Run(() => RunAsync(watch, data, stoppingToken), CancellationToken.None);
        }
    }

    private async Task RunAsync(Watch watch, JsonObject data, CancellationToken ct)
    {
        try
        {
            var result = await flows.RunAsync(watch.FlowId, "price_alert", data, ct);
            logger.LogInformation("Price alert fired for workflow {FlowId}: {Message}", watch.FlowId, result.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The run started by a price alert failed for workflow {FlowId}.", watch.FlowId);
        }

        if (watch.Alert.EveryTime) return;
        // Spent: retire by identity, so a run that ends after the trader switched the workflow off and on again cannot drop the newer watch.
        bool current;
        lock (gate)
        {
            current = watches.TryGetValue(watch.FlowId, out var w) && ReferenceEquals(w, watch);
            if (current) watches.Remove(watch.FlowId);
        }

        if (!current) return;
        await feed.UnsubscribeAsync(watch.AccountId, watch.Alert.Exchange, watch.Alert.Symbol, StreamMode.Ltp, CancellationToken.None);
        await using var db = await contexts.CreateDbContextAsync(CancellationToken.None);
        await db.FlowDefinitions.Where(f => f.Id == watch.FlowId).ExecuteUpdateAsync(u => u.SetProperty(f => f.IsActive, false), CancellationToken.None);
    }
}
