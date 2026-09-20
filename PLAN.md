# Prime — Project Plan \& Decision Log

A native Windows calculator app styled after GNOME Calculator. Built with C# + WPF (.NET 8).

\---

## 1\. Project Identity

|||
|-|-|
|**Name**|Prime (originally scaffolded as `GnomeCalc`, then `WinCalc`)|
|**Stack**|C# / WPF / .NET 8|
|**IDE**|Visual Studio Community 2022 (migrated from VS Code)|
|**Repo**|GitHub, branch `main`|
|**Location**|`F:\\Projects\\WinCalc`|

\---

## 2\. Key Decisions \& Rationale

|Decision|Rationale|
|-|-|
|**C# + WPF over Electron/other**|Native Windows performance, instant startup, small binary, no runtime bloat|
|**Custom-drawn UI (no native window chrome)**|Needed to mirror GNOME Calculator's rounded-card aesthetic|
|**Manual `Startup` event instead of `StartupUri`**|`StartupUri` failed silently on XAML errors; manual startup with try/catch surfaces real exceptions in a MessageBox|
|**Resource brushes set in C# code, not theme XAML files**|Switching theme XAML files via pack URIs was unreliable at runtime; direct `Resources\[key] = brush` is simpler and always works|
|**Custom recursive-descent expression parser (`ExprParser`)**|`DataTable.Compute` was the original approach but treats `^` as bitwise XOR, not exponentiation, and has no function support (sin, sqrt, etc.). Fully replaced.|
|**Buttons built in C# code-behind, not static XAML**|Different modes (Basic/Advanced) need different grids; building via `(string Label, string Kind)\[]\[]` layout arrays + a `PlaceLayout` helper avoids duplicating XAML|
|**Removed Financial / Programming / Conversion modes**|Decided to ship Basic + Advanced first; others may return later — explicitly deprioritized, not abandoned|
|**DWM native rounded corners (`DwmSetWindowAttribute`)**|Matches Windows 11 native app corner radius without transparency hacks|
|**`AllowsTransparency` removed**|Was the root cause of an invisible-window bug; fixed height + DWM corners replaced it|
|**Single self-contained published `.exe`**|`dotnet publish -r win-x64 --self-contained true -p:PublishSingleFile=true` — no .NET runtime install needed on target machine|
|**Inno Setup considered, not currently used**|User determined the raw `.exe` is sufficient for personal use; installer deferred until/unless wider distribution is needed|

\---

## 3\. Architecture Overview

```
Prime/
├── Prime.csproj          # .NET 8 WPF project, ApplicationIcon=icon.ico, Resource includes icon.ico/.png
├── App.xaml                # All button/combo/menu styles (Btn, OpBtn, EqBtn, IconBtn, ThemedCombo, ContextMenu, MenuItem)
├── App.xaml.cs              # Theme engine — ThemeMode enum (Light/Dark/System), ApplyColors() sets brushes directly, IsSystemDark() reads registry
├── MainWindow.xaml          # Title bar (hamburger, mode combo, minimize, close) + history/display area + button grid placeholder
├── MainWindow.xaml.cs       # All interaction logic, button-grid layout builder, input filtering, history rendering
├── Calculator.cs            # Expression state + custom ExprParser (recursive descent, proper precedence, right-assoc ^)
├── icon.ico / icon.png      # App icon (custom-generated, blue rounded square w/ button grid motif)
└── README.md
```

### Calculator.cs — Parser Grammar (precedence low → high)

```
AddSub   := MulDiv (('+' | '-') MulDiv)\*
MulDiv   := Power (('\*' | '/' | '%') Power)\*
Power    := Unary ('^' Power)?      # right-associative
Unary    := ('-' | '+')? Primary
Primary  := NUMBER | 'π' | 'e' | '(' AddSub ')' | FUNC '(' AddSub ')'
```

Supported functions: `sin cos tan asin acos atan sinh cosh tanh asinh acosh atanh ln log exp sqrt cbrt abs ceil floor`

Trig functions respect `UseRadians` (bool) — toggled via the Rad/Deg button in Advanced mode.

\---

## 4\. UI/UX Decisions

