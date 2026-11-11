namespace EPTradeBot.Core.Scheduling;

/// <summary>Where a hosted strategy's script may live. Pure: it looks only at the text of the path.</summary>
public static class StrategyPathRules
{
    /// <summary>
    /// A script path is a relative path to a .py file under the strategies directory: no root, no drive, no ".." step, no NUL or backslash
    /// (so it means the same on every platform), and not hidden. The directory check on the real file system is the supervisor's job.
    /// </summary>
    public static bool IsSafeRelative(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 260) return false;
        if (path.Contains('\0') || path.Contains('\\') || path.StartsWith('/') || path.Contains(':')) return false;
        if (!path.EndsWith(".py", StringComparison.Ordinal)) return false;
        return path.Split('/').All(part => part.Length > 0 && part != "." && part != ".." && !part.StartsWith('.'));
    }
}

/// <summary>The environment a strategy process is given.</summary>
public static class StrategyEnvironment
{
    /// <summary>
    /// The only variables copied from the platform's own environment. Everything else (the database connection string, the application key, the
    /// API-key pepper, the admin token, broker credentials) stays with the platform: a strategy is user code and gets what it needs to trade and nothing more.
    /// </summary>
    public static readonly IReadOnlyList<string> Inherited = ["PATH", "HOME", "LANG", "LC_ALL", "TZ", "VIRTUAL_ENV", "SYSTEMROOT"];

    public sealed record Inputs(Guid StrategyId, string Name, string Exchange, string ExecutionMode, string HostUrl, string? ApiKey);

    public static Dictionary<string, string> Build(IReadOnlyDictionary<string, string> platform, Inputs i)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in Inherited)
        {
            if (platform.TryGetValue(name, out var value)) env[name] = value;
        }

        env["PYTHONUNBUFFERED"] = "1";
        env["STRATEGY_ID"] = i.StrategyId.ToString("N");
        env["STRATEGY_NAME"] = i.Name;
        env["EPTRADE_STRATEGY_EXCHANGE"] = i.Exchange;
        env["EPTRADE_EXECUTION_MODE"] = i.ExecutionMode;
        env["EPTRADE_HOST"] = i.HostUrl;
        // The names OpenAlgo's scripts and its Python SDK read, so a script written there runs here unchanged.
        env["OPENALGO_HOST"] = i.HostUrl;
        env["OPENALGO_STRATEGY_EXCHANGE"] = i.Exchange;
        if (i.ApiKey is not null)
        {
            env["EPTRADE_API_KEY"] = i.ApiKey;
            env["OPENALGO_API_KEY"] = i.ApiKey;
        }

        return env;
    }

    /// <summary>The log file name for a strategy: a safe form of its name plus its id, so two names that sanitise alike never share a file.</summary>
    public static string LogFileName(string name, Guid id)
    {
        var safe = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        return $"{(safe.Length > 40 ? safe[..40] : safe)}-{id:N}.log";
    }
}
