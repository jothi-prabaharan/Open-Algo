using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EPTradeBot.Infrastructure.OpenAlgoApi;

/// <summary>
/// Visits every open strategy run on a short timer and applies its stops, targets, trails and limits. A run's working state is stored with it, so after a restart the first visit carries on
/// from where the run was. One failing run never holds back the others.
/// </summary>
public sealed class StrategyRunMonitor(OpenAlgoApiService api, IConfiguration configuration, TimeProvider clock, ILogger<StrategyRunMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = Math.Max(1, configuration.GetValue("Strategy:MonitorSeconds", 2));
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try { await VisitAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "The strategy monitor pass failed; it will run again."); }
        }
    }

    /// <summary>One pass over all open runs.</summary>
    public async Task VisitAsync(CancellationToken ct)
    {
        foreach (var id in await api.OpenRunIdsAsync(ct))
        {
            try { await api.RunTickAsync(id, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Watching strategy run {RunId} failed; it will be visited again.", id); }
        }
    }
}
