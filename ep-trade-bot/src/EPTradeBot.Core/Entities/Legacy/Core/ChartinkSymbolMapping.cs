// Generated from OpenAlgo table 'chartink_symbol_mappings' (database.chartink_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>chartink_symbol_mappings</c>.</summary>
public sealed class ChartinkSymbolMapping
{
    public int Id { get; set; }
    public int StrategyId { get; set; }
    public string ChartinkSymbol { get; set; } = "";
    public string Exchange { get; set; } = "";
    public int Quantity { get; set; }
    public string ProductType { get; set; } = "";
    public DateTime? CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
