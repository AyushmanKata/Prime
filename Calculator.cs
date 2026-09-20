using System.Globalization;
using System.Text.RegularExpressions;

namespace WinCalc;

/// <summary>One completed calculation: the expression typed and the result produced.</summary>
public record HistoryEntry(string Expression, string Result);

/// <summary>
/// Holds calculator state (current expression, error flag, history, angle mode),
/// owns every rule about what may be typed/appended, and drives evaluation via
/// <see cref="ExprParser"/>. UI code (MainWindow) only ever talks to this class —
/// it never touches ExprParser directly.
/// </summary>
public partial class Calculator
{
    public const int MaxDigitsPerNumber = 20;
    public const int MaxOperators = 15;

    /// <summary>Every character that counts as an operator for the operator cap and operator replacement.</summary>
    private const string OperatorChars = "+-*/^%×÷−";

    private string _expr = "";

    public List<HistoryEntry> History { get; } = [];

    /// <summary>
    /// The current expression. Assigning it always clears the error state and the
    /// "result is showing" state, because any edit means the old evaluation no longer applies.
    /// </summary>
    public string Expr
    {
        get => _expr;
        set { _expr = value; HasError = false; ResultShown = false; }
    }

    /// <summary>True after a failed <see cref="Evaluate"/>; the expression is left untouched so it can be fixed.</summary>
    public bool HasError { get; private set; }

    /// <summary>True right after a successful "=": typing a digit/constant/"(" then starts a new calculation instead of extending the result.</summary>
    public bool ResultShown { get; private set; }

    /// <summary>True = trig functions use radians; false = degrees. Toggled by the Rad/Deg button.</summary>
    public bool UseRadians { get; set; }

    // ── Evaluation ─────────────────────────────────────────────────────────

    /// <summary>
    /// Evaluates the current expression. On success the expression is replaced by the
    /// formatted result (and pushed to history unless nothing changed, e.g. "5" -> "5").
    /// On failure (bad syntax, divide by zero, domain error) HasError is set, "Error"
    /// is returned, and neither the expression nor the history is touched.
    /// </summary>
    public string Evaluate()
    {
        var raw = Expr.Trim();
        if (raw.Length == 0) return "";
        try
        {
            var d = ExprParser.Evaluate(Preprocess(raw), UseRadians);
            if (double.IsNaN(d) || double.IsInfinity(d))
                throw new ExprException("Result is not a finite number");

            var result = Format(d);
            if (result != raw) History.Add(new HistoryEntry(raw, result));
            Expr = result;
            ResultShown = true;
            return result;
        }
        catch (ExprException)
        {
            HasError = true;
            return "Error";
        }
    }

    /// <summary>
    /// Live "= result" preview shown above the input as the user types. Returns ""
    /// for empty input, a plain number, or anything that doesn't currently evaluate —
    /// a preview should never look like an error state.
    /// </summary>
    public string TryPreview()
    {
        var t = Expr.Trim();
        if (t.All(c => char.IsAsciiDigit(c) || c == '.')) return ""; // empty or a plain number
        try
        {
            var p = Preprocess(t);
            var d = ExprParser.Evaluate(p, UseRadians);
            if (double.IsNaN(d) || double.IsInfinity(d)) return "";
            var result = Format(d);
            return result == p ? "" : $"= {result}";
        }
        catch (ExprException) { return ""; }
    }

