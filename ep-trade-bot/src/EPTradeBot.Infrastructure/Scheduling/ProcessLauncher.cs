using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EPTradeBot.Infrastructure.Scheduling;

public sealed record ProcessLaunch(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment, string LogPath);

/// <summary>A process the platform started and is responsible for.</summary>
public interface IRunningProcess : IDisposable
{
    int Pid { get; }

    bool HasExited { get; }

    int? ExitCode { get; }

    /// <summary>Completes when the process has exited.</summary>
    Task Exited { get; }

    /// <summary>Asks the process to stop (SIGTERM).</summary>
    void Terminate();

    /// <summary>Ends the process now (SIGKILL).</summary>
    void Kill();
}

public interface IProcessLauncher
{
    IRunningProcess Launch(ProcessLaunch launch);

    /// <summary>Whether a process with this id is alive and its command line mentions the given text (so a reused id is not mistaken for ours).</summary>
    bool IsAliveAndMatches(int pid, string commandLineContains);

    void TerminateByPid(int pid);
}

/// <summary>
/// Starts processes with the operating system. The output goes to a log file through the shell's own redirection, not through a pipe the platform
/// would have to keep draining, and the process's environment is exactly the one given: nothing is inherited.
/// </summary>
public sealed partial class OsProcessLauncher : IProcessLauncher
{
    public IRunningProcess Launch(ProcessLaunch launch)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(launch.LogPath))!);
        var info = new ProcessStartInfo("/bin/sh")
        {
            WorkingDirectory = launch.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        // exec replaces the shell, so the process id is the strategy's own. The arguments are positional parameters, never parsed as shell text.
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("log=$1; shift; exec \"$@\" >>\"$log\" 2>&1");
        info.ArgumentList.Add("sh");
        info.ArgumentList.Add(Path.GetFullPath(launch.LogPath));
        info.ArgumentList.Add(launch.Executable);
        foreach (var a in launch.Arguments) info.ArgumentList.Add(a);
        info.Environment.Clear();
        foreach (var (k, v) in launch.Environment) info.Environment[k] = v;

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult();
        process.Start();
        if (process.HasExited) exited.TrySetResult();
        return new OsProcess(process, exited.Task);
    }

    public bool IsAliveAndMatches(int pid, string commandLineContains)
    {
        try
        {
            var cmdline = File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ');
            return cmdline.Contains(commandLineContains, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void TerminateByPid(int pid) => Signal(pid, 15);

    internal static void Signal(int pid, int signal)
    {
        if (OperatingSystem.IsWindows()) return;
        _ = SysKill(pid, signal);
    }

#pragma warning disable SYSLIB1054 // a two-integer call: no marshalling to generate, and the generator would need unsafe code switched on for the whole project
    [DllImport("libc", EntryPoint = "kill")]
    private static extern int SysKill(int pid, int signal);
#pragma warning restore SYSLIB1054

    private sealed class OsProcess(Process process, Task exited) : IRunningProcess
    {
        public int Pid { get; } = process.Id;

        public bool HasExited => process.HasExited;

        public int? ExitCode => process.HasExited ? process.ExitCode : null;

        public Task Exited { get; } = exited;

        public void Terminate()
        {
            if (!process.HasExited) Signal(Pid, 15);
        }

        public void Kill()
        {
            if (!process.HasExited) process.Kill();
        }

        public void Dispose() => process.Dispose();
    }
}
