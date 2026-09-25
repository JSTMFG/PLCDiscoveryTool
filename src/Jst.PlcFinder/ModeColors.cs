using Microsoft.Win32;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Jst.PlcFinder;

internal sealed record ModePalette(Color Background, Color Alternate, Color Header, Color Button,
    Color Hover, Color Border, Color Text, Color Muted, Color Selected, Color GridLine, Color Alert, Color Success);

internal static class ModeColors
{
    internal const string Default = "Navy";
    private static readonly (string Name, Color Color)[] Presets =
    [
        ("Navy", Color.FromRgb(0x10, 0x24, 0x3E)),
        ("Slate", Color.FromRgb(0x20, 0x2C, 0x3A)),
        ("Emerald", Color.FromRgb(0x10, 0x30, 0x2B)),
        ("Plum", Color.FromRgb(0x2B, 0x23, 0x3E)),
        ("Burgundy", Color.FromRgb(0x38, 0x22, 0x31)),
    ];

    internal static ModePalette Resolve(string? choice, bool taskbar)
    {
        if (string.Equals(choice, "Windows", StringComparison.OrdinalIgnoreCase))
            return WindowsPalette(taskbar);
        var preset = Presets.FirstOrDefault(p => string.Equals(p.Name, choice, StringComparison.OrdinalIgnoreCase));
        return DarkPalette(preset.Name is null ? Presets[0].Color : preset.Color);
    }

    internal static void Apply(ResourceDictionary resources, string? choice, bool taskbar)
    {
        var palette = Resolve(choice, taskbar);
        resources["ModeBackground"] = new SolidColorBrush(palette.Background);
        resources["ModeAlternate"] = new SolidColorBrush(palette.Alternate);
        resources["ModeHeader"] = new SolidColorBrush(palette.Header);
        resources["ModeButton"] = new SolidColorBrush(palette.Button);
        resources["ModeHover"] = new SolidColorBrush(palette.Hover);
        resources["ModeBorder"] = new SolidColorBrush(palette.Border);
        resources["ModeText"] = new SolidColorBrush(palette.Text);
        resources["ModeMuted"] = new SolidColorBrush(palette.Muted);
        resources["ModeSelected"] = new SolidColorBrush(palette.Selected);
        resources["ModeGridLine"] = new SolidColorBrush(palette.GridLine);
        resources["ModeAlert"] = new SolidColorBrush(palette.Alert);
        resources["ModeSuccess"] = new SolidColorBrush(palette.Success);
    }

    internal static (FrameworkElement Picker, Func<string> Read) Picker(string? choice, bool taskbar = false)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
        var options = Presets.Select(p => p.Name).Append("Windows").ToArray();
        string selected = options.FirstOrDefault(o => string.Equals(o, choice, StringComparison.OrdinalIgnoreCase)) ?? Default;
        string group = "palette-" + Guid.NewGuid().ToString("N");
        var radios = new List<(string Name, RadioButton Radio)>();
        foreach (string name in options)
        {
            var palette = Resolve(name, taskbar);
            var content = new StackPanel { Width = 76 };
            var preview = name == "Windows" && taskbar
                ? Color.FromRgb(palette.Background.R, palette.Background.G, palette.Background.B)
                : palette.Background;
            content.Children.Add(new Border { Background = new SolidColorBrush(preview),
                BorderBrush = new SolidColorBrush(palette.Border), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4), Width = 65, Height = 24, HorizontalAlignment = HorizontalAlignment.Left });
            content.Children.Add(new TextBlock { Text = name == "Windows" ? "Match Windows" : name,
                FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            var radio = new RadioButton { Content = content, GroupName = group, IsChecked = name == selected,
                Margin = new Thickness(0, 0, 8, 10), VerticalAlignment = VerticalAlignment.Top };
            if (name == "Windows") radio.ToolTip = taskbar
                ? "Shows the actual Windows taskbar color through the widget"
                : "Uses a neutral Windows light or dark surface";
            AutomationProperties.SetName(radio, name == "Windows" ? "Match Windows color scheme" : name + " color palette");
            radios.Add((name, radio)); panel.Children.Add(radio);
        }
        return (panel, () => radios.First(p => p.Radio.IsChecked == true).Name);
    }

