namespace EPTradeBot.Core.Indicators;

public static partial class Kernels
{
    public static (double[] Macd, double[] Signal, double[] Hist) Macd(double[] data, int fast, int slow, int signal)
    {
        var line = Sub(Ema(data, fast), Ema(data, slow));
        var sig = Ema(line, signal);
        return (line, sig, Sub(line, sig));
    }

    public static (double[] Upper, double[] Middle, double[] Lower) Bbands(double[] data, int period, double std)
    {
        var mid = Sma(data, period);
        var sd = Stdev(data, period);
        return (Zip(mid, sd, (m, s) => m + (std * s)), mid, Zip(mid, sd, (m, s) => m - (std * s)));
    }

    public static (double[] K, double[] D) Stochastic(double[] high, double[] low, double[] close, int kPeriod, int smoothK, int dPeriod)
    {
        var hh = Roll(high, kPeriod, Max);
        var ll = Roll(low, kPeriod, Min);
        var n = close.Length;
        var fastK = Full(n);
        var fk = kPeriod - 1;
        for (var i = fk; i < n; i++) fastK[i] = hh[i] != ll[i] ? 100.0 * (close[i] - ll[i]) / (hh[i] - ll[i]) : 50.0;
        var slowK = Concat(Full(fk), Sma(fastK[fk..], smoothK));
        var slowD = Concat(Full(fk + smoothK - 1), Sma(slowK[(fk + smoothK - 1)..], dPeriod));
        return (slowK, slowD);
    }

    public static double Max(ReadOnlySpan<double> w)
    {
        var m = double.NegativeInfinity;
        foreach (var x in w)
        {
            if (double.IsNaN(x)) return NaN;
            if (x > m) m = x;
        }

        return m;
    }

    public static double Min(ReadOnlySpan<double> w)
    {
        var m = double.PositiveInfinity;
        foreach (var x in w)
        {
            if (double.IsNaN(x)) return NaN;
            if (x < m) m = x;
        }

        return m;
    }

    public static double[] Concat(double[] a, double[] b)
    {
        var o = new double[a.Length + b.Length];
        a.CopyTo(o, 0);
        b.CopyTo(o, a.Length);
        return o;
    }

    public static double[] Cci(double[] high, double[] low, double[] close, int period)
    {
        var n = close.Length;
        var o = Full(n);
        if (period <= 0 || n < period) return o;
        var tp = new double[n];
        for (var i = 0; i < n; i++) tp[i] = (high[i] + low[i] + close[i]) / 3.0;
        double p = period, rsum = 0;
        for (var k = 0; k < period; k++) rsum += tp[k];
        for (var i = period - 1; i < n; i++)
        {
            if (i > period - 1) rsum = rsum + tp[i] - tp[i - period];
            var sma = rsum / p;
            double md = 0;
            for (var j = 0; j < period; j++) md += Math.Abs(tp[i + 1 - period + j] - sma);
            md /= p;
            o[i] = md != 0.0 ? (tp[i] - sma) / (0.015 * md) : 0.0;
        }

        return o;
    }

    public static double[] WilliamsR(double[] high, double[] low, double[] close, int period)
    {
        var hh = Roll(high, period, Max);
        var ll = Roll(low, period, Min);
        var o = Full(close.Length);
        for (var i = period - 1; i < close.Length; i++) o[i] = hh[i] != ll[i] ? -100.0 * (hh[i] - close[i]) / (hh[i] - ll[i]) : -50.0;
        return o;
    }

    public static double[] Vwma(double[] data, double[] volume, int period)
    {
        var n = data.Length;
        var o = Full(n);
        if (period <= 0 || n < period) return o;
        for (var i = period - 1; i < n; i++)
        {
            double spv = 0, sv = 0;
            var bad = false;
            for (var j = i - period + 1; j <= i; j++)
            {
                if (double.IsNaN(data[j]) || double.IsNaN(volume[j])) bad = true;
                else
                {
                    spv += data[j] * volume[j];
                    sv += volume[j];
                }
            }

            if (!bad) o[i] = sv > 0 ? spv / sv : data[i];
        }

        return o;
    }

    public static double[] VwmaStrict(double[] values, double[] volume, int length)
    {
        var n = values.Length;
        var o = Full(n);
        for (var i = length - 1; i < n; i++)
        {
            double sw = 0, sv = 0;
            for (var j = i - length + 1; j <= i; j++)
            {
                if (double.IsNaN(values[j]) || double.IsNaN(volume[j])) continue;
                sw += values[j] * volume[j];
                sv += volume[j];
            }

            if (sv > 0) o[i] = sw / sv;
        }

        return o;
    }

    public static double[] Bop(double[] open, double[] high, double[] low, double[] close)
    {
        var o = new double[close.Length];
        for (var i = 0; i < o.Length; i++)
        {
            var r = high[i] - low[i];
            o[i] = r != 0 ? (close[i] - open[i]) / r : 0.0;
        }

        return o;
    }

