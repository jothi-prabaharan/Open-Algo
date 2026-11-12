namespace EPTradeBot.Core.Portfolio;

/// <summary>The SIP could not be simulated.</summary>
public sealed class SipException(string message) : Exception(message);

/// <summary>Daily closes, ascending, one per session.</summary>
public sealed record PriceSeries(DateOnly[] Dates, double[] Closes)
{
    public int Count => Dates.Length;

    public bool IsEmpty => Dates.Length == 0;
}

/// <summary>A completed SIP: dated cash flows (investments negative, the closing value positive) and the daily value, invested and unit curves.</summary>
public sealed class SipResult
{
    public required string Symbol { get; init; }

    public required string Exchange { get; init; }

    public required List<(DateOnly When, double Amount)> CashFlows { get; init; }

    public required DateOnly[] Dates { get; init; }

    public required double[] Value { get; init; }

    public required double[] Invested { get; init; }

    public required double[] Units { get; init; }

    public required List<Installment> Installments { get; init; }

    public double TotalInvested { get; init; }

    public double FinalValue { get; init; }

    public double TotalUnits { get; init; }

    public double AverageCost { get; init; }

    public double AveragePrice { get; init; }

    public double Cash { get; init; }

    public double Charges { get; init; }

    public Dictionary<string, double> ChargeBreakdown { get; init; } = [];

    public List<string> Warnings { get; init; } = [];

    public int InstallmentCount => Installments.Count;
}

public sealed record SipOptions(string Frequency = "monthly", int DayOfMonth = 1, double StepUpPercent = 0, ITradeCosts? Costs = null, double BrokeragePercent = 0, double BrokerageFlat = 0);

/// <summary>
/// A SIP run against a price series, literally: buy whole shares at the close on each execution date, carry the unspent cash to the next installment, mark the holding every session.
/// OpenAlgo's sip.engine.
/// </summary>
public static class SipEngine
{
    private static double ChargeFor(double amount, SipOptions o) =>
        o.Costs is not null ? o.Costs.TotalFor(amount, 0.0, 1) : amount * (o.BrokeragePercent / 100.0) + o.BrokerageFlat;

    private static PriceSeries Window(PriceSeries closes, DateOnly start, DateOnly end)
    {
        var rows = closes.Dates.Zip(closes.Closes).Where(r => !double.IsNaN(r.Second)).Select((r, i) => (r.First, r.Second, i)).OrderBy(r => r.First).ThenBy(r => r.i)
            .Where(r => r.First >= start && r.First <= end).ToList();
        return new PriceSeries(rows.Select(r => r.First).ToArray(), rows.Select(r => r.Second).ToArray());
    }

