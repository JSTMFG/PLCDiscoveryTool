using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Jst.PlcFinder;

// Windows 11's composed taskbar can paint over foreign child HWNDs. Use a
// non-activating popup owned by the taskbar, positioned within its screen bounds.
internal sealed class TaskbarWidgetHost : IDisposable
{
    private readonly HwndSource source;
    private readonly TaskbarWidgetView content;
    private readonly DispatcherTimer layoutTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Action restore;
    private readonly Action reconnect;
    private readonly nint taskbar;
    private readonly System.Windows.Controls.ContextMenu? contextMenu;
    private bool menuOpen;
    private bool disposed;
    private int positionPercent;
    private bool besideClock;
    public nint Handle => source.Handle;
    internal nint TaskbarHandle => taskbar;
    internal bool IsVisible => !disposed && IsWindowVisible(source.Handle);
    internal bool IsContextMenuVisibleAboveTaskbar
    {
        get
        {
            if (contextMenu?.IsOpen != true || PresentationSource.FromVisual(contextMenu) is not HwndSource popup ||
                !GetWindowRect(popup.Handle, out var menu) || !GetWindowRect(taskbar, out var bar)) return false;
            var point = new NativePoint { X = menu.Left + menu.Width / 2, Y = menu.Top + menu.Height / 2 };
            return menu.Bottom <= bar.Top && WindowFromPoint(point) == popup.Handle;
        }
    }
    internal string PlacementDiagnostics
    {
        get
        {
            GetWindowRect(taskbar, out var bar); GetWindowRect(source.Handle, out var widget);
            DwmGetWindowAttribute(taskbar, 14, out int barCloaked, sizeof(int));
            DwmGetWindowAttribute(source.Handle, 14, out int widgetCloaked, sizeof(int));
            return $"Taskbar={bar.Left},{bar.Top},{bar.Right},{bar.Bottom}; Widget={widget.Left},{widget.Top},{widget.Right},{widget.Bottom}; DPI={GetDpiForWindow(taskbar)}; Cloaked={barCloaked}/{widgetCloaked}";
        }
    }
    public bool IsAttached => !disposed && !source.IsDisposed && IsWindow(taskbar) && GetWindow(source.Handle, 4) == taskbar; // GW_OWNER

    public TaskbarWidgetHost(TaskbarWidgetView content, TaskbarSettings settings, nint appWindow, Action restore, Action reconnect)
    {
        this.content = content;
        this.restore = restore;
        this.reconnect = reconnect;
        positionPercent = Math.Clamp(settings.PositionPercent, 0, 100);
        besideClock = settings.DockBesideClock;
        taskbar = TaskbarDisplays.ResolveTaskbar(appWindow, settings.DisplayDeviceName);
        if (taskbar == 0 || !GetWindowRect(taskbar, out var bounds) || bounds.Width <= bounds.Height)
            throw new InvalidOperationException("Taskbar mode requires an available horizontal Windows taskbar.");
        var parameters = new HwndSourceParameters("JST PLC Finder taskbar widget")
        {
            ParentWindow = taskbar, // An owner, rather than a parent, for WS_POPUP.
            WindowStyle = unchecked((int)0x80000000), // POPUP: avoid Explorer's child-window composition.
            ExtendedWindowStyle = 0x08000000 | 0x00000080, // NOACTIVATE | TOOLWINDOW (no extra taskbar button)
            WindowClassStyle = 0x0008, // CS_DBLCLKS
            UsesPerPixelOpacity = true,
            Width = 300, Height = 38
        };
        source = new HwndSource(parameters);
        // Install ahead of WPF's input hook so wheel input works without keyboard focus.
        source.AddHook(Hook);
        try
        {
            source.RootVisual = content;
            contextMenu = content.ContextMenu;
            if (contextMenu is not null)
            {
                contextMenu.Opened += ContextMenu_Opened;
                contextMenu.Closed += ContextMenu_Closed;
            }
            if (!IsAttached) throw new Win32Exception("Windows could not attach the widget to the taskbar.");
            Position();
            layoutTimer.Tick += (_, _) =>
            {
                if (!IsAttached) { layoutTimer.Stop(); reconnect(); }
                else
                {
                    try { Position(); }
                    catch (Win32Exception) { layoutTimer.Stop(); reconnect(); }
                }
            };
            layoutTimer.Start();
        }
        catch { Dispose(); throw; }
    }

