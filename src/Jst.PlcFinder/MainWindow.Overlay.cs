using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Jst.PlcFinder;

public partial class MainWindow
{
    private OverlayWindow? overlay;
    private OverlaySettings CurrentOverlaySettings => saved.Overlay ??= new();

    private IEnumerable<(string Key, string Label, string Path)> OverlayFields()
    {
        yield return ("ip", "IP address", "IpDisplay");
        yield return ("tracking0", TrackingSettings.Labels[0], "TrackingValues[0]");
        yield return ("station", "Station name", "Station");
        yield return ("connection", "Connection", "Status");
        yield return ("type", "Type", "DeviceKind");
        yield return ("software", "Software date", "SoftwareDate");
        yield return ("gem", "GEM date", "GemDate");
        yield return ("simulation", "Simulation", "Simulation");
        for (int i = 1; i < 5; i++) yield return ($"tracking{i}", TrackingSettings.Labels[i], $"TrackingValues[{i}]");
    }

    private void Overlay_Click(object sender, RoutedEventArgs e)
    {
        if (closing || trackingSavePending) return;
        if (!DevicesGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !DevicesGrid.CommitEdit(DataGridEditingUnit.Row, true) || trackingSavePending) return;
        if (taskbarWidget is not null) RestoreFromTaskbar();
        if (overlay is null)
        {
            overlay = new OverlayWindow();
            overlay.StationGrid.ItemsSource = view;
            overlay.RefreshControl.SetBinding(IsEnabledProperty, new Binding(nameof(IsEnabled)) { Source = RefreshButton });
            overlay.SettingsControl.SetBinding(IsEnabledProperty, new Binding(nameof(IsEnabled)) { Source = SettingsButton });
            overlay.StatusControl.SetBinding(TextBlock.TextProperty, new Binding(nameof(TextBlock.Text)) { Source = StatusText });
            overlay.RefreshControl.Click += Refresh_Click;
            overlay.SettingsControl.Click += (_, _) => { if (!busy && !trackingSavePending) ShowTrackingSettingsWindow(overlay!); };
            overlay.RestoreControl.Click += (_, _) => overlay?.Close();
            overlay.Closed += (_, _) =>
            {
                overlay = null;
                if (closing || switchingDisplayMode) return;
                Show();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
            };
        }
        overlay.ApplyTheme(CurrentOverlaySettings.ColorScheme);
        overlay.ApplySettings(CurrentOverlaySettings, OverlayFields());
        overlay.Show();
        Hide();
        overlay.Activate();
    }

    private Func<OverlaySettings> AddOverlaySettingsTab(TabControl tabs)
    {
        var settings = CurrentOverlaySettings;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Compact station overlay", FontSize = 20, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "Overlay mode hides the main window and follows its station filter. Use Main window (or Alt+F4) to return. Drag the title to move; drag the bottom-right corner to resize.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        var topmost = new CheckBox { Content = "Always on top", IsChecked = settings.AlwaysOnTop, Margin = new Thickness(0, 0, 0, 16) };
        panel.Children.Add(topmost);
        var opacity = new Slider { Minimum = 20, Maximum = 100, TickFrequency = 5, IsSnapToTickEnabled = true, Value = Math.Clamp(settings.OpacityPercent, 20, 100) };
        var opacityLabel = new TextBlock { Margin = new Thickness(0, 0, 0, 8) };
        opacityLabel.SetBinding(TextBlock.TextProperty, new Binding(nameof(Slider.Value)) { Source = opacity, StringFormat = "Opacity: {0:0}%" });
        panel.Children.Add(opacityLabel); panel.Children.Add(opacity);
        panel.Children.Add(new TextBlock { Text = "Overlay colors", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 6) });
        var (palettePicker, readPalette) = ModeColors.Picker(settings.ColorScheme);
        panel.Children.Add(palettePicker);
        panel.Children.Add(new TextBlock { Text = "Visible overlay fields", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 8) });
        var checks = new Dictionary<string, CheckBox>();
        foreach (var field in OverlayFields())
        {
            var check = new CheckBox { Content = field.Label, IsChecked = settings.IsVisible(field.Key), Margin = new Thickness(0, 5, 0, 5) };
            checks.Add(field.Key, check); panel.Children.Add(check);
        }
        tabs.Items.Add(new TabItem { Header = "Overlay", Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        if (overlay is not null) tabs.SelectedIndex = tabs.Items.Count - 1;
        return () => new OverlaySettings { AlwaysOnTop = topmost.IsChecked == true, OpacityPercent = (int)opacity.Value,
            ColorScheme = readPalette(), VisibleFields = checks.ToDictionary(p => p.Key, p => p.Value.IsChecked == true) };
    }
}
