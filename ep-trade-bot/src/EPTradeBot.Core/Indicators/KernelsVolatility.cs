namespace EPTradeBot.Core.Indicators;

public static partial class Kernels
{
    public static double[] Midpoint(double[] data, int period) => Zip(Highest(data, period), Lowest(data, period), (h, l) => (h + l) / 2.0);

    public static double[] Midprice(double[] high, double[] low, int period) => Zip(Highest(high, period), Lowest(low, period), (h, l) => (h + l) / 2.0);

    public static double[] PlusDm(double[] high, double[] low, int period) => DirectionalSum(high, low, period, plus: true);

    public static double[] MinusDm(double[] high, double[] low, int period) => DirectionalSum(high, low, period, plus: false);

    private static double[] DirectionalSum(double[] high, double[] low, int period, bool plus)
    {
        var n = high.Length;
        var o = Full(n);
        if (period == 0 || n < period) return o;
        double prev = 0;
        for (var i = 1; i < period; i++)
        {
            var dp = high[i] - high[i - 1];
            var dm = low[i - 1] - low[i];
            if (plus ? dp > 0.0 && dp > dm : dm > 0.0 && dm > dp) prev += plus ? dp : dm;
        }

        o[period - 1] = prev;
        for (var i = period; i < n; i++)
        {
            var dp = high[i] - high[i - 1];
            var dm = low[i - 1] - low[i];
            var add = plus ? (dp > 0.0 && dp > dm ? dp : 0.0) : (dm > 0.0 && dm > dp ? dm : 0.0);
            prev = prev - (prev / period) + add;
            o[i] = prev;
        }

        return o;
    }

    private static (double Tr, double P, double M) TalibSeed(double[] high, double[] low, double[] tr, int period)
    {
        double sTr = 0, sP = 0, sM = 0;
        for (var i = 1; i < period; i++)
        {
            var dp = high[i] - high[i - 1];
            var dm = low[i - 1] - low[i];
            if (dp > 0.0 && dp > dm) sP += dp;
            else if (dm > 0.0 && dm > dp) sM += dm;
            sTr += tr[i];
        }

        return (sTr, sP, sM);
    }

    private static double TalibStep(double[] high, double[] low, double[] tr, int i, int period, ref double sTr, ref double sP, ref double sM)
    {
        var dp = high[i] - high[i - 1];
        var dm = low[i - 1] - low[i];
        sTr = sTr - (sTr / period) + tr[i];
        sP = sP - (sP / period) + (dp > 0.0 && dp > dm ? dp : 0.0);
        sM = sM - (sM / period) + (dm > 0.0 && dm > dp ? dm : 0.0);
        if (sTr == 0.0) return 0.0;
        var pdi = 100.0 * sP / sTr;
        var mdi = 100.0 * sM / sTr;
        var den = pdi + mdi;
        return den != 0.0 ? 100.0 * Math.Abs(pdi - mdi) / den : 0.0;
    }

    public static double[] Dx(double[] high, double[] low, double[] close, int period)
    {
        var n = close.Length;
        var o = Full(n);
        if (period == 0 || n < period + 1) return o;
        var tr = TrueRange(high, low, close);
        var (sTr, sP, sM) = TalibSeed(high, low, tr, period);
        for (var i = period; i < n; i++) o[i] = TalibStep(high, low, tr, i, period, ref sTr, ref sP, ref sM);
        return o;
    }

    public static double[] AdxTalib(double[] high, double[] low, double[] close, int period)
    {
        var n = close.Length;
        var o = Full(n);
        if (period == 0 || n < 2 * period) return o;
        var tr = TrueRange(high, low, close);
        var (sTr, sP, sM) = TalibSeed(high, low, tr, period);
        double sum = 0;
        for (var i = period; i < 2 * period; i++) sum += TalibStep(high, low, tr, i, period, ref sTr, ref sP, ref sM);
        var prev = sum / period;
        o[(2 * period) - 1] = prev;
        for (var i = 2 * period; i < n; i++)
        {
            var d = TalibStep(high, low, tr, i, period, ref sTr, ref sP, ref sM);
            prev = ((prev * (period - 1)) + d) / period;
            o[i] = prev;
        }

        return o;
    }

