using Microsoft.Win32;
using System.Windows.Interop;
using System.Windows.Threading;

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
}
