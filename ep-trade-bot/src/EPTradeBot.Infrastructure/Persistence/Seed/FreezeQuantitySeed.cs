using System.Globalization;
using EPTradeBot.Core.Entities.Legacy.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EPTradeBot.Infrastructure.Persistence.Seed;

/// <summary>
/// Fills core.qty_freeze with NSE's F&amp;O freeze limits (OpenAlgo's data/qtyfreeze.csv, NFO) when
/// the table is empty, as OpenAlgo's ensure_qty_freeze_tables_exists does. A table that already
/// holds a row is left alone, so a limit an administrator set is never replaced. The kill switch
/// slices exits by these limits and the risk check reads them, so an empty table would send a
/// single exit above the exchange's limit, which the exchange refuses.
/// </summary>
public static class FreezeQuantitySeed
{
    private const string Resource = "EPTradeBot.Infrastructure.Persistence.Seed.qtyfreeze.csv";

    public static async Task SeedAsync(EpTradeDbContext db, ILogger logger, CancellationToken cancellationToken)
    {
        if (await db.Set<QtyFreeze>().AnyAsync(cancellationToken)) return;
        var rows = Read().ToList();
        db.Set<QtyFreeze>().AddRange(rows.Select(r => new QtyFreeze { Exchange = "NFO", Symbol = r.Symbol, FreezeQty = r.Quantity }));
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Loaded {Count} NFO freeze quantities.", rows.Count);
    }

    /// <summary>The seed's rows. Column names carry trailing spaces in NSE's file, so they are matched trimmed.</summary>
    public static IEnumerable<(string Symbol, int Quantity)> Read()
    {
        using var stream = typeof(FreezeQuantitySeed).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException("The freeze quantity seed is missing from the build.");
        using var reader = new StreamReader(stream);
        var header = reader.ReadLine()?.Split(',').Select(h => h.Trim().ToUpperInvariant()).ToArray() ?? [];
        var symbolColumn = Array.IndexOf(header, "SYMBOL");
        var quantityColumn = Array.FindIndex(header, h => h.Contains("FRZ", StringComparison.Ordinal) || h == "VOL_FRZ_QTY");
        if (symbolColumn < 0 || quantityColumn < 0) yield break;

        var seen = new HashSet<string>();
        while (reader.ReadLine() is { } line)
        {
            var cells = line.Split(',');
            if (cells.Length <= Math.Max(symbolColumn, quantityColumn)) continue;
            var symbol = cells[symbolColumn].Trim();
            if (symbol.Length == 0 || !int.TryParse(cells[quantityColumn].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantity)) continue;
            if (seen.Add(symbol)) yield return (symbol, quantity);
        }
    }
}