    public static double[] Adxr(double[] high, double[] low, double[] close, int period)
    {
        var a = AdxTalib(high, low, close, period);
        var o = Full(a.Length);
        if (period == 0) return o;
        var off = period - 1;
        for (var i = (2 * period) - 1 + off; i < a.Length; i++) if (!double.IsNaN(a[i]) && !double.IsNaN(a[i - off])) o[i] = (a[i] + a[i - off]) / 2.0;
        return o;
    }

    public static (double[] K, double[] D) Stochf(double[] high, double[] low, double[] close, int fastK, int fastD)
    {
        var n = close.Length;
        var k = Full(n);
        var d = Full(n);
        if (fastK == 0 || fastD == 0 || n < fastK) return (k, d);
        var hh = Highest(high, fastK);
        var ll = Lowest(low, fastK);
        var raw = Full(n);
        for (var i = fastK - 1; i < n; i++)
        {
            var range = hh[i] - ll[i];
            raw[i] = range != 0.0 ? 100.0 * (close[i] - ll[i]) / range : 0.0;
        }

        for (var i = fastK - 1 + fastD - 1; i < n; i++)
        {
            double s = 0;
            for (var j = i + 1 - fastD; j <= i; j++) s += raw[j];
            d[i] = s / fastD;
            k[i] = raw[i];
        }

        return (k, d);
    }

    public static double[] BbPercent(double[] data, int period, double std)
    {
        var (up, _, lo) = BbBands(data, period, std);
        var o = new double[data.Length];
        for (var i = 0; i < o.Length; i++) o[i] = !double.IsNaN(up[i]) && up[i] == lo[i] ? 0.5 : (data[i] - lo[i]) / (up[i] - lo[i]);
        return o;
    }

    public static double[] BbWidth(double[] data, int period, double std)
    {
        var (up, m, lo) = BbBands(data, period, std);
        var o = new double[data.Length];
        for (var i = 0; i < o.Length; i++) o[i] = !double.IsNaN(m[i]) && m[i] == 0 ? 0.0 : (up[i] - lo[i]) / m[i];
        return o;
    }

    private static (double[] Up, double[] Mid, double[] Lo) BbBands(double[] data, int period, double std)
    {
        var m = WinMean(data, period);
        var sd = WinStd(data, period);
        return (Zip(m, sd, (a, s) => a + (s * std)), m, Zip(m, sd, (a, s) => a - (s * std)));
    }

    public static (double[] Long, double[] Short) ChandelierExit(double[] high, double[] low, double[] close, int period, double mult)
    {
        var atr = WinMean(TrueRange(high, low, close), period);
        var hh = Highest(high, period);
        var ll = Lowest(low, period);
        return (Zip(hh, atr, (h, a) => h - (a * mult)), Zip(ll, atr, (l, a) => l + (a * mult)));
    }

    public static double[] Hv(double[] close, int length, double annual, double per)
    {
        var n = close.Length;
        var lr = Full(n);
        for (var i = 1; i < n; i++) if (close[i - 1] > 0 && close[i] > 0) lr[i] = Math.Log(close[i] / close[i - 1]);
        return Map(WinStd(lr, length), sd => 100.0 * sd * Math.Sqrt(annual / per));
    }

    public static double[][] UlcerIndex(double[] data, int length, int smooth, int signalLength, string signalType, bool returnSignal)
    {
        var hh = Highest(data, length);
        var dd = Full(data.Length);
        for (var i = 0; i < dd.Length; i++) if (!double.IsNaN(hh[i]) && hh[i] != 0) dd[i] = 100.0 * (data[i] - hh[i]) / hh[i];
        var ulcer = Map(Sma(Map(dd, x => x * x), smooth), Math.Sqrt);
        if (!returnSignal) return [ulcer];
        return [ulcer, signalType.Equals("SMA", StringComparison.OrdinalIgnoreCase) ? Sma(ulcer, signalLength) : Ema(ulcer, signalLength)];
    }

