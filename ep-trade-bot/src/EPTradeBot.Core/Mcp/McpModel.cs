using System.Text.Json.Nodes;
using EPTradeBot.Core.Flow;

namespace EPTradeBot.Core.Mcp;

/// <summary>What a tool call is running as: how to reach the platform, who is calling, and what they may do.</summary>
public sealed class McpSession
{
    public const string ReadScope = "mcp:read";
    public const string WriteScope = "mcp:write";

    public required IFlowApi Api { get; init; }

    /// <summary>Who is calling (an OAuth client id, or "stdio").</summary>
    public string ClientId { get; init; } = "stdio";

    /// <summary>The token the call came with, so its rate is counted; "stdio" for a local process.</summary>
    public string TokenId { get; init; } = "stdio";

    public IReadOnlySet<string> Scopes { get; init; } = new HashSet<string> { ReadScope, WriteScope };

    public bool Can(string scope) => Scopes.Contains(scope);

    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;
}

/// <summary>Which tools a server shows and runs.</summary>
public sealed class McpPolicy
{
    /// <summary>Only the tools that read. Writes are neither listed nor run.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>When set, only tools of these groups (orders, account, marketdata, research, system).</summary>
    public IReadOnlySet<string>? Toolsets { get; init; }

    public int ReadsPerMinute { get; init; } = 60;

    public int WritesPerMinute { get; init; } = 5;

    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed record McpAuditEntry(DateTime AtUtc, string ClientId, string TokenId, string Tool, string Scope, string ParamsHash, long DurationMs, string Outcome);

/// <summary>A tool: its name and schema as a client sees them, and what it does when called.</summary>
public sealed record McpTool(
    string Name,
    string Description,
    string Toolset,
    bool ReadOnly,
    bool Destructive,
    JsonObject Schema,
    Func<JsonObject, McpSession, CancellationToken, Task<JsonObject>> Run)
{
    public string Scope => ReadOnly ? McpSession.ReadScope : McpSession.WriteScope;
}

/// <summary>The answer to one JSON-RPC message, and how an HTTP transport should carry it.</summary>
public sealed record McpOutcome(JsonNode? Response, int HttpStatus = 200, string? ScopeError = null)
{
    public static readonly McpOutcome None = new((JsonNode?)null);
}
