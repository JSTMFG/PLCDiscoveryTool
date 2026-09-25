using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Jst.PlcFinder;

// Screen-pixel capture for the explicit --smoke-test only. Unlike RenderTargetBitmap,
// this checks what is actually visible in the taskbar after desktop composition.
internal static class TaskbarScreenCapture
{
    internal static void Save(nint taskbar, nint widget, string path, System.Windows.Media.Color? expectedBackground = null)
    {
        if (!GetWindowRect(taskbar, out var rect)) throw new InvalidOperationException("Cannot read taskbar bounds.");
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) throw new InvalidOperationException("Invalid taskbar bounds.");
        nint screen = GetDC(0), dc = CreateCompatibleDC(screen), bitmap = CreateCompatibleBitmap(screen, width, height);
        nint previous = SelectObject(dc, bitmap);
        try
        {
            if (!BitBlt(dc, 0, 0, width, height, screen, rect.Left, rect.Top, 0x40CC0020))
                throw new InvalidOperationException("Taskbar screen capture failed.");
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var file = File.Create(path)) encoder.Save(file);
            if (!GetWindowRect(widget, out var widgetRect) || widgetRect.Left < rect.Left || widgetRect.Right > rect.Right ||
                widgetRect.Top < rect.Top || widgetRect.Bottom > rect.Bottom)
                throw new InvalidOperationException("Widget is outside the visible taskbar bounds.");
            var visibleWidget = new System.Windows.Media.Imaging.CroppedBitmap(image,
                new Int32Rect(widgetRect.Left - rect.Left, widgetRect.Top - rect.Top, widgetRect.Right - widgetRect.Left, widgetRect.Bottom - widgetRect.Top));
            var pixels = new byte[visibleWidget.PixelWidth * visibleWidget.PixelHeight * 4];
            var bgra = new System.Windows.Media.Imaging.FormatConvertedBitmap(visibleWidget, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            bgra.CopyPixels(pixels, visibleWidget.PixelWidth * 4, 0);
            var expected = expectedBackground ?? ModeColors.Resolve(ModeColors.Default, taskbar: true).Background;
            int visibleBackgroundPixels = 0;
            if (expected.A < 10)
            {
                // Compare clear margins with the adjacent real taskbar surface.
                int sampleY = widgetRect.Top - rect.Top + 4;
                int sampleX = widgetRect.Left - rect.Left - 8;
                if (sampleX < 0) throw new InvalidOperationException("No space to sample the adjacent taskbar surface.");
                var background = Pixel(image, sampleX, sampleY);
                var samples = new[] { (5, 5), (visibleWidget.PixelWidth / 2, 5),
                    (visibleWidget.PixelWidth - 6, 5), (visibleWidget.PixelWidth / 2, visibleWidget.PixelHeight - 5) };
                int matching = samples.Count(p => Similar(Pixel(visibleWidget, p.Item1, p.Item2), background, 18));
                if (matching < 2) throw new InvalidOperationException("Match Windows still paints a panel over the taskbar.");
                var center = new NativePoint { X = widgetRect.Left + visibleWidget.PixelWidth / 2,
                    Y = widgetRect.Top + visibleWidget.PixelHeight / 2 };
                if (WindowFromPoint(center) != widget)
                    throw new InvalidOperationException("The seamless taskbar widget no longer accepts clicks.");
            }
            else
            {
                for (int i = 0; i < pixels.Length; i += 4)
                    if (Math.Abs(pixels[i] - expected.B) <= 2 && Math.Abs(pixels[i + 1] - expected.G) <= 2 && Math.Abs(pixels[i + 2] - expected.R) <= 2)
                        visibleBackgroundPixels++;
                if (visibleBackgroundPixels < visibleWidget.PixelWidth * visibleWidget.PixelHeight / 4)
                    throw new InvalidOperationException("Widget HWND exists but its colored surface is not visible in the actual taskbar screen pixels.");
            }
            int cropLeft = Math.Max(0, widgetRect.Left - rect.Left - 20);
            var closeup = new CroppedBitmap(image, new Int32Rect(cropLeft, 0, width - cropLeft, height));
            var closeupEncoder = new PngBitmapEncoder(); closeupEncoder.Frames.Add(BitmapFrame.Create(closeup));
            using var closeupFile = File.Create(Path.ChangeExtension(path, "closeup.png")); closeupEncoder.Save(closeupFile);
        }
        finally { SelectObject(dc, previous); DeleteObject(bitmap); DeleteDC(dc); ReleaseDC(0, screen); }
    }
    private static (byte B, byte G, byte R) Pixel(BitmapSource image, int x, int y)
    {
        var converted = new FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var buffer = new byte[4];
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), buffer, 4, 0);
        return (buffer[0], buffer[1], buffer[2]);
    }
    private static bool Similar((byte B, byte G, byte R) a, (byte B, byte G, byte R) b, int tolerance)
        => Math.Abs(a.B - b.B) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.R - b.R) <= tolerance;
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
}
