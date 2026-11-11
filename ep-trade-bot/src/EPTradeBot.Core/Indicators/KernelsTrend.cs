namespace EPTradeBot.Core.Indicators;

public static partial class Kernels
{
    public static (double[] DiPlus, double[] DiMinus, double[] Adx) Adx(double[] high, double[] low, double[] close, int period)
    {
        var n = close.Length;
        var tr = TrueRange(high, low, close);
        var dmp = new double[n];
        var dmm = new double[n];
        for (var i = 1; i < n; i++)
        {
            var up = high[i] - high[i - 1];
            var dn = low[i - 1] - low[i];
            dmp[i] = up > dn && up > 0 ? up : 0.0;
            dmm[i] = dn > up && dn > 0 ? dn : 0.0;
        }

        var smAtr = EmaWilder(tr, period);
        var smP = EmaWilder(dmp, period);
        var smM = EmaWilder(dmm, period);
        var dip = Full(n);
        var dim = Full(n);
        var dx = Full(n);
        for (var i = period - 1; i < n; i++)
        {
            if (double.IsNaN(smAtr[i]) || !(smAtr[i] > 0)) continue;
            dip[i] = smP[i] / smAtr[i] * 100;
            dim[i] = smM[i] / smAtr[i] * 100;
            var sum = dip[i] + dim[i];
            if (sum > 0) dx[i] = Math.Abs(dip[i] - dim[i]) / sum * 100;
        }

        return (dip, dim, EmaWilder(dx, period));
    }

    public static (double[] Up, double[] Down) Aroon(double[] high, double[] low, int period)
    {
        var n = high.Length;
        var up = Full(n);
        var down = Full(n);
        var lookback = period + 1;
        for (var i = lookback - 1; i < n; i++)
        {
            var ws = i - lookback + 1;
            int hp = 0, lp = 0;
            for (var j = 0; j < lookback; j++)
            {
                if (high[ws + j] > high[ws + hp]) hp = j;
                if (low[ws + j] < low[ws + lp]) lp = j;
            }

            up[i] = 100.0 * (period - (lookback - 1 - hp)) / period;
            down[i] = 100.0 * (period - (lookback - 1 - lp)) / period;
        }

        return (up, down);
    }

    public static double[] AroonOsc(double[] high, double[] low, int period)
    {
        var (u, d) = Aroon(high, low, period);
        return Sub(u, d);
    }

    public static double[][] PivotPoints(double[] high, double[] low, double[] close)
    {
        var n = high.Length;
        var r = Enumerable.Range(0, 7).Select(_ => new double[n]).ToArray();
        for (var i = 0; i < n; i++)
        {
            var p = (high[i] + low[i] + close[i]) / 3;
            r[0][i] = p;
            r[1][i] = (2 * p) - low[i];
            r[2][i] = (2 * p) - high[i];
            r[3][i] = p + (high[i] - low[i]);
            r[4][i] = p - (high[i] - low[i]);
            r[5][i] = high[i] + (2 * (p - low[i]));
            r[6][i] = low[i] - (2 * (high[i] - p));
        }

        return r;
    }

    public static (double[] High, double[] Low) Rwi(double[] high, double[] low, double[] close, int period)
    {
        var n = close.Length;
        var atr = WinMean(TrueRange(high, low, close), period);
        var rh = Full(n);
        var rl = Full(n);
        var sq = Math.Sqrt(period);
        for (var i = period; i < n; i++)
        {
            if (!(atr[i] > 0)) continue;
            rh[i] = (high[i] - low[i - period]) / (atr[i] * sq);
            rl[i] = (high[i - period] - low[i]) / (atr[i] * sq);
        }

        return (rh, rl);
    }

    public static (double[] Up, double[] Down) WilliamsFractals(double[] high, double[] low, int periods)
    {
        var n = periods;
        var length = high.Length;
        var fu = new double[length];
        var fd = new double[length];
        for (var center = n; center < length - n; center++)
        {
            fu[center] = Fractal(high, center, n, length, upper: true) ? 1.0 : 0.0;
            fd[center] = Fractal(low, center, n, length, upper: false) ? 1.0 : 0.0;
        }

        return (fu, fd);
    }