    public void ApplySettings(TaskbarSettings settings)
    {
        positionPercent = Math.Clamp(settings.PositionPercent, 0, 100);
        besideClock = settings.DockBesideClock;
        Position();
    }

    private void Position()
    {
        if (disposed || !GetWindowRect(taskbar, out var bounds)) return;
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(taskbar, 2), ref monitor)) return;
        // Auto-hide moves most of the taskbar outside its monitor. Keep the widget hidden too.
        int visibleHeight = Math.Min(bounds.Bottom, monitor.Monitor.Bottom) - Math.Max(bounds.Top, monitor.Monitor.Top);
        if (!IsWindowVisible(taskbar) || visibleHeight < bounds.Height / 2 || FullscreenAppCovers(monitor.Monitor))
        {
            ShowWindow(source.Handle, 0);
            return;
        }
        double scale = Math.Max(96, GetDpiForWindow(taskbar)) / 96.0;
        int gap = (int)(6 * scale);
        var tray = FindWindowEx(taskbar, 0, "TrayNotifyWnd", null);
        if (tray == 0) tray = FindWindowEx(taskbar, 0, "ClockButton", null);
        int right = bounds.Right - (int)(200 * scale); // Reserve clock/status area when no native tray boundary exists.
        if (tray != 0 && GetWindowRect(tray, out var trayBounds) && trayBounds.Left > bounds.Left)
            right = trayBounds.Left - gap;
        int left = bounds.Left + (int)(56 * scale);
        if (right - left < (int)(180 * scale)) { ShowWindow(source.Handle, 0); return; }
        int width = Math.Min((int)(300 * scale), right - left);
        int height = Math.Min((int)(38 * scale), bounds.Height - 4);
        int x = besideClock ? right - width : left + (int)((right - left - width) * positionPercent / 100.0);
        int y = bounds.Top + (bounds.Height - height) / 2;
        // Reasserting the widget's topmost order every tick can cover its own popup menu.
        uint flags = 0x0010 | 0x0040 | (menuOpen ? 0x0004u : 0u); // NOACTIVATE | SHOWWINDOW | optional NOZORDER
        if (!SetWindowPos(source.Handle, menuOpen ? 0 : new nint(-1), x, y, width, height, flags))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not position the taskbar widget.");
    }

    private void ContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        menuOpen = true;
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
        {
            if (disposed || contextMenu?.IsOpen != true) return;
            if (PresentationSource.FromVisual(contextMenu) is HwndSource popup)
                SetWindowPos(popup.Handle, new nint(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // NOMOVE | NOSIZE | NOACTIVATE
        }), DispatcherPriority.Loaded);
    }

    private void ContextMenu_Closed(object sender, RoutedEventArgs e) => menuOpen = false;

    private bool FullscreenAppCovers(NativeRect monitor)
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0 || foreground == taskbar || foreground == source.Handle || !GetWindowRect(foreground, out var rect)) return false;
        var className = new StringBuilder(64);
        GetClassName(foreground, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        // Only a full-monitor window, not an ordinary maximized window, should cover the widget.
        return rect.Left <= monitor.Left && rect.Top <= monitor.Top && rect.Right >= monitor.Right && rect.Bottom >= monitor.Bottom;
    }

    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x020A && !disposed && contextMenu?.IsOpen != true) // WM_MOUSEWHEEL on the non-activating widget.
        {
            handled = true;
            content.HandleMouseWheel(unchecked((short)((long)wParam >> 16)));
            return 0;
        }
        if (message == 0x0021) { handled = true; return 3; } // MA_NOACTIVATE
        if (message == 0x0203)
        {
            handled = true;
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => { if (!disposed) restore(); }));
        }
        if (message == 0x0082 && !disposed)
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => { if (!disposed) reconnect(); }));
        return 0;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        layoutTimer.Stop();
        if (contextMenu is not null)
        {
            contextMenu.Opened -= ContextMenu_Opened;
            contextMenu.Closed -= ContextMenu_Closed;
            contextMenu.IsOpen = false;
        }
        if (!source.IsDisposed) source.RootVisual = null;
        source.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder text, int capacity);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
}
