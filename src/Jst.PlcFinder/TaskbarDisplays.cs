using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Jst.PlcFinder;

internal sealed record TaskbarDisplay(string DeviceName, string Label, nint Monitor, nint Taskbar);

internal static class TaskbarDisplays
{
    internal static IReadOnlyList<TaskbarDisplay> Enumerate()
    {
        var displays = new List<TaskbarDisplay>();
        MonitorCallback callback = (nint monitor, nint dc, ref NativeRect rect, nint data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), DeviceName = "" };
            if (GetMonitorInfo(monitor, ref info))
            {
                string number = info.DeviceName.Replace(@"\\.\DISPLAY", "", StringComparison.OrdinalIgnoreCase);
                string label = $"Display {number} · {info.Bounds.Right - info.Bounds.Left} × {info.Bounds.Bottom - info.Bounds.Top}";
                if ((info.Flags & 1) != 0) label += " · Primary";
                var taskbar = FindForMonitor(monitor);
                if (taskbar == 0) label += " · No taskbar";
                displays.Add(new(info.DeviceName, label, monitor, taskbar));
            }
            return true;
        };
        if (!EnumDisplayMonitors(0, 0, callback, 0)) throw new Win32Exception("Could not list connected displays.");
        return displays.OrderBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static nint ResolveTaskbar(nint appWindow, string? deviceName)
    {
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            var selected = Enumerate().FirstOrDefault(d => string.Equals(d.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
            if (selected is not null && selected.Taskbar != 0) return selected.Taskbar;
        }
        // Preserve the saved choice while a display is disconnected or has no taskbar.
        var automatic = FindForMonitor(MonitorFromWindow(appWindow, 2));
        return automatic != 0 ? automatic : FindWindow("Shell_TrayWnd", null);
    }

    private static nint FindForMonitor(nint monitor)
    {
        var primary = FindWindow("Shell_TrayWnd", null);
        if (primary != 0 && MonitorFromWindow(primary, 2) == monitor) return primary;
        nint secondary = 0;
        while ((secondary = FindWindowEx(0, secondary, "Shell_SecondaryTrayWnd", null)) != 0)
            if (MonitorFromWindow(secondary, 2) == monitor) return secondary;
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
    {
        public int Size;
        public NativeRect Bounds, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }
    private delegate bool MonitorCallback(nint monitor, nint dc, ref NativeRect rect, nint data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
}
