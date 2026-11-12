namespace EPTradeBot.Core.Options;

/// <summary>The Black-76 model OpenAlgo prices options on futures and forwards with. Pure maths: units match OpenAlgo's (theta per calendar day, vega and rho per one percentage point).</summary>
public static class Black76
{
    /// <summary>Why a volatility could not be found: the price is outside what any volatility can produce.</summary>
    public const string BelowIntrinsic = "price is below intrinsic value";

    public const string AboveMaximum = "price exceeds theoretical maximum";

    private const double Lowest = 1e-6;
    private const double Highest = 20.0;

    public static double NormalCdf(double x) => 0.5 * Erfc(-x / Math.Sqrt(2.0));

    public static double NormalPdf(double x) => Math.Exp(-0.5 * x * x) / Math.Sqrt(2.0 * Math.PI);

    private static (double D1, double D2) D(double f, double k, double t, double sigma)
    {
        var sd = sigma * Math.Sqrt(t);
        var d1 = (Math.Log(f / k) + 0.5 * sigma * sigma * t) / sd;
        return (d1, d1 - sd);
    }

    private static double Sign(bool call) => call ? 1.0 : -1.0;

    public static double Price(bool call, double f, double k, double t, double r, double sigma)
    {
        var df = Math.Exp(-r * t);
        var (d1, d2) = D(f, k, t, sigma);
        var s = Sign(call);
        return df * s * (f * NormalCdf(s * d1) - k * NormalCdf(s * d2));
    }

    public static double Delta(bool call, double f, double k, double t, double r, double sigma)
    {
        var (d1, _) = D(f, k, t, sigma);
        var df = Math.Exp(-r * t);
        return call ? df * NormalCdf(d1) : -df * NormalCdf(-d1);
    }

    public static double Gamma(double f, double k, double t, double r, double sigma)
    {
        var (d1, _) = D(f, k, t, sigma);
        return Math.Exp(-r * t) * NormalPdf(d1) / (f * sigma * Math.Sqrt(t));
    }

    /// <summary>Per calendar day.</summary>
    public static double Theta(bool call, double f, double k, double t, double r, double sigma)
    {
        var (d1, _) = D(f, k, t, sigma);
        var price = Price(call, f, k, t, r, sigma);
        var decay = -f * Math.Exp(-r * t) * NormalPdf(d1) * sigma / (2.0 * Math.Sqrt(t));
        return (decay + r * price) / 365.0;
    }

    /// <summary>For one percentage point of volatility.</summary>
    public static double Vega(double f, double k, double t, double r, double sigma)
    {
        var (d1, _) = D(f, k, t, sigma);
        return f * Math.Exp(-r * t) * NormalPdf(d1) * Math.Sqrt(t) * 0.01;
    }

    /// <summary>For one percentage point of the rate.</summary>
    public static double Rho(bool call, double f, double k, double t, double r, double sigma) => -t * Price(call, f, k, t, r, sigma) * 0.01;

    /// <summary>The volatility (a decimal, 0.15 is 15 percent) that gives <paramref name="price"/>, or null with the reason in <paramref name="error"/>.</summary>
    public static double? ImpliedVolatility(double price, double f, double k, double r, double t, bool call, out string? error)
    {
        error = null;
        var df = Math.Exp(-r * t);
        var intrinsic = df * Math.Max(call ? f - k : k - f, 0.0);
        var ceiling = df * (call ? f : k);
        if (price < intrinsic) { error = BelowIntrinsic; return null; }
        if (price >= ceiling) { error = AboveMaximum; return null; }

        double lo = Lowest, hi = Highest;
        if (price <= Price(call, f, k, t, r, lo)) return lo;
        if (price >= Price(call, f, k, t, r, hi)) return hi;
        var sigma = 0.3;
        for (var i = 0; i < 200; i++)
        {
            var diff = Price(call, f, k, t, r, sigma) - price;
            if (diff > 0) hi = sigma; else lo = sigma;
            var vega = f * df * NormalPdf(D(f, k, t, sigma).D1) * Math.Sqrt(t);
            var next = vega > 1e-12 ? sigma - diff / vega : double.NaN;
            if (double.IsNaN(next) || next <= lo || next >= hi) next = 0.5 * (lo + hi);
            if (Math.Abs(next - sigma) < 1e-15) { sigma = next; break; }
            sigma = next;
        }

        return sigma;
    }

    private static readonly double[] SmallA = [3.1611237438705656, 113.86415415105016, 377.485237685302, 3209.3775891384694, 0.18577770618460315];
    private static readonly double[] SmallB = [23.601290952344122, 244.02463793444417, 1282.6165260773723, 2844.2368334391704];
    private static readonly double[] MidC = [0.5641884969886701, 8.883149794388377, 66.11919063714163, 298.6351381974001, 881.9522212417691, 1712.0476126340708, 2051.0783778260716, 1230.3393547979972, 2.1531153547440383e-8];
    private static readonly double[] MidD = [15.744926110709835, 117.6939508913125, 537.1811018620099, 1621.3895745666904, 3290.7992357334597, 4362.619090143247, 3439.3676741437216, 1230.339354803749];
    private static readonly double[] TailP = [0.3053266349612323, 0.36034489994980445, 0.12578172611122926, 0.016083785148742275, 0.0006587491615298378, 0.016315387137302097];
    private static readonly double[] TailQ = [2.568520192289822, 1.8729528499234604, 0.5279051029514285, 0.06051834131244132, 0.0023352049762686918];

    /// <summary>Complementary error function to full double precision (W. J. Cody's rational approximations, as in the C library).</summary>
    private static double Erfc(double x)
    {
        var y = Math.Abs(x);
        double result;
        if (y <= 0.46875)
        {
            var ysq = y > 1.11e-16 ? y * y : 0.0;
            var num = SmallA[4] * ysq;
            var den = ysq;
            for (var i = 0; i < 3; i++) { num = (num + SmallA[i]) * ysq; den = (den + SmallB[i]) * ysq; }
            var erf = x * (num + SmallA[3]) / (den + SmallB[3]);
            return 1.0 - erf;
        }

        if (y <= 4.0)
        {
            var num = MidC[8] * y;
            var den = y;
            for (var i = 0; i < 7; i++) { num = (num + MidC[i]) * y; den = (den + MidD[i]) * y; }
            result = (num + MidC[7]) / (den + MidD[7]);
        }
        else
        {
            if (y >= 27.0) return x < 0 ? 2.0 : 0.0;
            var ysq = 1.0 / (y * y);
            var num = TailP[5] * ysq;
            var den = ysq;
            for (var i = 0; i < 4; i++) { num = (num + TailP[i]) * ysq; den = (den + TailQ[i]) * ysq; }
            result = ysq * (num + TailP[4]) / (den + TailQ[4]);
            result = (1.0 / Math.Sqrt(Math.PI) - result) / y;
        }

        var sq = Math.Truncate(y * 16.0) / 16.0;
        var del = (y - sq) * (y + sq);
        result *= Math.Exp(-sq * sq) * Math.Exp(-del);
        return x < 0 ? 2.0 - result : result;
    }
}
