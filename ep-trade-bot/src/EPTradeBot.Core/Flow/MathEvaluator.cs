using System.Globalization;

namespace EPTradeBot.Core.Flow;

/// <summary>
/// Evaluates arithmetic text: + - * / % ** with brackets, unary signs, numbers and floor(x). Nothing else. Port of OpenAlgo's _safe_eval_math,
/// including Python's modulo (the sign follows the divisor), the cap of 100 on an exponent, and the error for dividing by zero.
/// </summary>
public static class MathEvaluator
{
    public static double Evaluate(string text)
    {
        var cleaned = text.Trim();
        if (cleaned.Length == 0) throw new FormatException("Empty expression");
        var p = new Parser(cleaned);
        var value = p.ParseExpression();
        p.SkipSpaces();
        if (!p.AtEnd) throw new FormatException($"Invalid expression syntax: unexpected '{p.Peek}'");
        return value;
    }

    private sealed class Parser(string s)
    {
        private int pos;

        public bool AtEnd => pos >= s.Length;

        public char Peek => s[pos];

        public void SkipSpaces()
        {
            while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
        }

        public double ParseExpression()
        {
            var left = ParseTerm();
            while (true)
            {
                SkipSpaces();
                if (AtEnd) return left;
                if (s[pos] == '+') { pos++; left += ParseTerm(); }
                else if (s[pos] == '-') { pos++; left -= ParseTerm(); }
                else return left;
            }
        }

        private double ParseTerm()
        {
            var left = ParseUnary();
            while (true)
            {
                SkipSpaces();
                if (AtEnd) return left;
                if (s[pos] == '*' && !(pos + 1 < s.Length && s[pos + 1] == '*')) { pos++; left *= ParseUnary(); }
                else if (s[pos] == '/' && !(pos + 1 < s.Length && s[pos + 1] == '/'))
                {
                    pos++;
                    var right = ParseUnary();
                    if (right == 0) throw new DivideByZeroException("float division by zero");
                    left /= right;
                }
                else if (s[pos] == '%')
                {
                    pos++;
                    var right = ParseUnary();
                    if (right == 0) throw new DivideByZeroException("float modulo by zero");
                    left = PyMod(left, right);
                }
                else return left;
            }
        }

        // A unary sign binds more loosely than a power: -2 ** 2 is -4, as in Python.
        private double ParseUnary()
        {
            SkipSpaces();
            if (!AtEnd && s[pos] == '-') { pos++; return -ParseUnary(); }
            if (!AtEnd && s[pos] == '+') { pos++; return ParseUnary(); }
            return ParsePower();
        }

        private double ParsePower()
        {
            var left = ParseAtom();
            SkipSpaces();
            if (pos + 1 < s.Length && s[pos] == '*' && s[pos + 1] == '*')
            {
                pos += 2;
                var right = ParseUnary();
                if (Math.Abs(right) > 100) throw new ArgumentException($"Exponent too large: {PyNum(right)} (max 100)");
                if (left < 0 && right != Math.Floor(right)) throw new ArgumentException("Result is not a real number");
                if (left == 0 && right < 0) throw new DivideByZeroException("zero cannot be raised to a negative power");
                return Math.Pow(left, right);
            }

            return left;
        }

        private double ParseAtom()
        {
            SkipSpaces();
            if (AtEnd) throw new FormatException("Invalid expression syntax: expression ends too soon");
            if (s[pos] == '(')
            {
                pos++;
                var v = ParseExpression();
                SkipSpaces();
                if (AtEnd || s[pos] != ')') throw new FormatException("Invalid expression syntax: missing ')'");
                pos++;
                return v;
            }

            if (char.IsAsciiLetter(s[pos]))
            {
                var start = pos;
                while (pos < s.Length && char.IsAsciiLetterOrDigit(s[pos])) pos++;
                var name = s[start..pos];
                if (name != "floor") throw new FormatException("Only floor(expression) is supported");
                SkipSpaces();
                if (AtEnd || s[pos] != '(') throw new FormatException("Only floor(expression) is supported");
                pos++;
                var v = ParseExpression();
                SkipSpaces();
                if (AtEnd || s[pos] != ')') throw new FormatException("Only floor(expression) takes one argument");
                pos++;
                return Math.Floor(v);
            }

            var begin = pos;
            while (pos < s.Length && (char.IsAsciiDigit(s[pos]) || s[pos] == '.')) pos++;
            if (pos < s.Length && (s[pos] == 'e' || s[pos] == 'E') && pos > begin)
            {
                var save = pos;
                pos++;
                if (pos < s.Length && (s[pos] == '+' || s[pos] == '-')) pos++;
                if (pos < s.Length && char.IsAsciiDigit(s[pos])) { while (pos < s.Length && char.IsAsciiDigit(s[pos])) pos++; }
                else pos = save;
            }

            if (begin == pos) throw new FormatException($"Invalid expression syntax: unexpected '{s[pos]}'");
            if (!double.TryParse(s.AsSpan(begin, pos - begin), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                throw new FormatException($"Invalid number '{s[begin..pos]}'");
            return n;
        }
    }

    private static double PyMod(double a, double b)
    {
        var r = a % b;
        return r != 0 && (r < 0) != (b < 0) ? r + b : r;
    }

    private static string PyNum(double d) => FlowContext.PyFloat(d);
}
