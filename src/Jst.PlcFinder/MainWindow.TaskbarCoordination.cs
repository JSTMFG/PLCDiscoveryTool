using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Jst.PlcFinder;

public partial class MainWindow
{
    private static readonly uint taskbarModeMessage = RegisterWindowMessage("JST.PlcFinder.TaskbarMode.v1");
    private readonly List<Window> peerHiddenWindows = [];
    private readonly DispatcherTimer taskbarPeerWatch = new() { Interval = TimeSpan.FromSeconds(2) };
    private int taskbarOwnerProcess;
    private bool taskbarCoordinationSmoke;

    private void InitializeTaskbarCoordination()
    {
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(TaskbarModeMessage);
        taskbarPeerWatch.Tick += (_, _) =>
        {
            try
            {
                using var owner = Process.GetProcessById(taskbarOwnerProcess);
                if (!owner.HasExited) return;
            }
            catch (ArgumentException) { }
            RestorePeerWindows();
        };
    }

    private void BroadcastTaskbarMode(bool active)
    {
        if (!smokeTest) PostMessage(new nint(0xffff), taskbarModeMessage, Environment.ProcessId, active ? 1 : 0);
    }

    private nint TaskbarModeMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if ((uint)message != taskbarModeMessage || (int)wParam == Environment.ProcessId || closing || smokeTest && !taskbarCoordinationSmoke) return 0;
        handled = true;
        if (lParam != 0)
        {
            StopTaskbarMode();
            taskbarOwnerProcess = (int)wParam;
            foreach (var window in Application.Current.Windows.Cast<Window>().Where(w => w.IsVisible).ToArray())
            {
                if (!peerHiddenWindows.Contains(window)) peerHiddenWindows.Add(window);
                window.Hide();
            }
            taskbarPeerWatch.Start();
        }
        else if (taskbarOwnerProcess == (int)wParam) RestorePeerWindows();
        return 0;
    }

    private void RestorePeerWindows()
    {
        taskbarPeerWatch.Stop();
        taskbarOwnerProcess = 0;
        if (!closing)
        {
            var openWindows = Application.Current.Windows.Cast<Window>().ToHashSet();
            foreach (var window in peerHiddenWindows.Where(openWindows.Contains)) window.Show();
            if (!IsVisible && overlay is null) Show();
        }
        peerHiddenWindows.Clear();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
}
