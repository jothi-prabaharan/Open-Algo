// Generated from OpenAlgo table 'leverage_config' (database.leverage_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>leverage_config</c>.</summary>
public sealed class LeverageConfig
{
    public int Id { get; set; } = 1;
    public double Leverage { get; set; } = 0.0;
    public DateTime? UpdatedAt { get; set; } = DateTime.UtcNow;
}