    private static ModePalette DarkPalette(Color background)
    {
        Color text = Color.FromRgb(0xF0, 0xF5, 0xFF);
        return new(background,
            Blend(background, Colors.White, 0.035), Blend(background, Colors.Black, 0.13),
            Blend(background, Colors.White, 0.10), Blend(background, Colors.White, 0.20),
            Blend(background, Colors.White, 0.18), text, Blend(background, Colors.White, 0.66),
            Blend(background, Colors.White, 0.25), Blend(background, Colors.White, 0.09),
            Color.FromRgb(0xFF, 0xD2, 0x8B), Color.FromRgb(0x92, 0xDF, 0xBD));
    }

    private static ModePalette WindowsPalette(bool taskbar)
    {
        try
        {
            if (SystemParameters.HighContrast)
            {
                if (taskbar) return SeamlessTaskbar(SystemColors.WindowColor, SystemColors.WindowTextColor,
                    SystemColors.GrayTextColor, SystemColors.HighlightTextColor, SystemColors.HighlightTextColor);
                return new(SystemColors.WindowColor, SystemColors.WindowColor, SystemColors.ControlColor,
                    SystemColors.ControlColor, SystemColors.HighlightColor, SystemColors.WindowTextColor,
                    SystemColors.WindowTextColor, SystemColors.GrayTextColor, SystemColors.HighlightColor,
                    SystemColors.WindowTextColor, SystemColors.HighlightTextColor, SystemColors.HighlightTextColor);
            }
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            bool light = Convert.ToInt32(key?.GetValue("SystemUsesLightTheme", 0)) != 0;
            Color shell = light ? Color.FromRgb(0xF3, 0xF3, 0xF3) : Color.FromRgb(0x20, 0x20, 0x20);
            if (taskbar) return SeamlessTaskbar(shell,
                light ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF5, 0xF5, 0xF5),
                light ? Color.FromRgb(0x4A, 0x4A, 0x4A) : Color.FromRgb(0xC8, 0xC8, 0xC8),
                light ? Color.FromRgb(0x8A, 0x4B, 0x00) : Color.FromRgb(0xFF, 0xD2, 0x8B),
                light ? Color.FromRgb(0x1A, 0x69, 0x40) : Color.FromRgb(0x92, 0xDF, 0xBD));
            if (!light) return DarkPalette(shell);
            Color background = shell;
            Color ink = Color.FromRgb(0x1B, 0x26, 0x34);
            Color muted = Color.FromRgb(0x4B, 0x59, 0x68);
            return new(background, Color.FromRgb(0xEB, 0xEB, 0xEB), Color.FromRgb(0xE5, 0xE5, 0xE5),
                Color.FromRgb(0xE0, 0xE0, 0xE0), Color.FromRgb(0xD5, 0xD5, 0xD5),
                Color.FromRgb(0xBD, 0xBD, 0xBD), ink, muted, Color.FromRgb(0xD0, 0xD0, 0xD0),
                Color.FromRgb(0xD9, 0xD9, 0xD9), Color.FromRgb(0x8A, 0x4B, 0x00), Color.FromRgb(0x1A, 0x69, 0x40));
        }
        catch { return DarkPalette(Presets[0].Color); }
    }

    private static ModePalette SeamlessTaskbar(Color shell, Color text, Color muted, Color alert, Color success)
    {
        // A fully transparent layered HWND passes mouse input through to Explorer.
        // One alpha step keeps the whole bar clickable while the real taskbar supplies its color.
        Color surface = Color.FromArgb(1, shell.R, shell.G, shell.B);
        return new(surface, surface, surface, surface, surface, surface,
            text, muted, surface, surface, alert, success);
    }

    private static Color Blend(Color a, Color b, double amount)
    {
        byte channel(byte left, byte right) => (byte)Math.Round(left * (1 - amount) + right * amount);
        return Color.FromRgb(channel(a.R, b.R), channel(a.G, b.G), channel(a.B, b.B));
    }

}
