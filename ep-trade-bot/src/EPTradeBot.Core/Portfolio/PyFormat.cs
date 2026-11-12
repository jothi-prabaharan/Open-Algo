using System.Globalization;
using System.Numerics;

namespace EPTradeBot.Core.Portfolio;

/// <summary>Python's number formatting, which rounds the exact binary value half to even (.NET rounds a tie away from zero).</summary>
public static class PyFormat
{
    /// <summary>f"{x:.{digits}f}"</summary>
    public static string Fixed(double x, int digits)
    {
        if (double.IsNaN(x)) return "nan";
        if (double.IsInfinity(x)) return x > 0 ? "inf" : "-inf";
        var bits = BitConverter.DoubleToInt64Bits(x);
        var negative = bits < 0;
        var exponent = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exponent == 0) exponent++;
        else mantissa |= 1L << 52;
        exponent -= 1075;
        // value = mantissa * 2^exponent; scaled = value * 10^digits, as a rational numerator/denominator.
        var numerator = new BigInteger(mantissa) * BigInteger.Pow(10, digits);
        var denominator = BigInteger.One;
        if (exponent > 0) numerator <<= exponent;
        else denominator <<= -exponent;
        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        var twice = remainder * 2;
        if (twice > denominator || (twice == denominator && !quotient.IsEven)) quotient += 1;
        var text = quotient.ToString(CultureInfo.InvariantCulture).PadLeft(digits + 1, '0');
        var result = digits == 0 ? text : text[..^digits] + "." + text[^digits..];
        return negative ? "-" + result : result;
    }

    /// <summary>f"{x:.{digits}%}"</summary>
    public static string Percent(double x, int digits) => Fixed(x * 100, digits) + "%";
}