    private static bool Fractal(double[] s, int center, int n, int length, bool upper)
    {
        bool Worse(double other, double mid, bool strict) => upper ? (strict ? other > mid : other >= mid) : (strict ? other < mid : other <= mid);
        for (var i = 1; i <= n; i++) if (Worse(s[center - i], s[center], false)) return false;
        var f = new[] { true, true, true, true, true };
        for (var i = 1; i <= n; i++)
        {
            if (center + i < length && Worse(s[center + i], s[center], false)) f[0] = false;
            for (var p = 1; p < 5; p++)
            {
                for (var q = 1; q <= p; q++) if (center + q < length && Worse(s[center + q], s[center], true)) f[p] = false;
                if (center + i + p < length && Worse(s[center + i + p], s[center], false)) f[p] = false;
            }
        }

        return f.Any(x => x);
    }

    private static (double Slope, double Intercept, bool Ok) LinregEnd(ReadOnlySpan<double> y, int period)
    {
        double sx = 0, sy = 0, sxy = 0, sx2 = 0;
        for (var i = 0; i < period; i++)
        {
            sx += i;
            sy += y[i];
            sxy += i * y[i];
            sx2 += (double)i * i;
        }

        var den = (period * sx2) - (sx * sx);
        if (den == 0) return (0, 0, false);
        var slope = ((period * sxy) - (sx * sy)) / den;
        return (slope, (sy - (slope * sx)) / period, true);
    }

    /// <summary>Least-squares line value at the window's end. The sums run on, so one NaN in the data leaves every value after it NaN.</summary>
    public static double[] Linreg(double[] data, int period)
    {
        var o = Roll(data, period, y =>
        {
            var (s, b, ok) = LinregEnd(y, period);
            return ok ? (s * (period - 1)) + b : y[^1];
        });
        var firstNaN = Array.FindIndex(data, double.IsNaN);
        if (firstNaN >= 0) for (var i = firstNaN; i < o.Length; i++) o[i] = NaN;
        return o;
    }

    public static double[] Tsf(double[] data, int period) => Roll(data, period, y =>
    {
        var (s, b, ok) = LinregEnd(y, period);
        return ok ? (s * period) + b : y[^1];
    });

    public static double[] Lrslope(double[] data, int period, double interval)
    {
        var n = data.Length;
        var o = Full(n);
        double End(ReadOnlySpan<double> y)
        {
            var (s, b, ok) = LinregEnd(y, period);
            return ok ? (s * (period - 1)) + b : y[^1];
        }

        for (var i = period; i < n; i++) o[i] = (End(data.AsSpan(i - period + 1, period)) - End(data.AsSpan(i - period, period))) / interval;
        return o;
    }

    public static double[] LinregAngle(double[] data, int period) => Roll(data, period, y =>
    {
        double sx = 0, sy = 0, sxy = 0, sx2 = 0;
        for (var i = 0; i < period; i++)
        {
            sx += i;
            sy += y[i];
            sxy += i * y[i];
            sx2 += (double)i * i;
        }

        var den = (period * sx2) - (sx * sx);
        var slope = den != 0 ? ((period * sxy) - (sx * sy)) / den : 0.0;
        return Math.Atan(slope) * 180.0 / Math.PI;
    });

    public static double[] LinregIntercept(double[] data, int period) => Roll(data, period, y =>
    {
        double sx = 0, sy = 0, sxy = 0, sx2 = 0;
        for (var i = 0; i < period; i++)
        {
            sx += i;
            sy += y[i];
            sxy += i * y[i];
            sx2 += (double)i * i;
        }

        var den = (period * sx2) - (sx * sx);
        var slope = den != 0 ? ((period * sxy) - (sx * sy)) / den : 0.0;
        return (sy - (slope * sx)) / period;
    });

    public static double[] Correl(double[] d1, double[] d2, int period)
    {
        var n = d1.Length;
        var o = Full(n);
        for (var i = period - 1; i < n; i++)
        {
            double mx = 0, my = 0;
            for (var j = i - period + 1; j <= i; j++)
            {
                mx += d1[j];
                my += d2[j];
            }

            mx /= period;
            my /= period;
            double num = 0, sx = 0, sy = 0;
            for (var j = i - period + 1; j <= i; j++)
            {
                num += (d1[j] - mx) * (d2[j] - my);
                sx += (d1[j] - mx) * (d1[j] - mx);
                sy += (d2[j] - my) * (d2[j] - my);
            }

            var den = Math.Sqrt(sx * sy);
            o[i] = den > 0 ? num / den : 0.0;
        }

        return o;
    }