|Element|Current State|
|-|-|
|**Title bar layout**|Hamburger (≡) far-left · Mode ComboBox absolutely centered (overlaid in same Grid cell, not column-based, to avoid off-center drift) · Minimize (−) and Close (×) far-right|
|**Modes**|`Basic` (4 cols × 5 rows) and `Advanced` (7 cols × 5 rows, with a secondary "swapped" layout for inverse/hyperbolic functions)|
|**Basic layout**|Redesigned to mirror Samsung/stock Android calculator: `C, (), %, ÷ / 7 8 9 × / 4 5 6 − / 1 2 3 + / ± 0 . =`|
|**Advanced layout (primary)**|`⇄ Rad √ C () % ÷ / sin cos tan 7 8 9 × / ln log 1/x 4 5 6 − / eˣ x² xʸ 1 2 3 + /|
|**Advanced layout (swapped via ⇄)**|inverse trig (sin⁻¹/cos⁻¹/tan⁻¹), hyperbolic (sinh/cosh/tanh + inverses), `³√, 2ˣ, x³, x!`|
|**Rad/Deg toggle**|Button label flips between "Rad" and "Deg" reflecting *current* mode; wired to `Calculator.UseRadians`|
|**History panel**|Scrollable; each entry clickable to restore that result into the input|
|**Expression preview**|Live, dimmed, right-aligned text above the input showing `= <result>` as user types; suppressed if result equals raw input or expression is invalid|
|**Input filtering**|`PreviewTextInput` + paste-sanitization restrict input to: digits, `. + - \* / × ÷ − % ^ ( ) π e space`|
|**Theme menu**|Hamburger → custom-styled `ContextMenu`: Light / Dark / System Default / separator / Copy Result / Clear History|
|**Window chrome**|`WindowStyle=None`, fixed `380×620`, native DWM rounded corners (Windows 11 style), custom title bar with `DragMove()`|
|**Icon**|Custom-generated blue rounded-square icon w/ 3×4 button grid motif, embedded as both `.ico` (exe/taskbar) and `.png` (window icon at runtime via `BitmapImage`)|

\---

## 5\. Bugs Fixed Along the Way (for future reference / regression awareness)

1. **`Current` property name collision** — shadowed `Application.Current`; renamed to `CurrentTheme`.
2. **Nested `<Style>` inside `<Setter.Value>` inside another `<Style>`** — silently broke BAML compilation, cascaded into 30+ fake "type does not exist" errors. Removed nested ComboBox style entirely; replaced with flat `ThemedCombo` style.
3. **`AllowsTransparency=True` + `SizeToContent=Auto` + code-behind-built buttons** → invisible 0-height window. Fixed with explicit `Height` and removing transparency.
4. **ComboBox `SelectionChanged` fires during `InitializeComponent()`** before `btnGrid` exists → NullReferenceException. Fixed with `if (btnGrid == null) return;` guard.
5. **`DataTable.Compute` treats `^` as XOR** — replaced entire eval engine with custom parser.
6. **`const double D2R = Math.PI / 180`** — invalid C# (Math.PI isn't a compile-time constant), silently failed to update behavior. Changed to local `double` variable.
7. **Wrong variable name passed to `ExprParser.Evaluate`** (`IsRadMode` instead of `UseRadians`) — Rad/Deg toggle had no effect for several iterations until traced.
8. **Repeated namespace drift** — files generated externally defaulted to `namespace GnomeCalc` after project renames; recurring source of `CS0246`/`CS1061` cascades. **Standing rule:** always `Ctrl+Shift+H` → replace `GnomeCalc` / `WinCalc` → `Prime` after receiving any new file.
9. **`bin/` and `obj/` accidentally tracked in Git** — removed via `git rm -r --cached bin/ obj/` + proper `.gitignore`.

\---

## 6\. Constraints \& Standing Rules

* **IDE:** Visual Studio Community 2022, `.NET desktop development` workload only (WinUI not needed).
* **Every externally-provided file must be checked for `namespace GnomeCalc` and corrected to `namespace Prime` before building.**
* **Editing `MainWindow.xaml` directly:** if VS opens a designer/XML view that blocks text edits, use *Right-click → Open With → XML (Text) Editor*.
* **Publishing is not automatic** — any feature change requires re-running:

```
  dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
  ```

  before the standalone `.exe` reflects new code.

* **Git workflow:** `git pull` before editing if GitHub was edited directly (e.g. README via web UI) → `git add .` → `git commit -m "..."` → `git push`.
* **No Financial/Programming/Conversion modes for now** — explicit scope cut, not forgotten.

\---

## 7\. Feature Backlog (proposed, not yet started)

Ranked by discussed priority:

### High impact

* \[ ] Persistent calculation history (currently lost on close)
* \[ ] Keyboard shortcuts: `Ctrl+C` copy, `Ctrl+V` paste, `Ctrl+Z` undo last entry

### Medium impact

* \[ ] Unit Converter mode (length, weight, temp, speed) — revisit previously-removed Conversion mode
* \[ ] Currency converter (live rates via free API)
* \[ ] Scientific constants quick-access panel (c, h, Nₐ, etc.)

### Polish

* \[ ] Button press / mode-switch / history-row animations
* \[ ] User-selectable accent color
* \[ ] "Always on top" toggle in hamburger menu

### Completed ✅

* \[x] Core Basic + Advanced calculator engine
* \[x] Custom expression parser w/ correct `^` precedence
* \[x] Light/Dark/System theme switching
* \[x] Custom app icon (taskbar + exe)
* \[x] Native rounded window corners
* \[x] Minimize button, repositioned title bar controls
* \[x] Inverse/hyperbolic function layout (swap toggle)
* \[x] Rad/Deg toggle for trig functions
* \[x] Live expression preview (in progress — UI overlap bug being fixed as of last session)

\---

## 8\. Immediate Next Step (as of last session)

**In progress:** Fixing layout overlap between the new expression-preview `TextBlock` and the input `TextBox` in `MainWindow.xaml`. Required changes:

1. Add a third `RowDefinition` to the display area's inner Grid.
2. Insert `txtPreview` TextBlock at `Grid.Row="1"`.
3. Move the expression input Grid to `Grid.Row="2"`.

Once confirmed working, **republish** the `.exe` to reflect the change in the standalone build.

\---

## 9\. Open Questions for Future Sessions

* Should history persist across app restarts (e.g. local JSON file)? Currently in-memory only.
* Should Conversion mode be reintroduced now that Basic/Advanced are stable?
* Is MSIX/Microsoft Store distribution still a goal, or is local-only `.exe` sufficient long-term?

