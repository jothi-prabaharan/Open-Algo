// Generated from OpenAlgo table 'watchlist_items' (database.watchlist_db). Maintained by hand from here on.
namespace EPTradeBot.Core.Entities.Legacy.Core;

/// <summary>Port of OpenAlgo table <c>watchlist_items</c>.</summary>
public sealed class WatchlistItem
{
    public int Id { get; set; }
    public int WatchlistId { get; set; }
    public string Symbol { get; set; } = "";
    public string Exchange { get; set; } = "";
    public int Position { get; set; } = 0;
    public DateTime? CreatedAt { get; set; } = DateTime.UtcNow;
}
