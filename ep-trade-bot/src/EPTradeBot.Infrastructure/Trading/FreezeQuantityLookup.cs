using System.Text.RegularExpressions;
using EPTradeBot.Core.Trading;
using EPTradeBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EPTradeBot.Infrastructure.Trading;

/// <summary>
/// Port of OpenAlgo get_freeze_qty_for_option: resolve the underlying (known indices
/// longest prefix first, else the leading letters) and read core.qty_freeze.
/// No row means 0, "no limit known", never 1.
/// </summary>
public sealed partial class FreezeQuantityLookup(IDbContextFactory<EpTradeDbContext> contexts) : IFreezeQuantityLookup
{
    private static readonly string[] IndexSymbols = ["BANKNIFTY", "FINNIFTY", "MIDCPNIFTY", "NIFTYNXT50", "NIFTYFPI", "NIFTY", "SENSEX50", "BANKEX", "SENSEX"];

    public async Task<int> GetAsync(string exchange, string symbol, CancellationToken cancellationToken = default)
    {
        var underlying = Underlying(symbol);
        if (underlying is null) return 0;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Set<EPTradeBot.Core.Entities.Legacy.Core.QtyFreeze>().AsNoTracking()
            .Where(f => f.Exchange == exchange && f.Symbol == underlying)
            .Select(f => f.FreezeQty)
            .FirstOrDefaultAsync(cancellationToken);
    }

    internal static string? Underlying(string symbol)
    {
        var upper = symbol.ToUpperInvariant();
        foreach (var idx in IndexSymbols)
        {
            if (upper.StartsWith(idx, StringComparison.Ordinal)) return idx;
        }

        var m = Leading().Match(upper);
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex("^([A-Z&-]+)")]
    private static partial Regex Leading();
}
