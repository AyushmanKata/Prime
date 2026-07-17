# WinCalc

A fast, native Windows calculator styled after GNOME Calculator.
Built with C# + WPF on .NET 10. Instant startup, tiny binary, no runtime required.

## Requirements

- Windows 10 / 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (only needed to build from source)

## Build & Run

```
dotnet run
```

Standalone exe (no .NET install needed on target machine):

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Output: `bin/Release/net10.0-windows/win-x64/publish/WinCalc.exe`

## Features

| Feature | Detail |
|---------|--------|
| **Modes** | Basic, Advanced |
| **Theme** | Light / Dark / System Default (reads Windows registry) |
| **History** | Scrollable; click any entry to restore that result |
| **Keyboard** | Full keyboard input · Enter to evaluate · Backspace to delete · Delete to clear · Esc to clear all |
| **Operators** | `+` `−` `×` `÷` `%` `^` `( )` |
| **Functions** | `sin` `cos` `tan` `sin⁻¹` `cos⁻¹` `tan⁻¹` `sinh` `cosh` `tanh` `sinh⁻¹` `cosh⁻¹` `tanh⁻¹` `ln` `log` `eˣ` `2ˣ` `x²` `x³` `xʸ` `√` `³√` `1/x` `\|x\|` `x!` `π` `e` |
| **Rad / Deg** | Toggle in Advanced mode; applies to all trig functions |
| **Input guard** | Max 20 digits per number · Max 15 operators per expression · Dynamic font scaling |
| **Extras** | Copy result · Clear expression (C) · Clear history (≡ menu) · Minimize fade animation |

## Theme Switching

Click **≡** → choose Light / Dark / System Default.  
System Default reads `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme`.

## File Structure

```
WinCalc/
├── WinCalc.csproj       # .NET 10 WPF project
├── App.xaml             # Global styles — shared button template + per-variant styles
├── App.xaml.cs          # Theme engine (AppTheme enum, ApplyColors, registry check)
├── ButtonChrome.cs      # Attached HoverBrush/PressBrush properties for button styles
├── MainWindow.xaml      # UI layout — title bar, history panel, input, button grid
├── MainWindow.xaml.cs   # All interaction logic + dynamic button grid builder
├── Calculator.cs        # Expression state + recursive-descent parser (ExprParser)
├── icon.ico             # App icon (taskbar + exe)
└── icon.png             # Window icon at runtime
```

## Parser Grammar

```
AddSub  := MulDiv (('+' | '-') MulDiv)*
MulDiv  := Power (('*' | '/' | '%') Power)*
Power   := Unary ('^' Power)?        # right-associative
Unary   := ('-' | '+')? Primary
Primary := NUMBER | 'π' | 'e' | '(' AddSub ')' | FUNC '(' AddSub ')'
```
