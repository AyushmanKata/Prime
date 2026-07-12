using System.Text.RegularExpressions;

namespace WinCalc;

public record HistoryEntry(string Expression, string Result);

public partial class Calculator
{
    public List<HistoryEntry> History { get; } = [];
    public string Expr { get; set; } = "";
    public bool HasError { get; private set; }
    public bool UseRadians { get; set; } = false;

    public string Evaluate()
    {
        var raw = Expr.Trim();
        if (string.IsNullOrEmpty(raw)) return "";
        try
        {
            var d = ExprParser.Evaluate(Preprocess(raw), UseRadians);
            var result = Format(d);
            History.Add(new HistoryEntry(raw, result));
            Expr = result;
            HasError = false;
            return result;
        }
        catch
        {
            HasError = true;
            return "Error";
        }
    }

    private static string Preprocess(string s)
    {
        s = s.Replace("×", "*")
             .Replace("÷", "/")
             .Replace("−", "-")
             .Replace(",", "");
        s = ImplicitA().Replace(s, "$1*$2");
        s = ImplicitB().Replace(s, "$1*$2");
        return s;
    }

    public static string Format(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) return "Error";
        if (d == Math.Truncate(d) && Math.Abs(d) < 1e15)
            return ((long)d).ToString();
        return d.ToString("G12");
    }

    public void Backspace() { if (Expr.Length > 0) Expr = Expr[..^1]; HasError = false; }
    public void ClearExpr()    { Expr = ""; HasError = false; }
    public void ClearHistory() { History.Clear(); }
    public void AppendToExpr(string s)
    {
        if (HasError) { Expr = ""; HasError = false; }
        Expr += s;
    }
    public string TryPreview()
    {
        if (string.IsNullOrWhiteSpace(Expr)) return "";
        try
        {
            var d = ExprParser.Evaluate(Preprocess(Expr), UseRadians);
            var result = Format(d);
            return result == Expr.Trim() ? "" : $"= {result}";
        }
        catch { return ""; }
    }

    [GeneratedRegex(@"(\d)([(πe])")]
    private static partial Regex ImplicitA();
    [GeneratedRegex(@"(\))(\d|[(])")]
    private static partial Regex ImplicitB();
}

internal class ExprParser
{
    private readonly string _s;
    private int _pos;

    // constructor replaced above

    private readonly bool _isRad;
    private ExprParser(string s, bool isRad) { _s = s; _isRad = isRad; }

    public static double Evaluate(string expr, bool isRad)
    {
        var p = new ExprParser(expr.Replace(" ", ""), isRad);
        double result = p.AddSub();
        if (p._pos != p._s.Length)
            throw new Exception($"Unexpected '{p._s[p._pos]}' at {p._pos}");
        return result;
    }

    private double AddSub()
    {
        double v = MulDiv();
        while (_pos < _s.Length && (_s[_pos] == '+' || _s[_pos] == '-'))
        {
            char op = _s[_pos++];
            v = op == '+' ? v + MulDiv() : v - MulDiv();
        }
        return v;
    }

    private double MulDiv()
    {
        double v = Power();
        while (_pos < _s.Length && (_s[_pos] == '*' || _s[_pos] == '/' || _s[_pos] == '%'))
        {
            char op = _s[_pos++];
            double r = Power();
            v = op == '*' ? v * r : op == '/' ? v / r : v % r;
        }
        return v;
    }

    private double Power()
    {
        double v = Unary();
        if (_pos < _s.Length && _s[_pos] == '^')
        {
            _pos++;
            v = Math.Pow(v, Power()); // right-associative
        }
        return v;
    }

    private double Unary()
    {
        if (_pos < _s.Length && _s[_pos] == '-') { _pos++; return -Primary(); }
        if (_pos < _s.Length && _s[_pos] == '+') { _pos++; return Primary(); }
        return Primary();
    }

    private double Primary()
    {
        if (_pos >= _s.Length) throw new Exception("Unexpected end");

        if (_s[_pos] == '(')
        {
            _pos++;
            double v = AddSub();
            if (_pos < _s.Length && _s[_pos] == ')') _pos++;
            return v;
        }

        if (char.IsDigit(_s[_pos]) || _s[_pos] == '.')
        {
            int start = _pos;
            while (_pos < _s.Length && (char.IsDigit(_s[_pos]) || _s[_pos] == '.'))
                _pos++;
            return double.Parse(_s[start.._pos],
                System.Globalization.CultureInfo.InvariantCulture);
        }

        if (_s[_pos] == 'π') { _pos++; return Math.PI; }

        if (char.IsLetter(_s[_pos]))
        {
            int start = _pos;
            while (_pos < _s.Length && char.IsLetter(_s[_pos])) _pos++;
            string name = _s[start.._pos].ToLower();

            if (name == "e" && (_pos >= _s.Length || _s[_pos] != '('))
                return Math.E;

            if (_pos < _s.Length && _s[_pos] == '(')
            {
                _pos++;
                double arg = AddSub();
                if (_pos < _s.Length && _s[_pos] == ')') _pos++;

                double d2r = Math.PI / 180.0;
                double r2d = 180.0 / Math.PI;

                return name switch
                {
                    "sin"   => Math.Sin(_isRad ? arg : arg * d2r),
                    "cos"   => Math.Cos(_isRad ? arg : arg * d2r),
                    "tan"   => Math.Tan(_isRad ? arg : arg * d2r),
                    "asin"  => _isRad ? Math.Asin(arg) : Math.Asin(arg) * r2d,
                    "acos"  => _isRad ? Math.Acos(arg) : Math.Acos(arg) * r2d,
                    "atan"  => _isRad ? Math.Atan(arg) : Math.Atan(arg) * r2d,
                    "sinh"  => Math.Sinh(arg),
                    "cosh"  => Math.Cosh(arg),
                    "tanh"  => Math.Tanh(arg),
                    "asinh" => Math.Asinh(arg),
                    "acosh" => Math.Acosh(arg),
                    "atanh" => Math.Atanh(arg),
                    "ln"    => Math.Log(arg),
                    "log"   => Math.Log10(arg),
                    "exp"   => Math.Exp(arg),
                    "sqrt"  => Math.Sqrt(arg),
                    "cbrt"  => Math.Cbrt(arg),
                    "abs"   => Math.Abs(arg),
                    "ceil"  => Math.Ceiling(arg),
                    "floor" => Math.Floor(arg),
                    _ => throw new ArgumentException($"Unknown function: {name}")
                };
            }
        }

        throw new Exception($"Unexpected '{_s[_pos]}' at {_pos}");
    }
}
