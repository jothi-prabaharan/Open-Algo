namespace EPTradeBot.Core.Brokers.Zerodha;

/// <summary>A quantity that cannot be written as a whole number of MCX contracts.</summary>
public sealed class McxQuantityException(string message) : Exception(message);

/// <summary>
/// MCX lot sizes and the unit conversion Kite needs because of them. Port of OpenAlgo's broker/zerodha/mapping/mcx_contract_size.py.
/// <para>
/// Kite denominates MCX order quantity in contracts (one contract is quantity 1), and every other Indian broker, and OpenAlgo's own API,
/// in units (one lot of CRUDEOIL is 100). The real lot is held in the master contract, so an order leaves for Kite as units divided by
/// the lot and a quantity read from Kite is multiplied back. The conversion belongs at the Kite boundary and nowhere else: applying it
/// twice places a 100 times larger order or a zero one.
/// </para>
/// Source of the table: Zerodha's commodity margin calculator, scraped 2026-09-02 and checked against Angel One's scrip master.
/// </summary>
public static class McxContracts
{
    /// <summary>Units of the commodity in one contract, by underlying.</summary>
    public static readonly IReadOnlyDictionary<string, int> Sizes = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["ALUMINI"] = 1, ["ALUMINIUM"] = 5, ["CARDAMOM"] = 100, ["COPPER"] = 2500, ["COTTON"] = 25, ["COTTONOIL"] = 5, ["CRUDEOIL"] = 100,
        ["CRUDEOILM"] = 10, ["ELECDMBL"] = 50, ["GOLD"] = 1, ["GOLDGUINEA"] = 8, ["GOLDM"] = 100, ["GOLDPETAL"] = 1, ["GOLDTEN"] = 10, ["KAPAS"] = 4,
        ["LEAD"] = 5, ["LEADMINI"] = 1, ["MCXBULLDEX"] = 30, ["MCXMETLDEX"] = 40, ["MENTHAOIL"] = 360, ["NATGASMINI"] = 250, ["NATURALGAS"] = 1250,
        ["NICKEL"] = 250, ["SILVER"] = 30, ["SILVER100"] = 100, ["SILVERM"] = 5, ["SILVERMIC"] = 1, ["STEELREBAR"] = 5, ["ZINC"] = 5, ["ZINCMINI"] = 1,
    };

    /// <summary>Roots MCX has revised: both sizes are live at once, so only the master contract's row for the expiry can say which.</summary>
    public static readonly IReadOnlySet<string> Revised = new HashSet<string>(StringComparer.Ordinal) { "MCXBULLDEX" };

    /// <summary>What one contract's price must be multiplied by to value it in rupees, where the quotation unit is not the contract unit.</summary>
    public static readonly IReadOnlyDictionary<string, int> QuotationMultipliers = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["GOLD"] = 100, ["GOLDM"] = 10, ["GOLDGUINEA"] = 1, ["GOLDTEN"] = 1, ["SILVER100"] = 10, ["ZINC"] = 5000, ["ZINCMINI"] = 1000,
        ["LEAD"] = 5000, ["LEADMINI"] = 1000, ["ALUMINIUM"] = 5000, ["ALUMINI"] = 1000, ["KAPAS"] = 200, ["COTTONOIL"] = 500,
    };

    private static readonly (string Root, int Size)[] RootsLongestFirst = [.. Sizes.OrderByDescending(kv => kv.Key.Length).Select(kv => (kv.Key, kv.Value))];

    /// <summary>The underlying a symbol belongs to, resolved longest first so GOLDPETAL is not read as GOLD and SILVERMIC not as SILVER.</summary>
    public static string? Root(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;
        var text = symbol.Trim().ToUpperInvariant();
        foreach (var (root, _) in RootsLongestFirst) if (text.StartsWith(root, StringComparison.Ordinal)) return root;
        return null;
    }

    /// <summary>
    /// Units per contract: the lot the master contract holds for the exact contract, else the table. Null when it cannot be known: a root MCX
    /// has revised, with no master row. An underlying in neither source converts by 1, as OpenAlgo does.
    /// </summary>
    public static int? UnitsPerContract(string? symbol, int masterLotSize)
    {
        if (masterLotSize > 0) return masterLotSize;
        var root = Root(symbol);
        if (root is null) return 1;
        return Revised.Contains(root) ? null : Sizes[root];
    }

    /// <summary>OpenAlgo units to the contract count Kite expects. Refused, not rounded, when the quantity is not a whole number of contracts.</summary>
    public static int ToKite(int quantity, string symbol, int masterLotSize, string field = "Quantity")
    {
        var size = UnitsPerContract(symbol, masterLotSize)
            ?? throw new McxQuantityException($"Cannot size {symbol}: MCX has revised its contract size and the master contract has no row for this expiry. Re-download the master contract, then retry.");
        if (size == 1) return quantity;
        var contracts = Math.DivRem(Math.Abs(quantity), size, out var remainder);
        if (remainder != 0) throw new McxQuantityException($"{field} must be in multiples of lot size {size} for {symbol}, got {Math.Abs(quantity)}");
        return quantity < 0 ? -contracts : contracts;
    }

    /// <summary>Kite's contract count to OpenAlgo units, for display. A size that cannot be known passes the quantity through.</summary>
    public static long FromKite(long quantity, string? symbol, int masterLotSize) => UnitsPerContract(symbol, masterLotSize) is { } size && size != 1 ? quantity * size : quantity;

    /// <summary>The rupee value of one unit's price: 1 off the table, else the quotation multiplier (or the contract size where the two agree).</summary>
    public static int PriceMultiplier(string? symbol, int masterLotSize)
    {
        var root = Root(symbol);
        if (root is null) return 1;
        return QuotationMultipliers.TryGetValue(root, out var m) ? m : UnitsPerContract(symbol, masterLotSize) ?? 1;
    }
}
