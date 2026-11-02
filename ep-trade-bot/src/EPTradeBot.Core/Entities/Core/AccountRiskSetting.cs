using System.ComponentModel.DataAnnotations;

namespace EPTradeBot.Core.Entities.Core;

/// <summary>Per-account risk limits, checked before every order is routed.</summary>
public sealed class AccountRiskSetting
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid AccountId { get; set; }

    /// <summary>Maximum intraday loss in INR (realised plus marked-to-market) before new orders are refused.</summary>
    [Range(typeof(decimal), "0", "1000000000")]
    public decimal MaxDailyLoss { get; set; }

    /// <summary>Maximum quantity of a single order leg. The exchange freeze limit applies on top.</summary>
    [Range(1, int.MaxValue)]
    public int MaxOrderQuantity { get; set; }

    /// <summary>Maximum gross open-position value in INR after the order.</summary>
    [Range(typeof(decimal), "0", "100000000000")]
    public decimal MaxPositionValue { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    [StringLength(80)]
    public string? UpdatedBy { get; set; }

    public uint Version { get; set; }
}