    /// <summary>
    /// Formats a double for display. Whole numbers print without a decimal point;
    /// everything else uses up to 12 significant digits. Always culture-invariant so
    /// the output can be parsed back (a comma decimal separator would break that).
    /// Very large/small values come out in scientific form ("1.5E-07"), which
    /// ExprParser understands.
    /// </summary>
    public static string Format(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) return "Error";
        if (d == Math.Truncate(d) && Math.Abs(d) < 1e15)
            return ((long)d).ToString(CultureInfo.InvariantCulture);
        return d.ToString("G12", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Normalizes UI symbols to what ExprParser understands, then inserts implicit
    /// multiplication ("2π" -> "2*π", "(2+3)4" -> "(2+3)*4", "5!3" -> "5!*3").
    /// Spaces are stripped first so "2 (3)" is treated like "2(3)".
    /// </summary>
    private static string Preprocess(string s)
    {
        s = s.Replace(" ", "")
             .Replace("×", "*")
             .Replace("÷", "/")
             .Replace("−", "-")
             .Replace(",", "");

        // '*' goes wherever one value ends and another begins with no operator between:
        //   digit -> ( π e     "2(3)" "2π"       )/! -> digit ( π e   "(1+1)2" "5!3"
        //   π/e   -> digit ( π e   "π2" "πe"
        // digit->digit and digit->'.' are deliberately excluded (same number literal).
        s = ImplicitAfterDigit().Replace(s, "$1*$2");
        s = ImplicitAfterCloseOrBang().Replace(s, "$1*$2");
        s = ImplicitAfterConstant().Replace(s, "$1*$2");
        return s;
    }

    // ── Editing ────────────────────────────────────────────────────────────

    public void ClearExpr()    => Expr = "";
    public void ClearHistory() => History.Clear();

    /// <summary>
    /// Deletes one character. Trailing spaces around operators are skipped, and
    /// deleting an opening parenthesis also removes the function name before it
    /// ("sin(" goes in one press).
    /// </summary>
    public void Backspace()
    {
        var t = Expr.TrimEnd();
        if (t.Length == 0) { Expr = ""; return; }

        if (t[^1] == '(')
        {
            int i = t.Length - 1;
            while (i > 0 && char.IsLetter(t[i - 1]) && t[i - 1] != 'π') i--;
            t = t[..i];
        }
        else t = t[..^1];

        Expr = t.TrimEnd();
    }

    /// <summary>
    /// Appends text if the input limits allow it. Starting a new number, constant or
    /// "(" right after "=" replaces the shown result instead of extending it.
    /// </summary>
    public bool TryAppend(string s)
    {
        if (s.Length == 0) return false;
        if (ResultShown && StartsOperand(s[0])) Expr = "";
        if (!AllowsInput(Expr, Expr.Length, s)) return false;
        Expr += s;
        return true;
    }

    /// <summary>
    /// Appends a binary operator. A dangling operator is replaced rather than stacked
    /// ("5 ×" then "+" gives "5 +"); with nothing to operate on, only a leading minus sign is accepted.
    /// </summary>
    public bool AppendOperator(string op, bool spaced = true)
    {
        var t = Expr.TrimEnd();
        if (t.Length > 0 && t[^1] != '%' && OperatorChars.Contains(t[^1]))
            t = t[..^1].TrimEnd();
        Expr = t;

        if (NeedsOperand(t)) return op == "−" && TryAppend(op);
        return TryAppend(spaced ? $" {op} " : op);
    }

    /// <summary>Appends '%' only when there is a value in front of it.</summary>
    public bool AppendPercent() => !NeedsOperand(Expr.TrimEnd()) && TryAppend("%");

    /// <summary>Picks "(" or ")" for the ( ) button: close only if a parenthesis is open and a value precedes the cursor.</summary>
    public bool AppendParen()
    {
        int open = Expr.Count(c => c == '('), close = Expr.Count(c => c == ')');
        bool canClose = open > close && !NeedsOperand(Expr.TrimEnd());
        return TryAppend(canClose ? ")" : "(");
    }

    /// <summary>
    /// Wraps the whole expression as name(expr) — "9" becomes "sqrt(9)". With nothing
    /// to wrap (empty, or right after an operator or "(") it just opens "name(" so the
    /// user can keep typing the argument.
    /// </summary>
    public void WrapFunction(string name)
    {
        if (NeedsOperand(Expr.TrimEnd())) { Expr += name + "("; return; }
        Expr = $"{name}({Expr})";
    }

    /// <summary>
    /// Rewrites the expression around itself using a template with {0} as the operand,
    /// e.g. "1/{0}", "{0}^2", "{0}!". Parentheses are added only when the operand is
    /// not a plain number, so "5" gives "5^2" but "2+3" gives "(2+3)^2".
    /// </summary>
    public void ApplyToExpr(string template)
    {
        var e = Expr.Trim();
        if (e.Length == 0 || NeedsOperand(e)) return;
        var operand = e.All(c => char.IsAsciiDigit(c) || c == '.') ? e : $"({e})";
        Expr = string.Format(template, operand);
    }

    // ── Input rules (shared by the on-screen buttons and the keyboard) ─────

    /// <summary>True for characters that can begin a new operand.</summary>
    public static bool StartsOperand(char c) => char.IsAsciiDigit(c) || c is '.' or 'π' or 'e' or '(';

    /// <summary>
    /// Whether <paramref name="input"/> may be inserted at <paramref name="caret"/> in
    /// <paramref name="text"/> without breaking a limit: at most 20 digits per number,
    /// one decimal point per number, at most 15 operators per expression.
    /// </summary>
    public static bool AllowsInput(string text, int caret, string input)
    {
        if (input.Length == 0) return true;
        caret = Math.Clamp(caret, 0, text.Length);

        char first = input[0];
        if (char.IsAsciiDigit(first) || first == '.')
        {
            int start = caret, end = caret;
            while (start > 0 && IsNumberChar(text[start - 1])) start--;
            while (end < text.Length && IsNumberChar(text[end])) end++;
            var run = text[start..end];

            if (first == '.' && run.Contains('.')) return false;
            if (char.IsAsciiDigit(first) && run.Count(char.IsAsciiDigit) >= MaxDigitsPerNumber) return false;
        }

        var op = input.Trim();
        if (op.Length > 0 && op.All(c => OperatorChars.Contains(c))
            && text.Count(c => OperatorChars.Contains(c)) >= MaxOperators)
            return false;

        return true;
    }

    private static bool IsNumberChar(char c) => char.IsAsciiDigit(c) || c == '.';

    /// <summary>True when nothing usable precedes the end of <paramref name="t"/> (already trimmed): empty, after "(", or after an operator.</summary>
    private static bool NeedsOperand(string t) =>
        t.Length == 0 || t[^1] == '(' || (t[^1] != '%' && OperatorChars.Contains(t[^1]));

    [GeneratedRegex(@"(\d)([(πe])")]
    private static partial Regex ImplicitAfterDigit();

    [GeneratedRegex(@"([)!])(\d|[(πe])")]
    private static partial Regex ImplicitAfterCloseOrBang();

    [GeneratedRegex(@"([πe])(\d|[(πe])")]
    private static partial Regex ImplicitAfterConstant();
}

/// <summary>Thrown for any expression the parser cannot evaluate (syntax, unknown name, nesting too deep).</summary>
internal sealed class ExprException(string message) : Exception(message);

/// <summary>
/// Recursive-descent expression parser/evaluator. Replaces the earlier
/// <c>DataTable.Compute</c> approach, which treated '^' as bitwise XOR and had
/// no function support.
///
/// Grammar (lowest to highest precedence):
/// <code>
/// AddSub  := MulDiv (('+' | '-') MulDiv)*
/// MulDiv  := Unary (('*' | '/' | '%') Unary)*
/// Unary   := ('-' | '+') Unary | Power           // so -2^2 == -(2^2) == -4
/// Power   := Postfix ('^' Unary)?                 // right-associative: 2^3^2 == 2^(3^2); 2^-1 works
/// Postfix := Primary ('!' | '%')*                 // '%' here means "percent" (see below)
/// Primary := NUMBER | 'π' | 'e' | '(' AddSub ')' | FUNC '(' AddSub ')'
/// </code>
/// '%' is percent (÷100) unless an operand follows it, in which case it is modulo:
/// "50%" = 0.5 but "10%3" = 1. Directly after '+' or '-', a plain percent is taken
/// of the left side, as on a phone calculator: "200+10%" = 220.
/// Missing closing parentheses are tolerated so the live preview works mid-typing.
/// </summary>
internal sealed class ExprParser
{
    private const int MaxDepth = 256; // guards against stack overflow from pasted "((((((…"