    public static double[] Beta(double[] asset, double[] market, int period)
    {
        var n = asset.Length;
        var o = Full(n);
        var ar = Full(n);
        var mr = Full(n);
        for (var i = 1; i < n; i++)
        {
            ar[i] = asset[i] - asset[i - 1];
            mr[i] = market[i] - market[i - 1];
        }

        for (var i = period; i < n; i++)
        {
            double ma = 0, mm = 0;
            for (var j = i - period + 1; j <= i; j++)
            {
                ma += ar[j];
                mm += mr[j];
            }

            ma /= period;
            mm /= period;
            double cov = 0, mv = 0;
            for (var j = i - period + 1; j <= i; j++)
            {
                cov += (ar[j] - ma) * (mr[j] - mm);
                mv += (mr[j] - mm) * (mr[j] - mm);
            }

            cov /= period;
            mv /= period;
            o[i] = mv > 0 ? cov / mv : 0.0;
        }

        return o;
    }

    public static double[][] Variance(double[] data, int lookback, string mode, int emaPeriod, int filterLookback, int emaLength, bool components)
    {
        var n = data.Length;
        var source = Full(n);
        if (mode == "LR")
        {
            for (var i = 1; i < n; i++) if (data[i] > 0 && data[i - 1] > 0) source[i] = Math.Log(data[i] / data[i - 1]) * 100;
        }
        else
        {
            source = (double[])data.Clone();
        }

        var variance = Full(n);
        var sd = Full(n);
        if (n >= lookback && lookback > 1)
        {
            double rs = 0, rsq = 0;
            for (var i = 0; i < lookback; i++)
            {
                if (double.IsNaN(source[i])) continue;
                rs += source[i];
                rsq += source[i] * source[i];
            }

            var mean = rs / lookback;
            var v = (rsq - (lookback * mean * mean)) / (lookback - 1);
            if (v >= 0)
            {
                variance[lookback - 1] = v;
                sd[lookback - 1] = Math.Sqrt(v);
            }

            for (var i = lookback; i < n; i++)
            {
                var old = source[i - lookback];
                var cur = source[i];
                if (!double.IsNaN(old))
                {
                    rs -= old;
                    rsq -= old * old;
                }

                if (!double.IsNaN(cur))
                {
                    rs += cur;
                    rsq += cur * cur;
                }

                mean = rs / lookback;
                v = (rsq - (lookback * mean * mean)) / (lookback - 1);
                if (v >= 0)
                {
                    variance[i] = v;
                    sd[i] = Math.Sqrt(v);
                }
            }
        }

        if (!components) return [variance];
        var emaVar = Ema(variance, emaPeriod);
        var varSma = Sma(variance, filterLookback);
        var varSd = Stdev(variance, filterLookback);
        var z = Full(n);
        for (var i = 0; i < n; i++) if (!double.IsNaN(variance[i]) && !double.IsNaN(varSma[i]) && !double.IsNaN(varSd[i]) && varSd[i] > 0) z[i] = (variance[i] - varSma[i]) / varSd[i];
        return [variance, emaVar, z, Ema(z, emaLength), sd];
    }

    public static double[] Median(double[] data, int period) => Roll(data, period, w =>
    {
        var a = w.ToArray();
        foreach (var x in a) if (double.IsNaN(x)) return NaN;
        Array.Sort(a);
        return a.Length % 2 == 1 ? a[a.Length / 2] : (a[(a.Length / 2) - 1] + a[a.Length / 2]) / 2.0;
    });

    private static double[] MedianPnr(double[] data, int period) => Roll(data, period, w =>
    {
        var a = w.ToArray();
        Array.Sort(a);
        return period % 2 == 1 ? a[period / 2] : a[(period / 2) - 1];
    });

    public static double[][] MedianBands(double[] high, double[] low, double[] close, double[]? source, int medianLength, int atrLength, double atrMult)
    {
        var src = source ?? Zip(high, low, (h, l) => (h + l) / 2.0);
        var med = MedianPnr(src, medianLength);
        var atr = Mul(AtrWilder(high, low, close, atrLength), atrMult);
        return [med, Add(med, atr), Sub(med, atr), EmaFirstValid(med, medianLength)];
    }

