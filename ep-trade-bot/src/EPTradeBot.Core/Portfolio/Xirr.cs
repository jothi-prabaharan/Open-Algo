namespace EPTradeBot.Core.Portfolio;

/// <summary>XIRR is not defined for these cash flows (too few, all one sign, all one date, or no root in a sane range).</summary>
public sealed class XirrException(string message) : Exception(message);

/// <summary>
/// The annualised rate that zeroes the present value of dated cash flows, the one return figure that respects when each rupee arrived. OpenAlgo's sip.xirr: NPV uses (1 + r)^years on a 365-day year
/// to match a spreadsheet, and the root is bracketed in [-99.99%, 10000%] and found with Brent's method to a tolerance of 1e-10.
/// </summary>
public static class Xirr
{
    private const double DaysPerYear = 365.0;
    private const double MinRate = -0.9999;
    private const double MaxRate = 100.0;

    private static double Npv(double rate, IReadOnlyList<(DateOnly When, double Amount)> flows, DateOnly t0)
    {
        var total = 0.0;
        foreach (var (when, amount) in flows)
        {
            var years = (when.DayNumber - t0.DayNumber) / DaysPerYear;
            total += amount / Math.Pow(1.0 + rate, years);
        }

        return total;
    }

    public static double Solve(IEnumerable<(DateOnly When, double Amount)> cashFlows)
    {
        var flows = cashFlows.Where(f => f.Amount != 0.0).Select((f, i) => (f.When, f.Amount, Index: i)).OrderBy(f => f.When).ThenBy(f => f.Index).Select(f => (f.When, f.Amount)).ToList();
        if (flows.Count < 2) throw new XirrException("XIRR needs at least two non-zero cash flows");
        var t0 = flows[0].When;
        if (flows[^1].When == t0) throw new XirrException("XIRR needs cash flows on at least two different dates");
        var hasNegative = flows.Any(f => f.Amount < 0);
        var hasPositive = flows.Any(f => f.Amount > 0);
        if (!(hasNegative && hasPositive))
            throw new XirrException($"XIRR is undefined for {(hasNegative ? "all outflows" : "all inflows")}: the series never crosses zero");

        double F(double r) => Npv(r, flows, t0);
        var fLo = F(MinRate);
        var fHi = F(MaxRate);
        if (!double.IsFinite(fLo) || !double.IsFinite(fHi)) throw new XirrException("NPV is not finite at the bracket edges");
        if (fLo * fHi > 0)
            throw new XirrException("no rate between -100% and 10000% zeroes the NPV; the cash flows are likely degenerate (a very short period, or a near-total loss)");
        return Brent(F, MinRate, MaxRate);
    }

    public static double? OrNull(IEnumerable<(DateOnly When, double Amount)> cashFlows)
    {
        try { return Solve(cashFlows); }
        catch (XirrException) { return null; }
    }

    /// <summary>Total gain as a fraction of what was put in.</summary>
    public static double AbsoluteReturn(double invested, double currentValue) => invested <= 0 ? 0.0 : (currentValue - invested) / invested;

    /// <summary>scipy.optimize.brentq (xtol 1e-10, rtol 8.9e-16, at most 200 iterations), step for step, so the root agrees to the last digits.</summary>
    private static double Brent(Func<double, double> f, double a, double b)
    {
        const double xtol = 1e-10;
        const double rtol = 8.881784197001252e-16;
        double xpre = a, xcur = b, xblk = 0.0, fblk = 0.0, spre = 0.0, scur = 0.0;
        double fpre = f(xpre), fcur = f(xcur);
        if (fpre == 0) return xpre;
        if (fcur == 0) return xcur;
        for (var i = 0; i < 200; i++)
        {
            if (fpre != 0 && fcur != 0 && double.IsNegative(fpre) != double.IsNegative(fcur))
            {
                xblk = xpre; fblk = fpre; spre = scur = xcur - xpre;
            }

            if (Math.Abs(fblk) < Math.Abs(fcur))
            {
                xpre = xcur; xcur = xblk; xblk = xpre;
                fpre = fcur; fcur = fblk; fblk = fpre;
            }

            var delta = (xtol + rtol * Math.Abs(xcur)) / 2;
            var sbis = (xblk - xcur) / 2;
            if (fcur == 0 || Math.Abs(sbis) < delta) return xcur;
            if (Math.Abs(spre) > delta && Math.Abs(fcur) < Math.Abs(fpre))
            {
                double stry;
                if (xpre == xblk) stry = -fcur * (xcur - xpre) / (fcur - fpre);
                else
                {
                    var dpre = (fpre - fcur) / (xpre - xcur);
                    var dblk = (fblk - fcur) / (xblk - xcur);
                    stry = -fcur * (fblk * dblk - fpre * dpre) / (dblk * dpre * (fblk - fpre));
                }

                if (2 * Math.Abs(stry) < Math.Min(Math.Abs(spre), 3 * Math.Abs(sbis) - delta)) { spre = scur; scur = stry; }
                else { spre = sbis; scur = sbis; }
            }
            else { spre = sbis; scur = sbis; }

            xpre = xcur; fpre = fcur;
            if (Math.Abs(scur) > delta) xcur += scur;
            else xcur += sbis > 0 ? delta : -delta;
            fcur = f(xcur);
        }

        throw new XirrException("solver failed to converge: Failed to converge after 200 iterations");
    }
}
