namespace EPTradeBot.Core.Portfolio;

/// <summary>One line on a contract note: what it is charged on (turnover, the buy leg, the sell leg, or each order) and whether tax applies to it.</summary>
public sealed record Charge(string Key, string Label, string Basis, double Rate = 0, double Flat = 0, double Cap = 0, bool Taxed = false)
{
    private static readonly HashSet<string> Bases = ["turnover", "buy", "sell", "order"];

    public Charge Validate()
    {
        if (!Bases.Contains(Basis)) throw new ArgumentException($"unknown charge basis '{Basis}'; expected one of {string.Join(", ", Bases.Order())}");
        foreach (var (name, value) in new[] { ("rate", Rate), ("flat", Flat), ("cap", Cap) })
            if (!double.IsFinite(value) || value < 0) throw new ArgumentException($"charge {name} must be finite and non-negative");
        return this;
    }

    public double Amount(double buyValue, double sellValue, int orders)
    {
        switch (Basis)
        {
            case "turnover": return (buyValue + sellValue) * Rate;
            case "buy": return buyValue * Rate;
            case "sell": return sellValue * Rate;
            default:
                if (orders <= 0) return 0.0;
                if (Flat > 0) return Flat * orders;
                var perOrder = (buyValue + sellValue) / orders;
                var charged = perOrder * Rate;
                if (Cap > 0) charged = Math.Min(charged, Cap);
                return charged * orders;
        }
    }
}

/// <summary>A market's charges plus the tax on the taxable ones. OpenAlgo's portfolio.costs.CostSchedule.</summary>
/// <summary>What the SIP and portfolio engines need from a cost model: the total for a trade and its itemisation.</summary>
public interface ITradeCosts
{
    double TotalFor(double buyValue, double sellValue, int orders);

    IReadOnlyList<KeyValuePair<string, double>> Itemise(double buyValue, double sellValue, int orders);
}

/// <summary>A flat model: basis points of traded value plus a slippage fraction, with nothing itemised.</summary>
public sealed record FlatCosts(double Bps = 0, double Slippage = 0) : ITradeCosts
{
    public FlatCosts Validate()
    {
        foreach (var (name, value) in new[] { ("bps", Bps), ("slippage", Slippage) })
            if (!double.IsFinite(value) || value < 0) throw new ArgumentException($"flat cost {name} must be finite and non-negative");
        return this;
    }

    public double Total => (Bps / 10_000.0) + Slippage;

    public double TotalFor(double buyValue, double sellValue, int orders) => (buyValue + sellValue) * Total;

    public IReadOnlyList<KeyValuePair<string, double>> Itemise(double buyValue, double sellValue, int orders) => [];
}

public sealed record CostSchedule(string Name, IReadOnlyList<Charge> Charges, double TaxRate = 0, double Slippage = 0, string Currency = "INR", string TaxLabel = "GST") : ITradeCosts
{
    public double TotalFor(double buyValue, double sellValue, int orders) => Charge(buyValue, sellValue, orders);

    public IReadOnlyList<KeyValuePair<string, double>> Itemise(double buyValue, double sellValue, int orders) => Breakdown(buyValue, sellValue, orders);

    public CostSchedule Validate()
    {
        foreach (var (name, value) in new[] { ("tax_rate", TaxRate), ("slippage", Slippage) })
            if (!double.IsFinite(value) || value < 0) throw new ArgumentException($"cost schedule {name} must be finite and non-negative");
        return this;
    }

    /// <summary>Every line, the tax, the slippage and the total, in that order, then the order count.</summary>
    public IReadOnlyList<KeyValuePair<string, double>> Breakdown(double buyValue, double sellValue, int orders = 0)
    {
        var lines = new List<KeyValuePair<string, double>>();
        var taxable = 0.0;
        foreach (var charge in Charges)
        {
            var value = charge.Amount(buyValue, sellValue, orders);
            lines.Add(new(charge.Key, value));
            if (charge.Taxed) taxable += value;
        }

        lines.Add(new("tax", taxable * TaxRate));
        lines.Add(new("slippage", (buyValue + sellValue) * Slippage));
        var total = 0.0;
        foreach (var l in lines) total += l.Value;
        lines.Add(new("total", total));
        lines.Add(new("orders", orders));
        return lines;
    }

    public double Charge(double buyValue, double sellValue, int orders = 0) => Breakdown(buyValue, sellValue, orders).First(l => l.Key == "total").Value;

    /// <summary>Edits per charge key ("rate", "flat", "cap"); unknown keys are ignored so a saved portfolio survives a schedule that gains or loses a line.</summary>
    public CostSchedule WithOverrides(IReadOnlyDictionary<string, IReadOnlyDictionary<string, double?>> overrides)
    {
        var updated = new List<Charge>();
        foreach (var charge in Charges)
        {
            if (!overrides.TryGetValue(charge.Key, out var patch) || patch.Count == 0) { updated.Add(charge); continue; }
            var c = charge;
            var any = false;
            foreach (var (k, v) in patch)
            {
                if (v is not { } value) continue;
                switch (k)
                {
                    case "rate": c = c with { Rate = value }; any = true; break;
                    case "flat": c = c with { Flat = value }; any = true; break;
                    case "cap": c = c with { Cap = value }; any = true; break;
                }
            }

            updated.Add(any ? c.Validate() : charge);
        }

        return this with { Charges = updated };
    }

    public static CostSchedule IndiaDelivery(string exchange = "NSE")
    {
        var txn = exchange.Equals("BSE", StringComparison.OrdinalIgnoreCase) ? 0.0000375 : 0.0000307;
        return new CostSchedule($"India delivery ({exchange.ToUpperInvariant()})",
        [
            new Charge("brokerage", "Brokerage", "order", Flat: 0.0, Cap: 20.0, Taxed: true),
            new Charge("stt", "STT", "turnover", Rate: 0.001),
            new Charge("exchange_txn", "Exchange txn charge", "turnover", Rate: txn, Taxed: true),
            new Charge("sebi", "SEBI charges", "turnover", Rate: 10.0 / 1_00_00_000, Taxed: true),
            new Charge("stamp_duty", "Stamp duty", "buy", Rate: 0.00015),
        ], TaxRate: 0.18);
    }

    public static CostSchedule UsEquity() => new("US equity", [new Charge("commission", "Commission", "order", Flat: 0.0), new Charge("sec_fee", "SEC fee", "sell", Rate: 0.0000278)], Currency: "USD", TaxLabel: "Tax");

    public static readonly string[] PresetNames = ["india_delivery_bse", "india_delivery_nse", "us_equity"];

    public static CostSchedule For(string name, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double?>>? overrides = null)
    {
        var schedule = name switch
        {
            "india_delivery_nse" => IndiaDelivery("NSE"),
            "india_delivery_bse" => IndiaDelivery("BSE"),
            "us_equity" => UsEquity(),
            _ => throw new ArgumentException($"unknown cost schedule '{name}'; have [{string.Join(", ", PresetNames.Select(n => $"'{n}'"))}]"),
        };
        return schedule.WithOverrides(overrides ?? new Dictionary<string, IReadOnlyDictionary<string, double?>>());
    }
}