    private readonly string _s;
    private readonly bool _isRad;
    private int _pos;
    private int _depth;

    private ExprParser(string s, bool isRad) { _s = s; _isRad = isRad; }

    private bool More => _pos < _s.Length;
    private char Cur => _s[_pos];

    /// <summary>Parses and evaluates <paramref name="expr"/> in one pass. Throws if any input remains unconsumed.</summary>
    public static double Evaluate(string expr, bool isRad)
    {
        var p = new ExprParser(expr.Replace(" ", ""), isRad);
        double result = p.AddSub();
        if (p.More) throw new ExprException($"Unexpected '{p.Cur}' at {p._pos}");
        return result;
    }

    private double AddSub()
    {
        double v = MulDiv();
        while (More && (Cur == '+' || Cur == '-'))
        {
            char op = _s[_pos++];
            int start = _pos;
            double r = MulDiv();
            if (IsPlainPercent(_s[start.._pos])) r *= v; // 200 + 10%  ->  200 + (10% of 200)
            v = op == '+' ? v + r : v - r;
        }
        return v;
    }

    private static bool IsPlainPercent(string term) =>
        term.Length > 1 && term[^1] == '%' && term[..^1].All(c => char.IsAsciiDigit(c) || c == '.');

    private double MulDiv()
    {
        double v = Unary();
        while (More && (Cur == '*' || Cur == '/' || Cur == '%'))
        {
            char op = _s[_pos++];
            double r = Unary();
            v = op == '*' ? v * r : op == '/' ? v / r : v % r;
        }
        return v;
    }

    /// <summary>Every recursion cycle in the grammar passes through here, so this is where nesting depth is limited.</summary>
    private double Unary()
    {
        if (_depth++ >= MaxDepth) throw new ExprException("Expression nested too deeply");
        try
        {
            if (More && Cur == '-') { _pos++; return -Unary(); }
            if (More && Cur == '+') { _pos++; return Unary(); }
            return Power();
        }
        finally { _depth--; }
    }