    public static (double[] Upper, double[] Mid, double[] Lower) Starc(double[] high, double[] low, double[] close, int maPeriod, int atrPeriod, double mult)
    {
        var m = WinMean(close, maPeriod);
        var atr = WinMean(TrueRange(high, low, close), atrPeriod);
        return (Zip(m, atr, (a, r) => a + (r * mult)), m, Zip(m, atr, (a, r) => a - (r * mult)));
    }

    public static (double[] Upper, double[] Mid, double[] Lower) Keltner(double[] high, double[] low, double[] close, int emaPeriod, int atrPeriod, double mult)
    {
        var mid = EmaSma(close, emaPeriod);
        var atr = AtrWilder(high, low, close, atrPeriod);
        return (Zip(mid, atr, (m, a) => m + (mult * a)), mid, Zip(mid, atr, (m, a) => m - (mult * a)));
    }

    public static (double[] Upper, double[] Mid, double[] Lower) Donchian(double[] high, double[] low, int period)
    {
        var u = Highest(high, period);
        var l = Lowest(low, period);
        return (u, Zip(u, l, (a, b) => (a + b) / 2.0), l);
    }

    public static double[] Chaikin(double[] high, double[] low, int emaPeriod, int rocPeriod) => Roc(EmaSma(Sub(high, low), emaPeriod), rocPeriod);

    public static double[] Natr(double[] high, double[] low, double[] close, int period)
    {
        var atr = AtrWilder(high, low, close, period);
        var o = new double[close.Length];
        for (var i = 0; i < o.Length; i++) o[i] = close[i] != 0 ? atr[i] / close[i] * 100.0 : 0.0;
        return o;
    }

    public static double[] RviVolatility(double[] data, int stdevPeriod, int rsiPeriod) => Rsi(Stdev(data, stdevPeriod), rsiPeriod);

    public static double[] Mass(double[] high, double[] low, int length)
    {
        var span = Sub(high, low);
        var e1 = Ema(span, 9);
        var e2 = Ema(e1, 9);
        var ratio = Full(span.Length);
        for (var i = 0; i < ratio.Length; i++) if (e2[i] != 0 && !double.IsNaN(e1[i]) && !double.IsNaN(e2[i])) ratio[i] = e1[i] / e2[i];
        return RollingSum(ratio, length);
    }

    public static (double[] Supertrend, double[] Direction) Supertrend(double[] high, double[] low, double[] close, int period, double mult)
    {
        var n = close.Length;
        var st = Full(n);
        var dr = Full(n);
        var atr = AtrWilder(high, low, close, period);
        var ub = new double[n];
        var lb = new double[n];
        for (var i = 0; i < n; i++)
        {
            var hl = (high[i] + low[i]) / 2.0;
            ub[i] = hl + (mult * atr[i]);
            lb[i] = hl - (mult * atr[i]);
        }

        var fu = Full(n);
        var fl = Full(n);
        var fv = period - 1;
        if (fv >= n) return (st, dr);
        fu[fv] = ub[fv];
        fl[fv] = lb[fv];
        dr[fv] = 1.0;
        st[fv] = ub[fv];
        for (var i = fv + 1; i < n; i++)
        {
            fl[i] = lb[i] > fl[i - 1] || close[i - 1] < fl[i - 1] ? lb[i] : fl[i - 1];
            fu[i] = ub[i] < fu[i - 1] || close[i - 1] > fu[i - 1] ? ub[i] : fu[i - 1];
            if (st[i - 1] == fu[i - 1]) dr[i] = close[i] > fu[i] ? -1.0 : 1.0;
            else dr[i] = close[i] < fl[i] ? 1.0 : -1.0;
            st[i] = dr[i] == -1.0 ? fl[i] : fu[i];
        }

        return (st, dr);
    }

