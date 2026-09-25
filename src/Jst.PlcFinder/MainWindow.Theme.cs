using Microsoft.Win32;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media;

namespace Jst.PlcFinder;

public partial class MainWindow
{
    private void InitializeModeThemeTracking()
    {
        SystemEvents.UserPreferenceChanged += WindowsThemeChanged;
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowsThemeMessage);
        Closed += (_, _) => SystemEvents.UserPreferenceChanged -= WindowsThemeChanged;
    }

    private void WindowsThemeChanged(object? sender, UserPreferenceChangedEventArgs args)
    {
        if (!Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ApplyActiveThemes));
    }

    private nint WindowsThemeMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // Windows broadcasts these when light/dark or the accent color changes.
        if (message is 0x001A or 0x031A or 0x0320 && !Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ApplyActiveThemes));
        return 0;
    }

    private void ApplyActiveThemes()
    {
        if (closing) return;
        overlay?.ApplyTheme(CurrentOverlaySettings.ColorScheme);
        taskbarView?.ApplyTheme(CurrentTaskbarSettings.ColorScheme);
    }

    private void ApplyAppearance()
    {
        bool dark = string.Equals(saved.Appearance, "Dark", StringComparison.OrdinalIgnoreCase);
        var colors = dark ? new Dictionary<string, string>
        {
            ["Brand"] = "#244E9A", ["AppBackground"] = "#151A23", ["AppSurface"] = "#202734",
            ["AppText"] = "#F0F3F9", ["AppMuted"] = "#ADB9CA", ["AppBorder"] = "#485568",
            ["AppInput"] = "#293344", ["AppHeader"] = "#303B4D", ["AppAlternate"] = "#252E3C",
            ["AppGridLine"] = "#3A4658", ["AppStatus"] = "#263142", ["AppSummary"] = "#2A3954",
            ["AppHint"] = "#9EABBD", ["AppSuccess"] = "#8CE0AF", ["AppAlert"] = "#FFD08A",
            ["AppBadge"] = "#344B73", ["AppAccentText"] = "#A9C8FF",
            ["AppNeutralBadge"] = "#394354", ["AppWarmBadge"] = "#6B4A25"
        } : new Dictionary<string, string>
        {
            ["Brand"] = "#0C2D83", ["AppBackground"] = "#F3F5F9", ["AppSurface"] = "#FFFFFF",
            ["AppText"] = "#17233A", ["AppMuted"] = "#657189", ["AppBorder"] = "#D7DDE8",
            ["AppInput"] = "#FFFFFF", ["AppHeader"] = "#EDF1F8", ["AppAlternate"] = "#FAFBFD",
            ["AppGridLine"] = "#EDF0F5", ["AppStatus"] = "#E8EDF5", ["AppSummary"] = "#EDF2FF",
            ["AppHint"] = "#7C879A", ["AppSuccess"] = "#167443", ["AppAlert"] = "#A55A00",
            ["AppBadge"] = "#DCE7FF", ["AppAccentText"] = "#0C2D83",
            ["AppNeutralBadge"] = "#EDF0F5", ["AppWarmBadge"] = "#FFE0A6"
        };
        foreach (var (key, value) in colors)
            Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
    }
}
