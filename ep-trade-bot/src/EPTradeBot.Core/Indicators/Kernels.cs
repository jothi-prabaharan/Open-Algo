namespace EPTradeBot.Core.Indicators;

/// <summary>
/// The compute kernels of openalgo.ta (openalgo/indicators/_backend.py). Each takes and returns plain arrays, with NaN for "no value".
/// Rolling-window kernels are window-local in NaN (a NaN blanks only the windows that contain it); recursive kernels skip leading NaNs, seed at the
/// first finite value and carry their state across an interior NaN.
/// </summary>
public static partial class Kernels
{
    private const double NaN = double.NaN;

    public static double[] Full(int n, double value = NaN)
    {
        var a = new double[n];
        Array.Fill(a, value);
        return a;
    }

    public static bool IsNaN(double x) => double.IsNaN(x);

    // ---- helpers ---------------------------------------------------------------------------------------------

    /// <summary>Start of the first run of <paramref name="period"/> values with no NaN, else -1.</summary>
    public static int FirstCleanWindow(double[] data, int period)
    {
        if (period <= 0 || data.Length < period) return -1;
        var run = 0;
        for (var i = 0; i < data.Length; i++)
        {
            run = double.IsNaN(data[i]) ? 0 : run + 1;
            if (run == period) return i + 1 - period;
        }

        return -1;
    }

    public static double[] Map(double[] a, Func<double, double> f) => a.Select(f).ToArray();

    public static double[] Zip(double[] a, double[] b, Func<double, double, double> f)
    {
        var o = new double[a.Length];
        for (var i = 0; i < o.Length; i++) o[i] = f(a[i], b[i]);
        return o;
    }

    public static double[] Add(double[] a, double[] b) => Zip(a, b, (x, y) => x + y);

    public static double[] Sub(double[] a, double[] b) => Zip(a, b, (x, y) => x - y);

    public static double[] Mul(double[] a, double k) => Map(a, x => x * k);

    /// <summary>Applies a reduction to each full window; NaN before the first window.</summary>
    public static double[] Roll(double[] data, int period, Func<ReadOnlySpan<double>, double> f)
    {
        var o = Full(data.Length);
        for (var i = period - 1; i < data.Length; i++) o[i] = f(data.AsSpan(i - period + 1, period));
        return o;
    }

    private static (double[] Sums, double[] Squares, int[] Nans) RollNan(double[] data, int period)
    {
        var count = data.Length - period + 1;
        var sums = new double[count];
        var squares = new double[count];
        var nans = new int[count];
        var c = new double[data.Length + 1];
        var q = new double[data.Length + 1];
        var m = new int[data.Length + 1];
        for (var i = 0; i < data.Length; i++)
        {
            var nan = double.IsNaN(data[i]);
            var v = nan ? 0.0 : data[i];
            c[i + 1] = c[i] + v;
            q[i + 1] = q[i] + (v * v);
            m[i + 1] = m[i] + (nan ? 1 : 0);
        }

        for (var i = 0; i < count; i++)
        {
            sums[i] = c[i + period] - c[i];
            squares[i] = q[i + period] - q[i];
            nans[i] = m[i + period] - m[i];
        }

        return (sums, squares, nans);
    }

    // ---- moving averages -----------------------------------------------------------------------------------------

    public static double[] Sma(double[] data, int period)
    {
        var n = data.Length;
        var o = Full(n);
        if (period <= 0 || n < period) return o;
        var (s, _, nans) = RollNan(data, period);
        for (var i = 0; i < s.Length; i++) o[period - 1 + i] = nans[i] == 0 ? s[i] / period : NaN;
        return o;
    }

    public static double[] RollingSum(double[] data, int period)
    {
        var n = data.Length;
        var o = Full(n);
        if (period <= 0 || n < period) return o;
        var (s, _, nans) = RollNan(data, period);
        for (var i = 0; i < s.Length; i++) o[period - 1 + i] = nans[i] == 0 ? s[i] : NaN;
        return o;
    }

    public static double[] WinMean(double[] data, int period) => Roll(data, period, w =>
    {
        double s = 0;
        foreach (var x in w) s += x;
        return s / w.Length;
    });

    public static double[] Stdev(double[] data, int period)
    {
        var n = data.Length;
        var o = Full(n);
        if (period <= 0 || n < period) return o;
        var (s, sq, nans) = RollNan(data, period);
        for (var i = 0; i < s.Length; i++)
        {
            var mean = s[i] / period;
            var v = (sq[i] / period) - (mean * mean);
            o[period - 1 + i] = nans[i] == 0 ? Math.Sqrt(Math.Max(0.0, v)) : NaN;
        }

        return o;
    }