    public static double[] Mode(double[] data, int period, int bins) => Roll(data, period, w =>
    {
        double mn = double.PositiveInfinity, mx = double.NegativeInfinity;
        foreach (var x in w)
        {
            if (x < mn) mn = x;
            if (x > mx) mx = x;
        }

        if (!(mx > mn)) return w[0];
        var bw = (mx - mn) / bins;
        var counts = new int[bins];
        foreach (var x in w) counts[Math.Clamp((int)((x - mn) / bw), 0, bins - 1)]++;
        var best = 0;
        for (var i = 1; i < bins; i++) if (counts[i] > counts[best]) best = i;
        return mn + ((best + 0.5) * bw);
    });

    public static (double[] Plus, double[] Minus) Vi(double[] high, double[] low, double[] close, int period)
    {
        var n = close.Length;
        var vmp = Full(n);
        var vmm = Full(n);
        for (var i = 1; i < n; i++)
        {
            vmp[i] = Math.Abs(high[i] - low[i - 1]);
            vmm[i] = Math.Abs(low[i] - high[i - 1]);
        }

        var tr = TrueRange(high, low, close);
        var a = RollingSum(vmp, period);
        var b = RollingSum(vmm, period);
        var c = RollingSum(tr, period);
        var vip = Full(n);
        var vim = Full(n);
        for (var i = period; i < n; i++)
        {
            var ok = c[i] > 0 && !double.IsNaN(c[i]);
            vip[i] = ok ? a[i] / c[i] : 0.0;
            vim[i] = ok ? b[i] / c[i] : 0.0;
        }

        return (vip, vim);
    }

    public static (double[] Upper, double[] Lower) Gator(double[] high, double[] low, int jawP, int teethP, int lipsP)
    {
        var hl2 = Zip(high, low, (h, l) => (h + l) / 2.0);
        var jaw = Shift(EmaWilder(hl2, jawP), 8);
        var teeth = Shift(EmaWilder(hl2, teethP), 5);
        var lips = Shift(EmaWilder(hl2, lipsP), 3);
        return (Zip(jaw, teeth, (a, b) => Math.Abs(a - b)), Zip(teeth, lips, (a, b) => -Math.Abs(a - b)));
    }

    public static double[] StochSingle(double[] data, int period)
    {
        var n = data.Length;
        var o = Full(n);
        for (var i = period - 1; i < n; i++)
        {
            double hi = double.NegativeInfinity, lo = double.PositiveInfinity;
            var any = false;
            for (var j = i - period + 1; j <= i; j++)
            {
                if (double.IsNaN(data[j])) continue;
                any = true;
                hi = Math.Max(hi, data[j]);
                lo = Math.Min(lo, data[j]);
            }

            if (!any) continue;
            if (hi != lo && !double.IsNaN(data[i])) o[i] = (data[i] - lo) / (hi - lo) * 100.0;
            else if (hi == lo) o[i] = 50.0;
        }

        return o;
    }

    public static double[] Stc(double[] data, int fast, int slow, int cycle, int d1, int d2)
    {
        var macd = Sub(Ema(data, fast), Ema(data, slow));
        var k = Map(StochSingle(macd, cycle), x => double.IsNaN(x) ? 0.0 : x);
        var d = Ema(k, d1);
        var kd = Map(StochSingle(d, cycle), x => double.IsNaN(x) ? 0.0 : x);
        return Map(Ema(kd, d2), x => Math.Clamp(x, 0.0, 100.0));
    }

    public static double[] Coppock(double[] data, int wmaLength, int longRoc, int shortRoc) => WmaNan(Add(Roc(data, longRoc), Roc(data, shortRoc)), wmaLength);

    public static (double[] Rvi, double[] Signal) RviVigor(double[] open, double[] high, double[] low, double[] close, int period)
    {
        var sco = Swma(Sub(close, open));
        var shl = Swma(Sub(high, low));
        var n = close.Length;
        var rvi = Full(n);
        for (var i = period + 2; i < n; i++)
        {
            double ns = 0, ds = 0;
            for (var j = i - period + 1; j <= i; j++)
            {
                if (!double.IsNaN(sco[j])) ns += sco[j];
                if (!double.IsNaN(shl[j])) ds += shl[j];
            }

            rvi[i] = ds != 0.0 ? ns / ds : 0.0;
        }

        return (rvi, Swma(rvi));
    }

