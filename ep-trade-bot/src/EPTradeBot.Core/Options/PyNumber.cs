using System.Globalization;

namespace EPTradeBot.Core.Options;

/// <summary>Number formatting that agrees with Python, which OpenAlgo's tools return: round() rounds by the exact value and settles only a true tie to the even digit.</summary>
public static class PyNumber
{
    public static double Round(double x, int digits)
    {
        if (double.IsNaN(x) || double.IsInfinity(x) || x == 0) return x;
        // .NET formats a double exactly, so a tie is recognised by the digits themselves rather than by a rounded multiplication, which can make a value just above .5 look like .5.
        var exact = Math.Abs(x).ToString("F" + (digits + 30), CultureInfo.InvariantCulture);
        var point = exact.IndexOf('.');
        var tail = exact[(point + 1 + digits)..];
        var isTie = tail[0] == '5' && tail.AsSpan(1).IndexOfAnyExcept('0') < 0;
        if (!isTie) return double.Parse(x.ToString("F" + digits, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var kept = exact[..(point + 1 + digits)].TrimEnd('.');
        var lastDigit = kept[^1] == '.' ? kept[^2] : kept[^1];
        var down = double.Parse(kept, CultureInfo.InvariantCulture);
        var up = down + Math.Pow(10, -digits);
        var result = (lastDigit - '0') % 2 == 0 ? down : double.Parse(up.ToString("F" + digits, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return x < 0 ? -result : result;
    }
}