    /// <summary>Per-window population standard deviation; NaN if the window holds a NaN.</summary>
    public static double[] WinStd(double[] data, int period) => Roll(data, period, w =>
    {
        foreach (var x in w) if (double.IsNaN(x)) return NaN;
        double m = 0;
        foreach (var x in w) m += x;
        m /= w.Length;
        double s = 0;
        foreach (var x in w) s += (x - m) * (x - m);
        return Math.Sqrt(s / w.Length);
    });

    public static double[] Wma(double[] data, int period)
    {
        var n = data.Length;
        var o = Full(n);
        if (period <= 0 || n < period) return o;
        var wsum = period * (period + 1) / 2.0;
        for (var i = period - 1; i < n; i++)
        {
            double s = 0;
            for (var j = 0; j < period; j++) s += data[i - period + 1 + j] * (j + 1);
            o[i] = s / wsum;
        }

        return o;
    }

    /// <summary>WMA that skips NaN inside a window and weights what is left.</summary>
    public static double[] WmaNan(double[] data, int period)
    {
        var n = data.Length;
        var o = Full(n);
        for (var i = period - 1; i < n; i++)
        {
            if (double.IsNaN(data[i])) continue;
            double ws = 0, vw = 0;
            for (var j = 0; j < period; j++)
            {
                var x = data[i - period + 1 + j];
                if (double.IsNaN(x)) continue;
                var w = j + 1;
                ws += x * w;
                vw += w;
            }

            if (vw > 0) o[i] = ws / vw;
        }

        return o;
    }

    public static double[] Hma(double[] data, int period)
    {
        var half = period / 2;
        var root = period > 0 ? (int)Math.Sqrt(period) : 0;
        if (period <= 0 || data.Length < period || half == 0 || root == 0) return Full(data.Length);
        var a = Wma(data, half);
        var b = Wma(data, period);
        return Wma(Zip(a, b, (x, y) => (2.0 * x) - y), root);
    }

    public static double[] Ema(double[] data, int period)
    {
        var o = Full(data.Length);
        var alpha = 2.0 / (period + 1.0);
        var prev = NaN;
        for (var i = 0; i < data.Length; i++)
        {
            var x = data[i];
            if (double.IsNaN(x)) continue;
            prev = double.IsNaN(prev) ? x : (alpha * x) + ((1.0 - alpha) * prev);
            o[i] = prev;
        }

        return o;
    }

    /// <summary>Wilder's smoothing, seeded on the mean of the first clean window.</summary>
    public static double[] EmaWilder(double[] data, int period)
    {
        var n = data.Length;
        var o = Full(n);
        if (period <= 0) return o;
        var fv = FirstCleanWindow(data, period);
        if (fv < 0) return o;
        double seed = 0;
        for (var i = fv; i < fv + period; i++) seed += data[i];
        var start = fv + period - 1;
        o[start] = seed / period;
        for (var i = start + 1; i < n; i++) o[i] = double.IsNaN(data[i]) ? o[i - 1] : ((o[i - 1] * (period - 1)) + data[i]) / period;
        return o;
    }

    /// <summary>EMA seeded on the mean of the first clean window.</summary>
    public static double[] EmaSma(double[] data, int period)
    {
        var n = data.Length;
        var o = Full(n);
        if (period <= 0 || n < period) return o;
        var fv = FirstCleanWindow(data, period);
        if (fv < 0) return o;
        var alpha = 2.0 / (period + 1.0);
        var start = fv + period - 1;
        double sum = 0;
        for (var i = fv; i < fv + period; i++) sum += data[i];
        var prev = sum / period;
        o[start] = prev;
        for (var i = start + 1; i < n; i++)
        {
            if (double.IsNaN(data[i])) continue;
            prev = (alpha * data[i]) + ((1.0 - alpha) * prev);
            o[i] = prev;
        }

        return o;
    }

    /// <summary>EMA seeded on the first finite value (the same recursion as <see cref="Ema"/>, carrying state across a NaN).</summary>
    public static double[] EmaFirstValid(double[] data, int period) => Ema(data, period);

    /// <summary>RMA/SMMA: the mean of the first clean window, then alpha = 1/length.</summary>
    public static double[] RmaSmma(double[] data, int length)
    {
        var n = data.Length;
        var o = Full(n);
        var fv = FirstCleanWindow(data, length);
        if (fv < 0) return o;
        double sum = 0;
        for (var i = fv; i < fv + length; i++) sum += data[i];
        var prev = sum / length;
        o[fv + length - 1] = prev;
        var alpha = 1.0 / length;
        for (var i = fv + length; i < n; i++)
        {
            if (double.IsNaN(data[i])) continue;
            prev = (alpha * data[i]) + ((1.0 - alpha) * prev);
            o[i] = prev;
        }

        return o;
    }