    public static (double[] Kst, double[] Signal) Kst(double[] data, int r1, int r2, int r3, int r4, int s1, int s2, int s3, int s4, int sig)
    {
        var a = WinMean(RocOsc(data, r1), s1);
        var b = WinMean(RocOsc(data, r2), s2);
        var c = WinMean(RocOsc(data, r3), s3);
        var d = WinMean(RocOsc(data, r4), s4);
        var k = new double[data.Length];
        for (var i = 0; i < k.Length; i++) k[i] = (a[i] * 1) + (b[i] * 2) + (c[i] * 3) + (d[i] * 4);
        return (k, WinMean(k, sig));
    }

    public static (double[] Tsi, double[] Signal) Tsi(double[] data, int longP, int shortP, int signalP)
    {
        var n = data.Length;
        var pc = new double[n];
        var apc = new double[n];
        for (var i = 1; i < n; i++)
        {
            pc[i] = data[i] - data[i - 1];
            apc[i] = Math.Abs(pc[i]);
        }

        var a = Ema(Ema(pc, longP), shortP);
        var b = Ema(Ema(apc, longP), shortP);
        var t = Zip(a, b, (x, y) => y != 0 ? 100 * (x / y) : 0.0);
        return (t, Ema(t, signalP));
    }

    public static double[] Uo(double[] high, double[] low, double[] close, int p1, int p2, int p3)
    {
        var n = close.Length;
        var tl = new double[n];
        if (n > 0) tl[0] = low[0];
        for (var i = 1; i < n; i++) tl[i] = Math.Min(low[i], close[i - 1]);
        var tr = TrueRange(high, low, close);
        var bp = Sub(close, tl);
        double[] Avg(int p)
        {
            var bps = Roll(bp, p, w => { double s = 0; foreach (var x in w) s += x; return s; });
            var trs = Roll(tr, p, w => { double s = 0; foreach (var x in w) s += x; return s; });
            return Zip(bps, trs, (x, y) => y > 0 ? x / y : 0.0);
        }

        var a1 = Avg(p1);
        var a2 = Avg(p2);
        var a3 = Avg(p3);
        var o = new double[n];
        var mp = Math.Max(p1, Math.Max(p2, p3));
        for (var i = 0; i < n; i++) o[i] = i < mp - 1 ? NaN : 100.0 * ((4 * a1[i]) + (2 * a2[i]) + a3[i]) / 7.0;
        return o;
    }

    public static double[] Ultosc(double[] high, double[] low, double[] close, int p1, int p2, int p3)
    {
        var n = close.Length;
        var tr = TrueRange(high, low, close);
        var bp = new double[n];
        if (n > 0) bp[0] = close[0] - Math.Min(low[0], close[0]);
        for (var i = 1; i < n; i++) bp[i] = close[i] - Math.Min(low[i], close[i - 1]);
        double[] Raw(int p)
        {
            var a = RollingSum(bp, p);
            var b = RollingSum(tr, p);
            return Zip(a, b, (x, y) => y > 0 ? x / y : 0.0);
        }

        var r1 = Raw(p1);
        var r2 = Raw(p2);
        var r3 = Raw(p3);
        var o = new double[n];
        var mp = Math.Max(p1, Math.Max(p2, p3));
        for (var i = 0; i < n; i++) o[i] = i < mp - 1 ? NaN : 100.0 * ((4 * r1[i]) + (2 * r2[i]) + r3[i]) / 7.0;
        return o;
    }

