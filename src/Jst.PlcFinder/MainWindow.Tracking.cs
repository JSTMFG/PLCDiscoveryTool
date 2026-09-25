using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Jst.PlcFinder.Core;

namespace Jst.PlcFinder;

public partial class MainWindow
{
    private DeveloperTrackingSettings TrackingSettings => saved.DeveloperTracking ?? new();

    private bool trackingEditActive, trackingSavePending;
    private string[]? trackingEditOriginal;

    private void Tracking_BeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        e.Cancel = busy || closing || trackingSavePending || e.Row.Item is not PlcRow { TrackingAvailable: true };
        if (e.Cancel) return;
        trackingEditOriginal = ((PlcRow)e.Row.Item).TrackingValues.ToArray();
        trackingEditActive = true;
    }

    private void Tracking_EditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        trackingEditActive = false;
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not PlcRow row || e.EditingElement is not TextBox input)
            return;
        var column = stationColumns.FirstOrDefault(pair => ReferenceEquals(pair.Value, e.Column));
        if (column.Key is null || !column.Key.StartsWith("tracking", StringComparison.Ordinal) || !int.TryParse(column.Key[8..], out int field) || field == 2)
            return;
        if (busy || closing || trackingSavePending || !row.TrackingAvailable)
        {
            e.Cancel = true;
            trackingEditActive = true;
            return;
        }
        var previousValues = trackingEditOriginal ?? row.TrackingValues.ToArray();
        string value = input.Text;
        if (string.Equals(value, previousValues[field], StringComparison.Ordinal)) return;
        var settings = TrackingSettings;
        var changes = new Dictionary<int, string> { [field] = value };
        if (field == 0) changes[2] = DateTime.Now.ToString("MMddyy", CultureInfo.InvariantCulture);
        // Reserve the operation before deferring out of WPF's cell edit transaction.
        trackingSavePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(async () =>
        {
            try
            {
                if (closing) return;
                bool written = false;
                Exception? failure = null;
                await StartOperationAsync(async token =>
                {
                    try
                    {
                        StatusText.Text = $"Saving {settings.Labels[field]} · {row.IpDisplay}";
                        await reader.WriteDeveloperTrackingAsync(row, settings, changes, token);
                        written = true;
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                        // Multiple tag writes are not atomic. Read the actual PLC state
                        // instead of pretending that a partially completed write rolled back.
                        if (token.IsCancellationRequested) row.ClearTracking();
                        else row.ApplyTracking(await reader.ReadDeveloperTrackingAsync(row, settings, token));
                    }
                }, isTrackingSave: true);
                if (!written)
                {
                    if (failure is null) row.ClearTracking();
                    StatusText.Text = "Developer tracking save was not completed · refresh to verify";
                    ShowInfo($"Could not complete the save to {row.IpDisplay}. Some fields may have changed.\n\n{failure?.Message ?? "The operation could not start."}");
                    return;
                }
                foreach (var change in changes) previousValues[change.Key] = change.Value;
                row.ApplyTracking(previousValues.Select(v => new FieldRead(v, null)).ToArray());
                StatusText.Text = $"Saved {settings.Labels[field]} · {row.IpDisplay}";
                AutoSizeStationColumns();
            }
            finally { trackingSavePending = false; }
        }));
    }

    private void ShowTrackingSettingsWindow(Window owner, bool selectTaskbar = false)
    {
        var current = TrackingSettings;
        var tabs = new TabControl();
        var columnPanel = new StackPanel { Margin = new Thickness(20) };
        var columnChecks = new Dictionary<string, CheckBox>();
        columnPanel.Children.Add(new TextBlock { Text = "Visible station columns", FontSize = 20, Margin = new Thickness(0, 0, 0, 12) });
        foreach (var entry in stationColumns)
        {
            var check = new CheckBox { Content = entry.Value.Header, IsChecked = entry.Value.Visibility == Visibility.Visible, Margin = new Thickness(0, 5, 0, 5) };
            columnChecks.Add(entry.Key, check);
            columnPanel.Children.Add(check);
        }
        tabs.Items.Add(new TabItem { Header = "Columns", Content = new ScrollViewer { Content = columnPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Developer tracking settings", FontSize = 20, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Map each PLC STRING tag and choose the field labels shown to users.", Foreground = (Brush)Application.Current.FindResource("AppMuted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12) });
        var tags = new TextBox[5]; var labels = new TextBox[5];
        for (int i = 0; i < 5; i++)
        {
            panel.Children.Add(new TextBlock { Text = $"Field {i} label", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 7, 0, 3) });
            labels[i] = new TextBox { Text = current.Labels[i] }; panel.Children.Add(labels[i]);
            panel.Children.Add(new TextBlock { Text = $"Field {i} PLC tag", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 7, 0, 3) });
            tags[i] = new TextBox { Text = current.Tags[i] }; panel.Children.Add(tags[i]);
        }
        panel.Children.Add(new TextBlock { Text = "Field 2 is the automatic Last claimed date. It uses MMDDYY whenever Developer changes.", Foreground = (Brush)Application.Current.FindResource("AppMuted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        var buttons = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
        var save = new Button { Content = "Save settings", Padding = new Thickness(10, 5, 10, 5) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0) };
        buttons.Children.Add(save); buttons.Children.Add(cancel);
        tabs.Items.Add(new TabItem { Header = "Developer tracking", Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        var readOverlaySettings = AddOverlaySettingsTab(tabs);
        var readTaskbarSettings = AddTaskbarSettingsTab(tabs, selectTaskbar);
        var readStartupSettings = AddStartupSettingsTab(tabs);
        var readAppearance = AddAppearanceSettingsTab(tabs);
        var layout = new DockPanel();
        buttons.Margin = new Thickness(20);
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(buttons); layout.Children.Add(tabs);
        var window = new Window
        {
            Owner = owner, Title = "PLC Finder Â· Settings", Width = 540, Height = 720,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = layout
        };
        cancel.Click += (_, _) => window.Close();
        save.Click += (_, _) =>
        {
            if (tags.Any(t => string.IsNullOrWhiteSpace(t.Text)) || labels.Any(l => string.IsNullOrWhiteSpace(l.Text)))
            { MessageBox.Show(window, "Each tracking field needs a label and PLC tag.", "Developer tracking", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            if (tags.Any(t => !DeveloperTrackingSettings.IsValidTag(t.Text.Trim())))
            { MessageBox.Show(window, "Use valid Logix tag references, such as DEBUG_DEVELOPER[0] or Program:MyProgram.DEBUG_DEVELOPER[0].", "Developer tracking", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            if (tags.Select(t => t.Text.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 5)
            { MessageBox.Show(window, "Each tracking field must map to a different PLC tag."); return; }
            if (!columnChecks.Values.Any(c => c.IsChecked == true))
            { MessageBox.Show(window, "Keep at least one station column visible."); return; }
            var overlaySettings = readOverlaySettings();
            if (!overlaySettings.VisibleFields.Values.Any(visible => visible))
            { MessageBox.Show(window, "Keep at least one overlay field visible."); return; }
            TaskbarSettings taskbarSettings;
            try { taskbarSettings = readTaskbarSettings(); }
            catch (FormatException ex) { MessageBox.Show(window, ex.Message); return; }
            (string Mode, bool ScanOnStartup, bool AutoScan, int Seconds) startupSettings;
            try { startupSettings = readStartupSettings(); }
            catch (FormatException ex) { MessageBox.Show(window, ex.Message); return; }
            CaptureUiSettings();
            var previous = saved.DeveloperTracking;
            var previousColumns = saved.VisibleColumns;
            var previousTaskbar = saved.Taskbar;
            saved.Taskbar = taskbarSettings;
            var previousOverlay = saved.Overlay;
            var previousStartup = (saved.StartupMode, saved.ScanOnStartup, saved.AutoScanEnabled, saved.AutoScanSeconds);
            var previousAppearance = saved.Appearance;
            saved.Overlay = overlaySettings;
            saved.StartupMode = startupSettings.Mode;
            saved.ScanOnStartup = startupSettings.ScanOnStartup;
            saved.AutoScanEnabled = startupSettings.AutoScan;
            saved.AutoScanSeconds = startupSettings.Seconds;
            saved.Appearance = readAppearance();
            saved.DeveloperTracking = new(tags[0].Text.Trim(), tags[1].Text.Trim(), tags[2].Text.Trim(), tags[3].Text.Trim(), tags[4].Text.Trim(),
                labels[0].Text.Trim(), labels[1].Text.Trim(), labels[2].Text.Trim(), labels[3].Text.Trim(), labels[4].Text.Trim());
            saved.VisibleColumns = columnChecks.ToDictionary(p => p.Key, p => p.Value.IsChecked == true);
            if (!SettingsStore.Save(saved))
            {
                saved.DeveloperTracking = previous; saved.VisibleColumns = previousColumns; saved.Overlay = previousOverlay; saved.Taskbar = previousTaskbar;
                (saved.StartupMode, saved.ScanOnStartup, saved.AutoScanEnabled, saved.AutoScanSeconds) = previousStartup;
                saved.Appearance = previousAppearance;
                MessageBox.Show(window, "Settings could not be saved. Please try again."); return;
            }
            ApplyStationColumns();
            ApplyTaskbarSettings();
            ApplyAutoScanSettings();
            UpdateTaskbarStation();
            overlay?.ApplySettings(CurrentOverlaySettings, OverlayFields());
            ApplyActiveThemes();
            ApplyAppearance();
            if (previous != saved.DeveloperTracking)
                foreach (var row in Devices) row.ClearTracking();
            StatusText.Text = "Settings saved Â· Refresh to read updated tracking mappings";
            window.Close();
        };
        window.ShowDialog();
    }
}