    public static double[] Zlema(double[] data, int period)
    {
        var n = data.Length;
        var lag = (period - 1) / 2;
        var adjusted = (double[])data.Clone();
        if (lag > 0) for (var i = lag; i < n; i++) adjusted[i] = (2.0 * data[i]) - data[i - lag];
        return Ema(adjusted, period);
    }

    public static double[] T3(double[] data, int period, double v)
    {
        double[] Gd(double[] x)
        {
            var e1 = Ema(x, period);
            var e2 = Ema(e1, period);
            return Zip(e1, e2, (a, b) => ((1.0 + v) * a) - (v * b));
        }

        return Gd(Gd(Gd(data)));
    }

    public static double[] KamaTv(double[] data, int length, int fast, int slow)
    {
        var n = data.Length;
        var o = Full(n);
        if (length <= 0 || n < length + 1) return o;
        var fa = 2.0 / (fast + 1);
        var sa = 2.0 / (slow + 1);
        for (var i = length; i < n; i++)
        {
            var mom = Math.Abs(data[i] - data[i - length]);
            double vol = 0;
            for (var j = i - length + 1; j <= i; j++) if (j > 0) vol += Math.Abs(data[j] - data[j - 1]);
            var er = vol != 0 ? mom / vol : 0.0;
            var alpha = Math.Pow((er * (fa - sa)) + sa, 2);
            var prev = i == length || double.IsNaN(o[i - 1]) ? data[i] : o[i - 1];
            o[i] = (alpha * data[i]) + ((1.0 - alpha) * prev);
        }

        return o;
    }

    public static double[] Trima(double[] data, int period)
    {
        var n = data.Length;
        var o = Full(n);
        var n1 = (period + 1) / 2;
        var n2 = period - n1 + 1;
        var first = Full(n);
        for (var i = n1 - 1; i < n; i++)
        {
            double s = 0;
            for (var j = i - n1 + 1; j <= i; j++) s += data[j];
            first[i] = s / n1;
        }

        for (var i = n1 + n2 - 2; i < n; i++)
        {
            if (double.IsNaN(first[i])) continue;
            var valid = new List<double>();
            for (var j = Math.Max(0, i - n2 + 1); j <= i; j++) if (!double.IsNaN(first[j])) valid.Add(first[j]);
            if (valid.Count >= n2) o[i] = valid.Skip(valid.Count - n2).Average();
        }

        return o;
    }

    public static double[] Alma(double[] data, int period, double offset, double sigma)
    {
        var n = data.Length;
        var o = Full(n);
        if (period <= 0 || n < period) return o;
        var m = offset * (period - 1);
        var s = period / sigma;
        var w = new double[period];
        double total = 0;
        for (var i = 0; i < period; i++)
        {
            w[i] = Math.Exp(-Math.Pow(i - m, 2) / (2 * s * s));
            total += w[i];
        }

        for (var i = period - 1; i < n; i++)
        {
            double sum = 0;
            for (var j = 0; j < period; j++) sum += w[j] / total * data[i - period + 1 + j];
            o[i] = sum;
        }

        return o;
    }

    public static double[] Mcginley(double[] data, int period)
    {
        var n = data.Length;
        var o = Full(n);
        if (period <= 0 || n < period) return o;
        double sum = 0;
        for (var i = 0; i < period; i++) sum += data[i];
        o[period - 1] = sum / period;
        for (var i = period; i < n; i++)
        {
            if (o[i - 1] != 0)
            {
                var ratio = data[i] / o[i - 1];
                o[i] = o[i - 1] + ((data[i] - o[i - 1]) / (period * Math.Pow(ratio, 4)));
            }
            else
            {
                o[i] = data[i];
            }
        }

        return o;
    }

    public static double[] Vidya(double[] data, int period, double alpha)
    {
        var n = data.Length;
        var o = Full(n);
        if (period <= 0 || n < period + 1) return o;
        var cmo = Full(n);
        for (var i = period; i < n; i++)
        {
            double g = 0, l = 0;
            for (var j = i - period + 1; j <= i; j++)
            {
                if (j <= 0) continue;
                var d = data[j] - data[j - 1];
                if (d > 0) g += d;
                else if (d < 0) l += -d;
            }

            cmo[i] = (g + l) != 0 ? 100 * (g - l) / (g + l) : 0.0;
        }

        o[period] = data[period];
        for (var i = period + 1; i < n; i++)
        {
            var sc = alpha * Math.Abs(cmo[i]) / 100;
            o[i] = o[i - 1] + (sc * (data[i] - o[i - 1]));
        }

        return o;
    }