    public static (double[] K, double[] D) StochRsi(double[] data, int rsiP, int stochP, int kP, int dP)
    {
        var r = Rsi(data, rsiP);
        var n = r.Length;
        var sr = Full(n);
        for (var i = stochP - 1; i < n; i++)
        {
            double hi = double.NegativeInfinity, lo = double.PositiveInfinity;
            var any = false;
            for (var j = i - stochP + 1; j <= i; j++)
            {
                if (double.IsNaN(r[j])) continue;
                any = true;
                hi = Math.Max(hi, r[j]);
                lo = Math.Min(lo, r[j]);
            }

            if (any) sr[i] = hi != lo ? (r[i] - lo) / (hi - lo) * 100 : 50.0;
        }

        double[] Smooth(double[] src, int p)
        {
            var o = Full(n);
            for (var i = p - 1; i < n; i++)
            {
                double s = 0;
                var c = 0;
                for (var j = i - p + 1; j <= i; j++)
                {
                    if (double.IsNaN(src[j])) continue;
                    s += src[j];
                    c++;
                }

                if (c > 0) o[i] = s / c;
            }

            return o;
        }

        var k = Smooth(sr, kP);
        return (k, Smooth(k, dP));
    }

    public static double[] Chop(double[] high, double[] low, double[] close, int period)
    {
        var n = close.Length;
        var sum = RollingSum(TrueRange(high, low, close), period);
        var hh = Highest(high, period);
        var ll = Lowest(low, period);
        var o = Full(n);
        for (var i = 0; i < n; i++)
        {
            var range = hh[i] - ll[i];
            var valid = range > 0 && sum[i] > 0 && !double.IsNaN(range) && !double.IsNaN(sum[i]);
            if (valid) { if (period > 1) o[i] = 100 * Math.Log10(sum[i] / range) / Math.Log10(period); }
            else if (!double.IsNaN(sum[i])) o[i] = 50.0;
        }

        return o;
    }

    public static double[] Cmo(double[] data, int period)
    {
        var n = data.Length;
        var o = Full(n);
        for (var i = period; i < n; i++)
        {
            double up = 0, dn = 0;
            for (var j = i - period + 1; j <= i; j++)
            {
                var d = data[j] - data[j - 1];
                if (d > 0) up += d;
                else dn += -d;
            }

            o[i] = up + dn != 0 ? 100.0 * (up - dn) / (up + dn) : 0.0;
        }

        return o;
    }

    public static double[] Trix(double[] data, int length)
    {
        var e = Ema(Ema(Ema(Map(data, Math.Log), length), length), length);
        var o = Full(data.Length);
        for (var i = 1; i < o.Length; i++) o[i] = (e[i] - e[i - 1]) * 10000.0;
        return o;
    }

    public static double[] Ao(double[] high, double[] low, int fast, int slow)
    {
        var mp = Zip(high, low, (h, l) => (h + l) / 2.0);
        return Sub(WinMean(mp, fast), WinMean(mp, slow));
    }

    public static double[] Ac(double[] high, double[] low, int period)
    {
        var a = Ao(high, low, 5, 34);
        return Sub(a, WinMean(a, period));
    }

    public static (double[] Ppo, double[] Signal, double[] Hist) Ppo(double[] data, int fast, int slow, int signal)
    {
        var fe = Ema(data, fast);
        var se = Ema(data, slow);
        var line = Zip(fe, se, (f, s) => s != 0 ? (f - s) / s * 100.0 : 0.0);
        var sig = Ema(line, signal);
        return (line, sig, Sub(line, sig));
    }

    public static double[] PriceOscillator(double[] data, int fast, int slow, string maType)
    {
        if (maType.Equals("SMA", StringComparison.OrdinalIgnoreCase)) return Sub(WinMean(data, fast), WinMean(data, slow));
        if (maType.Equals("EMA", StringComparison.OrdinalIgnoreCase)) return Sub(Ema(data, fast), Ema(data, slow));
        throw new ArgumentException($"Unsupported MA type: {maType}");
    }

    public static double[] Apo(double[] data, int fast, int slow, string maType) =>
        maType.Equals("EMA", StringComparison.OrdinalIgnoreCase) ? Sub(Ema(data, fast), Ema(data, slow)) : Sub(WinMean(data, fast), WinMean(data, slow));

    public static double[] Dpo(double[] data, int period, bool centered)
    {
        var n = data.Length;
        var sma = WinMean(data, period);
        var back = (int)((period / 2.0) + 1);
        var o = Full(n);
        if (back >= n) return o;
        for (var i = back; i < n; i++)
        {
            if (centered) { var sh = sma[i]; o[i] = double.IsNaN(sh) ? NaN : data[i - back] - sh; }
            else { var sh = sma[i - back]; o[i] = double.IsNaN(sh) ? NaN : data[i] - sh; }
        }

        return o;
    }
}
