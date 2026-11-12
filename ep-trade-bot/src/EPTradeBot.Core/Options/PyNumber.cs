using System.Globalization;

namespace EPTradeBot.Core.Options;

/// <summary>Number formatting that agrees with Python, which OpenAlgo's tools return: round() settles an exact tie to the even digit and rounds anything else by its true value.</summary>
public static class PyNumber
{
    public static double Round(double x, int digits)
    {
        if (double.IsNaN(x) || double.IsInfinity(x)) return x;
        var scale = Math.Pow(10, digits);
        var scaled = x * scale;
        if (scaled / scale == x && Math.Abs(scaled - Math.Truncate(scaled)) == 0.5 && Math.Abs(scaled) < 1e15)
            return Math.Round(scaled, MidpointRounding.ToEven) / scale;
        return double.Parse(x.ToString("F" + digits, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }
}
