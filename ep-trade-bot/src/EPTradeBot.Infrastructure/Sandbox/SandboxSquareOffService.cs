using EPTradeBot.Core.Configuration;
using EPTradeBot.Core.Time;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EPTradeBot.Infrastructure.Sandbox;

/// <summary>Runs the sandbox square-off sweep once a minute on trading days.</summary>
public sealed class SandboxSquareOffService(SandboxEngine engine, IOptions<SandboxOptions> options, TimeProvider clock, ILogger<SandboxSquareOffService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.AutoSquareOff) return;
        // Whatever the platform was down for at the last cut-off is settled first.
        try { await engine.CatchUpStaleMisAsync(stoppingToken); await engine.SettleT1Async(Ist.FromUtc(clock.GetUtcNow().UtcDateTime), stoppingToken); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "The sandbox catch-up after a restart failed; the square-off timer carries on."); }
        var lastSettled = DateOnly.FromDateTime(Ist.FromUtc(clock.GetUtcNow().UtcDateTime));
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            var now = Ist.FromUtc(clock.GetUtcNow().UtcDateTime);
            // Delivery positions of earlier days become holdings once, at the first check of each Indian day.
            if (DateOnly.FromDateTime(now) != lastSettled)
            {
                lastSettled = DateOnly.FromDateTime(now);
                try { await engine.SettleT1Async(now, stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "T+1 settlement of the sandbox's delivery positions failed; it will be retried at the next start."); }
            }

            // Nothing trades at the weekend, so there is nothing to close.
            if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            try
            {
                var (cancelled, closed) = await engine.SquareOffAsync(TimeOnly.FromDateTime(now), stoppingToken);
                if (cancelled + closed > 0) logger.LogInformation("Sandbox auto square-off cancelled {Cancelled} orders and closed {Closed} positions.", cancelled, closed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The sandbox square-off sweep failed; it will run again next minute.");
            }
        }
    }
}
