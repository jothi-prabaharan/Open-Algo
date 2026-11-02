using System.ComponentModel.DataAnnotations;

namespace EPTradeBot.Core.Entities.Core;

/// <summary>
/// A broker account the platform can route orders to. Every account is explicitly
/// classified LIVE or PAPER; there is no default that could place a real order by accident.
/// </summary>
public sealed class BrokerAccount
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    [Required, StringLength(64, MinimumLength = 1)]
    public string DisplayName { get; set; } = "";

    /// <summary>Broker plugin id, e.g. <c>zerodha</c>. For PAPER accounts, <c>sandbox</c>.</summary>
    [Required, StringLength(32, MinimumLength = 2), RegularExpression("^[a-z0-9_]+$")]
    public string Broker { get; set; } = "";

    /// <summary>Broker client / user id, as the broker reports it.</summary>
    [StringLength(64)]
    public string? ClientId { get; set; }

    [Required, EnumDataType(typeof(AccountType))]
    public AccountType AccountType { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>Encrypted with the platform key (AES-GCM, key derived with PBKDF2). Never plaintext.</summary>
    public string? AuthTokenEncrypted { get; set; }

    public string? FeedTokenEncrypted { get; set; }

    public DateTime? TokenIssuedAtUtc { get; set; }

    public bool KillSwitchEngaged { get; set; }

    public DateTime? KillSwitchEngagedAtUtc { get; set; }

    [StringLength(500)]
    public string? KillSwitchReason { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; set; }

    public AccountRiskSetting? RiskSetting { get; set; }
}
