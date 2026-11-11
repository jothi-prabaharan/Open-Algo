using System.Globalization;

namespace EPTradeBot.Core.Indicators;

/// <summary>The inputs of one indicator call: named series and named parameters, with the defaults of openalgo.ta for what is left out.</summary>
public sealed class IndicatorArgs(IReadOnlyDictionary<string, double[]> series, IReadOnlyDictionary<string, object> parameters)
{
    public double[] S(string name) => series.TryGetValue(name, out var s) ? s : throw new ArgumentException($"The series '{name}' is required.");

    public double[]? SOrNull(string name) => series.GetValueOrDefault(name);

    public int I(string name, int fallback) => parameters.TryGetValue(name, out var v) ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : fallback;

    public double D(string name, double fallback) => parameters.TryGetValue(name, out var v) ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : fallback;

    public string T(string name, string fallback) => parameters.TryGetValue(name, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture)! : fallback;

    public bool B(string name, bool fallback) => parameters.TryGetValue(name, out var v) ? Convert.ToBoolean(v, CultureInfo.InvariantCulture) : fallback;
}

/// <summary>The indicators of openalgo.ta, by name, each returning its outputs in the order the Python function returns them (booleans as 1 and 0).</summary>
public static class IndicatorRegistry
{
    private static double[] Bool(Func<int, bool> f, int n)
    {
        var o = new double[n];
        for (var i = 0; i < n; i++) o[i] = f(i) ? 1.0 : 0.0;
        return o;
    }

    private static double[][] One(double[] a) => [a];

    private static double[][] Two((double[], double[]) t) => [t.Item1, t.Item2];

    private static double[][] Three((double[], double[], double[]) t) => [t.Item1, t.Item2, t.Item3];

    /// <summary>
    /// EMA taken over x from <paramref name="start"/> on (seeded on x[start]), published from <paramref name="from"/>: how openalgo.ta stacks the
    /// averages of DEMA and TEMA, each one starting where the one beneath it first has a value.
    /// </summary>
    private static double[] EmaFrom(double[] x, int start, int period, int from)
    {
        var o = Kernels.Full(x.Length);
        if (start >= x.Length) return o;
        var tail = Kernels.Ema(x[start..], period);
        for (var i = from; i < x.Length; i++) if (i - start < tail.Length) o[i] = tail[i - start];
        return o;
    }

    private static double[] Dema(double[] d, int p)
    {
        var e1 = Kernels.Ema(d, p);
        var e2 = EmaFrom(e1, p - 1, p, 2 * (p - 1));
        return Kernels.Zip(e1, e2, (a, b) => (2 * a) - b);
    }

    private static double[] Tema(double[] d, int p)
    {
        var e1 = Kernels.Ema(d, p);
        var e2 = EmaFrom(e1, p - 1, p, 2 * (p - 1));
        var e3 = EmaFrom(e2, 2 * (p - 1), p, 3 * (p - 1));
        var o = new double[d.Length];
        for (var i = 0; i < o.Length; i++) o[i] = (3 * e1[i]) - (3 * e2[i]) + e3[i];
        return o;
    }

    private static double[] WindowMeanOfValid(double[] data, int period)
    {
        var o = Kernels.Full(data.Length);
        for (var i = period - 1; i < data.Length; i++)
        {
            double sum = 0;
            var c = 0;
            for (var j = i - period + 1; j <= i; j++)
            {
                if (double.IsNaN(data[j])) continue;
                sum += data[j];
                c++;
            }

            if (c > 0) o[i] = sum / c;
        }

        return o;
    }

    private static bool Up(double[] a, double[] b, int i) => i > 0 && !double.IsNaN(a[i]) && !double.IsNaN(b[i]) && !double.IsNaN(a[i - 1]) && !double.IsNaN(b[i - 1]) && a[i] > b[i] && a[i - 1] <= b[i - 1];

    private static bool Down(double[] a, double[] b, int i) => i > 0 && !double.IsNaN(a[i]) && !double.IsNaN(b[i]) && !double.IsNaN(a[i - 1]) && !double.IsNaN(b[i - 1]) && a[i] < b[i] && a[i - 1] >= b[i - 1];