    private double Power()
    {
        double v = Postfix();
        if (More && Cur == '^')
        {
            _pos++;
            v = Math.Pow(v, Unary()); // right side goes through Unary: right-associative, and allows a sign
        }
        return v;
    }

    private double Postfix()
    {
        double v = Primary();
        while (More)
        {
            if (Cur == '!') { _pos++; v = Factorial(v); }
            else if (Cur == '%' && !StartsOperand(_pos + 1)) { _pos++; v /= 100; }
            else break;
        }
        return v;
    }

    private bool StartsOperand(int i) =>
        i < _s.Length && (char.IsAsciiDigit(_s[i]) || _s[i] is '.' or '(' || char.IsLetter(_s[i]));

    /// <summary>Integer factorial for 0–170 (171! overflows double); anything else is undefined -> NaN -> "Error".</summary>
    private static double Factorial(double v)
    {
        if (v < 0 || v != Math.Floor(v) || v > 170) return double.NaN;
        double f = 1;
        for (int i = 2; i <= (int)v; i++) f *= i;
        return f;
    }

    /// <summary>Parses a number, constant, parenthesized sub-expression, or function call.</summary>
    private double Primary()
    {
        if (!More) throw new ExprException("Unexpected end");
        char c = Cur;

        if (c == '(')
        {
            _pos++;
            double v = AddSub();
            if (More && Cur == ')') _pos++;
            return v;
        }
        if (char.IsAsciiDigit(c) || c == '.') return Number();
        if (c == 'π') { _pos++; return Math.PI; }
        if (char.IsLetter(c)) return NameOrCall();

        throw new ExprException($"Unexpected '{c}' at {_pos}");
    }

    private double Number()
    {
        int start = _pos;
        while (More && (char.IsAsciiDigit(Cur) || Cur == '.')) _pos++;

        // Scientific notation as produced by Calculator.Format, e.g. 1.5E-07 or 1E+20.
        if (More && Cur == 'E')
        {
            int p = _pos + 1;
            if (p < _s.Length && (_s[p] == '+' || _s[p] == '-')) p++;
            if (p < _s.Length && char.IsAsciiDigit(_s[p]))
            {
                while (p < _s.Length && char.IsAsciiDigit(_s[p])) p++;
                _pos = p;
            }
        }

        if (!double.TryParse(_s.AsSpan(start.._pos), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new ExprException($"Bad number '{_s[start.._pos]}'");
        return value;
    }

    /// <summary>Reads an identifier: either a function call like sqrt(…) or the constant "e".</summary>
    private double NameOrCall()
    {
        int start = _pos;
        while (More && char.IsLetter(Cur) && Cur != 'π') _pos++;
        string name = _s[start.._pos].ToLowerInvariant();

        if (More && Cur == '(')
        {
            _pos++;
            double arg = AddSub();
            if (More && Cur == ')') _pos++;
            return Apply(name, arg);
        }

        if (name == "e") return Math.E;
        throw new ExprException($"Unknown name '{name}'");
    }

    /// <summary>Applies a named function. Trig respects the Rad/Deg setting; results that are zero only through rounding noise are snapped to 0.</summary>
    private double Apply(string name, double x)
    {
        double toRad   = _isRad ? 1.0 : Math.PI / 180.0;
        double fromRad = _isRad ? 1.0 : 180.0 / Math.PI;

        return name switch
        {
            "sin"   => Snap(Math.Sin(x * toRad)),
            "cos"   => Snap(Math.Cos(x * toRad)),
            "tan"   => Math.Abs(Math.Cos(x * toRad)) < 1e-15 ? double.NaN : Snap(Math.Tan(x * toRad)), // tan(90°) is undefined
            "asin"  => Math.Asin(x) * fromRad,
            "acos"  => Math.Acos(x) * fromRad,
            "atan"  => Math.Atan(x) * fromRad,
            "sinh"  => Math.Sinh(x),
            "cosh"  => Math.Cosh(x),
            "tanh"  => Math.Tanh(x),
            "asinh" => Math.Asinh(x),
            "acosh" => Math.Acosh(x),
            "atanh" => Math.Atanh(x),
            "ln"    => Math.Log(x),
            "log"   => Math.Log10(x),
            "exp"   => Math.Exp(x),
            "sqrt"  => Math.Sqrt(x),
            "cbrt"  => Math.Cbrt(x),
            "abs"   => Math.Abs(x),
            "ceil"  => Math.Ceiling(x),
            "floor" => Math.Floor(x),
            _ => throw new ExprException($"Unknown function '{name}'")
        };
    }

    /// <summary>sin(180°) is 1.2E-16 in floating point; show the 0 the user expects.</summary>
    private static double Snap(double v) => Math.Abs(v) < 1e-12 ? 0 : v;
}
