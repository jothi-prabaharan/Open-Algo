using System.ComponentModel.DataAnnotations;

namespace EPTradeBot.Core.Entities.Core;

/// <summary>
/// An /api/v1 key. Each key belongs to exactly one account, so a request names the
/// account it trades by the key it carries. Only a keyed hash (HMAC-SHA256 with the
/// install's pepper) is stored; the key itself is shown once, when it is created.
/// </summary>
public sealed class AccountApiKey
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid AccountId { get; set; }

    /// <summary>Lowercase hex HMAC-SHA256 of the key.</summary>
    [Required, StringLength(64, MinimumLength = 64)]
    public string KeyHash { get; set; } = "";

    [Required, StringLength(64, MinimumLength = 1)]
    public string Label { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? RevokedAtUtc { get; set; }

    public uint Version { get; set; }
}