    public static SipResult Run(PriceSeries closes, string symbol, string exchange, DateOnly start, DateOnly end, double amount, SipOptions options, IEnumerable<string>? warnings = null)
    {
        if (closes is null || closes.IsEmpty) throw new SipException("no price data for this symbol and period");
        if (closes.Closes.All(double.IsNaN)) throw new SipException("price series is empty after dropping missing sessions");
        var window = Window(closes, start, end);
        if (window.IsEmpty) throw new SipException($"no trading sessions between {start:yyyy-MM-dd} and {end:yyyy-MM-dd} for {symbol}");
        if (window.Closes.Any(c => c <= 0)) throw new SipException("price series contains non-positive closes");

        var installments = SipSchedule.Build(window.Dates, start, end, amount, options.Frequency, options.DayOfMonth, options.StepUpPercent);
        var priceOn = new Dictionary<DateOnly, double>();
        for (var i = 0; i < window.Count; i++) priceOn[window.Dates[i]] = window.Closes[i];

        var unitsBy = new Dictionary<DateOnly, double>();
        var cashBy = new Dictionary<DateOnly, double>();
        var investedBy = new Dictionary<DateOnly, double>();
        double totalUnits = 0, cash = 0, totalInvested = 0, totalCharges = 0;
        var breakdown = new Dictionary<string, double>();
        var order = new List<string>();
        var flows = new List<(DateOnly, double)>();

        foreach (var inst in installments)
        {
            var price = priceOn[inst.Executed];
            var budget = cash + inst.Amount;
            var shares = (long)Math.Floor(budget / price);
            var charge = ChargeFor(shares * price, options);
            while (shares > 0 && shares * price + charge > budget)
            {
                shares--;
                charge = ChargeFor(shares * price, options);
            }

            if (shares == 0) charge = 0.0;
            var spent = shares * price;
            cash = budget - spent - charge;
            totalUnits += shares;
            totalInvested += inst.Amount;
            totalCharges += charge;
            if (options.Costs is not null && shares != 0)
                foreach (var (key, line) in options.Costs.Itemise(spent, 0.0, 1))
                {
                    if (key == "orders") continue;
                    if (!breakdown.ContainsKey(key)) order.Add(key);
                    breakdown[key] = breakdown.GetValueOrDefault(key) + line;
                }

            unitsBy[inst.Executed] = totalUnits;
            cashBy[inst.Executed] = cash;
            investedBy[inst.Executed] = totalInvested;
            flows.Add((inst.Executed, -inst.Amount));
        }

        double Carry(Dictionary<DateOnly, double> map, ref double last, DateOnly d) { if (map.TryGetValue(d, out var v)) last = v; return last; }
        double lu = 0, li = 0, lc = 0;
        var units = new double[window.Count];
        var invested = new double[window.Count];
        var value = new double[window.Count];
        for (var i = 0; i < window.Count; i++)
        {
            units[i] = Carry(unitsBy, ref lu, window.Dates[i]);
            invested[i] = Carry(investedBy, ref li, window.Dates[i]);
            var held = Carry(cashBy, ref lc, window.Dates[i]);
            value[i] = units[i] * window.Closes[i] + held;
        }

        var finalValue = value[^1];
        flows.Add((window.Dates[^1], finalValue));
        if (totalUnits == 0) throw new SipException("no installment could afford a single share: the price exceeds the amount available after charges. Increase the installment.");

        var deployed = totalInvested - totalCharges - cash;
        var executed = installments.Select(i => priceOn[i.Executed]).ToList();
        return new SipResult
        {
            Symbol = symbol, Exchange = exchange, CashFlows = flows, Dates = window.Dates, Value = value, Invested = invested, Units = units, Installments = installments, TotalInvested = totalInvested,
            FinalValue = finalValue, TotalUnits = totalUnits, AverageCost = totalUnits > 0 ? deployed / totalUnits : 0.0, AveragePrice = executed.Sum() / executed.Count, Cash = cash, Charges = totalCharges,
            ChargeBreakdown = order.ToDictionary(k => k, k => Portfolio.PyRound(breakdown[k], 2)), Warnings = (warnings ?? []).ToList(),
        };
    }

    public sealed record Lumpsum(double Invested, double FinalValue, double Units, double EntryPrice, DateOnly EntryDate, List<(DateOnly When, double Amount)> CashFlows);

    /// <summary>The same total money deployed in one go on the first session, whole shares only, for comparison.</summary>
    public static Lumpsum RunLumpsum(PriceSeries closes, DateOnly start, DateOnly end, double amount, SipOptions options)
    {
        var window = Window(closes, start, end);
        if (window.IsEmpty) throw new SipException("no sessions available for the lumpsum comparison");
        var entry = window.Closes[0];
        var units = (long)Math.Floor(amount / entry);
        var charge = ChargeFor(units * entry, options);
        while (units > 0 && units * entry + charge > amount)
        {
            units--;
            charge = ChargeFor(units * entry, options);
        }

        var residual = amount - units * entry - charge;
        var final = units * window.Closes[^1] + residual;
        return new Lumpsum(amount, final, units, entry, window.Dates[0], [(window.Dates[0], -amount), (window.Dates[^1], final)]);
    }
}

internal static class Portfolio
{
    public static double PyRound(double x, int digits) => EPTradeBot.Core.Options.PyNumber.Round(x, digits);
}