    public static double[] Swma(double[] data)
    {
        var n = data.Length;
        var o = Full(n);
        for (var i = 3; i < n; i++) o[i] = (data[i - 3] + (2 * data[i - 2]) + (2 * data[i - 1]) + data[i]) / 6.0;
        return o;
    }

    /// <summary>Highest of the finite values in the window; NaN only when there are none.</summary>
    public static double[] Highest(double[] data, int period) => Roll(data, period, w =>
    {
        var m = double.NegativeInfinity;
        var any = false;
        foreach (var x in w)
        {
            if (double.IsNaN(x)) continue;
            any = true;
            if (x > m) m = x;
        }

        return any ? m : NaN;
    });

    public static double[] Lowest(double[] data, int period) => Roll(data, period, w =>
    {
        var m = double.PositiveInfinity;
        var any = false;
        foreach (var x in w)
        {
            if (double.IsNaN(x)) continue;
            any = true;
            if (x < m) m = x;
        }

        return any ? m : NaN;
    });

    public static double[] Shift(double[] a, int k)
    {
        var o = Full(a.Length);
        if (k >= 0 && k < a.Length) for (var i = k; i < a.Length; i++) o[i] = a[i - k];
        return o;
    }

    // ---- ranges and momentum -----------------------------------------------------------------------------------

    public static double[] TrueRange(double[] high, double[] low, double[] close)
    {
        var n = high.Length;
        var tr = new double[n];
        if (n == 0) return tr;
        tr[0] = high[0] - low[0];
        for (var i = 1; i < n; i++)
        {
            var hl = high[i] - low[i];
            var hc = Math.Abs(high[i] - close[i - 1]);
            var lc = Math.Abs(low[i] - close[i - 1]);
            tr[i] = double.IsNaN(hl) || double.IsNaN(hc) || double.IsNaN(lc) ? NaN : Math.Max(Math.Max(hl, hc), lc);
        }

        return tr;
    }

    public static double[] AtrWilder(double[] high, double[] low, double[] close, int period) =>
        period <= 0 || high.Length < period ? Full(high.Length) : EmaWilder(TrueRange(high, low, close), period);

    public static double[] Rsi(double[] data, int period)
    {
        var n = data.Length;
        var o = Full(n);
        if (period <= 0 || n < period + 1) return o;
        var fv = FirstCleanWindow(data, period + 1);
        if (fv < 0) return o;
        var gains = new double[n - 1];
        var losses = new double[n - 1];
        var deltas = new double[n - 1];
        for (var i = 0; i < n - 1; i++)
        {
            deltas[i] = data[i + 1] - data[i];
            gains[i] = deltas[i] > 0 ? deltas[i] : 0.0;
            losses[i] = deltas[i] < 0 ? -deltas[i] : 0.0;
        }

        double ag = 0, al = 0;
        for (var i = fv; i < fv + period; i++)
        {
            ag += gains[i];
            al += losses[i];
        }

        ag /= period;
        al /= period;
        var start = fv + period;
        o[start] = al == 0 ? 100.0 : 100.0 - (100.0 / (1.0 + (ag / al)));
        for (var i = start; i < n - 1; i++)
        {
            if (double.IsNaN(deltas[i])) continue;
            ag = ((ag * (period - 1)) + gains[i]) / period;
            al = ((al * (period - 1)) + losses[i]) / period;
            o[i + 1] = al == 0 ? 100.0 : 100.0 - (100.0 / (1.0 + (ag / al)));
        }

        return o;
    }

    public static double[] Roc(double[] data, int length)
    {
        var n = data.Length;
        var o = Full(n);
        for (var i = length; i < n; i++)
        {
            var prev = data[i - length];
            if (prev != 0) o[i] = (data[i] - prev) / prev * 100.0;
        }

        return o;
    }

    /// <summary>ROC with 0 where the divisor is 0 (the oscillator family's).</summary>
    public static double[] RocOsc(double[] data, int period)
    {
        var n = data.Length;
        var o = Full(n);
        if (period >= n) return o;
        for (var i = period; i < n; i++)
        {
            var prev = data[i - period];
            o[i] = prev != 0 ? (data[i] - prev) / prev * 100.0 : 0.0;
        }

        return o;
    }

    public static double[] Mom(double[] data, int period)
    {
        var o = Full(data.Length);
        for (var i = period; i < data.Length; i++) o[i] = data[i] - data[i - period];
        return o;
    }

    public static double[] Rocp(double[] data, int period)
    {
        var o = Full(data.Length);
        for (var i = period; i < data.Length; i++) o[i] = (data[i] - data[i - period]) / data[i - period];
        return o;
    }

    public static double[] Rocr(double[] data, int period)
    {
        var o = Full(data.Length);
        for (var i = period; i < data.Length; i++) o[i] = data[i] / data[i - period];
        return o;
    }
}