    public static (double[] Bull, double[] Bear) Elderray(double[] high, double[] low, double[] close, int period)
    {
        var e = Ema(close, period);
        return (Sub(high, e), Sub(low, e));
    }

    public static double[] UpdownStreak(double[] data)
    {
        var s = new double[data.Length];
        for (var i = 1; i < data.Length; i++)
        {
            if (data[i] == data[i - 1]) s[i] = 0.0;
            else if (data[i] > data[i - 1]) s[i] = s[i - 1] <= 0 ? 1.0 : s[i - 1] + 1.0;
            else s[i] = s[i - 1] >= 0 ? -1.0 : s[i - 1] - 1.0;
        }

        return s;
    }

    public static double[] PercentRank(double[] data, int period) => Roll(data, period, w =>
    {
        var last = w[^1];
        var below = 0;
        for (var i = 0; i < w.Length - 1; i++) if (w[i] < last) below++;
        return (double)below / period * 100.0;
    });

    public static double[] Crsi(double[] data, int lenRsi, int lenUpDown, int lenRoc)
    {
        var pr = Rsi(data, lenRsi);
        var sr = Rsi(UpdownStreak(data), lenUpDown);
        var rank = PercentRank(Roc(data, 1), lenRoc);
        var o = Full(data.Length);
        for (var i = 0; i < o.Length; i++) if (!double.IsNaN(pr[i]) && !double.IsNaN(sr[i]) && !double.IsNaN(rank[i])) o[i] = (pr[i] + sr[i] + rank[i]) / 3.0;
        return o;
    }

    public static (double[] Jaw, double[] Teeth, double[] Lips) Alligator(double[] data, int jawP, int jawS, int teethP, int teethS, int lipsP, int lipsS) =>
        (Shift(EmaWilder(data, jawP), jawS), Shift(EmaWilder(data, teethP), teethS), Shift(EmaWilder(data, lipsP), lipsS));

    public static (double[] Upper, double[] Mid, double[] Lower) MaEnvelopes(double[] data, int period, double percentage, string maType)
    {
        double[] ma;
        if (maType.Equals("SMA", StringComparison.OrdinalIgnoreCase)) ma = WinMean(data, period);
        else if (maType.Equals("EMA", StringComparison.OrdinalIgnoreCase)) ma = Ema(data, period);
        else throw new ArgumentException($"Unsupported MA type: {maType}");
        var mult = percentage / 100;
        return (Mul(ma, 1 + mult), ma, Mul(ma, 1 - mult));
    }

    // ---- volume ----------------------------------------------------------------------------------------------

    public static double[] Obv(double[] close, double[] volume)
    {
        var o = new double[close.Length];
        // Sign times volume, summed: a NaN volume makes the running total NaN from there on, as a cumulative sum does.
        for (var i = 1; i < o.Length; i++) o[i] = o[i - 1] + ((close[i] > close[i - 1] ? 1.0 : close[i] < close[i - 1] ? -1.0 : 0.0) * volume[i]);
        return o;
    }

    private static double Mfm(double h, double l, double c) => h - l != 0 ? ((c - l) - (h - c)) / (h - l) : 0.0;

    public static double[] Adl(double[] high, double[] low, double[] close, double[] volume)
    {
        var o = Full(close.Length);
        o[0] = 0.0;
        double acc = 0;
        for (var i = 1; i < o.Length; i++)
        {
            acc += Mfm(high[i], low[i], close[i]) * volume[i];
            o[i] = acc;
        }

        return o;
    }

    public static double[] Cmf(double[] high, double[] low, double[] close, double[] volume, int period)
    {
        var n = close.Length;
        var mfv = new double[n];
        for (var i = 0; i < n; i++) mfv[i] = Mfm(high[i], low[i], close[i]) * volume[i];
        var a = RollingSum(mfv, period);
        var b = RollingSum(volume, period);
        var o = Full(n);
        for (var i = 0; i < n; i++) if (!double.IsNaN(a[i]) && !double.IsNaN(b[i])) o[i] = b[i] != 0 ? a[i] / b[i] : 0.0;
        return o;
    }

    public static double[] Mfi(double[] high, double[] low, double[] close, double[] volume, int period)
    {
        var n = close.Length;
        var o = Full(n);
        var tp = new double[n];
        for (var i = 0; i < n; i++) tp[i] = (high[i] + low[i] + close[i]) / 3.0;
        var pos = new double[n];
        var neg = new double[n];
        for (var i = 1; i < n; i++)
        {
            var flow = tp[i] * volume[i];
            if (tp[i] > tp[i - 1]) pos[i] = flow;
            else if (tp[i] < tp[i - 1]) neg[i] = flow;
        }

        // A running window that starts at bar 1, so the first value (at period - 1) holds one flow fewer than a full window.
        double ps = 0, ns = 0;
        for (var i = 1; i < n; i++)
        {
            ps += pos[i];
            ns += neg[i];
            if (i >= period)
            {
                ps -= pos[i - period];
                ns -= neg[i - period];
            }

            if (i >= period - 1) o[i] = ns == 0 ? 100.0 : 100.0 - (100.0 / (1.0 + (ps / ns)));
        }

        return o;
    }