    public static (double[] Long, double[] Short) ChandeKroll(double[] high, double[] low, double[] close, int p, double x, int q)
    {
        var n = close.Length;
        var longStop = Full(n);
        var shortStop = Full(n);
        var atr = AtrWilder(high, low, close, p);
        var fhs = Full(n);
        var fls = Full(n);
        for (var i = p - 1; i < n; i++)
        {
            double hi = double.NegativeInfinity, lo = double.PositiveInfinity;
            for (var j = i - p + 1; j <= i; j++)
            {
                hi = Math.Max(hi, high[j]);
                lo = Math.Min(lo, low[j]);
            }

            fhs[i] = hi - (x * atr[i]);
            fls[i] = lo + (x * atr[i]);
        }

        for (var i = p + q - 2; i < n; i++)
        {
            double sh = double.NegativeInfinity, ll = double.PositiveInfinity;
            var h = false;
            var l = false;
            for (var j = i - q + 1; j <= i; j++)
            {
                if (!double.IsNaN(fhs[j])) { sh = Math.Max(sh, fhs[j]); h = true; }
                if (!double.IsNaN(fls[j])) { ll = Math.Min(ll, fls[j]); l = true; }
            }

            if (h) shortStop[i] = sh;
            if (l) longStop[i] = ll;
        }

        return (longStop, shortStop);
    }

    public static double[][] Ichimoku(double[] high, double[] low, double[] close, int conv, int baseP, int span2, int disp)
    {
        var n = close.Length;
        double[] Don(int p) => Zip(Highest(high, p), Lowest(low, p), (h, l) => (h + l) / 2.0);
        var conversion = Don(conv);
        var baseLine = Don(baseP);
        var lead1 = Zip(conversion, baseLine, (a, b) => (a + b) / 2.0);
        var lead2 = Don(span2);
        var off = disp - 1;
        var la = Full(n);
        var lb = Full(n);
        if (off > 0 && off < n)
        {
            for (var i = off; i < n; i++)
            {
                la[i] = lead1[i - off];
                lb[i] = lead2[i - off];
            }
        }
        else if (off == 0)
        {
            la = (double[])lead1.Clone();
            lb = (double[])lead2.Clone();
        }

        var lag = Full(n);
        var offLag = -disp + 1;
        if (offLag < 0)
        {
            var sh = -offLag;
            if (sh < n) for (var i = 0; i < n - sh; i++) lag[i] = close[i + sh];
        }
        else if (offLag > 0)
        {
            if (offLag < n) for (var i = offLag; i < n; i++) lag[i] = close[i - offLag];
        }
        else
        {
            lag = (double[])close.Clone();
        }

        return [conversion, baseLine, la, lb, lag];
    }

    public static (double[] Fish, double[] Trigger) Fisher(double[] high, double[] low, int length)
    {
        var n = high.Length;
        var hl2 = Zip(high, low, (h, l) => (h + l) / 2.0);
        var fish1 = Full(n);
        var fish2 = Full(n);
        double value = 0, prev = 0;
        for (var i = length - 1; i < n; i++)
        {
            double hi = double.NegativeInfinity, lo = double.PositiveInfinity;
            for (var j = i - length + 1; j <= i; j++)
            {
                hi = Math.Max(hi, hl2[j]);
                lo = Math.Min(lo, hl2[j]);
            }

            if (hi != lo)
            {
                var v = (0.66 * (((hl2[i] - lo) / (hi - lo)) - 0.5)) + (0.67 * value);
                value = v > 0.99 ? 0.999 : v < -0.99 ? -0.999 : v;
                fish1[i] = (0.5 * Math.Log((1 + value) / (1 - value))) + (0.5 * prev);
                prev = fish1[i];
            }
            else
            {
                fish1[i] = prev;
            }

            fish2[i] = i > length - 1 ? fish1[i - 1] : 0.0;
        }

        return (fish1, fish2);
    }