    public static IReadOnlyDictionary<string, Func<IndicatorArgs, double[][]>> All { get; } = new Dictionary<string, Func<IndicatorArgs, double[][]>>(StringComparer.Ordinal)
    {
        ["accelerator_oscillator"] = a => One(Kernels.Ac(a.S("high"), a.S("low"), a.I("period", 5))),
        ["adl"] = a => One(Kernels.Adl(a.S("high"), a.S("low"), a.S("close"), a.S("volume"))),
        ["adx"] = a => Three(Kernels.Adx(a.S("high"), a.S("low"), a.S("close"), a.I("period", 14))),
        ["adxr"] = a => One(Kernels.Adxr(a.S("high"), a.S("low"), a.S("close"), a.I("period", 14))),
        ["alligator"] = a => Three(Kernels.Alligator(a.S("data"), a.I("jaw_period", 13), a.I("jaw_shift", 8), a.I("teeth_period", 8), a.I("teeth_shift", 5), a.I("lips_period", 5), a.I("lips_shift", 3))),
        ["alma"] = a => One(Kernels.Alma(a.S("data"), a.I("period", 21), a.D("offset", 0.85), a.D("sigma", 6.0))),
        ["apo"] = a => One(Kernels.Apo(a.S("data"), a.I("fast_period", 12), a.I("slow_period", 26), a.T("ma_type", "SMA"))),
        ["aroon"] = a => Two(Kernels.Aroon(a.S("high"), a.S("low"), a.I("period", 25))),
        ["aroon_oscillator"] = a => One(Kernels.AroonOsc(a.S("high"), a.S("low"), a.I("period", 14))),
        ["atr"] = a => One(Kernels.AtrWilder(a.S("high"), a.S("low"), a.S("close"), a.I("period", 14))),
        ["avgprice"] = a => One(Enumerable.Range(0, a.S("close").Length).Select(i => (a.S("open")[i] + a.S("high")[i] + a.S("low")[i] + a.S("close")[i]) / 4.0).ToArray()),
        ["awesome_oscillator"] = a => One(Kernels.Ao(a.S("high"), a.S("low"), a.I("fast_period", 5), a.I("slow_period", 34))),
        ["bbands"] = a => Three(Kernels.Bbands(a.S("data"), a.I("period", 20), a.D("std_dev", 2.0))),
        ["bbpercent"] = a => One(Kernels.BbPercent(a.S("data"), a.I("period", 20), a.D("std_dev", 2.0))),
        ["bbwidth"] = a => One(Kernels.BbWidth(a.S("data"), a.I("period", 20), a.D("std_dev", 2.0))),
        ["beta"] = a => One(Kernels.Beta(a.S("asset"), a.S("market"), a.I("period", 252))),
        ["bop"] = a => One(Kernels.Bop(a.S("open"), a.S("high"), a.S("low"), a.S("close"))),
        ["cci"] = a => One(Kernels.Cci(a.S("high"), a.S("low"), a.S("close"), a.I("period", 20))),
        ["chaikin"] = a => One(Kernels.Chaikin(a.S("high"), a.S("low"), a.I("ema_period", 10), a.I("roc_period", 10))),
        ["chandelier_exit"] = a => Two(Kernels.ChandelierExit(a.S("high"), a.S("low"), a.S("close"), a.I("period", 22), a.D("multiplier", 3.0))),
        ["change"] = a => One(Kernels.Mom(a.S("data"), a.I("length", 1))),
        ["cho"] = a => One(Kernels.Cho(a.S("high"), a.S("low"), a.S("close"), a.S("volume"), a.I("fast_period", 3), a.I("slow_period", 10))),
        ["chop"] = a => One(Kernels.Chop(a.S("high"), a.S("low"), a.S("close"), a.I("period", 14))),
        ["ckstop"] = a => Two(Kernels.ChandeKroll(a.S("high"), a.S("low"), a.S("close"), a.I("p", 10), a.D("x", 1.0), a.I("q", 9))),
        ["cmf"] = a => One(Kernels.Cmf(a.S("high"), a.S("low"), a.S("close"), a.S("volume"), a.I("period", 20))),
        ["cmo"] = a => One(Kernels.Cmo(a.S("data"), a.I("period", 14))),
        ["coppock"] = a => One(Kernels.Coppock(a.S("data"), a.I("wma_length", 10), a.I("long_roc_length", 14), a.I("short_roc_length", 11))),
        ["correlation"] = a => One(Kernels.Correl(a.S("data1"), a.S("data2"), a.I("period", 20))),
        ["cross"] = a => One(Bool(i => Up(a.S("series1"), a.S("series2"), i) || Down(a.S("series1"), a.S("series2"), i), a.S("series1").Length)),
        ["crossover"] = a => One(Bool(i => Up(a.S("series1"), a.S("series2"), i), a.S("series1").Length)),
        ["crossunder"] = a => One(Bool(i => Down(a.S("series1"), a.S("series2"), i), a.S("series1").Length)),
        ["crsi"] = a => One(Kernels.Crsi(a.S("data"), a.I("lenrsi", 3), a.I("lenupdown", 2), a.I("lenroc", 100))),
        ["dema"] = a => One(Dema(a.S("data"), a.I("period", 14))),
        ["dmi"] = a => { var (p, m, _) = Kernels.Adx(a.S("high"), a.S("low"), a.S("close"), a.I("period", 14)); return [p, m]; },
        ["donchian"] = a => Three(Kernels.Donchian(a.S("high"), a.S("low"), a.I("period", 20))),
        ["dpo"] = a => One(Kernels.Dpo(a.S("data"), a.I("period", 21), a.B("is_centered", false))),
        ["dx"] = a => One(Kernels.Dx(a.S("high"), a.S("low"), a.S("close"), a.I("period", 14))),
        ["elderray"] = a => Two(Kernels.Elderray(a.S("high"), a.S("low"), a.S("close"), a.I("period", 13))),
        ["ema"] = a => One(Kernels.Ema(a.S("data"), a.I("period", 14))),
        ["emv"] = a => One(Kernels.Emv(a.S("high"), a.S("low"), a.S("volume"), a.I("length", 14), a.D("divisor", 10000))),
        ["exrem"] = a =>
        {
            var p = a.S("primary");
            var s = a.S("secondary");
            var o = new double[p.Length];
            var active = false;
            for (var i = 0; i < o.Length; i++)
            {
                if (!active && p[i] != 0) { o[i] = 1; active = true; }
                else if (s[i] != 0) active = false;
            }

            return [o];
        },
        ["falling"] = a => { var d = a.S("data"); var l = a.I("length", 5); return One(Bool(i => i >= l && !double.IsNaN(d[i]) && !double.IsNaN(d[i - l]) && d[i] < d[i - l], d.Length)); },
        ["fisher"] = a => Two(Kernels.Fisher(a.S("high"), a.S("low"), a.I("length", 9))),
        ["flip"] = a =>
        {
            var p = a.S("primary");
            var s = a.S("secondary");
            var o = new double[p.Length];
            var active = false;
            for (var i = 0; i < o.Length; i++)
            {
                if (p[i] != 0) active = true;
                else if (s[i] != 0) active = false;
                o[i] = active ? 1 : 0;
            }

            return [o];
        },
        ["force_index"] = a => One(Kernels.ForceIndex(a.S("close"), a.S("volume"), a.I("length", 13))),
        ["fractals"] = a => Two(Kernels.WilliamsFractals(a.S("high"), a.S("low"), a.I("periods", 2))),
        ["frama"] = a => One(Kernels.Frama(a.S("high"), a.S("low"), a.I("period", 26))),
        ["gator_oscillator"] = a => Two(Kernels.Gator(a.S("high"), a.S("low"), a.I("jaw_period", 13), a.I("teeth_period", 8), a.I("lips_period", 5))),
        ["highest"] = a => One(Kernels.Highest(a.S("data"), a.I("period", 14))),
        ["hma"] = a => One(Kernels.Hma(a.S("data"), a.I("period", 14))),
        ["hv"] = a => One(Kernels.Hv(a.S("close"), a.I("length", 10), a.D("annual", 365), a.D("per", 1))),
        ["ichimoku"] = a => Kernels.Ichimoku(a.S("high"), a.S("low"), a.S("close"), a.I("conversion_periods", 9), a.I("base_periods", 26), a.I("lagging_span2_periods", 52), a.I("displacement", 26)),
        ["kama"] = a => One(Kernels.KamaTv(a.S("data"), a.I("length", 14), a.I("fast_length", 2), a.I("slow_length", 30))),
        ["keltner"] = a => Three(Kernels.Keltner(a.S("high"), a.S("low"), a.S("close"), a.I("ema_period", 20), a.I("atr_period", 10), a.D("multiplier", 2.0))),
        ["kst"] = a => Two(Kernels.Kst(a.S("data"), a.I("roclen1", 10), a.I("roclen2", 15), a.I("roclen3", 20), a.I("roclen4", 30), a.I("smalen1", 10), a.I("smalen2", 10), a.I("smalen3", 10), a.I("smalen4", 15), a.I("siglen", 9))),
        ["kvo"] = a => Two(Kernels.Kvo(a.S("high"), a.S("low"), a.S("close"), a.S("volume"), a.I("trig_len", 13), a.I("fast_x", 34), a.I("slow_x", 55))),
        ["linreg"] = a => One(Kernels.Linreg(a.S("data"), a.I("period", 14))),
        ["linregangle"] = a => One(Kernels.LinregAngle(a.S("data"), a.I("period", 14))),
        ["linregintercept"] = a => One(Kernels.LinregIntercept(a.S("data"), a.I("period", 14))),
        ["lowest"] = a => One(Kernels.Lowest(a.S("data"), a.I("period", 14))),
        ["lrslope"] = a => One(Kernels.Lrslope(a.S("data"), a.I("period", 100), a.D("interval", 1))),
        ["ma_envelopes"] = a => Three(Kernels.MaEnvelopes(a.S("data"), a.I("period", 20), a.D("percentage", 2.5), a.T("ma_type", "SMA"))),
        ["macd"] = a => Three(Kernels.Macd(a.S("data"), a.I("fast_period", 12), a.I("slow_period", 26), a.I("signal_period", 9))),
        ["massindex"] = a => One(Kernels.Mass(a.S("high"), a.S("low"), a.I("length", 10))),
        ["mcginley"] = a => One(Kernels.Mcginley(a.S("data"), a.I("period", 14))),
        ["median"] = a => One(Kernels.Median(a.S("data"), a.I("period", 3))),
        ["median_bands"] = a => Kernels.MedianBands(a.S("high"), a.S("low"), a.S("close"), a.SOrNull("source"), a.I("median_length", 3), a.I("atr_length", 14), a.D("atr_mult", 2.0)),
        ["medprice"] = a => One(Kernels.Zip(a.S("high"), a.S("low"), (h, l) => (h + l) / 2.0)),
        ["mfi"] = a => One(Kernels.Mfi(a.S("high"), a.S("low"), a.S("close"), a.S("volume"), a.I("period", 14))),
        ["midpoint"] = a => One(Kernels.Midpoint(a.S("data"), a.I("period", 14))),
        ["midprice"] = a => One(Kernels.Midprice(a.S("high"), a.S("low"), a.I("period", 14))),
        ["minus_dm"] = a => One(Kernels.MinusDm(a.S("high"), a.S("low"), a.I("period", 14))),
        ["mode"] = a => One(Kernels.Mode(a.S("data"), a.I("period", 20), a.I("bins", 10))),
        ["mom"] = a => One(Kernels.Mom(a.S("data"), a.I("period", 10))),
        ["natr"] = a => One(Kernels.Natr(a.S("high"), a.S("low"), a.S("close"), a.I("period", 14))),
        ["nvi"] = a => One(Kernels.Nvi(a.S("close"), a.S("volume"))),
        ["nvi_with_ema"] = a => { var n = Kernels.Nvi(a.S("close"), a.S("volume")); return [n, Kernels.EmaFirstValid(n, a.I("ema_length", 255))]; },
        ["pvi_with_signal"] = a =>
        {
            var p = Kernels.Pvi(a.S("close"), a.S("volume"), a.D("initial_value", 100.0));
            var len = a.I("signal_length", 255);
            return [p, a.T("signal_type", "EMA").Equals("SMA", StringComparison.OrdinalIgnoreCase) ? WindowMeanOfValid(p, len) : Kernels.EmaFirstValid(p, len)];
        },
        ["obv_smoothed"] = a =>
        {
            var obv = Kernels.Obv(a.S("close"), a.S("volume"));
            var len = a.I("ma_length", 20);
            switch (a.T("ma_type", "None"))
            {
                case "None": return [obv];
                case "SMA": return [Kernels.Sma(obv, len)];
                case "SMA + Bollinger Bands": { var (u, m, l) = Kernels.Bbands(obv, a.I("bb_length", 20), a.D("bb_mult", 2.0)); return [u, m, l]; }
                case "EMA": return [Kernels.Ema(obv, len)];
                case "SMMA (RMA)": return [Kernels.RmaSmma(obv, len)];
                case "WMA": return [Kernels.Wma(obv, len)];
                case "VWMA": return [Kernels.VwmaStrict(obv, a.S("volume"), len)];
                default: throw new ArgumentException($"Unsupported ma_type: {a.T("ma_type", "None")}");
            }
        },
        ["vwap"] = a =>
        {
            var h = a.S("high");
            var l = a.S("low");
            var c = a.S("close");
            var src = new double[c.Length];
            for (var i = 0; i < src.Length; i++) src[i] = (h[i] + l[i] + c[i]) / 3.0;
            return [Kernels.SessionVwap(src, a.S("volume"), a.SOrNull("session_starts") ?? new double[c.Length]).Vwap];
        },
        ["obv"] = a => One(Kernels.Obv(a.S("close"), a.S("volume"))),
        ["pivot_points"] = a => Kernels.PivotPoints(a.S("high"), a.S("low"), a.S("close")),
        ["plus_dm"] = a => One(Kernels.PlusDm(a.S("high"), a.S("low"), a.I("period", 14))),
        ["po"] = a => One(Kernels.PriceOscillator(a.S("data"), a.I("fast_period", 10), a.I("slow_period", 20), a.T("ma_type", "SMA"))),
        ["ppo"] = a => Three(Kernels.Ppo(a.S("data"), a.I("fast_period", 12), a.I("slow_period", 26), a.I("signal_period", 9))),
        ["psar"] = a => One(Kernels.Sar(a.S("high"), a.S("low"), a.D("acceleration", 0.02), a.D("maximum", 0.2))),
        ["pvi"] = a => One(Kernels.Pvi(a.S("close"), a.S("volume"), a.D("initial_value", 100.0))),
        ["pvt"] = a => One(Kernels.Pvt(a.S("close"), a.S("volume"))),
        ["rising"] = a => { var d = a.S("data"); var l = a.I("length", 5); return One(Bool(i => i >= l && !double.IsNaN(d[i]) && !double.IsNaN(d[i - l]) && d[i] > d[i - l], d.Length)); },
        ["roc"] = a => One(Kernels.Roc(a.S("data"), a.I("length", 10))),
        ["rocp"] = a => One(Kernels.Rocp(a.S("data"), a.I("period", 10))),
        ["rocr"] = a => One(Kernels.Rocr(a.S("data"), a.I("period", 10))),
        ["rocr100"] = a => One(Kernels.Mul(Kernels.Rocr(a.S("data"), a.I("period", 10)), 100.0)),
        ["rsi"] = a => One(Kernels.Rsi(a.S("data"), a.I("period", 14))),
        ["rvi"] = a => Two(Kernels.RviVigor(a.S("open"), a.S("high"), a.S("low"), a.S("close"), a.I("period", 10))),
        ["rvol"] = a => One(Kernels.Rvol(a.S("volume"), a.I("period", 20))),
        ["rwi"] = a => Two(Kernels.Rwi(a.S("high"), a.S("low"), a.S("close"), a.I("period", 14))),
        ["sma"] = a => One(Kernels.Sma(a.S("data"), a.I("period", 14))),
        ["starc"] = a => Three(Kernels.Starc(a.S("high"), a.S("low"), a.S("close"), a.I("ma_period", 5), a.I("atr_period", 15), a.D("multiplier", 1.33))),
        ["stc"] = a => One(Kernels.Stc(a.S("data"), a.I("fast_length", 23), a.I("slow_length", 50), a.I("cycle_length", 10), a.I("d1_length", 3), a.I("d2_length", 3))),
        ["stdev"] = a => One(Kernels.Stdev(a.S("data"), a.I("period", 14))),
        ["stochastic"] = a => Two(Kernels.Stochastic(a.S("high"), a.S("low"), a.S("close"), a.I("k_period", 14), a.I("smooth_k", 3), a.I("d_period", 3))),
        ["stochf"] = a => Two(Kernels.Stochf(a.S("high"), a.S("low"), a.S("close"), a.I("fastk_period", 5), a.I("fastd_period", 3))),
        ["stochrsi"] = a => Two(Kernels.StochRsi(a.S("data"), a.I("rsi_period", 14), a.I("stoch_period", 14), a.I("k_period", 3), a.I("d_period", 3))),
        ["supertrend"] = a => Two(Kernels.Supertrend(a.S("high"), a.S("low"), a.S("close"), a.I("period", 10), a.D("multiplier", 3.0))),
        ["t3"] = a => One(Kernels.T3(a.S("data"), a.I("period", 21), a.D("v_factor", 0.7))),
        ["tema"] = a => One(Tema(a.S("data"), a.I("period", 14))),
        ["trima"] = a => One(Kernels.Trima(a.S("data"), a.I("period", 20))),
        ["trix"] = a => One(Kernels.Trix(a.S("data"), a.I("length", 18))),
        ["true_range"] = a => One(Kernels.TrueRange(a.S("high"), a.S("low"), a.S("close"))),
        ["tsf"] = a => One(Kernels.Tsf(a.S("data"), a.I("period", 14))),
        ["tsi"] = a => Two(Kernels.Tsi(a.S("data"), a.I("long_period", 25), a.I("short_period", 13), a.I("signal_period", 13))),
        ["typprice"] = a => One(Enumerable.Range(0, a.S("close").Length).Select(i => (a.S("high")[i] + a.S("low")[i] + a.S("close")[i]) / 3.0).ToArray()),
        ["ulcerindex"] = a => Kernels.UlcerIndex(a.S("data"), a.I("length", 14), a.I("smooth_length", 14), a.I("signal_length", 52), a.T("signal_type", "SMA"), a.B("return_signal", false)),
        ["ultimate_oscillator"] = a => One(Kernels.Uo(a.S("high"), a.S("low"), a.S("close"), a.I("period1", 7), a.I("period2", 14), a.I("period3", 28))),
        ["uo_oscillator"] = a => One(Kernels.Uo(a.S("high"), a.S("low"), a.S("close"), a.I("period1", 7), a.I("period2", 14), a.I("period3", 28))),
        ["valuewhen"] = a =>
        {
            var expr = a.S("expr");
            var arr = a.S("array");
            var n = a.I("n", 1);
            var o = Kernels.Full(expr.Length);
            var hits = new List<int>();
            for (var i = 0; i < expr.Length; i++)
            {
                if (expr[i] != 0) hits.Add(i);
                if (hits.Count >= n) o[i] = arr[hits[^n]];
            }

            return [o];
        },
        ["variance"] = a => Kernels.Variance(a.S("data"), a.I("lookback", 20), a.T("mode", "PR"), a.I("ema_period", 20), a.I("filter_lookback", 20), a.I("ema_length", 14), a.B("return_components", false)),
        ["vi"] = a => Two(Kernels.Vi(a.S("high"), a.S("low"), a.S("close"), a.I("period", 14))),
        ["vidya"] = a => One(Kernels.Vidya(a.S("data"), a.I("period", 14), a.D("alpha", 0.2))),
        ["volosc"] = a => One(Kernels.Volosc(a.S("volume"), a.I("short_length", 5), a.I("long_length", 10))),
        ["vroc"] = a => One(Kernels.Vroc(a.S("volume"), a.I("period", 25))),
        ["vwma"] = a => One(Kernels.Vwma(a.S("data"), a.S("volume"), a.I("period", 14))),
        ["wclprice"] = a => One(Enumerable.Range(0, a.S("close").Length).Select(i => (a.S("high")[i] + a.S("low")[i] + (2.0 * a.S("close")[i])) / 4.0).ToArray()),
        ["williams_r"] = a => One(Kernels.WilliamsR(a.S("high"), a.S("low"), a.S("close"), a.I("period", 14))),
        ["wma"] = a => One(Kernels.Wma(a.S("data"), a.I("period", 14))),
        ["zlema"] = a => One(Kernels.Zlema(a.S("data"), a.I("period", 14))),
    };

    /// <summary>Series names the Python functions use, mapped to the series the call carries (open_prices is the open, source the data).</summary>
    public static string[] Names => [.. All.Keys.Order(StringComparer.Ordinal)];
}
