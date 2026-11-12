using System.ComponentModel.DataAnnotations;

namespace EPTradeBot.Core.Configuration;

/// <summary>Bound from <c>Database</c>. Validated at startup.</summary>
public sealed class DatabaseOptions
{
    public const string Section = "Database";

    [Required(AllowEmptyStrings = false)]
    public string ConnectionString { get; set; } = "";

    /// <summary>Apply pending EF Core migrations before the host starts listening.</summary>
    public bool MigrateOnStartup { get; set; } = true;

    [Range(1, 60)]
    public int MigrationRetryCount { get; set; } = 10;

    [Range(1, 300)]
    public int CommandTimeoutSeconds { get; set; } = 30;
}

/// <summary>Bound from <c>Sandbox</c>.</summary>
public sealed class SandboxOptions
{
    public const string Section = "Sandbox";

    /// <summary>Starting capital of each PAPER account, INR. Default 1 crore.</summary>
    [Range(typeof(decimal), "1", "100000000000")]
    public decimal StartingCapital { get; set; } = 10_000_000m;
}

/// <summary>Bound from <c>Security</c>.</summary>
public sealed class SecurityOptions
{
    public const string Section = "Security";

    /// <summary>Application key used to derive the encryption key for stored broker and LLM secrets.</summary>
    [Required(AllowEmptyStrings = false), StringLength(256, MinimumLength = 32)]
    public string AppKey { get; set; } = "";

    /// <summary>Pepper mixed into API key hashes.</summary>
    [Required(AllowEmptyStrings = false), StringLength(256, MinimumLength = 32)]
    public string ApiKeyPepper { get; set; } = "";

    [Range(100_000, 10_000_000)]
    public int Pbkdf2Iterations { get; set; } = 600_000;
}

/// <summary>Bound from <c>Streaming</c>.</summary>
public sealed class StreamingOptions
{
    public const string Section = "Streaming";

    [Range(1024, 65535)]
    public int WebSocketPort { get; set; } = 8765;

    /// <summary>The bus port. Fixed by configuration; it never drifts to another port at runtime.</summary>
    [Range(1024, 65535)]
    public int BusPort { get; set; } = 5555;

    /// <summary>Address the WebSocket proxy listens on. Loopback by default: put it behind the reverse proxy to expose it.</summary>
    [Required(AllowEmptyStrings = false)]
    public string WebSocketHost { get; set; } = "127.0.0.1";

    /// <summary>Address of the market-data bus. The proxy's SUB socket is the only one that binds it; every publisher connects.</summary>
    [Required(AllowEmptyStrings = false)]
    public string BusHost { get; set; } = "127.0.0.1";

    /// <summary>Starts the bus and the proxy. Off only in tests that do not need them.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Connects broker market-data feeds on demand. Off only in tests that publish to the bus themselves.</summary>
    public bool FeedsEnabled { get; set; } = true;

    /// <summary>The first wait before a feed reconnects; it grows by half again after every failure up to a minute.</summary>
    [Range(1, 60_000)]
    public int FeedReconnectBaseMs { get; set; } = 2000;

    /// <summary>A feed that hears nothing for this long is treated as dead and reconnected.</summary>
    [Range(1, 3600)]
    public int FeedStallSeconds { get; set; } = 90;

    /// <summary>A feed that does not connect in this long is retried.</summary>
    [Range(1, 120)]
    public int FeedConnectSeconds { get; set; } = 15;

    /// <summary>Instruments one account may hold on a broker feed (Kite allows 3000 on a connection).</summary>
    [Range(1, 10_000)]
    public int FeedMaxInstruments { get; set; } = 3000;
}

/// <summary>
/// Bound from <c>Management</c>. Until the login of Task Group 5 lands, the
/// account, risk, order and kill-switch endpoints require this token in the
/// <c>X-Admin-Token</c> header. Startup fails without it.
/// </summary>
public sealed class ManagementOptions
{
    public const string Section = "Management";

    [Required(AllowEmptyStrings = false), StringLength(256, MinimumLength = 32, ErrorMessage = "Management:AdminToken must be at least 32 characters.")]
    public string AdminToken { get; set; } = "";
}

