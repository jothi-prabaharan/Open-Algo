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
