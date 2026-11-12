using System.ComponentModel.DataAnnotations;

namespace EPTradeBot.Core.Entities.Core;

/// <summary>An application (Claude, Cursor, ...) that registered itself to ask for access over MCP. Registering grants nothing; the trader's approval does.</summary>
public sealed class McpClient
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    [Required, StringLength(64, MinimumLength = 8)]
    public string ClientId { get; set; } = "";

    [Required, StringLength(100, MinimumLength = 1)]
    public string Name { get; set; } = "";

    /// <summary>The addresses it may be sent back to after approval, as a JSON array. An approval is only ever sent to one of these, exactly.</summary>
    [Required]
    public string RedirectUrisJson { get; set; } = "[]";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>An approval waiting to be exchanged for tokens: single use, short lived, kept only as a hash.</summary>
public sealed class McpAuthorizationCode
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    [Required, StringLength(64, MinimumLength = 64)]
    public string CodeHash { get; set; } = "";

    [Required, StringLength(64)]
    public string ClientId { get; set; } = "";

    public Guid AccountId { get; set; }

    [Required, StringLength(100)]
    public string Scope { get; set; } = "";

    [Required, RegularExpression("^(auto|semi_auto)$")]
    public string OrderMode { get; set; } = "semi_auto";

    [Required, StringLength(500)]
    public string RedirectUri { get; set; } = "";

    [Required, StringLength(128)]
    public string CodeChallenge { get; set; } = "";

    public DateTime ExpiresAtUtc { get; set; }

    public DateTime? UsedAtUtc { get; set; }
}

/// <summary>What an approved application holds: access for an account, in a scope and an order mode, revocable at any time.</summary>
public sealed class McpToken
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    [Required, StringLength(64)]
    public string ClientId { get; set; } = "";

    public Guid AccountId { get; set; }

    [Required, StringLength(100)]
    public string Scope { get; set; } = "";

    /// <summary>auto sends the application's orders; semi_auto holds them in the Action Center for approval.</summary>
    [Required, RegularExpression("^(auto|semi_auto)$")]
    public string OrderMode { get; set; } = "semi_auto";

    [Required, StringLength(64, MinimumLength = 64)]
    public string AccessHash { get; set; } = "";

    [Required, StringLength(64, MinimumLength = 64)]
    public string RefreshHash { get; set; } = "";

    /// <summary>The refresh token it replaced. Presenting it again means a copy of it was kept, so the whole grant is revoked.</summary>
    [StringLength(64)]
    public string? PreviousRefreshHash { get; set; }

    public DateTime AccessExpiresAtUtc { get; set; }

    public DateTime RefreshExpiresAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? RevokedAtUtc { get; set; }

    public DateTime? LastUsedAtUtc { get; set; }
}

/// <summary>One tool call made over MCP: who, what, when and how it went. The arguments are kept only as a fingerprint.</summary>
public sealed class McpCallRecord
{
    public long Id { get; set; }

    public DateTime AtUtc { get; set; }

    [Required, StringLength(64)]
    public string ClientId { get; set; } = "";

    [Required, StringLength(64)]
    public string TokenId { get; set; } = "";

    [Required, StringLength(64)]
    public string Tool { get; set; } = "";

    [Required, StringLength(20)]
    public string Scope { get; set; } = "";

    [Required, StringLength(32)]
    public string ParamsHash { get; set; } = "";

    public long DurationMs { get; set; }

    [Required, StringLength(20)]
    public string Outcome { get; set; } = "";
}
