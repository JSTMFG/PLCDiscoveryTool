using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Jst.PlcFinder.Core;

namespace Jst.PlcFinder;

public partial class MainWindow
{
    private TaskbarWidgetHost? taskbarWidget;
    private TaskbarWidgetView? taskbarView;
    private readonly PlcCarousel taskbarCarousel = new();
    private readonly DispatcherTimer taskbarScroll = new();
    private readonly DispatcherTimer taskbarRefresh = new();
    private readonly DispatcherTimer taskbarReconnect = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool switchingDisplayMode;
    private TaskbarSettings CurrentTaskbarSettings => saved.Taskbar ??= new();

    private void Taskbar_Click(object sender, RoutedEventArgs e) => EnterTaskbarMode();

    private void EnterTaskbarMode()
    {
        if (closing || taskbarWidget is not null || trackingSavePending) return;
        if (!DevicesGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !DevicesGrid.CommitEdit(DataGridEditingUnit.Row, true) || trackingSavePending) return;
        var content = new TaskbarWidgetView();
        content.ApplyTheme(CurrentTaskbarSettings.ColorScheme);
        content.SettingsItem.SetBinding(IsEnabledProperty, new Binding(nameof(IsEnabled)) { Source = SettingsButton });
        content.RestoreRequested += RestoreFromTaskbar;
        content.SettingsRequested += () => { RestoreFromTaskbar(); if (!busy && !trackingSavePending) ShowTrackingSettingsWindow(this, selectTaskbar: true); };
        content.NextRequested += () => UpdateTaskbarStation(true);
        content.ScrollRequested += steps =>
        {
            UpdateTaskbarStation(offset: steps);
            // Give the manually selected PLC a full display interval.
            taskbarScroll.Stop();
            taskbarScroll.Start();
        };
        content.HideRequested += HideCurrentTaskbarPlc;
        content.ExitRequested += Close;
        try
        {
            // Only hide the application once the embedded HWND has been created successfully.
            taskbarWidget = CreateTaskbarHost(content);
            taskbarView = content;
            switchingDisplayMode = true;
            try { overlay?.Close(); }
            finally { switchingDisplayMode = false; }
            Hide();
            foreach (var row in Devices) offlineTransitions.Observe(row.Key, row.Status);
            Devices.CollectionChanged += TaskbarDevicesChanged;
            taskbarScroll.Tick += TaskbarScrollTick;
            taskbarRefresh.Tick += TaskbarRefreshTick;
            ApplyTaskbarSettings();
            UpdateTaskbarStation();
            taskbarScroll.Start();
            BroadcastTaskbarMode(true);
        }
        catch (Exception ex)
        {
            RestoreFromTaskbar();
            if (smokeTest) throw;
            ShowInfo("Taskbar mode could not start: " + ex.Message);
        }
    }

    private TaskbarWidgetHost CreateTaskbarHost(TaskbarWidgetView content) => new(content, CurrentTaskbarSettings,
        new System.Windows.Interop.WindowInteropHelper(this).Handle, RestoreFromTaskbar, ReconnectTaskbar);

    private void ReconnectTaskbar()
    {
        if (closing || taskbarWidget is null || taskbarReconnect.IsEnabled) return;
        // Task View and shell transitions can invalidate the popup or its owner.
        // Keep the selected mode and its live view; only explicit restore actions leave it.
        taskbarWidget.Dispose();
        taskbarReconnect.Tick += ReconnectTaskbarTick;
        taskbarReconnect.Start();
    }

    private void ReconnectTaskbarTick(object? sender, EventArgs e)
    {
        if (closing || taskbarView is null) return;
        try
        {
            taskbarWidget = CreateTaskbarHost(taskbarView);
            taskbarReconnect.Stop();
            taskbarReconnect.Tick -= ReconnectTaskbarTick;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Explorer may still be rebuilding its taskbar. Retry without showing the main window.
            System.Diagnostics.Trace.WriteLine("Taskbar reconnect pending: " + ex.Message);
        }
    }

