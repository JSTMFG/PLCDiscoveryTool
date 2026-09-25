using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Jst.PlcFinder;

public partial class MainWindow
{
    private void About_Click(object sender, RoutedEventArgs e)
    {
        var tabs = new TabControl();
        var help = "JST PLC Finder 1.1\n\n" +
            "1. Enable one or more search ranges and select Scan selected ranges.\n" +
            "2. Results appear as devices answer. Hover an unavailable value to see its status.\n" +
            "3. Use Refresh or enable Auto-refresh to monitor known devices. Settings > Startup & scans can scan selected ranges at startup and regularly in Main, Overlay, or Taskbar mode to find new devices. Monitoring pauses during edits and operations. Stop ends the current scan and main-window Auto-refresh.\n\n" +
            "Ranges: 10.10.10.X, 10.10.9.0/24, 192.168.9.1-192.168.9.100, or a single IP. Overlapping ranges are scanned once. X means a /24, with hosts .1 through .254. /31 and /32 retain their host addresses.\n\n" +
            "Taskbar mode can refresh detected PLCs on its own interval (Settings > Taskbar). Completed scans and refreshes can notify you when a PLC stops responding or responds again. Automatic scans can report newly discovered network devices, and refreshes can report Developer tracking changes. Choose each alert under Settings > Notifications.\n\n" +
            "The network must already be configured to reach the selected subnets. Discovery uses the selected adapter; PLC tag reads follow Windows routing. The app does not assign IPs or create routes.\n\n" +
            "For a ControlLogix Ethernet bridge, PLC Finder automatically checks standard backplane slots 0–16 and lists each responding controller separately. You can still enter a processor route in Advanced when a chassis uses a nonstandard path (1,0 means backplane port 1, slot 0). Multiple routes can be separated with semicolons.\n\n" +
            "Routes use numeric port/slot pairs. Ports are 0–15 and slots are 0–255. Extended Ethernet/DH+ routes are not supported.\n\n" +
            "Tags: STATION.NAME, STATION.SOFTWARE_DATE, STATION.EIBHOST[2], STATION.OO.2. OO uses the letter O. By default, bit 2 = 1 means simulation ON. Advanced options allow inverted polarity and a program scope.\n\n" +
            "Station name and dates default to Logix STRING (length followed by bytes). Advanced options allow numeric date types, displayed exactly as raw values. Custom UDT string layouts are not supported. Tags must permit external reads. Run/Program mode is unrelated to the simulation bit.\n\n" +
            "Missing fields are Unavailable/Unknown. Station tags not found means the configured station tags could not be located; it does not determine whether a program is loaded. Check the tag names, program scope, external access, and route. Tag timeouts are reported separately. Old readings are marked stale. Not responding means there was no timely EtherNet/IP reply; it does not prove physical disconnection.\n\n" +
            "Developer tracking: edit a field directly in the Stations list, then press Enter or Tab to save. The default tags are DEBUG_DEVELOPER[0] through [4]. Developer and Job number are editable, Last claimed date is set automatically to MMDDYY whenever Developer changes, and Fields 4–5 are available for future use. If any tracking tag cannot be read, the editor shows Developer tracking not installed. Use Settings to change labels, mappings, and visible columns. Fields 4 and 5 are hidden by default. Saving writes only the changed tracking fields to that selected PLC.\n\n" +
            "Optional Logix Echo reset: install the JST Echo Reset Agent on the Echo server, then enter its address under Advanced. The portable agent uses trusted-network mode without a pairing key. A device receives the Echo badge only after a live inventory match by controller serial, IP, chassis, and route/slot. Physical and ambiguous devices never receive the reset option.\n\n" +
            "CompactLogix / ControlLogix are supported. Other AB families may be discovered but their tag maps are not supported. Standard EtherNet/IP explicit messaging uses TCP/UDP 44818. Apart from explicitly saved Developer Tracking STRING fields, the program does not write PLC tags or change controller mode. The optional Echo reset uses the server agent and Echo controller GUID; it cannot issue that operation to a physical PLC.\n\n" +
            "Copy selected result cells with Ctrl+C.\n\n" +
            "Distribution: copy only JST PLC Finder.exe. The logo, .NET runtime, and native PLC library are bundled. Runtime libraries may extract into the user's .NET cache. Preferences are stored under HKCU\\Software\\JST\\PlcFinder; no companion configuration file is required.\n\n" +
            "libplctag.NativeImport 1.0.41; libplctag " + Core.TagReader.Version + ". Third-party licenses and source locations are included in the next tab.";
        tabs.Items.Add(new TabItem { Header = "Getting started", Content = ReadOnlyText(help) });
        string licenseText = "";
        var assembly = Assembly.GetExecutingAssembly();
        foreach (string name in assembly.GetManifestResourceNames().Where(n => n.Contains("Licenses")))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            licenseText += reader.ReadToEnd() + "\n\n";
        }
        tabs.Items.Add(new TabItem { Header = "Licenses / source", Content = ReadOnlyText(licenseText) });
        new Window { Owner = this, Title = "JST PLC Finder · Help", Width = 760, Height = 650, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = tabs }.ShowDialog();
    }

    private static TextBox ReadOnlyText(string text) => new() { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(15), BorderThickness = new Thickness(0), Background = Brushes.Transparent };

    private async Task SmokeTestAsync()
    {
        // Invoked explicitly by --smoke-test; operates only on sample data and the WPF visual tree.
        try
        {
            Core.TagReader.VerifyNativeLibrary();
            var output = Path.Combine(Environment.CurrentDirectory, "artifacts");
            Directory.CreateDirectory(output);
            RefreshIntervalBox.Text = "12";
            if (!ApplyRefreshInterval() || monitor.Interval != TimeSpan.FromSeconds(12))
                throw new InvalidOperationException("Auto-refresh interval did not update.");
            MonitorBox.IsChecked = true;
            if (!monitor.IsEnabled || !saved.AutoRefreshEnabled)
                throw new InvalidOperationException("Auto-refresh did not start.");
            MonitorBox.IsChecked = false;
            if (monitor.IsEnabled || saved.AutoRefreshEnabled)
                throw new InvalidOperationException("Auto-refresh did not stop.");
            RefreshIntervalBox.Text = "5";
            ApplyRefreshInterval();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            SaveVisual(Path.Combine(output, "empty-state.png"));
            PopulateSmokeRows();
            Devices[0].ApplyTracking(Enumerable.Range(0, 5).Select(i => new Core.FieldRead($"VALUE_{i}", null)).ToArray());
            DevicesGrid.SelectedItem = Devices[0];
            DevicesGrid.CurrentCell = new DataGridCellInfo(Devices[0], stationColumns["tracking0"]);
            DevicesGrid.ScrollIntoView(Devices[0], stationColumns["tracking0"]);
            DevicesGrid.Focus();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!DevicesGrid.BeginEdit() || !trackingEditActive)
                throw new InvalidOperationException("Developer tracking cell is not editable.");
            DevicesGrid.CancelEdit(DataGridEditingUnit.Cell);
            DevicesGrid.CancelEdit(DataGridEditingUnit.Row);
            if (trackingEditActive) throw new InvalidOperationException("Cancelled edit did not release monitoring pause.");
            trackingSavePending = true;
            bool competingOperationRan = false;
            await StartOperationAsync(_ => { competingOperationRan = true; return Task.CompletedTask; });
            trackingSavePending = false;
            if (competingOperationRan) throw new InvalidOperationException("An operation bypassed the pending PLC save.");
            var echoId = Guid.NewGuid();
            ApplyEchoInventory(new(DateTimeOffset.UtcNow, "SMOKE", [new(echoId, "TEST_STATION_ECHO", ["10.10.9.12"], Guid.Empty, "", 0, 0x1002, true, "On")]));
            DevicesGrid.SelectedItem = Devices[2];
            if (!Devices[2].IsEcho || !DevicesGrid.Columns.Any(c => Equals(c.Header, "Reset PLC")) ||
                DevicesGrid.Columns.Any(c => Equals(c.Header, "Updated") || Equals(c.Header, "Route")))
                throw new InvalidOperationException("Echo row reset UI failed.");
            FilterBox.Text = "10.10.9";
            if (view.Cast<object>().Count() != 1) throw new InvalidOperationException("IP filter failed.");
            FilterBox.Text = "WELD";
            if (view.Cast<object>().Count() != 1) throw new InvalidOperationException("Station filter failed.");
            FilterBox.Text = "";
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (Devices.Count != 5 || Devices[2].Simulation != "ON" || !Devices[3].Simulation.Contains("stale")) throw new InvalidOperationException("Sample state invalid.");
            SaveVisual(Path.Combine(output, "sample-state.png"));
            Overlay_Click(this, new RoutedEventArgs());
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (IsVisible || overlay is null || !overlay.IsVisible || overlay.Opacity != 0.8 || overlay.Topmost || overlay.StationGrid.Columns.Count != 3)
                throw new InvalidOperationException("Default overlay presentation failed.");
            if (!ReferenceEquals(overlay.StationGrid.ItemsSource, view) || overlay.StationGrid.Items.Count != Devices.Count)
                throw new InvalidOperationException("Overlay did not share live station results.");
            var overlayVisual = (FrameworkElement)overlay.Content;
            var overlayBitmap = new RenderTargetBitmap((int)overlayVisual.ActualWidth, (int)overlayVisual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            overlayBitmap.Render(overlayVisual);
            var overlayEncoder = new PngBitmapEncoder(); overlayEncoder.Frames.Add(BitmapFrame.Create(overlayBitmap));
            using (var overlayOutput = File.Create(Path.Combine(output, "overlay.png"))) overlayEncoder.Save(overlayOutput);
            overlay.ApplyTheme("Emerald");
            if (((SolidColorBrush)overlay.Resources["ModeBackground"]).Color != ModeColors.Resolve("Emerald", taskbar: false).Background)
                throw new InvalidOperationException("Overlay palette did not update its background.");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var emeraldBitmap = new RenderTargetBitmap((int)overlayVisual.ActualWidth, (int)overlayVisual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            emeraldBitmap.Render(overlayVisual);
            var emeraldEncoder = new PngBitmapEncoder(); emeraldEncoder.Frames.Add(BitmapFrame.Create(emeraldBitmap));
            using (var emeraldOutput = File.Create(Path.Combine(output, "overlay-emerald.png"))) emeraldEncoder.Save(emeraldOutput);
            overlay.ApplyTheme("Windows");
            if (((SolidColorBrush)overlay.Resources["ModeBackground"]).Color != ModeColors.Resolve("Windows", taskbar: false).Background)
                throw new InvalidOperationException("Overlay did not match the current Windows color scheme.");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var windowsBitmap = new RenderTargetBitmap((int)overlayVisual.ActualWidth, (int)overlayVisual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            windowsBitmap.Render(overlayVisual);
            var windowsEncoder = new PngBitmapEncoder(); windowsEncoder.Frames.Add(BitmapFrame.Create(windowsBitmap));
            using (var windowsOutput = File.Create(Path.Combine(output, "overlay-match-windows.png"))) windowsEncoder.Save(windowsOutput);
            overlay.ApplyTheme(ModeColors.Default);
            var overlayTabs = new TabControl();
            var readOverlayPalette = AddOverlaySettingsTab(overlayTabs);
            var overlayPanel = (StackPanel)((ScrollViewer)((TabItem)overlayTabs.Items[0]).Content).Content;
            var overlayRadios = overlayPanel.Children.OfType<WrapPanel>().SelectMany(p => p.Children.OfType<RadioButton>()).ToArray();
            if (overlayRadios.Length != 6) throw new InvalidOperationException("Overlay palette options are missing.");
            overlayRadios[3].IsChecked = true;
            if (readOverlayPalette().ColorScheme != "Plum") throw new InvalidOperationException("Overlay palette selection failed.");
            overlayRadios[^1].IsChecked = true;
            if (readOverlayPalette().ColorScheme != "Windows") throw new InvalidOperationException("Overlay Match Windows selection failed.");
            var paletteRoundTrip = System.Text.Json.JsonSerializer.Deserialize<UserSettings>(System.Text.Json.JsonSerializer.Serialize(
                new UserSettings { Overlay = readOverlayPalette(), Taskbar = new() { ColorScheme = "Emerald" } }))!;
            if (paletteRoundTrip.Overlay.ColorScheme != "Windows" || paletteRoundTrip.Taskbar.ColorScheme != "Emerald")
                throw new InvalidOperationException("Separate mode palette preferences did not survive serialization.");
            var customOverlay = new OverlaySettings { AlwaysOnTop = true, OpacityPercent = 55, VisibleFields = new() { ["connection"] = true, ["tracking1"] = true } };
            overlay.ApplySettings(customOverlay, OverlayFields());
            if (!overlay.Topmost || overlay.Opacity != 0.55 || overlay.StationGrid.Columns.Count != 5)
                throw new InvalidOperationException("Overlay settings were not applied.");
            var roundTrip = System.Text.Json.JsonSerializer.Deserialize<UserSettings>(System.Text.Json.JsonSerializer.Serialize(new UserSettings { Overlay = customOverlay }))!;
            if (!roundTrip.Overlay.AlwaysOnTop || roundTrip.Overlay.OpacityPercent != 55 || !roundTrip.Overlay.IsVisible("tracking1"))
                throw new InvalidOperationException("Overlay preferences did not survive serialization.");
            FilterBox.Text = "WELD";
            if (overlay.StationGrid.Items.Count != 1) throw new InvalidOperationException("Overlay did not follow the station filter.");
            FilterBox.Text = "";
            var overlayOperation = StartOperationAsync(t => Task.Delay(30000, t));
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (overlay.RefreshControl.IsEnabled || overlay.SettingsControl.IsEnabled) throw new InvalidOperationException("Overlay operation controls did not disable.");
            Stop_Click(this, new RoutedEventArgs()); await overlayOperation;
            overlay.Close();
            if (!IsVisible || overlay is not null) throw new InvalidOperationException("Closing overlay did not restore the main window.");
            Overlay_Click(this, new RoutedEventArgs());
            if (overlay is null || !overlay.IsVisible) throw new InvalidOperationException("Overlay did not reopen.");
            overlay.Close();
            await SmokeTaskbarAsync(output);
            Width = 1120; Height = 720;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            SaveVisual(Path.Combine(output, "compact.png"));
            for (int i = 0; i < 45; i++)
            {
                var row = new Core.PlcRow(new($"10.20.0.{i + 1}", 1, 14, "UI test", "1.0", (uint)i), "");
                row.Apply([new($"TEST_{i:00}",null), new("2026-09-21",null), new("2026-09-01",null), new("Off",null)]);
                Devices.Add(row);
            }
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (view.Cast<object>().Count() != 50) throw new InvalidOperationException("50-row UI load failed.");
            var pending = StartOperationAsync(t => Task.Delay(30000, t));
            if (!busy || !StopButton.IsEnabled) throw new InvalidOperationException("Operation controls did not update.");
            Stop_Click(this, new RoutedEventArgs());
            await pending;
            if (busy || operation is not null) throw new InvalidOperationException("Stop failed to release the running operation.");
            File.WriteAllText(Path.Combine(output, "smoke-test.txt"), $"PASS: native library {Core.TagReader.Version}, embedded logo, WPF startup, inline edit/cancel and pending-save exclusion, IP/name filters, Echo identification controls, auto-refresh interval/start/stop, test sample states, default and compact layouts, 50-row UI load, Stop/cancellation controls, overlay defaults/live view/filter/settings/serialization/busy controls/restore/reopen, taskbar owner HWND/actual screen visibility/live values/timer/filter independence/empty state/double-click/settings/cleanup, independent overlay/taskbar color palettes and Windows color matching.\n");
            Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            Directory.CreateDirectory("artifacts");
            File.WriteAllText("artifacts/smoke-test.txt", ex.ToString());
            Application.Current.Shutdown(1);
        }
    }

    // Test-only data for the explicit --smoke-test run. It is not reachable from the normal UI.
    private void PopulateSmokeRows()
    {
        Devices.Clear();
        var names = new[] { "LOAD_STATION", "WELD_CELL_02", "TEST_STATION", "PACK_OUT", "Unavailable" };
        var ips = new[] { "10.10.10.20", "10.10.10.25", "10.10.9.12", "192.168.9.30", "192.168.9.42" };
        for (int i = 0; i < names.Length; i++)
        {
            var row = new Core.PlcRow(new(ips[i], 1, i == 4 ? (ushort)12 : (ushort)14, i == 4 ? "1756-EN2T" : "CompactLogix / test", "35.11", (uint)(0x1000 + i)), "");
            if (i < 4) row.Apply([new(names[i], null), new("2026-09-01", null), new("2026-08-28", null), new(i == 2 ? "ON" : "Off", null)]);
            else row.Mark("Route needed", "Test fixture requires a controller route.");
            if (i == 3) row.Mark("Not responding", "Test fixture marks prior values stale.");
            Devices.Add(row);
        }
        UpdateCounts(); DevicesGrid.SelectedIndex = 2;
    }

    private void SaveVisual(string path)
    {
        UpdateLayout();
        var visual = (FrameworkElement)Content;
        var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }
}