/// <summary>Which broker plugins load, and the keys each one reads when it is called.</summary>
public sealed class BrokerOptions
{
    public const string Section = "Brokers";

    /// <summary>Plugin names to enable (OpenAlgo's VALID_BROKERS). A plugin not listed is not offered.</summary>
    public List<string> Enabled { get; set; } = [];

    /// <summary>Per broker, the application keys it needs (for example ApiKey, ApiSecret). Read on every call.</summary>
    public Dictionary<string, Dictionary<string, string>> Keys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional per-broker base address override, for a broker's own test environment.</summary>
    public Dictionary<string, string> BaseUrls { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional per-broker market-data feed address override, for a broker's own test environment.</summary>
    public Dictionary<string, string> FeedUrls { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional per-broker pause, in milliseconds, between that broker's history calls. Absent means the broker's own limit, as OpenAlgo paces it.</summary>
    public Dictionary<string, int> HistoryGapMs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional per-broker wait, in milliseconds, between the retries of a refused or rate-limited call. Absent means the broker's own backoff, as OpenAlgo waits.</summary>
    public Dictionary<string, int> RetryBackoffMs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [System.ComponentModel.DataAnnotations.Range(1, 120)]
    public int TimeoutSeconds { get; set; } = 15;
}

/// <summary>Bound from <c>Scheduling</c>.</summary>
public sealed class SchedulingOptions
{
    public const string Section = "Scheduling";

    /// <summary>Starts the scheduler. Off only in tests that read the schedule without running it.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>Bound from <c>Strategies</c>.</summary>
public sealed class StrategyHostOptions
{
    public const string Section = "Strategies";

    /// <summary>The folder scripts live in; a hosted strategy's script path is relative to it.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Directory { get; set; } = "strategies";

    /// <summary>The Python interpreter a script is run with.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Interpreter { get; set; } = "python3";

    [Required(AllowEmptyStrings = false)]
    public string LogDirectory { get; set; } = "log/strategies";

    /// <summary>The address a strategy calls back to place its orders.</summary>
    [Required(AllowEmptyStrings = false)]
    public string HostUrl { get; set; } = "http://127.0.0.1:5000";

    /// <summary>How long a strategy is given to stop after the request to stop, before it is ended.</summary>
    [Range(1, 300)]
    public int StopGraceSeconds { get; set; } = 10;

    /// <summary>How often the supervisor checks that what is marked running is running.</summary>
    [Range(1, 600)]
    public int ReapSeconds { get; set; } = 15;

    [Range(1, 200)]
    public int MaxRunning { get; set; } = 20;

    /// <summary>Starts the supervisor in place of the placeholder controller. Off only in tests that supply their own.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>Bound from <c>Agent</c>.</summary>
public sealed class AgentOptions
{
    public const string Section = "Agent";

    /// <summary>The most rounds of tool calls one question may take before the assistant must answer.</summary>
    [Range(1, 20)]
    public int MaxToolRounds { get; set; } = 8;

    /// <summary>The most orders the assistant may propose in one answer, so a confused model cannot flood the Action Center.</summary>
    [Range(0, 20)]
    public int MaxProposalsPerAnswer { get; set; } = 3;

    [Range(5, 600)]
    public int TimeoutSeconds { get; set; } = 90;
}

/// <summary>Bound from <c>Mcp</c>.</summary>
public sealed class McpOptions
{
    public const string Section = "Mcp";

    /// <summary>How the outside world addresses this install, e.g. https://bot.example.com. Left empty, the address of the request is used.</summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>Browser origins allowed to call /mcp (e.g. https://claude.ai). A request from any other browser origin is refused; one with no origin (a desktop client) is not affected.</summary>
    public List<string> AllowedOrigins { get; set; } = [];

    [Range(5, 1440)]
    public int AccessTokenMinutes { get; set; } = 60;

    [Range(1, 365)]
    public int RefreshTokenDays { get; set; } = 30;

    [Range(1, 500)]
    public int MaxClients { get; set; } = 50;

    [Range(1, 600)]
    public int ReadsPerMinute { get; set; } = 60;

    [Range(1, 600)]
    public int WritesPerMinute { get; set; } = 5;

    /// <summary>Only the tools that read are offered over HTTP.</summary>
    public bool ReadOnly { get; set; }
}
