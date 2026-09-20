using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Prime;

/// <summary>
/// App entry point and theme engine. Colors are pushed into
/// Application.Resources as SolidColorBrush values at runtime (rather than
/// swapping theme XAML files via pack URIs, which proved unreliable) — every
/// themed control binds to these resource keys with DynamicResource so a
/// theme change repaints the whole UI instantly.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Named "AppTheme" (not "ThemeMode") to avoid colliding with .NET 10 WPF's
    /// new built-in Application.ThemeMode property, which caused a CS0108
    /// "hides inherited member" warning under the previous name.
    /// </summary>
    public enum AppTheme { Light, Dark, System }

    public AppTheme CurrentTheme { get; private set; } = AppTheme.System;

    /// <summary>
    /// Manual startup (wired via Startup="App_Startup" in App.xaml) instead of
    /// StartupUri, so any XAML/init exception is caught and shown in a
    /// MessageBox rather than failing silently.
    /// </summary>
    private void App_Startup(object sender, StartupEventArgs e)
    {
        try
        {
            SetTheme(AppTheme.System);
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            var window = new MainWindow();
            window.Show();
            window.Activate();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Startup error:\n\n{ex.Message}\n\n{ex.StackTrace}",
                            "Calculator Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        base.OnExit(e);
    }

    /// <summary>
    /// Follows the Windows light/dark setting live while "System Default" is selected.
    /// SystemEvents raises this on its own thread, so the repaint is marshalled to the UI thread.
    /// </summary>
    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General || CurrentTheme != AppTheme.System) return;
        Dispatcher.BeginInvoke(() => SetTheme(AppTheme.System));
    }

    /// <summary>Switches theme and repaints all bound brushes. "System" resolves against the current Windows setting.</summary>
    public void SetTheme(AppTheme mode)
    {
        CurrentTheme = mode;
        bool dark = mode == AppTheme.Dark ||
                   (mode == AppTheme.System && IsSystemDark());
        ApplyColors(dark);
    }

    /// <summary>Writes the full light or dark palette into Application.Resources.</summary>
    private void ApplyColors(bool dark)
    {
        if (dark)
        {
            Set("CardBg",    "#242330");
            Set("HistBg",    "#1E1C2A");
            Set("BtnBg",     "#35334A");
            Set("BtnHover",  "#42405A");
            Set("BtnPress",  "#4E4B68");
            Set("OpBg",      "#2D2B3E");
            Set("OpHover",   "#3A3850");
            Set("Fg",        "#EEEDF4");
            Set("SubFg",     "#888AAA");
            Set("AccentBg",  "#3584E4");
            Set("AccentHov", "#2C74D0");
            Set("TitleBg",   "#1E1C2A");
            Set("WarnFg",    "#FF6B6B");
            Set("HistLine",  "#2E2C3E");
        }
        else
        {
            Set("CardBg",    "#FFFFFF");
            Set("HistBg",    "#F5F4F8");
            Set("BtnBg",     "#EFEFEF");
            Set("BtnHover",  "#E2E0EA");
            Set("BtnPress",  "#D5D3DE");
            Set("OpBg",      "#E4E2EC");
            Set("OpHover",   "#D8D5E4");
            Set("Fg",        "#1C1B1F");
            Set("SubFg",     "#888888");
            Set("AccentBg",  "#3584E4");
            Set("AccentHov", "#2C74D0");
            Set("TitleBg",   "#FAFAF8");
            Set("WarnFg",    "#CC3333");
            Set("HistLine",  "#EBEBEB");
        }
    }

    /// <summary>Parses a hex color and stores it as a SolidColorBrush under the given resource key.</summary>
    private void Set(string key, string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        Resources[key] = new SolidColorBrush(color);
    }

    /// <summary>Reads the Windows "app mode" registry setting. Defaults to light (false) if unreadable, e.g. on older Windows builds.</summary>
    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }
}
