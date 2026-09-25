using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Jst.PlcFinder.Core;

namespace Jst.PlcFinder;

public partial class TaskbarWidgetView : UserControl
{
    public event Action? RestoreRequested;
    public event Action? SettingsRequested;
    public event Action? NextRequested;
    public event Action<int>? ScrollRequested;
    private int wheelRemainder;
    public event Action? HideRequested;
    public event Action? ExitRequested;

    public TaskbarWidgetView()
    {
        InitializeComponent();
        PreviewMouseWheel += (_, e) =>
        {
            HandleMouseWheel(e.Delta);
            e.Handled = true;
        };
    }

    public void ApplyTheme(string? scheme) => ModeColors.Apply(Resources, scheme, taskbar: true);

    internal void HandleMouseWheel(int delta)
    {
        wheelRemainder += delta;
        int steps = wheelRemainder / 120;
        wheelRemainder %= 120;
        if (steps != 0) ScrollRequested?.Invoke(-steps);
    }

    internal void ShowStation(PlcRow? row, int index, int count)
    {
        DataContext = row;
        StationContent.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        SimulationText.Visibility = CountText.Visibility = StationContent.Visibility;
        EmptyText.Visibility = row is null ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = $"{index + 1} / {count}";
        HideItem.IsEnabled = row is not null;
    }

    private void Widget_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        e.Handled = true;
        RestoreRequested?.Invoke();
    }
    private void Open_Click(object sender, RoutedEventArgs e) => RestoreRequested?.Invoke();
    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    private void Next_Click(object sender, RoutedEventArgs e) => NextRequested?.Invoke();
    private void Hide_Click(object sender, RoutedEventArgs e) => HideRequested?.Invoke();
    private void Exit_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();
}
