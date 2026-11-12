namespace EPTradeBot.Core.Portfolio;

public static partial class ReturnStats
{
    public static double Volatility(double[] r) => Std(r) * Math.Sqrt(TradingDays);

    public static double Sharpe(double[] r, double rf)
    {
        var x = Excess(r, rf, TradingDays);
        return Mean(x) / Std(x) * Math.Sqrt(TradingDays);
    }

    public static double Sortino(double[] r, double rf)
    {
        var x = Excess(r, rf, TradingDays);
        var downside = Math.Sqrt(x.Where(v => v < 0).Sum(v => v * v) / x.Length);
        return (downside == 0 ? double.NaN : Mean(x) / downside) * Math.Sqrt(TradingDays);
    }

    /// <summary>The worst fall of the running value of the returns, as OpenAlgo asks for it (max_drawdown of the cumulative product).</summary>
    public static double MaxDrawdownOfReturns(double[] r) => MaxDrawdown(PriceCurve(r));

    public static double Calmar(double[] r) => Cagr(r) / Math.Abs(MaxDrawdown(r));

    public static double Median(IReadOnlyList<double> v) => Quantile(v, 0.5);
}