    public static double[] Emv(double[] high, double[] low, double[] volume, int length, double divisor)
    {
        var n = high.Length;
        var raw = Full(n);
        for (var i = 1; i < n; i++)
        {
            var chl2 = ((high[i] + low[i]) / 2) - ((high[i - 1] + low[i - 1]) / 2);
            var hlr = high[i] - low[i];
            raw[i] = volume[i] > 0 && hlr > 0 ? divisor * chl2 * hlr / volume[i] : 0.0;
        }

        return WinMean(raw, length);
    }

    public static double[] ForceIndex(double[] close, double[] volume, int length)
    {
        var raw = Full(close.Length);
        for (var i = 1; i < raw.Length; i++) raw[i] = volume[i] * (close[i] - close[i - 1]);
        return EmaFirstValid(raw, length);
    }

    public static double[] Nvi(double[] close, double[] volume)
    {
        var o = new double[close.Length];
        double acc = 0;
        for (var i = 1; i < o.Length; i++)
        {
            var roc = close[i - 1] != 0 ? (close[i] - close[i - 1]) / close[i - 1] * 100.0 : 0.0;
            acc += volume[i] < volume[i - 1] ? roc : 0.0;
            o[i] = acc;
        }

        return o;
    }

    public static double[] Pvi(double[] close, double[] volume, double initial)
    {
        var o = Full(close.Length);
        o[0] = initial;
        for (var i = 1; i < o.Length; i++)
        {
            var ratio = volume[i] > volume[i - 1] && close[i - 1] != 0 ? close[i] / close[i - 1] : 1.0;
            o[i] = o[i - 1] * ratio;
        }

        return o;
    }

    public static double[] Pvt(double[] close, double[] volume)
    {
        var o = new double[close.Length];
        double acc = 0;
        for (var i = 1; i < o.Length; i++)
        {
            acc += close[i - 1] != 0 ? (close[i] - close[i - 1]) / close[i - 1] * volume[i] : 0.0;
            o[i] = acc;
        }

        return o;
    }

    public static double[] Vroc(double[] volume, int period)
    {
        var o = Full(volume.Length);
        for (var i = period; i < o.Length; i++) o[i] = volume[i - period] != 0 ? (volume[i] - volume[i - period]) / volume[i - period] * 100.0 : 0.0;
        return o;
    }

    public static double[] Volosc(double[] volume, int shortLength, int longLength)
    {
        var se = EmaFirstValid(volume, shortLength);
        var le = EmaFirstValid(volume, longLength);
        return Zip(se, le, (s, l) => !double.IsNaN(l) && l != 0 ? 100.0 * (s - l) / l : NaN);
    }

    public static (double[] Kvo, double[] Trigger) Kvo(double[] high, double[] low, double[] close, double[] volume, int trigLen, int fastX, int slowX)
    {
        var n = close.Length;
        var hlc3 = new double[n];
        for (var i = 0; i < n; i++) hlc3[i] = (high[i] + low[i] + close[i]) / 3.0;
        var xt = new double[n];
        if (n > 0) xt[0] = volume[0] * 100.0;
        for (var i = 1; i < n; i++) xt[i] = hlc3[i] > hlc3[i - 1] ? volume[i] * 100.0 : -volume[i] * 100.0;
        var k = Sub(Ema(xt, fastX), Ema(xt, slowX));
        return (k, Ema(k, trigLen));
    }

    public static double[] Rvol(double[] volume, int period)
    {
        var avg = WinMean(volume, period);
        var o = new double[volume.Length];
        for (var i = 0; i < o.Length; i++) o[i] = double.IsNaN(avg[i]) ? NaN : avg[i] > 0 ? volume[i] / avg[i] : 1.0;
        return o;
    }

    public static double[] Cho(double[] high, double[] low, double[] close, double[] volume, int fast, int slow)
    {
        var n = close.Length;
        var adl = new double[n];
        double acc = 0;
        for (var i = 0; i < n; i++)
        {
            acc += Mfm(high[i], low[i], close[i]) * volume[i];
            adl[i] = acc;
        }

        return Sub(Ema(adl, fast), Ema(adl, slow));
    }

    public static (double[] Vwap, double[] Sd) SessionVwap(double[] source, double[] volume, double[] starts)
    {
        var n = source.Length;
        var vwap = Full(n);
        var sd = Full(n);
        double spv = 0, sv = 0, spv2 = 0;
        for (var i = 0; i < n; i++)
        {
            if (starts[i] != 0.0 || i == 0) spv = sv = spv2 = 0.0;
            spv += source[i] * volume[i];
            sv += volume[i];
            spv2 += source[i] * source[i] * volume[i];
            if (sv > 0)
            {
                vwap[i] = spv / sv;
                sd[i] = Math.Sqrt(Math.Max(0.0, (spv2 / sv) - (vwap[i] * vwap[i])));
            }
            else
            {
                vwap[i] = source[i];
                sd[i] = 0.0;
            }
        }

        return (vwap, sd);
    }
}
