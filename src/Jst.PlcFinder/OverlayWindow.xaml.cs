using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace Jst.PlcFinder;

public partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();
        EmptyText.SetBinding(VisibilityProperty, new Binding(nameof(ItemsControl.HasItems))
        {
            Source = StationGrid, Converter = new EmptyVisibilityConverter()
        });
    }

    public void ApplySettings(OverlaySettings settings, IEnumerable<(string Key, string Label, string Path)> fields)
    {
        Topmost = settings.AlwaysOnTop;
        Opacity = Math.Clamp(settings.OpacityPercent, 20, 100) / 100.0;
        StationGrid.Columns.Clear();
        foreach (var field in fields.Where(f => settings.IsVisible(f.Key)))
        {
            var textStyle = new Style(typeof(TextBlock));
            textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            textStyle.Setters.Add(new Setter(ToolTipProperty, new Binding(field.Path)));
            StationGrid.Columns.Add(new DataGridTextColumn
            {
                Header = field.Label, Binding = new Binding(field.Path),
                SortMemberPath = field.Key == "ip" ? "IpSort" : field.Path,
                Width = new DataGridLength(field.Key == "station" ? 1.3 : 1, DataGridLengthUnitType.Star),
                MinWidth = field.Key == "ip" ? 130 : 120, ElementStyle = textStyle
            });
        }
    }

    public void ApplyTheme(string? scheme) => ModeColors.Apply(Resources, scheme, taskbar: false);

    private void Resize_Drag(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        Width = Math.Max(MinWidth, ActualWidth + e.HorizontalChange);
        Height = Math.Max(MinHeight, ActualHeight + e.VerticalChange);
    }

    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private sealed class EmptyVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => value is true ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }
}

