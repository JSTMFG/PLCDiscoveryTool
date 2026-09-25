using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Jst.PlcFinder;

public partial class MainWindow
{
    private async Task SmokeOverlayTransitionsAsync()
    {
        var activeOverlay = overlay!;
        var handle = new WindowInteropHelper(activeOverlay).Handle;
        foreach (bool topmost in new[] { false, true })
        {
            activeOverlay.Topmost = topmost;
            SendModeSmokeMessage(handle, 0x001C, 0, 0); // WM_ACTIVATEAPP: shell takes focus.
            ShowModeSmokeWindow(handle, 0); // Temporary shell visibility changes must not close the overlay.
            await Task.Delay(100);
            ShowModeSmokeWindow(handle, 4); // SW_SHOWNOACTIVATE
            activeOverlay.WindowState = WindowState.Minimized;
            activeOverlay.WindowState = WindowState.Normal;
            SendModeSmokeMessage(handle, 0x001C, 1, 0);
            await Task.Delay(100);
            if (overlay != activeOverlay || !activeOverlay.IsVisible || IsVisible || taskbarWidget is not null ||
                activeOverlay.Topmost != topmost)
                throw new InvalidOperationException("Overlay activation/visibility transitions changed display mode.");
        }
        activeOverlay.ApplySettings(CurrentOverlaySettings, OverlayFields());
    }

    private async Task SmokeTaskbarTransitionsAsync()
    {
        var viewBefore = taskbarView;
        var hostBefore = taskbarWidget!;
        SendModeSmokeMessage(hostBefore.Handle, 0x001C, 0, 0);
        ShowModeSmokeWindow(hostBefore.Handle, 0);
        await Task.Delay(600);
        if (IsVisible || overlay is not null || taskbarWidget != hostBefore || !hostBefore.IsAttached)
            throw new InvalidOperationException("Taskbar activation/visibility transitions changed display mode.");

        // Exercise actual HWND loss rather than posting a fake destruction notification.
        if (!DestroyModeSmokeWindow(hostBefore.Handle))
            throw new InvalidOperationException("Could not destroy the smoke widget HWND.");
        await Task.Delay(1200);
        if (IsVisible || overlay is not null || taskbarWidget == hostBefore || taskbarWidget?.IsAttached != true ||
            taskbarView != viewBefore || !taskbarScroll.IsEnabled || taskbarReconnect.IsEnabled)
            throw new InvalidOperationException("Taskbar HWND recovery did not preserve mode, live view, and cycling.");

        // Explicitly leaving the mode must cancel any pending reconnect.
        ReconnectTaskbar();
        RestoreFromTaskbar();
        await Task.Delay(600);
        if (!IsVisible || taskbarWidget is not null || taskbarReconnect.IsEnabled)
            throw new InvalidOperationException("Taskbar reconnected after an explicit restore.");
        EnterTaskbarMode();
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendModeSmokeMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    private static extern bool ShowModeSmokeWindow(nint hwnd, int command);
    [DllImport("user32.dll", EntryPoint = "DestroyWindow")]
    private static extern bool DestroyModeSmokeWindow(nint hwnd);
}
