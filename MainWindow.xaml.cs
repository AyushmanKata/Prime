using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WinCalc;

/// <summary>
/// Code-behind for the calculator window: button-grid construction, keyboard
/// handling, history rendering, and the title-bar chrome (drag, minimize, close,
/// theme menu). All calculation state and every editing/input rule live in
/// <see cref="Calculator"/> (_c) — this class only translates UI events into
/// calls on it and redraws.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Which button layout is currently shown.</summary>
    private enum CalcMode { Basic, Advanced }

    private readonly Calculator _c = new();
    private readonly App _app = (App)Application.Current;
    private CalcMode _mode = CalcMode.Basic;
    private bool _suppressTextChange; // guards against re-entrant TextChanged while we rewrite txtExpr.Text ourselves
    private bool _advancedSwapped;    // Advanced mode has two function layouts, toggled by the ⇄ button

    /// <summary>
    /// Button kinds that simply wrap the expression as "name(expr)". The button kind
    /// and the ExprParser function name are identical, so a set is enough. To add one:
    /// add its kind here, add a case in ExprParser.Apply, and add a button with that
    /// kind to a layout below.
    /// </summary>
    private static readonly HashSet<string> WrapFunctionKinds =
    [
        "sin", "cos", "tan",
        "asin", "acos", "atan",
        "sinh", "cosh", "tanh",
        "asinh", "acosh", "atanh",
        "ln", "log", "exp",
        "sqrt", "cbrt", "abs",
    ];

    /// <summary>Every character the expression box may contain ('E' is only for scientific notation such as 1E-05). Anything else is rejected.</summary>
    private static readonly HashSet<char> ValidChars =
        [.. "0123456789.+-*/×÷−%^()!πeE "];

    // ── DWM rounded corners ────────────────────────────────────────────────
    // WindowStyle="None" gives a borderless window with no OS-drawn corners, so
    // Windows 11's native rounded-corner look is requested explicitly via DWM
    // rather than faked with transparency (which previously caused an
    // invisible-window bug — see AllowsTransparency note in PLAN.md).
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr,
        ref int attrValue, int attrSize);
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    public MainWindow()
    {
        InitializeComponent();
        // Mode_Changed ignores the ComboBox's initial SelectionChanged (it fires inside
        // InitializeComponent, before btnGrid exists), so the first build happens here.
        BuildButtons();
    }

    private void Window_Loaded(object s, RoutedEventArgs e)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int pref = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));

        Activate();
        txtExpr.Focus();
    }

    // ── Title bar ──────────────────────────────────────────────────────────

    /// <summary>Lets the borderless window be dragged by its custom title bar.</summary>
    private void TitleBar_Drag(object s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void Close_Click(object s, RoutedEventArgs e)    => Close();
    private void Minimize_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    /// <summary>Builds and shows the hamburger (≡) menu: theme choices, copy result, clear history.</summary>
    private void Menu_Click(object s, RoutedEventArgs e)
    {
        var cm = new ContextMenu();
        cm.Items.Add(MkItem("☀  Light",          () => _app.SetTheme(App.AppTheme.Light)));
        cm.Items.Add(MkItem("🌙  Dark",           () => _app.SetTheme(App.AppTheme.Dark)));
        cm.Items.Add(MkItem("⚙  System Default", () => _app.SetTheme(App.AppTheme.System)));
        cm.Items.Add(new Separator());
        cm.Items.Add(MkItem("📋  Copy Result",   CopyResult));
        cm.Items.Add(MkItem("🗑  Clear History", ClearHistory));
        cm.PlacementTarget = btnMenu;
        cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        cm.IsOpen = true;
    }

    /// <summary>Builds a MenuItem with an inline click action, avoiding a named handler per entry.</summary>
    private static MenuItem MkItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>Rebuilds the button grid for the newly selected mode, resetting any function-layout swap.</summary>
    private void Mode_Changed(object s, SelectionChangedEventArgs e)
    {
        if (btnGrid == null) return; // fires once during InitializeComponent, before the grid exists — ignore
        _mode = ((ComboBox)s).SelectedIndex == 1 ? CalcMode.Advanced : CalcMode.Basic;
        _advancedSwapped = false;
        BuildButtons();
    }

    // ── Input filtering ────────────────────────────────────────────────────

    /// <summary>
    /// Rejects disallowed characters as they're typed and applies the shared limits in
    /// <see cref="Calculator.AllowsInput"/>. Typing a digit/constant/"(" while a result
    /// is showing starts a fresh expression, exactly like the on-screen buttons.
    /// </summary>
    private void txtExpr_PreviewInput(object s, TextCompositionEventArgs e)
    {
        if (e.Text.Length == 0 || !e.Text.All(ValidChars.Contains)) { e.Handled = true; return; }

        if (_c.ResultShown && Calculator.StartsOperand(e.Text[0]))
            txtExpr.Clear(); // TextChanged fires synchronously and resets _c.ResultShown

        // Check limits against the text as it will be once any selection is replaced.
        var text = txtExpr.Text;
        int caret = txtExpr.SelectionStart;
        if (txtExpr.SelectionLength > 0) text = text.Remove(caret, txtExpr.SelectionLength);

        if (!Calculator.AllowsInput(text, caret, e.Text)) e.Handled = true;
    }

    /// <summary>
    /// Keeps txtExpr and the Calculator in sync. Also the second line of defense
    /// against invalid characters (pasted text bypasses PreviewTextInput): strips
    /// anything not in ValidChars while preserving the caret.
    /// </summary>
    private void Expr_Changed(object s, TextChangedEventArgs e)
    {
        if (_suppressTextChange) return;

        var raw      = txtExpr.Text;
        var filtered = new string(raw.Where(ValidChars.Contains).ToArray());
        if (filtered != raw)
        {
            _suppressTextChange = true;
            int caret = Math.Max(0, txtExpr.CaretIndex - (raw.Length - filtered.Length));
            txtExpr.Text = filtered;
            txtExpr.CaretIndex = Math.Min(caret, filtered.Length);
            _suppressTextChange = false;
        }

        _c.Expr = txtExpr.Text; // also clears any error state
        txtExpr.SetResourceReference(TextBox.ForegroundProperty, "Fg");
        txtPreview.Text = _c.TryPreview();
        UpdateExprFontSize();
    }

    /// <summary>Shrinks the expression font as it grows so long expressions stay on screen.</summary>
    private void UpdateExprFontSize()
    {
        int len = txtExpr.Text.Length;
        txtExpr.FontSize = len switch
        {
            < 10 => 32,
            < 16 => 26,
            < 22 => 20,
            < 30 => 16,
            _ => 13
        };
    }

    /// <summary>Enter = calculate, Escape = clear everything, Delete = clear expression only.</summary>
    private void HandleShortcutKeys(KeyEventArgs e)
    {
        if (e.Key == Key.Enter)  { e.Handled = true; Calculate(); }
        if (e.Key == Key.Escape) { e.Handled = true; ClearAll(); }
        if (e.Key == Key.Delete) { e.Handled = true; ClearExpr(); }
    }

    // Wired to txtExpr's PreviewKeyDown in XAML: TextBox consumes Delete before a
    // plain KeyDown would see it, so tunneling (Preview) is required for that shortcut.
    private void Expr_KeyDown(object s, KeyEventArgs e) => HandleShortcutKeys(e);

    // Wired to the Window's bubbling KeyDown: catches the same shortcuts when focus
    // is on another control (e.g. the mode ComboBox).
    private void Window_KeyDown(object s, KeyEventArgs e) => HandleShortcutKeys(e);

    /// <summary>
    /// Repaints the expression box, error color, live preview and font size from
    /// Calculator state. The foreground is set as a resource *reference* (not a
    /// brush value) so it keeps following theme changes.
    /// </summary>
    private void RefreshDisplay()
    {
        _suppressTextChange = true;
        txtExpr.Text = _c.Expr;
        txtExpr.CaretIndex = txtExpr.Text.Length;
        _suppressTextChange = false;

        txtExpr.SetResourceReference(TextBox.ForegroundProperty, _c.HasError ? "WarnFg" : "Fg");
        txtPreview.Text = _c.HasError ? "" : _c.TryPreview();
        UpdateExprFontSize();
    }

    // ── Calculation ────────────────────────────────────────────────────────

    private void Calculate()
    {
        if (string.IsNullOrWhiteSpace(_c.Expr)) return;

        var before = _c.Expr.Trim();
        int historyCount = _c.History.Count;
        var result = _c.Evaluate();

        // Evaluate skips history when nothing changed (e.g. "5" -> "5"), and on error.
        if (_c.History.Count > historyCount) AddHistoryRow(before, result);

        RefreshDisplay();
        txtExpr.Focus();
    }

    private void ClearExpr()    { _c.ClearExpr(); RefreshDisplay(); }
    private void ClearHistory() { _c.ClearHistory(); histPanel.Children.Clear(); RefreshDisplay(); }
    private void ClearAll()     { _c.ClearExpr(); ClearHistory(); }

    /// <summary>Copies the live preview result if there is one, otherwise the current expression/result.</summary>
    private void CopyResult()
    {
        var text = txtPreview.Text.StartsWith("= ") ? txtPreview.Text[2..] : _c.Expr;
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); }
        catch (COMException) { /* clipboard briefly locked by another app; nothing useful to do */ }
    }

    // ── History ────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds one clickable "expr = result" row; clicking restores the result into the
    /// input. Colors are resource references, so existing rows repaint on theme change.
    /// </summary>
    private void AddHistoryRow(string expr, string result)
    {
        var sep = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 1) };
        sep.SetResourceReference(Border.BackgroundProperty, "HistLine");
        histPanel.Children.Add(sep);

        var row = new Grid { Margin = new Thickness(0, 1, 0, 1), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var t1 = HistoryText(expr,   "SubFg");
        var t2 = HistoryText(" = ",  "SubFg", margin: new Thickness(4, 0, 4, 0));
        var t3 = HistoryText(result, "Fg", FontWeights.Bold);
        Grid.SetColumn(t1, 0);
        Grid.SetColumn(t2, 1);
        Grid.SetColumn(t3, 2);
        row.Children.Add(t1);
        row.Children.Add(t2);
        row.Children.Add(t3);

        row.MouseLeftButtonDown += (_, _) => { _c.Expr = result; RefreshDisplay(); txtExpr.Focus(); };

        histPanel.Children.Add(row);
        histScroll.ScrollToBottom();
    }

    private static TextBlock HistoryText(string text, string brushKey,
        FontWeight? weight = null, Thickness? margin = null)
    {
        var t = new TextBlock { Text = text, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
        t.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        if (weight is { } w) t.FontWeight = w;
        if (margin is { } m) t.Margin = m;
        return t;
    }

    // ── Button Grid ────────────────────────────────────────────────────────

    // Layouts are (Label, Kind)[][] — one array per row — placed by PlaceLayout, which
    // avoids near-duplicate XAML for Basic vs Advanced vs the swapped Advanced grid.
    private static readonly (string L, string K)[][] BasicLayout =
    [
        [("C","clear"),  ("( )","paren"), ("%","percent"), ("⌫","back") ],
        [("7","num"),    ("8","num"),     ("9","num"),     ("÷","op")   ],
        [("4","num"),    ("5","num"),     ("6","num"),     ("×","op")   ],
        [("1","num"),    ("2","num"),     ("3","num"),     ("−","op")   ],
        [("0","num"),    (".","num"),     ("=","eq"),      ("+","op")   ],
    ];

    // Primary Advanced layout (direct trig, ln/log, 1/x, x², xʸ, |x|).
    private static readonly (string L, string K)[][] AdvancedLayout =
    [
        [("⇄","swap"), ("●Deg","rad"), ("√","sqrt"),  ("C","clear"), ("( )","paren"), ("%","percent"), ("⌫","back")],
        [("sin","sin"), ("cos","cos"), ("tan","tan"), ("7","num"),   ("8","num"),     ("9","num"),     ("÷","op")],
        [("ln","ln"),   ("log","log"), ("1/x","inv"), ("4","num"),   ("5","num"),     ("6","num"),     ("×","op")],
        [("eˣ","exp"),  ("x²","sq"),   ("xʸ","pow"),  ("1","num"),   ("2","num"),     ("3","num"),     ("−","op")],
        [("|x|","abs"), ("π","pi"),    ("e","euler"), ("0","num"),   (".","num"),     ("=","eq"),      ("+","op")],
    ];

    // Swapped Advanced layout, toggled via ⇄ (inverse/hyperbolic trig, ³√, 2ˣ, x³, x!).
    // Digit/operator columns match AdvancedLayout so the grid doesn't jump when swapping.
    private static readonly (string L, string K)[][] AdvancedLayout2 =
    [
        [("⇄","swap"),      ("●Deg","rad"),      ("³√","cbrt"),       ("C","clear"), ("( )","paren"), ("%","percent"), ("⌫","back")],
        [("sin⁻¹","asin"),  ("cos⁻¹","acos"),    ("tan⁻¹","atan"),    ("7","num"),   ("8","num"),     ("9","num"),     ("÷","op")],
        [("sinh","sinh"),   ("cosh","cosh"),     ("tanh","tanh"),     ("4","num"),   ("5","num"),     ("6","num"),     ("×","op")],
        [("sinh⁻¹","asinh"),("cosh⁻¹","acosh"),  ("tanh⁻¹","atanh"),  ("1","num"),   ("2","num"),     ("3","num"),     ("−","op")],
        [("2ˣ","pow2"),     ("x³","cube"),       ("x!","fact"),       ("0","num"),   (".","num"),     ("=","eq"),      ("+","op")],
    ];

    /// <summary>Clears and rebuilds the whole button grid for the current mode/layout.</summary>
    private void BuildButtons()
    {
        btnGrid.Children.Clear();
        btnGrid.RowDefinitions.Clear();
        btnGrid.ColumnDefinitions.Clear();

        if (_mode == CalcMode.Advanced) PlaceLayout(_advancedSwapped ? AdvancedLayout2 : AdvancedLayout, 52);
        else                            PlaceLayout(BasicLayout, 54);

        UpdateRadButton(); // layouts hard-code "●Deg"; re-sync the label with the real angle mode
    }

    /// <summary>Creates equal-width columns and fixed-height rows sized from the layout, then places one button per cell.</summary>
    private void PlaceLayout((string L, string K)[][] layout, double rowH)
    {
        int cols = layout.Max(r => r.Length);
        for (int i = 0; i < cols; i++)
            btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < layout.Length; i++)
            btnGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(rowH) });

        for (int r = 0; r < layout.Length; r++)
        for (int c = 0; c < layout[r].Length; c++)
        {
            var (label, kind) = layout[r][c];
            if (string.IsNullOrEmpty(label) || kind == "skip") continue;

            var btn = MakeBtn(label, kind);
            Grid.SetRow(btn, r);
            Grid.SetColumn(btn, c);
            btnGrid.Children.Add(btn);
        }
    }

    /// <summary>Creates one calculator button styled by kind (equals / operator-like / plain) and wires its click handler.</summary>
    private Button MakeBtn(string label, string kind)
    {
        var styleKey = kind switch
        {
            "eq"                                              => "EqBtn",
            "op" or "percent" or "paren" or "clear" or "back" => "OpBtn",
            _                                                 => "Btn"
        };
        var btn = new Button
        {
            Content = label,
            Tag     = kind,
            Style   = (Style)Application.Current.Resources[styleKey],
        };
        btn.Click += (_, _) => HandleBtn(label, kind);
        return btn;
    }

    /// <summary>Updates the Rad/Deg button's label (found by its "rad" Tag) to reflect the current angle mode.</summary>
    private void UpdateRadButton()
    {
        foreach (var child in btnGrid.Children)
        {
            if (child is Button btn && btn.Tag as string == "rad")
            {
                btn.Content = _c.UseRadians ? "●Rad" : "●Deg";
                break;
            }
        }
    }

    // ── Button actions ─────────────────────────────────────────────────────

    /// <summary>Central dispatch for every calculator button click, keyed by the button's "kind".</summary>
    private void HandleBtn(string label, string kind)
    {
        switch (kind)
        {
            case "clear": _c.ClearExpr(); break;
            case "back":  _c.Backspace(); break;
            case "eq":    Calculate();    break;

            case "swap": // toggle between AdvancedLayout and AdvancedLayout2
                _advancedSwapped = !_advancedSwapped;
                BuildButtons();
                break;

            case "rad": // toggle degrees/radians for trig functions
                _c.UseRadians = !_c.UseRadians;
                UpdateRadButton();
                break;

            case "num":     _c.TryAppend(label);                  break;
            case "op":      _c.AppendOperator(label);             break;
            case "pow":     _c.AppendOperator("^", spaced: false); break;
            case "percent": _c.AppendPercent();                   break;
            case "paren":   _c.AppendParen();                     break;
            case "pi":      _c.TryAppend("π");                    break;
            case "euler":   _c.TryAppend("e");                    break;

            // Rewrite the whole expression around itself; parentheses only when needed.
            case "inv":  _c.ApplyToExpr("1/{0}");  break;
            case "sq":   _c.ApplyToExpr("{0}^2");  break;
            case "cube": _c.ApplyToExpr("{0}^3");  break;
            case "pow2": _c.ApplyToExpr("2^{0}");  break;
            case "fact": _c.ApplyToExpr("{0}!");   break;

            default:
                // sin, cos, ln, sqrt, ... — see WrapFunctionKinds.
                if (WrapFunctionKinds.Contains(kind)) _c.WrapFunction(kind);
                break;
        }

        RefreshDisplay();
        txtExpr.Focus();
        txtExpr.CaretIndex = txtExpr.Text.Length;
    }
}
