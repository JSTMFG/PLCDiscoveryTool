using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Jst.PlcFinder.Core;

namespace Jst.PlcFinder;

public partial class MainWindow
{
    private async Task SmokeTaskbarAsync(string output)
    {
        var defaults = System.Text.Json.JsonSerializer.Deserialize<UserSettings>("{}")!;
        if (defaults.Taskbar.ScrollSeconds != 5 || !defaults.Taskbar.DockBesideClock || defaults.Taskbar.DisplayDeviceName != "" ||
            defaults.Taskbar.AutoRefreshEnabled || defaults.Taskbar.AutoRefreshSeconds != 5)
            throw new InvalidOperationException("Taskbar defaults failed.");
        saved.Taskbar = new() { ScrollSeconds = 1, PositionPercent = 100 };
        var serialized = System.Text.Json.JsonSerializer.Serialize(saved);
        if (System.Text.Json.JsonSerializer.Deserialize<UserSettings>(serialized)!.Taskbar.ScrollSeconds != 1)
            throw new InvalidOperationException("Taskbar settings persistence failed.");
        FilterBox.Text = "WELD";
        Overlay_Click(this, new RoutedEventArgs());
        await SmokeOverlayTransitionsAsync();
        EnterTaskbarMode();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (IsVisible || overlay is not null || taskbarWidget?.IsAttached != true || !taskbarScroll.IsEnabled || taskbarCarousel.Count != 4)
            throw new InvalidOperationException("Taskbar embedding, mode exclusivity, or filter independence failed.");
        await SmokeTaskbarTransitionsAsync();
        var first = taskbarCarousel.Current!;
        first.ApplyTracking(Enumerable.Range(0, 5).Select(i => new FieldRead(i == 0 ? "Chris" : "Value", null)).ToArray());
        first.Apply([new("TEST", null), new("Date", null), new("Gem", null), new("ON", null)]);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (taskbarView!.DeveloperText.Text != "Chris" || taskbarView.SimulationText.Text != "SIM ON" ||
            taskbarView.StationNameText.Text != "TEST" || taskbarView.IpText.Text != first.IpDisplay)
            throw new InvalidOperationException("Taskbar IP, station, Developer, or simulation live bindings failed.");
        taskbarView.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)taskbarView.ActualWidth, (int)taskbarView.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(taskbarView);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(output, "taskbar-widget.png"))) encoder.Save(file);
        await Task.Delay(1250);
        File.WriteAllText(Path.Combine(output, "taskbar-display-visibility.txt"), "");
        if (taskbarWidget!.IsVisible)
            TaskbarScreenCapture.Save(taskbarWidget.TaskbarHandle, taskbarWidget.Handle, Path.Combine(output, "taskbar-screen.png"));
        else File.AppendAllText(Path.Combine(output, "taskbar-display-visibility.txt"), "Automatic: screen-pixel check skipped because fullscreen/auto-hide currently conceals the widget.\n");
        if (taskbarCarousel.Current == first) throw new InvalidOperationException("Taskbar scroll timer did not advance.");
        if (taskbarWidget!.IsVisible)
        {
            taskbarView.ContextMenu.IsOpen = true;
            await Task.Delay(400); // Include at least one widget positioning tick while the menu is open.
            if (!taskbarWidget.IsContextMenuVisibleAboveTaskbar)
                throw new InvalidOperationException("Taskbar context menu is behind or overlapping the taskbar.");
            taskbarView.ContextMenu.IsOpen = false;
        }
        var hiddenRow = taskbarCarousel.Current!;
        taskbarView!.HideItem.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
        if (taskbarCarousel.Count != 3 || taskbarCarousel.Current?.Key == hiddenRow.Key ||
            !CurrentTaskbarSettings.HiddenPlcKeys.Contains(hiddenRow.Key) ||
            !System.Text.Json.JsonSerializer.Deserialize<UserSettings>(System.Text.Json.JsonSerializer.Serialize(saved))!
                .Taskbar.HiddenPlcKeys.Contains(hiddenRow.Key))
            throw new InvalidOperationException("Taskbar context-menu exclusion or persistence failed.");
        var filterTabs = new System.Windows.Controls.TabControl();
        var readFilterSettings = AddTaskbarSettingsTab(filterTabs, false);
        var filterPanel = (System.Windows.Controls.StackPanel)((System.Windows.Controls.ScrollViewer)((System.Windows.Controls.TabItem)filterTabs.Items[0]).Content).Content;
        var hiddenCheck = filterPanel.Children.OfType<System.Windows.Controls.CheckBox>()
            .Single(check => (check.Content as string)?.StartsWith(hiddenRow.IpDisplay + " · ", StringComparison.Ordinal) == true);
        if (hiddenCheck.IsChecked != false) throw new InvalidOperationException("Excluded PLC was not unchecked in taskbar settings.");
        hiddenCheck.IsChecked = true;
        saved.Taskbar = readFilterSettings();
        UpdateTaskbarStation();
        if (taskbarCarousel.Count != 4 || CurrentTaskbarSettings.HiddenPlcKeys.Count != 0)
            throw new InvalidOperationException("Taskbar settings did not restore the excluded PLC.");
        saved.Taskbar.ScrollSeconds = 30; ApplyTaskbarSettings();
        if (taskbarScroll.Interval != TimeSpan.FromSeconds(30)) throw new InvalidOperationException("Taskbar interval did not apply.");
        var handle = taskbarWidget!.Handle;
        var beforeWheel = taskbarCarousel.Current;
        int beforeWheelIndex = taskbarCarousel.Index;
        var wheelPoint = taskbarView!.PointToScreen(new Point(20, 15));
        nint wheelPosition = ((int)wheelPoint.Y << 16) | ((int)wheelPoint.X & 0xffff);
        SendModeSmokeMessage(handle, 0x020A, 60 << 16, wheelPosition);
        if (taskbarCarousel.Current != beforeWheel)
            throw new InvalidOperationException("Partial wheel input advanced too early.");
        SendModeSmokeMessage(handle, 0x020A, 60 << 16, wheelPosition);
        if (taskbarCarousel.Index != (beforeWheelIndex + taskbarCarousel.Count - 1) % taskbarCarousel.Count)
            throw new InvalidOperationException("Wheel up did not select the previous PLC.");
        SendModeSmokeMessage(handle, 0x020A, -120 << 16, wheelPosition);
        if (taskbarCarousel.Current != beforeWheel || IsVisible || !taskbarScroll.IsEnabled)
            throw new InvalidOperationException("Wheel down did not restore the next PLC in taskbar mode.");
        PostMessage(handle, 0x0203, 1, (12 << 16) | 12); // Real HWND double-click input.
        PostMessage(handle, 0x0202, 0, (12 << 16) | 12);
        await Task.Delay(100);
        if (!IsVisible || taskbarWidget is not null || taskbarScroll.IsEnabled || taskbarRefresh.IsEnabled)
            throw new InvalidOperationException("Taskbar double-click did not restore the app and stop cycling.");
        var rows = Devices.ToArray();
        Devices.Clear();
        EnterTaskbarMode();
        if (taskbarCarousel.Count != 0 || taskbarView!.EmptyText.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Empty taskbar widget failed.");
        Devices.Add(rows[0]);
        if (taskbarCarousel.Count != 1 || taskbarView.EmptyText.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Taskbar did not pick up a newly detected PLC.");
        RestoreFromTaskbar();
        saved.Taskbar = new() { AutoRefreshEnabled = true, AutoRefreshSeconds = 2 };
        EnterTaskbarMode();
        if (!taskbarRefresh.IsEnabled || taskbarRefresh.Interval != TimeSpan.FromSeconds(2))
            throw new InvalidOperationException("Taskbar-only auto-refresh did not start at its configured rate.");
        Devices.Clear();
        await Task.Delay(2200);
        if (busy || activeTask is not null)
            throw new InvalidOperationException("Taskbar auto-refresh attempted a read without detected PLCs.");
        RestoreFromTaskbar();
        if (taskbarRefresh.IsEnabled) throw new InvalidOperationException("Taskbar auto-refresh continued after leaving taskbar mode.");
        Devices.Clear(); foreach (var row in rows) Devices.Add(row);
        saved.Taskbar = new(); FilterBox.Text = "";
        var connectedDisplays = TaskbarDisplays.Enumerate();
        File.WriteAllLines(Path.Combine(output, "taskbar-displays.txt"), connectedDisplays.Select(d => $"{d.DeviceName}: {d.Label}"));
        var displayTabs = new System.Windows.Controls.TabControl();
        var readDisplaySettings = AddTaskbarSettingsTab(displayTabs, false);
        var settingsPanel = (System.Windows.Controls.StackPanel)((System.Windows.Controls.ScrollViewer)((System.Windows.Controls.TabItem)displayTabs.Items[0]).Content).Content;
        var colorRadios = settingsPanel.Children.OfType<System.Windows.Controls.WrapPanel>()
            .SelectMany(p => p.Children.OfType<System.Windows.Controls.RadioButton>()).ToArray();
        if (colorRadios.Length != 6) throw new InvalidOperationException("Taskbar color palette options are missing.");
        colorRadios[^1].IsChecked = true;
        if (readDisplaySettings().ColorScheme != "Windows") throw new InvalidOperationException("Match Windows palette selection failed.");
        colorRadios[0].IsChecked = true;
        var displayPicker = settingsPanel.Children.OfType<System.Windows.Controls.ComboBox>().Single();
        var refreshToggle = settingsPanel.Children.OfType<System.Windows.Controls.CheckBox>()
            .Single(c => Equals(c.Content, "Refresh PLC readings while in taskbar mode"));
        var taskbarBoxes = settingsPanel.Children.OfType<System.Windows.Controls.TextBox>().ToArray();
        refreshToggle.IsChecked = true;
        taskbarBoxes[1].Text = "7";
        var configuredRefresh = readDisplaySettings();
        if (!configuredRefresh.AutoRefreshEnabled || configuredRefresh.AutoRefreshSeconds != 7 ||
            System.Text.Json.JsonSerializer.Deserialize<UserSettings>(System.Text.Json.JsonSerializer.Serialize(new UserSettings { Taskbar = configuredRefresh }))!.Taskbar.AutoRefreshSeconds != 7)
            throw new InvalidOperationException("Taskbar auto-refresh settings were not saved separately.");
        taskbarBoxes[1].Text = "1";
        try { readDisplaySettings(); throw new InvalidOperationException("Invalid taskbar refresh interval was accepted."); }
        catch (FormatException) { }
        refreshToggle.IsChecked = false;
        taskbarBoxes[1].Text = "5";
        var mainHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        foreach (var display in connectedDisplays.Where(d => d.Taskbar != 0))
        {
            displayPicker.SelectedItem = ((IEnumerable<TaskbarDisplay>)displayPicker.ItemsSource).Single(d => d.DeviceName == display.DeviceName);
            saved.Taskbar = readDisplaySettings();
            if (saved.Taskbar.DisplayDeviceName != display.DeviceName ||
                System.Text.Json.JsonSerializer.Deserialize<UserSettings>(System.Text.Json.JsonSerializer.Serialize(saved))!.Taskbar.DisplayDeviceName != display.DeviceName)
                throw new InvalidOperationException("Taskbar display selection was not retained by the settings UI or serialization.");
            EnterTaskbarMode();
            await Task.Delay(500);
            if (taskbarWidget?.TaskbarHandle != display.Taskbar)
                throw new InvalidOperationException("Widget attached to the wrong display's taskbar.");
            File.AppendAllText(Path.Combine(output, "taskbar-display-visibility.txt"), $"{display.Label}: {taskbarWidget.PlacementDiagnostics}\n");
            if (taskbarWidget.IsVisible)
            {
                TaskbarScreenCapture.Save(display.Taskbar, taskbarWidget.Handle, Path.Combine(output, $"taskbar-display-{Array.IndexOf(connectedDisplays.ToArray(), display)}.png"));
                File.AppendAllText(Path.Combine(output, "taskbar-display-visibility.txt"), $"{display.Label}: taskbar attachment and screen-pixel checks passed.\n");
            }
            else File.AppendAllText(Path.Combine(output, "taskbar-display-visibility.txt"), $"{display.Label}: taskbar attachment passed; screen-pixel check skipped because fullscreen/auto-hide currently conceals the widget.\n");
            RestoreFromTaskbar();
        }
        saved.Taskbar = new() { ColorScheme = "Emerald" };
        EnterTaskbarMode();
        await Task.Delay(500);
        if (taskbarWidget is null || taskbarView is null ||
            ((SolidColorBrush)taskbarView.Resources["ModeBackground"]).Color != ModeColors.Resolve("Emerald", taskbar: true).Background)
            throw new InvalidOperationException("Taskbar palette did not apply.");
        if (taskbarWidget.IsVisible)
            TaskbarScreenCapture.Save(taskbarWidget.TaskbarHandle, taskbarWidget.Handle,
                Path.Combine(output, "taskbar-emerald.png"), ModeColors.Resolve("Emerald", taskbar: true).Background);
        RestoreFromTaskbar();
        saved.Taskbar = new() { ColorScheme = "Windows" };
        EnterTaskbarMode();
        if (taskbarView is null || ((SolidColorBrush)taskbarView.Resources["ModeBackground"]).Color !=
            ModeColors.Resolve("Windows", taskbar: true).Background)
            throw new InvalidOperationException("Taskbar did not match the current Windows color scheme.");
        await Task.Delay(500);
        if (taskbarWidget?.IsVisible == true)
            TaskbarScreenCapture.Save(taskbarWidget.TaskbarHandle, taskbarWidget.Handle,
                Path.Combine(output, "taskbar-match-windows.png"), ModeColors.Resolve("Windows", taskbar: true).Background);
        RestoreFromTaskbar();
        string unavailableDisplay = "Disconnected-smoke-display";
        saved.Taskbar.DisplayDeviceName = unavailableDisplay;
        if (TaskbarDisplays.ResolveTaskbar(mainHwnd, unavailableDisplay) != TaskbarDisplays.ResolveTaskbar(mainHwnd, ""))
            throw new InvalidOperationException("Disconnected display did not fall back to Automatic.");
        var disconnectedTabs = new System.Windows.Controls.TabControl();
        var readDisconnectedSettings = AddTaskbarSettingsTab(disconnectedTabs, false);
        if (readDisconnectedSettings().DisplayDeviceName != unavailableDisplay)
            throw new InvalidOperationException("Opening settings discarded the disconnected display preference.");
        saved.Taskbar = new();
        // Exercise the same registered native message used by other copies, without broadcasting
        // smoke-test messages to any real application instances on the user's desktop.
        taskbarCoordinationSmoke = true;
        var mainHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        PostMessage(mainHandle, taskbarModeMessage, int.MaxValue, 1);
        await Task.Delay(100);
        if (IsVisible || !taskbarPeerWatch.IsEnabled) throw new InvalidOperationException("Peer taskbar mode did not hide the app.");
        PostMessage(mainHandle, taskbarModeMessage, int.MaxValue, 0);
        await Task.Delay(100);
        if (!IsVisible || taskbarPeerWatch.IsEnabled) throw new InvalidOperationException("Peer restore did not bring the app back.");
        PostMessage(mainHandle, taskbarModeMessage, int.MaxValue, 1);
        await Task.Delay(2300);
        if (!IsVisible || taskbarPeerWatch.IsEnabled) throw new InvalidOperationException("App did not recover after its taskbar owner disappeared.");
        taskbarCoordinationSmoke = false;
    }
}


