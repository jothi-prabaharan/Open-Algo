using System.Collections;
using EPTradeBot.Core.Configuration;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Scheduling;
using EPTradeBot.Infrastructure.Persistence;
using EPTradeBot.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EPTradeBot.Infrastructure.Scheduling;

/// <summary>
/// Runs hosted strategies as isolated child processes: each with its own short-lived API key (in the strategy's execution mode, so a semi-auto
/// strategy's orders wait in the Action Center), a clean environment, and a log file. Port of the process handling in OpenAlgo's
/// blueprints/python_strategy.py. A strategy that ends by itself is recorded as ended and is not restarted: the schedule decides when it runs.
/// </summary>
public sealed class StrategySupervisor(
    IDbContextFactory<EpTradeDbContext> contexts,
    ApiKeyService keys,
    IProcessLauncher launcher,
    IOptions<StrategyHostOptions> options,
    TimeProvider clock,
    ILogger<StrategySupervisor> logger) : IStrategyController, IHostedService
{
    private sealed class Run(IRunningProcess process, Guid accountId, Guid keyId)
    {
        public IRunningProcess Process { get; } = process;

        public Guid AccountId { get; } = accountId;

        public Guid KeyId { get; } = keyId;

        public bool Stopping { get; set; }
    }

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<Guid, Run> runs = [];
    private CancellationTokenSource? stopping;
    private Task? reaper;

    private StrategyHostOptions Options => options.Value;

    public int RunningCount
    {
        get
        {
            lock (runs) return runs.Count;
        }
    }

    public string LogPath(HostedStrategy s) => Path.Combine(Options.LogDirectory, StrategyEnvironment.LogFileName(s.Name, s.Id));

    public async Task<ControllerResult> StartAsync(Guid strategyId, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            return await StartLockedAsync(strategyId, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ControllerResult> StopAsync(Guid strategyId, string reason, CancellationToken ct)
    {
        Run? run;
        lock (runs) runs.TryGetValue(strategyId, out run);
        if (run is null) return new ControllerResult(true, "It was not running.");
        run.Stopping = true;
        await EndAsync(strategyId, run, ct);
        return new ControllerResult(true, "Stopped.");
    }

    private async Task<ControllerResult> StartLockedAsync(Guid id, CancellationToken ct)
    {
        lock (runs)
        {
            if (runs.ContainsKey(id)) return new ControllerResult(true, "It is already running.");
            if (runs.Count >= Options.MaxRunning)
                return new ControllerResult(false, $"{Options.MaxRunning} strategies are already running, which is the most this install runs at once. Stop one first.");
        }

        await using var db = await contexts.CreateDbContextAsync(ct);
        var s = await db.HostedStrategies.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return new ControllerResult(false, "This strategy does not exist.");
        if (!s.IsEnabled) return new ControllerResult(false, "This strategy is switched off. Switch it on to run it.");
        if (s.AccountId is null) return new ControllerResult(false, "Choose the account this strategy trades on before starting it.");
        var account = await db.BrokerAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == s.AccountId, ct);
        if (account is null) return new ControllerResult(false, "The account this strategy trades on no longer exists.");
        if (!account.IsEnabled) return new ControllerResult(false, "The account this strategy trades on is switched off.");
        if (account.KillSwitchEngaged) return new ControllerResult(false, "The kill switch is on for this strategy's account. Release it before starting the strategy.");

        var script = ResolveScript(s.ScriptPath);
        if (script is null) return new ControllerResult(false, "The script for this strategy could not be found in the strategies folder.");

        var mode = s.ExecutionMode == ExecutionMode.Auto ? "auto" : "semi_auto";
        var label = $"strategy:{s.Name}";
        var (record, apiKey) = await keys.IssueAsync(account.Id, label.Length > 64 ? label[..64] : label, ct, mode);

        IRunningProcess process;
        try
        {
            var env = StrategyEnvironment.Build(PlatformEnvironment(), new StrategyEnvironment.Inputs(s.Id, s.Name, s.Exchange, mode, Options.HostUrl, apiKey));
            process = launcher.Launch(new ProcessLaunch(Options.Interpreter, ["-u", script], Path.GetDirectoryName(script)!, env, LogPath(s)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await keys.RevokeAsync(account.Id, record.Id, CancellationToken.None);
            logger.LogError(ex, "Strategy {StrategyId} could not be started.", id);
            return new ControllerResult(false, "The strategy could not be started. Check that Python is installed and the script can be run; the server log has the detail.");
        }

        var run = new Run(process, account.Id, record.Id);
        lock (runs) runs[id] = run;
        var now = clock.GetUtcNow().UtcDateTime;
        await db.HostedStrategies.Where(x => x.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.ProcessId, process.Pid).SetProperty(x => x.IsRunning, true).SetProperty(x => x.LastStartedAtUtc, now), ct);
        _ = Task.Run(() => WatchAsync(id, run), CancellationToken.None);
        logger.LogInformation("Strategy {StrategyId} started as process {Pid}.", id, process.Pid);
        return new ControllerResult(true, "Started.");
    }

    /// <summary>The script's full path, or null when it is missing or resolves (through a link or otherwise) outside the strategies folder.</summary>
    private string? ResolveScript(string relative)
    {
        if (!StrategyPathRules.IsSafeRelative(relative)) return null;
        var root = Path.GetFullPath(Options.Directory);
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!File.Exists(full)) return null;
        var real = File.ResolveLinkTarget(full, returnFinalTarget: true)?.FullName ?? full;
        var realRoot = Directory.ResolveLinkTarget(root, returnFinalTarget: true)?.FullName ?? root;
        return IsInside(realRoot, real) ? full : null;
    }

    private static bool IsInside(string root, string path) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static Dictionary<string, string> PlatformEnvironment()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            if (e.Key is string k && e.Value is string v) map[k] = v;
        }

        return map;
    }

    /// <summary>Waits for the process to end by itself; an end that was not asked for is recorded.</summary>
    private async Task WatchAsync(Guid id, Run run)
    {
        await run.Process.Exited;
        if (run.Stopping) return;
        try
        {
            lock (runs)
            {
                if (!runs.TryGetValue(id, out var current) || !ReferenceEquals(current, run)) return;
                runs.Remove(id);
            }

            var code = run.Process.ExitCode;
            await keys.RevokeAsync(run.AccountId, run.KeyId, CancellationToken.None);
            var now = clock.GetUtcNow().UtcDateTime;
            var message = code == 0 ? "The strategy finished." : $"The strategy stopped by itself (exit code {code}). Its log has the reason.";
            await using var db = await contexts.CreateDbContextAsync(CancellationToken.None);
            await db.HostedStrategies.Where(x => x.Id == id && x.IsRunning).ExecuteUpdateAsync(u => u
                .SetProperty(x => x.IsRunning, false).SetProperty(x => x.ProcessId, (int?)null)
                .SetProperty(x => x.LastStoppedAtUtc, now).SetProperty(x => x.LastError, message), CancellationToken.None);
            run.Process.Dispose();
            logger.LogWarning("Strategy {StrategyId} ended by itself with exit code {Code}.", id, code);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The end of strategy {StrategyId} could not be recorded.", id);
        }
    }

    /// <summary>Asks the process to stop, ends it if it does not in time, and revokes its key.</summary>
    private async Task EndAsync(Guid id, Run run, CancellationToken ct)
    {
        run.Process.Terminate();
        var grace = Task.Delay(TimeSpan.FromSeconds(Options.StopGraceSeconds), CancellationToken.None);
        if (await Task.WhenAny(run.Process.Exited, grace) != run.Process.Exited)
        {
            logger.LogWarning("Strategy {StrategyId} did not stop in {Seconds}s; ending it.", id, Options.StopGraceSeconds);
            run.Process.Kill();
            await run.Process.Exited;
        }

        lock (runs)
        {
            if (runs.TryGetValue(id, out var current) && ReferenceEquals(current, run)) runs.Remove(id);
        }

        await keys.RevokeAsync(run.AccountId, run.KeyId, CancellationToken.None);
        run.Process.Dispose();
    }

    /// <summary>
    /// Brings the rows in line with the processes: a row marked running whose process is gone is marked ended, and a process left from before a
    /// restart (one the platform no longer holds a key or handle for) is stopped, because nothing could stop it later.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var marked = await db.HostedStrategies.AsNoTracking().Where(x => x.IsRunning).Select(x => new { x.Id, x.ProcessId, x.ScriptPath }).ToListAsync(ct);
        foreach (var row in marked)
        {
            bool tracked;
            lock (runs) tracked = runs.ContainsKey(row.Id);
            if (tracked) continue;
            if (row.ProcessId is { } pid && launcher.IsAliveAndMatches(pid, Path.GetFileName(row.ScriptPath)))
            {
                launcher.TerminateByPid(pid);
                logger.LogWarning("Strategy {StrategyId} was left running by an earlier start of the platform; it was stopped.", row.Id);
            }

            var now = clock.GetUtcNow().UtcDateTime;
            await db.HostedStrategies.Where(x => x.Id == row.Id && x.IsRunning).ExecuteUpdateAsync(u => u
                .SetProperty(x => x.IsRunning, false).SetProperty(x => x.ProcessId, (int?)null).SetProperty(x => x.LastStoppedAtUtc, now)
                .SetProperty(x => x.LastError, "The strategy was not running any more, so it is marked stopped."), ct);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await ReconcileAsync(cancellationToken);
        stopping = new CancellationTokenSource();
        reaper = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Options.ReapSeconds));
            try
            {
                while (await timer.WaitForNextTickAsync(stopping.Token))
                {
                    try
                    {
                        await ReconcileAsync(stopping.Token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogError(ex, "The strategy check failed.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (stopping is not null) await stopping.CancelAsync();
        if (reaper is not null) await reaper;
        List<(Guid Id, Run Run)> all;
        lock (runs) all = runs.Select(kv => (kv.Key, kv.Value)).ToList();
        foreach (var (id, run) in all) run.Stopping = true;
        await Task.WhenAll(all.Select(x => EndAsync(x.Id, x.Run, CancellationToken.None)));
    }
}
