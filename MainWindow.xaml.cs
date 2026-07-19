using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WinCalc;

/// <summary>
/// Code-behind for the calculator window: button-grid construction, input
/// filtering/validation, keyboard shortcuts, history rendering, and the
/// title-bar chrome (drag, minimize, close, theme menu).
/// All calculation state itself lives in <see cref="Calculator"/> (_c) —
/// this class only translates UI events into calls on it and redraws.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Which button layout is currently shown. Replaces an earlier raw string field for type safety.</summary>
    private enum CalcMode { Basic, Advanced }

    private readonly Calculator _c = new();
    private readonly App _app = (App)Application.Current;
    private CalcMode _mode = CalcMode.Basic;
    private bool _suppressTextChange; // guards against re-entrant TextChanged while we rewrite txtExpr.Text ourselves
    private bool _advancedSwapped;    // Advanced mode has two function layouts, toggled by the ⇄ button

    /// <summary>
    /// Button kinds that simply wrap the current expression as "name(expr)" with
    /// no other logic (unlike "sq", "inv", "fact", etc., which each need custom
    /// formatting). The button kind and the ExprParser function name are always
    /// identical for these, so a HashSet is sufficient — no need for a
    /// dictionary mapping a key to itself. To add a new single-arg wrapping
    /// function: add its kind here, add a case in ExprParser.Primary, and add a
    /// button with that kind to a layout below.
    /// </summary>
    private static readonly HashSet<string> WrapFunctionKinds =
    [
        "asin", "acos", "atan",
        "sinh", "cosh", "tanh",
        "asinh", "acosh", "atanh",
        "ln", "log", "exp",
        "sqrt", "cbrt", "abs",
    ];

    /// <summary>Every character the expression box is allowed to contain. Anything else is rejected in <see cref="txtExpr_PreviewInput"/>.</summary>
    private static readonly HashSet<char> ValidChars =
        [.. "0123456789.+-*/×÷−%^() πe"];

    // ── DWM rounded corners ────────────────────────────────────────────────
    // WindowStyle="None" gives a borderless window with no OS-drawn corners,
    // so Windows 11's native rounded-corner look is requested explicitly here
    // via DWM rather than faked with transparency (which previously caused an
    // invisible-window bug — see AllowsTransparency note in PLAN.md).
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr,
        ref int attrValue, int attrSize);
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    public MainWindow()
    {
        InitializeComponent();

        // Deferred to run after InitializeComponent fully completes: building
        // the button grid touches btnGrid, and the mode ComboBox's initial
        // SelectionChanged can fire *during* InitializeComponent (before
        // btnGrid exists). Mode_Changed guards against that with a null check;
        // this defers the first real build until the window is safely ready.
        Dispatcher.BeginInvoke(() =>
        {
            try { BuildButtons(); txtExpr.Focus(); }
            catch (Exception ex) { MessageBox.Show($"Build error:\n{ex.Message}", "Error"); }
        });
    }

    private void Window_Loaded(object s, RoutedEventArgs e)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int pref = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));

        Icon = new System.Windows.Media.Imaging.BitmapImage(
            new Uri("pack://application:,,,/icon.png"));
        Activate();
        Focus();
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
        cm.Items.Add(MkItem("☀  Light",         () => _app.SetTheme(App.AppTheme.Light)));
        cm.Items.Add(MkItem("🌙  Dark",           () => _app.SetTheme(App.AppTheme.Dark)));
        cm.Items.Add(MkItem("⚙  System Default", () => _app.SetTheme(App.AppTheme.System)));
        cm.Items.Add(new Separator());
        cm.Items.Add(MkItem("📋  Copy Result",   CopyResult));
        cm.Items.Add(MkItem("🗑  Clear History", ClearHistory));
        cm.PlacementTarget = btnMenu;
        cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        cm.IsOpen = true;
    }

    /// <summary>Small helper to build a MenuItem with a click action inline, avoiding a named handler per menu entry.</summary>
    private static MenuItem MkItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>Rebuilds the button grid for the newly selected mode (Basic/Advanced), resetting any function-layout swap.</summary>
    private void Mode_Changed(object s, SelectionChangedEventArgs e)
    {
        if (btnGrid == null) return; // fires once during InitializeComponent, before the grid exists — ignore
        if (cmbMode.SelectedItem is ComboBoxItem item)
        {
            _mode = item.Content.ToString() == "Advanced" ? CalcMode.Advanced : CalcMode.Basic;
            _advancedSwapped = false;
            BuildButtons();
        }
    }

    // ── Input filtering ────────────────────────────────────────────────────

    /// <summary>Rejects disallowed characters as they're typed, and enforces the per-number digit cap and per-expression operator cap.</summary>
    private void txtExpr_PreviewInput(object s, TextCompositionEventArgs e)
    {
        if (!e.Text.All(c => ValidChars.Contains(c))) { e.Handled = true; return; }

        // Cap each individual number at 20 digits: find the contiguous digit
        // run the caret sits inside/next to and block once it's long enough.
        if (char.IsDigit(e.Text[0]))
        {
            var txt = txtExpr.Text;
            int pos = txtExpr.CaretIndex;
            int start = pos, end = pos;
            while (start > 0 && char.IsDigit(txt[start - 1])) start--;
            while (end < txt.Length && char.IsDigit(txt[end])) end++;
            if (end - start >= 20) { e.Handled = true; return; }
        }

        // Cap the whole expression at 15 operators to prevent runaway length.
        if (e.Text.All(c => "+-*/^%".Contains(c)) && txtExpr.Text.Count(c => "+-*/^%".Contains(c)) >= 15)
        { e.Handled = true; return; }
    }

    /// <summary>
    /// Keeps txtExpr's text and the underlying Calculator in sync. Also acts as
    /// a second line of defense against invalid characters (e.g. pasted text,
    /// which bypasses PreviewTextInput) by stripping anything not in
    /// ValidChars and preserving caret position.
    /// </summary>
    private void Expr_Changed(object s, TextChangedEventArgs e)
    {
        if (_suppressTextChange) return;
        var raw      = txtExpr.Text;
        var filtered = new string(raw.Where(c => ValidChars.Contains(c)).ToArray());
        if (filtered != raw)
        {
            _suppressTextChange = true;
            int caret = Math.Max(0, txtExpr.CaretIndex - (raw.Length - filtered.Length));
            txtExpr.Text = filtered;
            txtExpr.CaretIndex = Math.Min(caret, filtered.Length);
            _suppressTextChange = false;
        }
        _c.Expr = txtExpr.Text;
        txtPreview.Text = _c.TryPreview();
    }

    /// <summary>Shrinks the expression font as it grows, keeping long expressions on screen without a fixed max height.</summary>
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

    private void txtExpr_SizeChanged(object s, SizeChangedEventArgs e) => UpdateExprFontSize();

    /// <summary>Enter = calculate, Escape = clear everything, Delete = clear expression only.</summary>
    private void HandleShortcutKeys(KeyEventArgs e)
    {
        if (e.Key == Key.Enter)  { e.Handled = true; Calculate(); }
        if (e.Key == Key.Escape) { e.Handled = true; ClearAll(); }
        if (e.Key == Key.Delete) { e.Handled = true; ClearExpr(); }
    }

    // Wired to txtExpr's PreviewKeyDown in XAML: TextBox consumes Delete
    // before a plain KeyDown would ever see it, so tunneling (Preview) is
    // required here specifically for the Delete-clears-expression shortcut.
    private void Expr_KeyDown(object s, KeyEventArgs e) => HandleShortcutKeys(e);

    // Wired to the Window's bubbling KeyDown in XAML: catches the same
    // shortcuts when focus is on some other control (e.g. the mode
    // ComboBox) rather than the expression box.
    private void Window_KeyDown(object s, KeyEventArgs e) => HandleShortcutKeys(e);

    /// <summary>Repaints the expression box, error color, live preview, and font size from current Calculator state.</summary>
    private void RefreshDisplay()
    {
        _suppressTextChange = true;
        txtExpr.Text = _c.Expr;
        txtExpr.CaretIndex = txtExpr.Text.Length;
        txtExpr.Foreground = _c.HasError
            ? (Brush)Application.Current.Resources["WarnFg"]
            : (Brush)Application.Current.Resources["Fg"];
        _suppressTextChange = false;
        txtPreview.Text = _c.HasError ? "" : _c.TryPreview();
        UpdateExprFontSize();
    }

    // ── Calculation ────────────────────────────────────────────────────────
    private void Calculate()
    {
        if (string.IsNullOrWhiteSpace(_c.Expr)) return;
        var before = _c.Expr;
        var result = _c.Evaluate();
        if (result == "Error") { RefreshDisplay(); return; }
        AddHistoryRow(before, result);
        RefreshDisplay();
        txtExpr.Focus();
    }

    private void ClearExpr()    { _c.ClearExpr(); RefreshDisplay(); }
    private void ClearHistory() { _c.ClearHistory(); histPanel.Children.Clear(); RefreshDisplay(); }
    private void ClearAll()     { _c.ClearExpr(); _c.ClearHistory(); histPanel.Children.Clear(); RefreshDisplay(); }
    private void CopyResult()   { if (!string.IsNullOrEmpty(_c.Expr)) Clipboard.SetText(_c.Expr); }

    // ── History ────────────────────────────────────────────────────────────

    /// <summary>Adds one clickable "expr = result" row to the history panel; clicking it restores the result into the input.</summary>
    private void AddHistoryRow(string expr, string result)
    {
        var subFg = (Brush)Application.Current.Resources["SubFg"];
        var fg    = (Brush)Application.Current.Resources["Fg"];
        var line  = (Brush)Application.Current.Resources["HistLine"];

        histPanel.Children.Add(new Border
            { Height = 1, Background = line, Margin = new Thickness(0,0,0,1) });

        var row = new Grid { Margin = new Thickness(0,1,0,1), Cursor = Cursors.Hand };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var t1 = new TextBlock { Text = expr,   Foreground = subFg, FontSize = 14,
                                 TextTrimming = TextTrimming.CharacterEllipsis };
        var t2 = new TextBlock { Text = " = ",  Foreground = subFg, FontSize = 14,
                                 Margin = new Thickness(4,0,4,0) };
        var t3 = new TextBlock { Text = result, Foreground = fg,    FontSize = 14,
                                 FontWeight = FontWeights.Bold };

        t1.SetValue(Grid.ColumnProperty, 0);
        t2.SetValue(Grid.ColumnProperty, 1);
        t3.SetValue(Grid.ColumnProperty, 2);
        row.Children.Add(t1); row.Children.Add(t2); row.Children.Add(t3);
        row.MouseLeftButtonDown += (_, _) => { _c.Expr = result; RefreshDisplay(); };

        histPanel.Children.Add(row);
        histScroll.ScrollToBottom();
    }

    // ── Button Grid ────────────────────────────────────────────────────────

    /// <summary>Clears and rebuilds the whole button grid for the current mode/layout.</summary>
    private void BuildButtons()
    {
        if (btnGrid == null) return;
        btnGrid.Children.Clear();
        btnGrid.RowDefinitions.Clear();
        btnGrid.ColumnDefinitions.Clear();
        if (_mode == CalcMode.Advanced) BuildAdvanced();
        else                            BuildBasic();
    }

    // Layouts are declared as (Label, Kind)[][] — one array per row — and
    // placed into the Grid by PlaceLayout below. This avoids hand-writing
    // near-duplicate XAML for Basic vs Advanced vs the swapped Advanced grid.
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
        [("⇄","swap"),  ("●Deg", "rad"), ("√","sqrt"),  ("C","clear"),   ("( )","paren"), ("%","percent"), ("⌫","back")],
        [("sin","fn"),  ("cos","fn"),  ("tan","fn"),  ("7","num"),     ("8","num"),     ("9","num"),     ("÷","op")],
        [("ln","ln"),   ("log","log"), ("1/x","inv"), ("4","num"),     ("5","num"),     ("6","num"),     ("×","op")],
        [("eˣ","exp"),  ("x²","sq"),   ("xʸ","pow"),  ("1","num"),     ("2","num"),     ("3","num"),     ("−","op")],
        [("|x|","abs"), ("π","pi"),    ("e","euler"), ("0","num"),     (".","num"),     ("=","eq"),      ("+","op")],
    ];

    // Swapped Advanced layout, toggled via ⇄ (inverse/hyperbolic trig, ³√, 2ˣ, x³, x!).
    // Digit/operator columns are kept identical to AdvancedLayout so the grid
    // doesn't visually jump when swapping.
    private static readonly (string L, string K)[][] AdvancedLayout2 =
    [
        [("⇄","swap"), ("●Deg", "rad"),    ("³√","cbrt"),    ("C","clear"),  ("( )","paren"), ("%","percent"), ("⌫","back")],
        [("sin⁻¹","asin"),  ("cos⁻¹","acos"), ("tan⁻¹","atan"), ("7","num"),   ("8","num"),     ("9","num"),     ("÷","op")],
        [("sinh","sinh"),    ("cosh","cosh"),  ("tanh","tanh"),  ("4","num"),   ("5","num"),     ("6","num"),     ("×","op")],
        [("sinh⁻¹","asinh"),("cosh⁻¹","acosh"),("tanh⁻¹","atanh"),("1","num"), ("2","num"),     ("3","num"),     ("−","op")],
        [("2ˣ","pow2"),     ("x³","cube"),    ("x!","fact"),    ("0","num"),     (".","num"),     ("=","eq"),      ("+","op")],
    ];

    private void BuildBasic() => PlaceLayout(BasicLayout, 4, 5, 54);

    private void BuildAdvanced()
    {
        var layout = _advancedSwapped ? AdvancedLayout2 : AdvancedLayout;
        PlaceLayout(layout, 7, 5, 52);
    }

    /// <summary>Creates equal-width columns and fixed-height rows for the button grid.</summary>
    private void SetupGrid(int cols, int rows, double rowH)
    {
        for (int i = 0; i < cols; i++)
            btnGrid.ColumnDefinitions.Add(new ColumnDefinition
                { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < rows; i++)
            btnGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(rowH) });
    }

    /// <summary>Lays out a (Label, Kind)[][] grid definition into btnGrid, skipping any blank/"skip" cells.</summary>
    private void PlaceLayout((string L, string K)[][] layout, int cols, int rows, double rowH)
    {
        SetupGrid(cols, rows, rowH);
        for (int r = 0; r < layout.Length; r++)
        for (int c = 0; c < layout[r].Length; c++)
        {
            var (label, kind) = layout[r][c];
            if (string.IsNullOrEmpty(label) || kind == "skip") continue;

            var btn = MakeBtn(label, kind);
            btn.SetValue(Grid.RowProperty, r);
            btn.SetValue(Grid.ColumnProperty, c);
            btnGrid.Children.Add(btn);
        }
    }

    /// <summary>Creates one calculator button styled by kind (equals / operator-like / plain) and wires its click handler.</summary>
    private Button MakeBtn(string label, string kind)
    {
        var styleKey = kind switch
        {
            "eq"                                   => "EqBtn",
            "op" or "percent" or "paren" or "clear" or "back" => "OpBtn",
            _                                      => "Btn"
        };
        var btn = new Button
        {
            Content  = label,
            Tag      = kind,
            Style    = (Style)Application.Current.Resources[styleKey],
            Cursor   = Cursors.Hand
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
            case "clear": ClearExpr(); return;
            case "back":  _c.Backspace(); break;
            case "eq":    Calculate(); return;

            case "swap": // toggle between AdvancedLayout and AdvancedLayout2
                _advancedSwapped = !_advancedSwapped;
                BuildButtons();
                return;

            case "rad": // toggle degrees/radians for trig functions
                _c.UseRadians = !_c.UseRadians;
                UpdateRadButton();
                return;

            case "num": _c.AppendToExpr(label); break;
            case "op":  _c.AppendToExpr(" " + label + " "); break;
            case "percent": _c.AppendToExpr("%"); break;

            case "paren": // auto-pick '(' or ')' based on how many are currently open
                int o = _c.Expr.Count(ch => ch == '(');
                int cl = _c.Expr.Count(ch => ch == ')');
                _c.AppendToExpr(o > cl ? ")" : "(");
                break;

            case "fn": // sin/cos/tan: wrap the whole current expression as an argument
                _c.Expr = $"{label}({_c.Expr})";
                break;

            case "inv":  if (!string.IsNullOrEmpty(_c.Expr)) _c.Expr = $"1/({_c.Expr})";  break;
            case "sq":   if (!string.IsNullOrEmpty(_c.Expr)) _c.Expr = $"({_c.Expr})^2";  break;
            case "cube": if (!string.IsNullOrEmpty(_c.Expr)) _c.Expr = $"({_c.Expr})^3";  break;
            case "pow2": if (!string.IsNullOrEmpty(_c.Expr)) _c.Expr = $"2^({_c.Expr})";  break;
            case "pow":  _c.AppendToExpr("^"); break;

            case "fact": // in-place integer factorial, 0–20 only (beyond 20! overflows long)
                if (long.TryParse(_c.Expr, out long n) && n >= 0 && n <= 20)
                { long f = 1; for (long i = 2; i <= n; i++) f *= i; _c.Expr = f.ToString(); }
                break;

            case "pi":    _c.AppendToExpr("π"); break;
            case "euler": _c.AppendToExpr("e"); break;

            default:
                // Simple single-arg wrapping functions (asin, ln, sqrt, ...) — see WrapFunctionKinds.
                if (WrapFunctionKinds.Contains(kind))
                    _c.Expr = $"{kind}({_c.Expr})";
                break;
        }
        RefreshDisplay();
        txtExpr.Focus();
        txtExpr.CaretIndex = txtExpr.Text.Length;
    }
}
