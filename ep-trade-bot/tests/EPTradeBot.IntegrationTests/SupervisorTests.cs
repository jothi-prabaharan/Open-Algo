using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EPTradeBot.Core.Entities.Core;
using EPTradeBot.Core.Scheduling;
using EPTradeBot.Infrastructure.Scheduling;
using EPTradeBot.Infrastructure.Security;
using EPTradeBot.Infrastructure.Trading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EPTradeBot.IntegrationTests;

/// <summary>The supervisor, with real child processes: a real Python interpreter running small scripts in a temporary strategies folder.</summary>
public sealed class SupervisorTests
{
    private sealed record Setup(TestHost Host, string Scripts, string Logs, Guid Account) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            try { Directory.Delete(Root, true); } catch (IOException) { }
        }

        public string Root => Path.GetDirectoryName(Scripts)!;
    }

    private const string Loop = "import time\nwhile True:\n    time.sleep(0.2)\n";

    // Prints what it was given, then runs until it is stopped, saying so when asked nicely.
    private const string Reporter = """
        import os, signal, sys, time
        print("KEY=" + os.environ.get("EPTRADE_API_KEY", ""), flush=True)
        print("ID=" + os.environ.get("STRATEGY_ID", ""), flush=True)
        print("MODE=" + os.environ.get("EPTRADE_EXECUTION_MODE", ""), flush=True)
        print("ENVNAMES=" + ",".join(sorted(os.environ)), flush=True)
        print("CWD=" + os.getcwd(), flush=True)
        def bye(*a):
            print("BYE", flush=True)
            sys.exit(0)
        signal.signal(signal.SIGTERM, bye)
        while True:
            time.sleep(0.2)
        """;

    private static async Task<Setup> NewAsync(int maxRunning = 20)
    {
        var root = Path.Combine(Path.GetTempPath(), "eptrade-sup-" + Guid.NewGuid().ToString("N"));
        var scripts = Path.Combine(root, "strategies");
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(scripts);
        var host = new TestHost("Development", b =>
        {
            b.UseSetting("Strategies:Enabled", "true");
            b.UseSetting("Strategies:Directory", scripts);
            b.UseSetting("Strategies:LogDirectory", logs);
            b.UseSetting("Strategies:StopGraceSeconds", "1");
            b.UseSetting("Strategies:ReapSeconds", "1");
            b.UseSetting("Strategies:MaxRunning", maxRunning.ToString());
        });
        var account = await host.CreateAccountAsync(AccountType.PAPER, "paper-one");
        return new Setup(host, scripts, logs, account);
    }

    private static void Script(Setup s, string name, string body)
    {
        var path = Path.Combine(s.Scripts, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, body);
    }

    private static async Task<Guid> RegisterAsync(Setup s, string name = "alpha", string script = "alpha.py", object? account = null, string mode = "semi_auto")
    {
        var response = await s.Host.Admin.PostAsJsonAsync("/api/v1/hosted-strategies", new { name, script_path = script, account_id = account ?? s.Account, execution_mode = mode });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Guid.Parse((await response.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<string>());
    }

    private static async Task<HostedStrategy> RowAsync(Setup s, Guid id)
    {
        await using var db = s.Host.NewDb();
        return await db.HostedStrategies.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    private static async Task<bool> WaitAsync(Func<Task<bool>> condition, int seconds = 15)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (await condition()) return true;
            await Task.Delay(100);
        }

        return false;
    }

    private static async Task<string> LogAsync(Setup s, Guid id, string contains)
    {
        string text = "";
        await WaitAsync(async () =>
        {
            var path = s.Host.Get<StrategySupervisor>().LogPath(await RowAsync(s, id));
            text = File.Exists(path) ? await File.ReadAllTextAsync(path) : "";
            return text.Contains(contains);
        });
        return text;
    }

    private static string Value(string log, string name) =>
        log.Split('\n').First(l => l.StartsWith(name + "=")).Substring(name.Length + 1).Trim();

    [PostgresFact]
    public async Task A_started_strategy_runs_as_its_own_process_with_its_own_key_and_a_clean_environment()
    {
        await using var s = await NewAsync();
        Script(s, "alpha.py", Reporter);
        var id = await RegisterAsync(s, mode: "semi_auto");

        var started = await s.Host.Admin.PostAsync($"/api/v1/hosted-strategies/{id}/start", null);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);

        var log = await LogAsync(s, id, "ENVNAMES=");
        var row = await RowAsync(s, id);
        Assert.True(row.IsRunning);
        Assert.NotNull(row.ProcessId);
        Assert.Equal(id.ToString("N"), Value(log, "ID"));
        Assert.Equal("semi_auto", Value(log, "MODE"));
        Assert.Equal(Path.GetFullPath(s.Scripts), Value(log, "CWD"));

        // The key it was given belongs to its account, in the strategy's mode.
        var resolved = await s.Host.Get<ApiKeyService>().ResolveCallAsync(Value(log, "KEY"));
        Assert.Equal(s.Account, resolved!.Value.Account.Id);
        Assert.Equal("semi_auto", resolved.Value.OrderMode);

        // And none of the platform's own settings came with it.
        var names = Value(log, "ENVNAMES").Split(',');
        Assert.DoesNotContain(names, n => n.Contains("Security") || n.Contains("Management") || n.Contains("Database") || n.StartsWith("ASPNETCORE") || n.StartsWith("DOTNET"));
        Assert.Contains("OPENALGO_API_KEY", names);

        await s.Host.Admin.PostAsync($"/api/v1/hosted-strategies/{id}/stop", null);
    }

    [PostgresFact]
    public async Task A_stop_asks_politely_first_then_revokes_the_key()
    {
        await using var s = await NewAsync();
        Script(s, "alpha.py", Reporter);
        var id = await RegisterAsync(s);
        await s.Host.Admin.PostAsync($"/api/v1/hosted-strategies/{id}/start", null);
        var key = Value(await LogAsync(s, id, "ENVNAMES="), "KEY");
        var pid = (await RowAsync(s, id)).ProcessId!.Value;

        Assert.Equal(HttpStatusCode.OK, (await s.Host.Admin.PostAsync($"/api/v1/hosted-strategies/{id}/stop", null)).StatusCode);

        Assert.Contains("BYE", await LogAsync(s, id, "BYE"));
        var row = await RowAsync(s, id);
        Assert.False(row.IsRunning);
        Assert.Null(row.ProcessId);
        Assert.Null(await s.Host.Get<ApiKeyService>().ResolveCallAsync(key));
        Assert.False(Directory.Exists($"/proc/{pid}") && File.ReadAllText($"/proc/{pid}/cmdline").Contains("alpha.py"));
    }

    [PostgresFact]
    public async Task A_strategy_that_ignores_the_request_to_stop_is_ended_after_the_grace_period()
    {
        await using var s = await NewAsync();
        Script(s, "stubborn.py", "import signal, time\nsignal.signal(signal.SIGTERM, signal.SIG_IGN)\nprint('up', flush=True)\nwhile True:\n    time.sleep(0.2)\n");
        var id = await RegisterAsync(s, "stubborn", "stubborn.py");
        await s.Host.Admin.PostAsync($"/api/v1/hosted-strategies/{id}/start", null);
        await LogAsync(s, id, "up");
        var pid = (await RowAsync(s, id)).ProcessId!.Value;

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(HttpStatusCode.OK, (await s.Host.Admin.PostAsync($"/api/v1/hosted-strategies/{id}/stop", null)).StatusCode);
        watch.Stop();

        Assert.InRange(watch.Elapsed.TotalSeconds, 0.9, 8);
        Assert.False(await ProcessAliveAsync(pid, "stubborn.py"));
        Assert.False((await RowAsync(s, id)).IsRunning);
    }

    private static Task<bool> ProcessAliveAsync(int pid, string name) => WaitAsync(() =>
        Task.FromResult(!(Directory.Exists($"/proc/{pid}") && SafeCmd(pid).Contains(name))), 1).ContinueWith(t => !t.Result);

    private static string SafeCmd(int pid)
    {
        try { return File.ReadAllText($"/proc/{pid}/cmdline"); } catch (IOException) { return ""; }
    }

    [PostgresFact]
    public async Task A_strategy_that_ends_by_itself_is_recorded_and_not_restarted()
    {
        await using var s = await NewAsync();
        Script(s, "crash.py", "import sys, os\nprint('KEY=' + os.environ['EPTRADE_API_KEY'], flush=True)\nsys.exit(3)\n");
        var id = await RegisterAsync(s, "crash", "crash.py");
        await s.Host.Admin.PostAsync($"/api/v1/hosted-strategies/{id}/start", null);
        var key = Value(await LogAsync(s, id, "KEY="), "KEY");

        Assert.True(await WaitAsync(async () => !(await RowAsync(s, id)).IsRunning));
        var row = await RowAsync(s, id);
        Assert.Contains("exit code 3", row.LastError);
        Assert.Contains("log", row.LastError);
        Assert.Null(row.ProcessId);
        Assert.NotNull(row.LastStoppedAtUtc);
        Assert.Null(await s.Host.Get<ApiKeyService>().ResolveCallAsync(key));
        await Task.Delay(2500); // the reaper has run more than once and has not brought it back
        Assert.False((await RowAsync(s, id)).IsRunning);
    }

    [PostgresFact]
    public async Task A_strategy_that_finishes_normally_says_so()
    {
        await using var s = await NewAsync();
        Script(s, "done.py", "print('done')\n");
        var id = await RegisterAsync(s, "done", "done.py");
        await s.Host.Admin.PostAsync($"/api/v1/hosted-strategies/{id}/start", null);
        Assert.True(await WaitAsync(async () => !(await RowAsync(s, id)).IsRunning));
        Assert.Equal("The strategy finished.", (await RowAsync(s, id)).LastError);
    }

    [PostgresFact]
    public async Task Starting_is_refused_in_plain_words_when_it_cannot_be_done()
    {
        await using var s = await NewAsync();
        Script(s, "alpha.py", Loop);

        async Task<string> TryStart(Guid id)
        {
            var r = await s.Host.Admin.PostAsync($"/api/v1/hosted-strategies/{id}/start", null);
            Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
            return await r.Content.ReadAsStringAsync();
        }

        var noAccount = await s.Host.Admin.PostAsJsonAsync("/api/v1/hosted-strategies", new { name = "noacct", script_path = "alpha.py" });
        var noAccountId = Guid.Parse((await noAccount.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<string>());
        Assert.Contains("Choose the account", await TryStart(noAccountId));

        var missing = await RegisterAsync(s, "missing", "nothere.py");
        Assert.Contains("could not be found", await TryStart(missing));

        var off = await RegisterAsync(s, "off");
        await s.Host.Admin.PutAsJsonAsync($"/api/v1/hosted-strategies/{off}/schedule", new { is_enabled = false });
        Assert.Contains("switched off", await TryStart(off));

        var killed = await RegisterAsync(s, "killed");
        await s.Host.Admin.PostAsJsonAsync($"/api/v1/killswitch/account/{s.Account}", new { engage = true, reason = "test" });
        Assert.Contains("kill switch", await TryStart(killed));

        foreach (var id in new[] { noAccountId, missing, off, killed })
        {
            Assert.False((await RowAsync(s, id)).IsRunning);
            Assert.Equal(0, s.Host.Get<StrategySupervisor>().RunningCount);
        }
    }

    [PostgresFact]
    public async Task A_script_cannot_reach_outside_the_strategies_folder()
    {
        await using var s = await NewAsync();
        var outside = Path.Combine(s.Root, "outside.py");
        File.WriteAllText(outside, "print('escaped')\n");
        File.CreateSymbolicLink(Path.Combine(s.Scripts, "link.py"), outside);
        var id = await RegisterAsync(s, "linked", "link.py");

        var r = await s.Host.Admin.PostAsync($"/api/v1/hosted-strategies/{id}/start", null);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.False(File.Exists(Path.Combine(s.Logs, StrategyEnvironment.LogFileName("linked", id))));

        foreach (var bad in new[] { "../outside.py", "/etc/passwd.py", "a/../../outside.py", "run.sh" })
        {
            var create = await s.Host.Admin.PostAsJsonAsync("/api/v1/hosted-strategies", new { name = "bad" + bad.GetHashCode(), script_path = bad });
            Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        }
    }

    [PostgresFact]
    public async Task No_more_than_the_limit_run_at_once()
    {
        await using var s = await NewAsync(maxRunning: 1);
        Script(s, "alpha.py", Loop);
        var a = await RegisterAsync(s, "a");
        var b = await RegisterAsync(s, "b");
        Assert.Equal(HttpStatusCode.OK, (await s.Host.Admin.PostAsync($"/api/v1/hosted-strategies/{a}/start", null)).StatusCode);

        var second = await s.Host.Admin.PostAsync($"/api/v1/hosted-strategies/{b}/start", null);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("at once", await second.Content.ReadAsStringAsync());
        await s.Host.Admin.PostAsync($"/api/v1/hosted-strategies/{a}/stop", null);
    }

    [PostgresFact]
    public async Task The_logs_endpoint_returns_the_end_of_the_log()
    {
        await using var s = await NewAsync();
        Script(s, "alpha.py", "for i in range(50):\n    print('line', i, flush=True)\nimport time\nwhile True:\n    time.sleep(0.2)\n");
        var id = await RegisterAsync(s);
        await s.Host.Admin.PostAsync($"/api/v1/hosted-strategies/{id}/start", null);
        await LogAsync(s, id, "line 49");

        var tail = await s.Host.Admin.GetFromJsonAsync<JsonObject>($"/api/v1/hosted-strategies/{id}/logs?lines=5");
        Assert.Equal(["line 45", "line 46", "line 47", "line 48", "line 49"], tail!["lines"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        await s.Host.Admin.PostAsync($"/api/v1/hosted-strategies/{id}/stop", null);
    }

    [PostgresFact]
    public async Task A_process_left_from_before_a_restart_is_stopped_and_the_row_corrected()
    {
        await using var s = await NewAsync();
        Script(s, "orphan.py", Loop);
        var id = await RegisterAsync(s, "orphan", "orphan.py");
        // A process the supervisor does not hold, as one left by an earlier run of the platform would be.
        var launcher = new OsProcessLauncher();
        var left = launcher.Launch(new ProcessLaunch("python3", ["-u", Path.Combine(s.Scripts, "orphan.py")], s.Scripts, new Dictionary<string, string> { ["PATH"] = Environment.GetEnvironmentVariable("PATH")! }, Path.Combine(s.Logs, "orphan.log")));
        // A process that has only just been forked still shows its parent's command line; one left by an earlier start of the platform has long since started.
        Assert.True(await WaitAsync(() => Task.FromResult(SafeCmd(left.Pid).Contains("orphan.py")), 10));
        await using (var db = s.Host.NewDb())
            await db.HostedStrategies.Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsRunning, true).SetProperty(x => x.ProcessId, left.Pid));

        await s.Host.Get<StrategySupervisor>().ReconcileAsync(default);

        Assert.True(await WaitAsync(() => Task.FromResult(left.HasExited), 10));
        var row = await RowAsync(s, id);
        Assert.False(row.IsRunning);
        Assert.Null(row.ProcessId);
    }

    [PostgresFact]
    public async Task A_reused_process_id_is_not_mistaken_for_the_strategy()
    {
        await using var s = await NewAsync();
        Script(s, "alpha.py", Loop);
        var id = await RegisterAsync(s);
        // Alive, but not running this script: another program that was given the old process id.
        var launcher = new OsProcessLauncher();
        Script(s, "other.py", Loop);
        var other = launcher.Launch(new ProcessLaunch("python3", ["-u", Path.Combine(s.Scripts, "other.py")], s.Scripts, new Dictionary<string, string> { ["PATH"] = Environment.GetEnvironmentVariable("PATH")! }, Path.Combine(s.Logs, "other.log")));
        try
        {
            Assert.True(await WaitAsync(() => Task.FromResult(SafeCmd(other.Pid).Contains("other.py")), 10));
            await using (var db = s.Host.NewDb())
                await db.HostedStrategies.Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsRunning, true).SetProperty(x => x.ProcessId, other.Pid));

            await s.Host.Get<StrategySupervisor>().ReconcileAsync(default);

            Assert.False(other.HasExited);
            Assert.False((await RowAsync(s, id)).IsRunning);
        }
        finally
        {
            other.Kill();
        }
    }

    [PostgresFact]
    public async Task The_reaper_corrects_a_row_marked_running_with_no_process()
    {
        await using var s = await NewAsync();
        Script(s, "alpha.py", Loop);
        var id = await RegisterAsync(s);
        await using (var db = s.Host.NewDb())
            await db.HostedStrategies.Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsRunning, true));
        Assert.True(await WaitAsync(async () => !(await RowAsync(s, id)).IsRunning, 10));
    }

    [PostgresFact]
    public async Task Engaging_an_accounts_kill_switch_stops_its_strategies_and_holds_them_stopped()
    {
        await using var s = await NewAsync();
        Script(s, "alpha.py", Reporter);
        var id = await RegisterAsync(s);
        await s.Host.Admin.PostAsync($"/api/v1/hosted-strategies/{id}/start", null);
        var key = Value(await LogAsync(s, id, "ENVNAMES="), "KEY");

        var kill = await s.Host.Admin.PostAsJsonAsync($"/api/v1/killswitch/account/{s.Account}", new { engage = true, reason = "test" });
        Assert.Equal(HttpStatusCode.OK, kill.StatusCode);

        var row = await RowAsync(s, id);
        Assert.False(row.IsRunning);
        Assert.True(row.ManuallyStopped);
        Assert.Null(await s.Host.Get<ApiKeyService>().ResolveCallAsync(key));
        Assert.Equal(0, s.Host.Get<StrategySupervisor>().RunningCount);
    }

    [PostgresFact]
    public async Task The_global_kill_switch_stops_every_running_strategy()
    {
        await using var s = await NewAsync();
        Script(s, "alpha.py", Loop);
        var a = await RegisterAsync(s, "a");
        var b = await RegisterAsync(s, "b");
        await s.Host.Admin.PostAsync($"/api/v1/hosted-strategies/{a}/start", null);
        await s.Host.Admin.PostAsync($"/api/v1/hosted-strategies/{b}/start", null);
        Assert.Equal(2, s.Host.Get<StrategySupervisor>().RunningCount);

        var kill = await s.Host.Admin.PostAsJsonAsync("/api/v1/killswitch/global", new { confirmation = "KILL ALL", reason = "test" });
        Assert.Equal(HttpStatusCode.OK, kill.StatusCode);

        Assert.Equal(0, s.Host.Get<StrategySupervisor>().RunningCount);
        Assert.All(new[] { a, b }, id => Assert.False(RowAsync(s, id).Result.IsRunning));
    }

    [PostgresFact]
    public async Task The_global_kill_switch_also_stops_a_strategy_whose_account_is_gone()
    {
        await using var s = await NewAsync();
        Script(s, "alpha.py", Loop);
        var id = await RegisterAsync(s);
        await s.Host.Admin.PostAsync($"/api/v1/hosted-strategies/{id}/start", null);
        // Its account was removed while it ran: no account's switch reaches it, only the global one does.
        await using (var db = s.Host.NewDb())
            await db.HostedStrategies.Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.AccountId, (Guid?)null));

        await s.Host.Admin.PostAsJsonAsync("/api/v1/killswitch/global", new { confirmation = "KILL ALL", reason = "test" });

        Assert.Equal(0, s.Host.Get<StrategySupervisor>().RunningCount);
        Assert.False((await RowAsync(s, id)).IsRunning);
    }

    [PostgresFact]
    public async Task Shutting_down_the_platform_stops_the_strategies_it_started()
    {
        var s = await NewAsync();
        Script(s, "alpha.py", Loop);
        var id = await RegisterAsync(s);
        await s.Host.Admin.PostAsync($"/api/v1/hosted-strategies/{id}/start", null);
        var pid = (await RowAsync(s, id)).ProcessId!.Value;

        await s.DisposeAsync();

        Assert.False(await ProcessAliveAsync(pid, "alpha.py"));
    }

    [PostgresFact]
    public async Task Two_callers_revoking_the_same_key_at_once_both_succeed_and_the_first_time_is_kept()
    {
        await using var s = await NewAsync();
        var account = await s.Host.CreateAccountAsync(AccountType.PAPER, "revoke-race");
        var keys = s.Host.Get<EPTradeBot.Infrastructure.Security.ApiKeyService>();
        var key = await keys.IssueAsync(account, "race");

        // A strategy's exit watcher and the platform shutting down both end the same run, so both revoke its key.
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => keys.RevokeAsync(account, key.Record.Id))));
        Assert.All(results, Assert.True);
        await using var db = s.Host.NewDb();
        var first = (await db.AccountApiKeys.AsNoTracking().SingleAsync(k => k.Id == key.Record.Id)).RevokedAtUtc;
        Assert.NotNull(first);
        Assert.True(await keys.RevokeAsync(account, key.Record.Id));
        Assert.Equal(first, (await db.AccountApiKeys.AsNoTracking().SingleAsync(k => k.Id == key.Record.Id)).RevokedAtUtc);
        Assert.False(await keys.RevokeAsync(account, Guid.NewGuid()));
    }
}