    public static double[] Sar(double[] high, double[] low, double acceleration, double maximum)
    {
        var n = high.Length;
        var sar = new double[n];
        var trend = new double[n];
        sar[0] = low[0];
        trend[0] = 1;
        var af = acceleration;
        var ep = high[0];
        for (var i = 1; i < n; i++)
        {
            sar[i] = sar[i - 1] + (af * (ep - sar[i - 1]));
            if (trend[i - 1] == 1)
            {
                if (low[i] <= sar[i])
                {
                    trend[i] = -1;
                    sar[i] = ep;
                    ep = low[i];
                    af = acceleration;
                }
                else
                {
                    trend[i] = 1;
                    if (high[i] > ep)
                    {
                        ep = high[i];
                        af = Math.Min(af + acceleration, maximum);
                    }

                    sar[i] = i >= 2 ? Math.Min(sar[i], Math.Min(low[i - 1], low[i - 2])) : Math.Min(sar[i], low[i - 1]);
                }
            }
            else if (high[i] >= sar[i])
            {
                trend[i] = 1;
                sar[i] = ep;
                ep = high[i];
                af = acceleration;
            }
            else
            {
                trend[i] = -1;
                if (low[i] < ep)
                {
                    ep = low[i];
                    af = Math.Min(af + acceleration, maximum);
                }

                sar[i] = i >= 2 ? Math.Max(sar[i], Math.Max(high[i - 1], high[i - 2])) : Math.Max(sar[i], high[i - 1]);
            }
        }

        return sar;
    }

    public static double[] Frama(double[] high, double[] low, int period)
    {
        var n = high.Length;
        var price = Zip(high, low, (h, l) => (h + l) / 2.0);
        var filt = Full(n);
        if (n > 0) filt[0] = price[0];
        var half = period / 2;
        for (var i = 1; i < n; i++)
        {
            if (i < period)
            {
                filt[i] = price[i];
                continue;
            }

            double mx = double.NegativeInfinity, mn = double.PositiveInfinity;
            for (var j = i - period + 1; j <= i; j++)
            {
                mx = Math.Max(mx, high[j]);
                mn = Math.Min(mn, low[j]);
            }

            var n3 = (mx - mn) / period;
            double hh = high[i], ll = low[i];
            for (var c = 0; c < half; c++)
            {
                var idx = i - c;
                if (idx < 0) continue;
                if (high[idx] > hh) hh = high[idx];
                if (low[idx] < ll) ll = low[idx];
            }

            var n1 = (hh - ll) / half;
            hh = i - half >= 0 ? high[i - half] : high[0];
            ll = i - half >= 0 ? low[i - half] : low[0];
            for (var c = half; c < period; c++)
            {
                var idx = i - c;
                if (idx < 0) continue;
                if (high[idx] > hh) hh = high[idx];
                if (low[idx] < ll) ll = low[idx];
            }

            var n2 = (hh - ll) / half;
            var dimen = n1 > 0 && n2 > 0 && n3 > 0 ? (Math.Log(n1 + n2) - Math.Log(n3)) / Math.Log(2) : 1.0;
            var alpha = Math.Exp(-4.6 * (dimen - 1));
            alpha = Math.Max(Math.Min(alpha, 1.0), 0.01);
            filt[i] = (alpha * price[i]) + ((1 - alpha) * filt[i - 1]);
        }

        var smoothed = Full(n);
        for (var i = 0; i < n; i++)
        {
            if (i < period + 1) smoothed[i] = price[i];
            else
            {
                double s = 0;
                for (var j = i - 4; j <= i; j++) s += filt[j];
                smoothed[i] = s / 5.0;
            }
        }

        return smoothed;
    }
}