    private void TaskbarScrollTick(object? sender, EventArgs e) => UpdateTaskbarStation(true);
    private async void TaskbarRefreshTick(object? sender, EventArgs e)
    {
        if (taskbarWidget is not null && !closing && IsEnabled && nativeReady && !busy &&
            !trackingEditActive && !trackingSavePending && Devices.Count > 0)
            await StartOperationAsync(t => RefreshAsync(true, t, automatic: true));
    }
    private void TaskbarDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateTaskbarStation();
    private void HideCurrentTaskbarPlc()
    {
        var row = taskbarCarousel.Current;
        if (row is null) return;
        var previous = CurrentTaskbarSettings.HiddenPlcKeys;
        CurrentTaskbarSettings.HiddenPlcKeys = [.. previous ?? [], row.Key];
        CurrentTaskbarSettings.HiddenPlcKeys = CurrentTaskbarSettings.HiddenPlcKeys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!smokeTest && !SettingsStore.Save(saved))
        {
            CurrentTaskbarSettings.HiddenPlcKeys = previous ?? [];
            ShowInfo("The taskbar filter could not be saved. Please try again.");
            return;
        }
        UpdateTaskbarStation();
    }
    private void UpdateTaskbarStation(bool advance = false, int offset = 0)
    {
        taskbarCarousel.Update(Devices, advance, CurrentTaskbarSettings.HiddenPlcKeys, offset);
        taskbarView?.ShowStation(taskbarCarousel.Current, taskbarCarousel.Index, taskbarCarousel.Count);
    }

    private void ApplyTaskbarSettings()
    {
        taskbarScroll.Interval = TimeSpan.FromSeconds(Math.Clamp(CurrentTaskbarSettings.ScrollSeconds, 1, 3600));
        taskbarRefresh.Interval = TimeSpan.FromSeconds(Math.Clamp(CurrentTaskbarSettings.AutoRefreshSeconds, 2, 3600));
        if (taskbarWidget is not null && CurrentTaskbarSettings.AutoRefreshEnabled) taskbarRefresh.Start();
        else taskbarRefresh.Stop();
        taskbarWidget?.ApplySettings(CurrentTaskbarSettings);
        taskbarView?.ApplyTheme(CurrentTaskbarSettings.ColorScheme);
    }

    private void StopTaskbarMode()
    {
        offlineTransitions.Clear();
        taskbarReconnect.Stop();
        taskbarReconnect.Tick -= ReconnectTaskbarTick;
        taskbarScroll.Stop();
        taskbarRefresh.Stop();
        taskbarScroll.Tick -= TaskbarScrollTick;
        taskbarRefresh.Tick -= TaskbarRefreshTick;
        Devices.CollectionChanged -= TaskbarDevicesChanged;
        var host = taskbarWidget;
        taskbarWidget = null;
        taskbarView = null;
        host?.Dispose();
    }

    private void RestoreFromTaskbar()
    {
        bool wasActive = taskbarWidget is not null;
        StopTaskbarMode();
        if (wasActive) BroadcastTaskbarMode(false);
        if (closing) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private Func<TaskbarSettings> AddTaskbarSettingsTab(TabControl tabs, bool selectTaskbar)
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Taskbar widget", FontSize = 20, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "Turn on Taskbar mode from the main header. The widget sits beside the clock/status area in the Windows taskbar on your chosen display and shows one detected PLC at a time: IP address, Developer, and simulation state. Double-click it to restore PLC Finder. Right-click for more options.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "Display", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6) });
        var displays = new List<TaskbarDisplay> { new("", "Automatic · Same display as the main window", 0, 0) };
        displays.AddRange(TaskbarDisplays.Enumerate());
        string selectedDevice = CurrentTaskbarSettings.DisplayDeviceName ?? "";
        if (!displays.Any(d => string.Equals(d.DeviceName, selectedDevice, StringComparison.OrdinalIgnoreCase)))
            displays.Add(new(selectedDevice, $"{selectedDevice} · Disconnected", 0, 0));
        var display = new ComboBox { ItemsSource = displays, DisplayMemberPath = nameof(TaskbarDisplay.Label),
            SelectedItem = displays.First(d => string.Equals(d.DeviceName, selectedDevice, StringComparison.OrdinalIgnoreCase)) };
        System.Windows.Automation.AutomationProperties.SetName(display, "Taskbar display");
        panel.Children.Add(display);
        panel.Children.Add(new TextBlock { Text = "If the selected display is disconnected or has no Windows taskbar, Automatic is used temporarily. Your saved display choice is retained.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) });
        panel.Children.Add(new TextBlock { Text = "Seconds per PLC (1–3600)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 6) });
        var seconds = new TextBox { Text = Math.Clamp(CurrentTaskbarSettings.ScrollSeconds, 1, 3600).ToString() };
        panel.Children.Add(seconds);
        panel.Children.Add(new TextBlock { Text = "Cycles through included PLCs in IP order, independently of the main station filter. This interval changes which PLC is displayed; it does not read PLC values.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "PLCs shown in taskbar scrolling", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 6) });
        panel.Children.Add(new TextBlock { Text = "Uncheck a PLC to hide it. A PLC hidden from the taskbar is still scanned and refreshed. You can also hide the current PLC from the taskbar right-click menu.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        var hiddenKeys = CurrentTaskbarSettings.HiddenPlcKeys ?? [];
        var plcChecks = new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);
        var detectedPlcs = Devices.Where(r => r.Identity.IsController || r.IsEcho || r.Identity.IsBridge && r.Route.Length > 0)
            .OrderBy(r => r.IpSort).ThenBy(r => r.Route, StringComparer.Ordinal).ToArray();
        foreach (var row in detectedPlcs)
        {
            var check = new CheckBox { Content = $"{row.IpDisplay} · {row.Station}", IsChecked = !hiddenKeys.Contains(row.Key, StringComparer.OrdinalIgnoreCase), Margin = new Thickness(0, 3, 0, 3) };
            plcChecks[row.Key] = check;
            panel.Children.Add(check);
        }
        foreach (var key in hiddenKeys.Except(plcChecks.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var check = new CheckBox { Content = $"{key.Replace('|', ' ')} · currently undiscovered", IsChecked = false, Margin = new Thickness(0, 3, 0, 3) };
            plcChecks[key] = check;
            panel.Children.Add(check);
        }
        if (plcChecks.Count == 0) panel.Children.Add(new TextBlock { Text = "No PLCs detected yet. Scan to populate this list.", Margin = new Thickness(0, 3, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "Taskbar auto-refresh", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 6) });
        var autoRefresh = new CheckBox { Content = "Refresh PLC readings while in taskbar mode", IsChecked = CurrentTaskbarSettings.AutoRefreshEnabled };
        panel.Children.Add(autoRefresh);
        panel.Children.Add(new TextBlock { Text = "Refresh every (seconds, 2–3600)", Margin = new Thickness(0, 8, 0, 5) });
        var refreshSeconds = new TextBox { Text = Math.Clamp(CurrentTaskbarSettings.AutoRefreshSeconds, 2, 3600).ToString(),
            IsEnabled = autoRefresh.IsChecked == true };
        autoRefresh.Checked += (_, _) => refreshSeconds.IsEnabled = true;
        autoRefresh.Unchecked += (_, _) => refreshSeconds.IsEnabled = false;
        panel.Children.Add(refreshSeconds);
        panel.Children.Add(new TextBlock { Text = "This setting runs only in taskbar mode. The main window's Auto-refresh setting is separate.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 6, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "Taskbar colors", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 6) });
        var (palettePicker, readPalette) = ModeColors.Picker(CurrentTaskbarSettings.ColorScheme, taskbar: true);
        panel.Children.Add(palettePicker);
        var dock = new CheckBox { Content = "Keep beside the Windows clock / status area", IsChecked = CurrentTaskbarSettings.DockBesideClock, Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(dock);
        var position = new Slider { Minimum = 0, Maximum = 100, TickFrequency = 1, IsSnapToTickEnabled = true, Value = Math.Clamp(CurrentTaskbarSettings.PositionPercent, 0, 100) };
        var label = new TextBlock { Margin = new Thickness(0, 12, 0, 8) };
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(Slider.Value)) { Source = position, StringFormat = "Taskbar position: {0:0}% (left to right)" });
        position.IsEnabled = dock.IsChecked != true;
        dock.Checked += (_, _) => position.IsEnabled = false;
        dock.Unchecked += (_, _) => position.IsEnabled = true;
        panel.Children.Add(label); panel.Children.Add(position);
        panel.Children.Add(new TextBlock { Text = "Choose an unused area of your taskbar. The widget occupies existing taskbar space and may overlap app buttons on a crowded taskbar. It follows taskbar auto-hide and reconnects automatically after Windows taskbar transitions.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        tabs.Items.Add(new TabItem { Header = "Taskbar", Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        if (selectTaskbar) tabs.SelectedIndex = tabs.Items.Count - 1;
        return () =>
        {
            if (!int.TryParse(seconds.Text, out int value) || value is < 1 or > 3600)
                throw new FormatException("Taskbar scroll rate must be a whole number from 1 to 3600 seconds.");
            int refreshValue = Math.Clamp(CurrentTaskbarSettings.AutoRefreshSeconds, 2, 3600);
            if (autoRefresh.IsChecked == true)
            {
                if (!int.TryParse(refreshSeconds.Text, out refreshValue) || refreshValue is < 2 or > 3600)
                    throw new FormatException("Taskbar auto-refresh interval must be a whole number from 2 to 3600 seconds.");
            }
            return new TaskbarSettings { DisplayDeviceName = (display.SelectedItem as TaskbarDisplay)?.DeviceName ?? "",
                HiddenPlcKeys = plcChecks.Where(p => p.Value.IsChecked != true).Select(p => p.Key).ToList(),
                ScrollSeconds = value, AutoRefreshEnabled = autoRefresh.IsChecked == true, AutoRefreshSeconds = refreshValue,
                ColorScheme = readPalette(),
                DockBesideClock = dock.IsChecked == true, PositionPercent = (int)position.Value };
        };
    }
}




