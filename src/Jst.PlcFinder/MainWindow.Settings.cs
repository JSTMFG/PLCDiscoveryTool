using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Jst.PlcFinder;

public partial class MainWindow
{
    private readonly Dictionary<string, DataGridColumn> stationColumns = new();

    private void InitializeStationColumns()
    {
        string[] keys = ["ip", "station", "connection", "type", "software", "gem", "simulation", "reset"];
        for (int i = 0; i < keys.Length; i++)
        {
            DevicesGrid.Columns[i].IsReadOnly = true;
            stationColumns.Add(keys[i], DevicesGrid.Columns[i]);
        }
        for (int i = 0; i < 5; i++)
        {
            var column = new DataGridTextColumn
            {
                Header = TrackingSettings.Labels[i], Binding = new Binding($"TrackingValues[{i}]") { UpdateSourceTrigger = UpdateSourceTrigger.Explicit },
                Width = i == 0 ? 240 : 160, MinWidth = 100, IsReadOnly = i == 2,
                ElementStyle = (Style)FindResource("FieldText")
            };
            DevicesGrid.Columns.Add(column);
            stationColumns.Add($"tracking{i}", column);
        }
        ApplyStationColumns();
        DevicesGrid.BeginningEdit += Tracking_BeginningEdit;
    }

    private void ApplyStationColumns()
    {
        saved.VisibleColumns ??= new();
        foreach (var entry in stationColumns)
            entry.Value.Visibility = saved.VisibleColumns.GetValueOrDefault(entry.Key,
                entry.Key is not ("tracking3" or "tracking4")) ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < 5; i++) stationColumns[$"tracking{i}"].Header = TrackingSettings.Labels[i];
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (!busy && !trackingSavePending) ShowTrackingSettingsWindow(this);
    }

    private Func<(string Mode, bool ScanOnStartup, bool AutoScan, int Seconds)> AddStartupSettingsTab(TabControl tabs)
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "On program startup", FontSize = 20, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "Start in", Margin = new Thickness(0, 0, 0, 5) });
        var mode = new ComboBox { ItemsSource = new[] { "Main", "Overlay", "Taskbar" },
            SelectedItem = saved.StartupMode is "Overlay" or "Taskbar" ? saved.StartupMode : "Main" };
        panel.Children.Add(mode);
        var startupScan = new CheckBox { Content = "Scan selected ranges when the program starts",
            IsChecked = saved.ScanOnStartup, Margin = new Thickness(0, 18, 0, 8) };
        panel.Children.Add(startupScan);
        panel.Children.Add(new TextBlock { Text = "Automatic network discovery", FontSize = 20, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 18, 0, 12) });
        var auto = new CheckBox { Content = "Scan selected ranges regularly in all modes",
            IsChecked = saved.AutoScanEnabled, Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(auto);
        panel.Children.Add(new TextBlock { Text = "Scan every (seconds, 30–3600)", Margin = new Thickness(0, 0, 0, 5) });
        var seconds = new TextBox { Text = Math.Clamp(saved.AutoScanSeconds, 30, 3600).ToString(),
            IsEnabled = auto.IsChecked == true };
        auto.Checked += (_, _) => seconds.IsEnabled = true;
        auto.Unchecked += (_, _) => seconds.IsEnabled = false;
        panel.Children.Add(seconds);
        panel.Children.Add(new TextBlock { Text = "Scans discover new devices. Auto-refresh updates readings from devices already found. Automatic scans above the large scan threshold are skipped.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        tabs.Items.Add(new TabItem { Header = "Startup & scans", Content = new ScrollViewer { Content = panel } });
        return () =>
        {
            int interval = Math.Clamp(saved.AutoScanSeconds, 30, 3600);
            if (auto.IsChecked == true && (!int.TryParse(seconds.Text, out interval) || interval is < 30 or > 3600))
                throw new FormatException("Automatic scan interval must be a whole number from 30 to 3600 seconds.");
            return ((string)mode.SelectedItem, startupScan.IsChecked == true, auto.IsChecked == true, interval);
        };
    }

    private void ApplyAutoScanSettings()
    {
        autoScan.Stop();
        autoScan.Interval = TimeSpan.FromSeconds(Math.Clamp(saved.AutoScanSeconds, 30, 3600));
        if (saved.AutoScanEnabled && !smokeTest) autoScan.Start();
    }
}
