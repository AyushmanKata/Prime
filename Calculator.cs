using System.Text.RegularExpressions;

namespace WinCalc;

/// <summary>One completed calculation: the expression typed and the result produced.</summary>
public record HistoryEntry(string Expression, string Result);

/// <summary>
/// Holds calculator state (current expression, error flag, history, angle mode)
/// and drives evaluation via <see cref="ExprParser"/>. UI code (MainWindow) only
/// ever talks to this class — it never touches ExprParser directly.
/// </summary>
public partial class Calculator
{
    public List<HistoryEntry> History { get; } = [];
    public string Expr { get; set; } = "";
    public bool HasError { get; private set; }

    /// <summary>True = trig functions use radians; false = degrees. Toggled by the Rad/Deg button.</summary>
    public bool UseRadians { get; set; } = false;

    /// <summary>
    /// Evaluates the current expression, updates state, and returns the display string.
    /// On success: expression is replaced with the formatted result and pushed to history.
    /// On failure: HasError is set and "Error" is returned (Expr is left untouched so the
    /// user can see and fix what they typed).
    /// </summary>
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
            // Any parse/eval failure (bad syntax, divide by zero -> NaN/Inf, etc.)
            // is reported generically as "Error" — the parser's exception detail
            // isn't shown to the user, only used internally.
            HasError = true;
            return "Error";
        }
    }

    /// <summary>
    /// Normalizes UI symbols to the characters ExprParser understands, then inserts
    /// implicit multiplication (e.g. "2π" -> "2*π", "(2+3)4" -> "(2+3)*4") so users
    /// can chain a number/constant/parenthesis directly against another without
    /// typing '*'.
    /// </summary>
    private static string Preprocess(string s)
    {
        s = s.Replace("×", "*")
             .Replace("÷", "/")
             .Replace("−", "-")
             .Replace(",", ""); // defensive: ',' isn't in ValidChars, but strip it if it ever sneaks in

        // Insert '*' at every point where one value ends and another begins
        // without an explicit operator between them. Three directions are needed:
        //   digit -> ( / π / e        e.g. "2(3)" "2π" "2e"
        //   )     -> digit / ( / π/e  e.g. "(1+1)2" "(1+1)(3)" "(1+1)π"
        //   π/e   -> digit / ( / π/e  e.g. "π2" "π(3)" "πe"
        // Digit->digit and digit->'.' are deliberately excluded since those are
        // just part of the same number literal.
        s = ImplicitAfterDigit().Replace(s, "$1*$2");
        s = ImplicitAfterCloseParen().Replace(s, "$1*$2");
        s = ImplicitAfterConstant().Replace(s, "$1*$2");
        return s;
    }

    /// <summary>Formats a double for display: whole numbers print without a decimal point; NaN/Infinity become "Error".</summary>
    public static string Format(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) return "Error";
        if (d == Math.Truncate(d) && Math.Abs(d) < 1e15)
            return ((long)d).ToString();
        return d.ToString("G12");
    }

    public void Backspace()    { if (Expr.Length > 0) Expr = Expr[..^1]; HasError = false; }
    public void ClearExpr()    { Expr = ""; HasError = false; }
    public void ClearHistory() { History.Clear(); }

    /// <summary>Appends text to the expression, first clearing a stale "Error" display if present.</summary>
    public void AppendToExpr(string s)
    {
        if (HasError) { Expr = ""; HasError = false; }
        Expr += s;
    }

    /// <summary>
    /// Live "= result" preview shown above the input as the user types.
    /// Returns "" (no preview) for a plain number, an empty expression, or
    /// anything that doesn't currently parse — previews should never look
    /// like an error state.
    /// </summary>
    public string TryPreview()
    {
        if (Expr.Trim().All(c => char.IsDigit(c) || c == '.')) return "";
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
    private static partial Regex ImplicitAfterDigit();

    [GeneratedRegex(@"(\))(\d|[(πe])")]
    private static partial Regex ImplicitAfterCloseParen();

    [GeneratedRegex(@"([πe])(\d|[(πe])")]
    private static partial Regex ImplicitAfterConstant();
}

/// <summary>
/// Recursive-descent expression parser/evaluator. Replaces the earlier
/// <c>DataTable.Compute</c> approach, which treated '^' as bitwise XOR and had
/// no function support.
///
/// Grammar (lowest to highest precedence):
/// <code>
/// AddSub  := MulDiv (('+' | '-') MulDiv)*
/// MulDiv  := Power (('*' | '/' | '%') Power)*
/// Power   := Unary ('^' Power)?        // right-associative: 2^3^2 == 2^(3^2)
/// Unary   := ('-' | '+')? Primary
/// Primary := NUMBER | 'π' | 'e' | '(' AddSub ')' | FUNC '(' AddSub ')'
/// </code>
/// Each grammar rule below is one method; parsing consumes characters from
/// <see cref="_s"/> left to right via <see cref="_pos"/>, with no backtracking.
/// </summary>
internal class ExprParser
{
    private readonly string _s;
    private int _pos;
    private readonly bool _isRad;

    private ExprParser(string s, bool isRad) { _s = s; _isRad = isRad; }

    /// <summary>Parses and evaluates <paramref name="expr"/> in one pass. Throws if any input remains unconsumed (i.e. trailing garbage).</summary>
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
            v = Math.Pow(v, Power()); // right-associative: recurse into Power, not Unary
        }
        return v;
    }

    private double Unary()
    {
        if (_pos < _s.Length && _s[_pos] == '-') { _pos++; return -Primary(); }
        if (_pos < _s.Length && _s[_pos] == '+') { _pos++; return Primary(); }
        return Primary();
    }

    /// <summary>Parses a number, constant, parenthesized sub-expression, or function call.</summary>
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
            return double.Parse(_s[start.._pos], System.Globalization.CultureInfo.InvariantCulture);
        }

        if (_s[_pos] == 'π') { _pos++; return Math.PI; }

        if (char.IsLetter(_s[_pos]))
        {
            // Read a full identifier (function name, or the constant "e").
            int start = _pos;
            while (_pos < _s.Length && char.IsLetter(_s[_pos])) _pos++;
            string name = _s[start.._pos].ToLower();

            // "e" not followed by '(' is the constant, not a function call.
            if (name == "e" && (_pos >= _s.Length || _s[_pos] != '('))
                return Math.E;

            if (_pos < _s.Length && _s[_pos] == '(')
            {
                _pos++;
                double arg = AddSub();
                if (_pos < _s.Length && _s[_pos] == ')') _pos++;

                double d2r = Math.PI / 180.0; // degrees -> radians
                double r2d = 180.0 / Math.PI; // radians -> degrees

                // Direct trig takes a degree/radian argument depending on UseRadians;
                // inverse trig returns radians/degrees the same way. Everything else
                // is unit-agnostic.
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
