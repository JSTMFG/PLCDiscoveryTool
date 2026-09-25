using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using System.Diagnostics;
using Jst.PlcFinder.Core;

namespace Jst.PlcFinder;

public partial class MainWindow : Window
{
    public ObservableCollection<SearchRange> Ranges { get; } = [];
    public ObservableCollection<PlcRow> Devices { get; } = [];
    private readonly Discovery discovery = new();
    private readonly BackplaneDiscovery backplanes = new();
    private readonly TagReader reader = new();
    private readonly SemaphoreSlim readSlots = new(4);
    private readonly DispatcherTimer monitor = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer autoScan = new() { Interval = TimeSpan.FromSeconds(300) };
    private readonly bool smokeTest;
    private readonly bool notificationSmokeTest;
    private CancellationTokenSource? operation;
    private Task? activeTask;
    private bool busy, closing, nativeReady;
    private readonly List<IpInterval> scannedRanges = [];
    internal bool IsShutdownInProgress => closing;
    private readonly UserSettings saved;
    private ICollectionView view = null!;
    private record Adapter(string Label, string Ip, int Prefix)
    {
        public IPAddress? Address => Ip.Length == 0 ? null : IPAddress.Parse(Ip);
    }

    public MainWindow(bool smoke = false, bool notificationSmoke = false)
    {
        smokeTest = smoke;
        notificationSmokeTest = notificationSmoke;
        saved = smoke ? new() : SettingsStore.Load();
        InitializeComponent();
        ApplyAppearance();
        InitializeModeThemeTracking();
        InitializeTaskbarCoordination();
        RangeGrid.ItemsSource = Ranges;
        Ranges.CollectionChanged += (_, args) =>
        {
            if (args.OldItems is not null) foreach (SearchRange r in args.OldItems) r.PropertyChanged -= RangeChanged;
            if (args.NewItems is not null) foreach (SearchRange r in args.NewItems) r.PropertyChanged += RangeChanged;
            UpdateRangeSummary();
        };
        foreach (var r in saved.Ranges ?? UserSettings.Defaults()) Ranges.Add(r);
        view = CollectionViewSource.GetDefaultView(Devices);
        view.Filter = o => o is PlcRow r && ($"{r.Ip} {r.Station}").Contains(FilterBox.Text, StringComparison.OrdinalIgnoreCase);
        view.SortDescriptions.Add(new(nameof(PlcRow.IpSort), ListSortDirection.Ascending));
        DevicesGrid.ItemsSource = view;
        InitializeStationColumns();
        DevicesGrid.Columns.FirstOrDefault(c => c.SortMemberPath == nameof(PlcRow.IpSort))!.SortDirection = ListSortDirection.Ascending;
        var adapters = new List<Adapter> { new("Automatic (Windows routing)", "", 0) };
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            foreach (var address in nic.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                adapters.Add(new($"{nic.Name} · {address.Address}", address.Address.ToString(), address.PrefixLength));
        AdaptersBox.ItemsSource = adapters;
        AdaptersBox.SelectedItem = adapters.FirstOrDefault(a => a.Ip == saved.AdapterIp) ?? adapters[0];
        RoutesBox.Text = saved.Routes; ScopeBox.Text = saved.ProgramScope;
        EchoAddressBox.Text = saved.EchoAgentAddress;
        SoftwareFormatBox.ItemsSource = new[] { "STRING", "DINT", "LINT", "INT", "REAL" };
        GemFormatBox.ItemsSource = new[] { "STRING", "DINT", "LINT", "INT", "REAL" };
        SoftwareFormatBox.SelectedItem = saved.SoftwareFormat; GemFormatBox.SelectedItem = saved.GemFormat;
        InvertBox.IsChecked = saved.InvertSimulation; ThresholdBox.Text = saved.LargeScanThreshold.ToString();
        monitor.Tick += async (_, _) =>
        {
            if (!closing && IsEnabled && taskbarWidget is null && overlay?.IsEnabled != false && nativeReady && !busy && !trackingEditActive && !trackingSavePending && MonitorBox.IsChecked == true && Devices.Count > 0)
                await StartOperationAsync(t => RefreshAsync(true, t, automatic: true));
        };
        autoScan.Tick += async (_, _) =>
        {
            if (!closing && nativeReady && !busy && !trackingEditActive && !trackingSavePending)
                await ScanSelectedRangesAsync(true);
        };
        saved.AutoRefreshSeconds = Math.Clamp(saved.AutoRefreshSeconds, 2, 3600);
        monitor.Interval = TimeSpan.FromSeconds(saved.AutoRefreshSeconds);
        RefreshIntervalBox.Text = saved.AutoRefreshSeconds.ToString();
        MonitorBox.IsChecked = saved.AutoRefreshEnabled;
        Closing += Window_Closing;
        Loaded += async (_, _) =>
        {
            try { TagReader.VerifyNativeLibrary(); nativeReady = true; SetBusy(); }
            catch (Exception ex) { StatusText.Text = "PLC library failed to load: " + ex.Message; ScanButton.IsEnabled = RefreshButton.IsEnabled = false; }
            if (notificationSmokeTest) SmokeNotification();
            else if (smoke) await SmokeTestAsync();
            else
            {
                if (saved.StartupMode == "Taskbar") EnterTaskbarMode();
                else if (saved.StartupMode == "Overlay") Overlay_Click(this, new RoutedEventArgs());
                if (nativeReady && saved.ScanOnStartup) await ScanSelectedRangesAsync(true);
                ApplyAutoScanSettings();
            }
        };
        UpdateRangeSummary();
    }

    private void RangeChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateRangeSummary();
        if (e.PropertyName != nameof(SearchRange.Enabled) || sender is not SearchRange { Enabled: false } disabled)
            return;

        IpInterval removed;
        try { removed = IpRanges.Parse(disabled.Range); }
        catch (FormatException) { return; }

        var retained = new List<IpInterval>();
        foreach (var range in Ranges.Where(r => r.Enabled))
        {
            try { retained.Add(IpRanges.Parse(range.Range)); }
            catch (FormatException) { }
        }
        foreach (var row in Devices.Where(r => r.IpSort >= removed.First && r.IpSort <= removed.Last &&
                     !retained.Any(range => r.IpSort >= range.First && r.IpSort <= range.Last)).ToList())
            Devices.Remove(row);
        UpdateCounts();
    }
    private void NetworksToggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = NetworksSidebar.Visibility != Visibility.Visible;
        NetworksSidebar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        NetworksColumn.Width = new GridLength(show ? 310 : 0);
        NetworksGapColumn.Width = new GridLength(show ? 18 : 0);
        string label = show ? "Hide search networks" : "Show search networks";
        NetworksToggleButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(NetworksToggleButton, label);
    }
    private void Range_EditEnding(object sender, DataGridCellEditEndingEventArgs e) => Dispatcher.BeginInvoke(UpdateRangeSummary);
    private void UpdateRangeSummary()
    {
        if (RangeSummary is null) return;
        try
        {
            var intervals = IpRanges.Normalize(Ranges.Where(r => r.Enabled).Select(r => r.Range));
            ulong count = intervals.Aggregate(0UL, (n, r) => n + r.Count);
            RangeSummary.Text = $"{Ranges.Count(r => r.Enabled)} ranges selected · {count:N0} addresses";
            DurationSummary.Text = count == 0 ? "Enable a range to start a scan." : $"Discovery estimate: about {Math.Ceiling(Math.Ceiling((double)count / ScanPipeline.DiscoveryConcurrency) * Discovery.ProbeBudgetMs / 1000):N0} sec if targets time out. Tag reads may add time.";
            ScanButton.IsEnabled = !busy && nativeReady && count > 0;
        }
        catch (FormatException ex) { RangeSummary.Text = ex.Message; DurationSummary.Text = "Edit the range to continue."; ScanButton.IsEnabled = false; }
    }

    private void SetScanScope(IReadOnlyCollection<IpInterval> ranges)
    {
        scannedRanges.Clear();
        scannedRanges.AddRange(ranges);
        foreach (var row in Devices.Where(row => !scannedRanges.Any(range =>
                     row.IpSort >= range.First && row.IpSort <= range.Last)).ToArray())
            Devices.Remove(row);
        UpdateCounts();
    }

    private void AddRange_Click(object sender, RoutedEventArgs e) { var r = new SearchRange { Range = "10.10.10.X" }; Ranges.Add(r); RangeGrid.SelectedItem = r; }
    private void RemoveRange_Click(object sender, RoutedEventArgs e) { if (RangeGrid.SelectedItem is SearchRange r) Ranges.Remove(r); }
    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveRange(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveRange(1);
    private void MoveRange(int delta) { int i = RangeGrid.SelectedIndex; if (i >= 0 && i + delta >= 0 && i + delta < Ranges.Count) Ranges.Move(i, i + delta); }
    private void ClearRanges_Click(object sender, RoutedEventArgs e) => Ranges.Clear();
    private void DefaultRanges_Click(object sender, RoutedEventArgs e) { Ranges.Clear(); foreach (var r in UserSettings.Defaults()) Ranges.Add(r); }
    private void LocalRange_Click(object sender, RoutedEventArgs e)
    {
        if (AdaptersBox.SelectedItem is not Adapter { Ip.Length: > 0 } adapter) { ShowInfo("Choose a specific network adapter first."); return; }
        string range = $"{adapter.Ip}/{adapter.Prefix}";
        Ranges.Add(new() { Enabled = true, Range = range });
    }
    private void Adapter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (AdapterInfo is not null && AdaptersBox.SelectedItem is Adapter a)
            AdapterInfo.Text = a.Ip.Length > 0 ? $"{a.Ip}/{a.Prefix} · routed subnets must be reachable" : "Uses the PC's existing routes to each network.";
    }

    internal static string[] ParseRoutes(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [""];
        var routes = text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (routes.Length == 0) throw new FormatException("Enter a route such as 1,0 or leave it blank for direct access.");
        foreach (var route in routes)
        {
            var parts = route.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || parts.Length % 2 != 0 || parts.Where((_, i) => i % 2 == 0).Any(p => !byte.TryParse(p, out var n) || n > 15) || parts.Where((_, i) => i % 2 == 1).Any(p => !byte.TryParse(p, out _)) )
                throw new FormatException("Routes use port,slot pairs. Ports are 0–15 and slots are 0–255, for example 1,0 or 1,16. Separate routes with semicolons.");
        }
        return routes.Select(r => string.Join(',', r.Split(',').Select(p => byte.Parse(p.Trim())))).Distinct().ToArray();
    }
    private TagSettings ReadTagSettings()
    {
        string scope = ScopeBox.Text.Trim();
        if (scope.Length > 0 && !Regex.IsMatch(scope, @"^[A-Za-z_][A-Za-z0-9_]{0,39}$")) throw new FormatException("Program scope must be a PLC program name, without Program: or punctuation.");
        return new(scope, SoftwareFormatBox.SelectedItem as string ?? "STRING", GemFormatBox.SelectedItem as string ?? "STRING", InvertBox.IsChecked == true);
    }
    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanSelectedRangesAsync(false);

    private async Task ScanSelectedRangesAsync(bool automatic)
    {
        if (busy) return;
        try
        {
            RangeGrid.CommitEdit(DataGridEditingUnit.Cell, true); RangeGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var ranges = IpRanges.Normalize(Ranges.Where(r => r.Enabled).Select(r => r.Range));
            ulong count = ranges.Aggregate(0UL, (n, r) => n + r.Count);
            if (count == 0) { if (!automatic) ShowInfo("Enable at least one search range."); return; }
            if (!int.TryParse(ThresholdBox.Text, out int threshold) || threshold < 1 || threshold > 65536) throw new FormatException("Use a confirmation threshold between 1 and 65,536 addresses.");
            if (count > 65536) throw new FormatException("Split this scan into smaller selections of at most 65,536 addresses.");
            if (count > (ulong)threshold)
            {
                if (automatic) { StatusText.Text = $"Automatic scan skipped · {count:N0} addresses exceed the {threshold:N0} address threshold"; return; }
                if (MessageBox.Show(this, $"Search {count:N0} unique addresses across the enabled ranges? This may take a long time.", "Large scan", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            }
            var routes = ParseRoutes(RoutesBox.Text); var settings = ReadTagSettings();
            var local = (AdaptersBox.SelectedItem as Adapter)?.Address;
            var knownDevices = automatic ? ScanChanges.KnownNetworkDevices(Devices) : null;
            await StartOperationAsync(async token =>
            {
                SetScanScope(ranges);
                foreach (var row in Devices.Where(r => ranges.Any(x => r.IpSort >= x.First && r.IpSort <= x.Last))) row.Mark("Checking", "Waiting for this scan");
                var elapsed = Stopwatch.StartNew();
                var seen = new HashSet<string>();
                var targetRows = new HashSet<string>();
                bool reporting = true;
                var progress = new Progress<Core.ScanProgress>(p =>
                {
                    if (!reporting) return;
                    ScanProgress.Value = p.CheckedAddresses * 100.0 / count;
                    StatusText.Text = $"Searching · {p.CheckedAddresses:N0} / {count:N0} addresses · {p.ReadControllers:N0} / {p.QueuedControllers:N0} stations read";
                });
                try
                {
                    await ScanPipeline.RunAsync(IpRanges.Expand(ranges),
                    (ip, ct) => discovery.ProbeAsync(ip, local, ct),
                    async (identity, ct) =>
                    {
                        var routesForDevice = await Dispatcher.InvokeAsync(() =>
                        {
                            seen.Add(identity.Ip);
                            return routes.Concat(Devices.Where(r => r.Ip == identity.Ip).Select(r => r.Route)).Distinct().ToArray();
                        });
                        return await PrepareIdentityAsync(identity, routesForDevice, targetRows, ct);
                    }, (row, ct) => ReadRowAsync(row, settings, ct), progress, token);
                }
                finally { reporting = false; }
                ScanProgress.Value = 100;
                foreach (var row in Devices.Where(r => ranges.Any(x => r.IpSort >= x.First && r.IpSort <= x.Last) && !seen.Contains(r.Ip))) row.Mark("Not responding", "No EtherNet/IP reply during this scan");
                await RefreshEchoInventoryAsync(token, true);
                if (automatic && taskbarWidget is not null && knownDevices is not null)
                    ShowNewDevices(ScanChanges.NewNetworkDevices(knownDevices,
                        Devices.Where(row => seen.Contains(row.Ip))));
                StatusText.Text = $"Scan complete in {elapsed.Elapsed.TotalSeconds:N1} sec · {count:N0} addresses checked · {seen.Count} Allen-Bradley endpoints found";
                AutoSizeStationColumns();
            });
        }
        catch (Exception ex) { if (automatic) StatusText.Text = "Automatic scan skipped: " + ex.Message; else ShowInfo(ex.Message); }
    }

    private async Task HandleIdentityAsync(DeviceIdentity identity, string[] routes, TagSettings settings, HashSet<string> processed, CancellationToken token)
    {
        foreach (var row in await PrepareIdentityAsync(identity, routes, processed, token))
            await ReadRowAsync(row, settings, token);
    }

    private async Task<IReadOnlyList<PlcRow>> PrepareIdentityAsync(DeviceIdentity identity, string[] routes, HashSet<string> processed, CancellationToken token)
    {
        // A 1756 Ethernet module identifies itself as a bridge at its IP address.
        // Prefer its reported controllers over any saved manual routes; the latter are
        // still used as a fallback for nonstandard chassis paths.
        if (identity.IsBridge)
        {
            var controllers = await backplanes.DiscoverAsync(identity, token);
            if (controllers.Count > 0)
            {
                var rows = new List<PlcRow>();
                foreach (var controller in controllers)
                    rows.AddRange(await PrepareIdentityAsync(controller.Identity, [controller.Route], processed, token));
                await Dispatcher.InvokeAsync(() =>
                {
                    var bridgeRow = Devices.FirstOrDefault(r => r.Ip == identity.Ip && r.Route.Length == 0 && r.Identity.IsBridge);
                    if (bridgeRow is not null) Devices.Remove(bridgeRow);
                    UpdateCounts();
                });
                return rows;
            }
        }
        var pending = new List<PlcRow>();
        foreach (string route in routes)
        {
            token.ThrowIfCancellationRequested();
            PlcRow? row = null;
            await Dispatcher.InvokeAsync(() =>
            {
                string key = identity.Ip + "|" + route;
                if (!processed.Add(key)) return;
                row = Devices.FirstOrDefault(r => r.Key == key);
                if (row is not null && row.Identity.Serial != identity.Serial) { Devices.Remove(row); row = null; }
                if (row is null) { row = new(identity, route); Devices.Add(row); }
                else row.ConfirmIdentity(identity);
                UpdateCounts();
            });
            if (row is null) continue;
            if (!identity.IsController && !identity.IsBridge) { await Dispatcher.InvokeAsync(() => row.Mark("Unsupported", "AB device is not a Logix controller or Ethernet bridge.")); continue; }
            if (identity.IsBridge && route.Length == 0) { await Dispatcher.InvokeAsync(() => row.Mark("No controller found", "No Logix controller responded in backplane slots 0–16. Enter a route in Advanced if this chassis uses a nonstandard path.")); continue; }
            if (identity.IsController && (identity.Product.Contains("MicroLogix", StringComparison.OrdinalIgnoreCase) || identity.Product.Contains("SLC", StringComparison.OrdinalIgnoreCase) || identity.Product.Contains("PLC-5", StringComparison.OrdinalIgnoreCase) || identity.Product.Contains("Micro8", StringComparison.OrdinalIgnoreCase)))
            { await Dispatcher.InvokeAsync(() => row.Mark("Unsupported", "This release reads CompactLogix/ControlLogix symbolic tags.")); continue; }
            pending.Add(row);
        }
        return pending;
    }

    private async Task ReadRowAsync(PlcRow row, TagSettings settings, CancellationToken token,
        Action<PlcRow, IReadOnlyList<DeveloperFieldChange>>? recordTrackingChanges = null)
    {
        var selectedIp = await Dispatcher.InvokeAsync(() => (AdaptersBox.SelectedItem as Adapter)?.Ip ?? "");
        if (selectedIp.Length > 0)
        {
            // Native libplctag follows OS routes. Prevent associating a tag response from
            // another interface with a discovery result on overlapping industrial subnets.
            using var routeCheck = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            routeCheck.Connect(IPAddress.Parse(row.Ip), 44818); // UDP connect selects a route; sends no packet.
            if ((routeCheck.LocalEndPoint as IPEndPoint)?.Address.ToString() != selectedIp)
            {
                await Dispatcher.InvokeAsync(() => row.Mark("Route mismatch", "Windows routes tag reads through a different local IP. Choose Automatic or correct the PC network route, then refresh."));
                return;
            }
        }
        await readSlots.WaitAsync(token);
        try
        {
            var values = await reader.ReadAsync(row, settings, token);
            var tracking = await reader.ReadDeveloperTrackingAsync(row, TrackingSettings, token);
            await Dispatcher.InvokeAsync(() =>
            {
                var changes = recordTrackingChanges is null ? [] : row.TrackingChanges(tracking, TrackingSettings.Labels);
                row.ApplyTracking(tracking);
                if (changes.Count > 0) recordTrackingChanges?.Invoke(row, changes);
            });
            await Dispatcher.InvokeAsync(() => { row.Apply(values); UpdateCounts(); view.Refresh(); });
        }
        finally { readSlots.Release(); }
    }

    private async void Broadcast_Click(object sender, RoutedEventArgs e)
    {
        if (AdaptersBox.SelectedItem is not Adapter { Ip.Length: > 0 } adapter) { ShowInfo("Choose a specific network adapter for local discovery."); return; }
        try
        {
            var routes = ParseRoutes(RoutesBox.Text); var settings = ReadTagSettings();
            uint mask = adapter.Prefix == 0 ? 0 : uint.MaxValue << (32 - adapter.Prefix);
            var broadcast = IpRanges.Address(IpRanges.Number(adapter.Ip) | ~mask);
            await StartOperationAsync(async token =>
            {
                SetScanScope([IpRanges.Parse($"{adapter.Ip}/{adapter.Prefix}")]);
                StatusText.Text = "Discovering the selected local network…"; ScanProgress.IsIndeterminate = true;
                var found = await discovery.BroadcastAsync(adapter.Address!, broadcast, token);
                var processed = new HashSet<string>();
                foreach (var id in found.Where(d => d.IsAllenBradley)) await HandleIdentityAsync(id, routes, settings, processed, token);
                await RefreshEchoInventoryAsync(token, true);
                StatusText.Text = $"Local discovery complete · {found.Count(d => d.IsAllenBradley)} Allen-Bradley endpoints found";
                AutoSizeStationColumns();
            });
        }
        catch (Exception ex) { ShowInfo(ex.Message); }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await StartOperationAsync(t => RefreshAsync(true, t));
    }
    private async Task RefreshAsync(bool force, CancellationToken token, bool automatic = false)
    {
        var settings = ReadTagSettings();
        var local = (AdaptersBox.SelectedItem as Adapter)?.Address;
        var rows = Devices.Where(r => r.Status != "Unsupported" && r.Status != "Route needed" && (force || r.NextPoll <= DateTimeOffset.UtcNow)).ToArray();
        if (rows.Length == 0) return;
        bool taskbarRefresh = taskbarWidget is not null;
        if (taskbarRefresh)
            foreach (var row in rows.Where(row => row.Status == "Online")) offlineTransitions.Observe(row.Key, row.Status);
        var trackingChanges = new List<(PlcRow Row, IReadOnlyList<DeveloperFieldChange> Changes)>();
        StatusText.Text = $"{(automatic ? "Auto-refreshing" : "Refreshing")} {rows.Length} stations…";
        await Parallel.ForEachAsync(rows, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (row, ct) =>
        {
            var segments = row.Route.Split(',');
            var identity = segments is ["1", var slotText] && byte.TryParse(slotText, out var slot)
                ? await BackplaneDiscovery.ProbeSlotAsync(row.Ip, slot, ct)
                : await discovery.ProbeAsync(row.Ip, local, ct);
            if (identity is null) { await Dispatcher.InvokeAsync(() => row.Mark("Not responding", "No current EtherNet/IP reply")); return; }
            if (!identity.IsAllenBradley || identity.Serial != row.Identity.Serial) { await Dispatcher.InvokeAsync(() => row.Mark("Device changed", "A different device answered at this address. Scan again.")); return; }
            await Dispatcher.InvokeAsync(() => row.ConfirmIdentity(identity));
            await ReadRowAsync(row, settings, ct, (changedRow, changes) => trackingChanges.Add((changedRow, changes)));
        });
        if (trackingChanges.Count > 0) ShowDeveloperChanges(trackingChanges);
        if (taskbarRefresh && taskbarWidget is not null)
        {
            var wentOffline = rows.Where(row => offlineTransitions.Observe(row.Key, row.Status)).ToArray();
            ShowOfflineDevices(wentOffline);
        }
        StatusText.Text = (automatic ? "Auto-refresh" : "Refresh") + " complete · " + DateTime.Now.ToString("HH:mm:ss");
        AutoSizeStationColumns();
    }

    private async Task StartOperationAsync(Func<CancellationToken, Task> work, bool isTrackingSave = false)
    {
        if (busy || closing || (trackingSavePending && !isTrackingSave)) return;
        busy = true; operation = new(); SetBusy();
        try { activeTask = work(operation.Token); await activeTask; }
        catch (OperationCanceledException)
        {
            reader.Dispose();
            foreach (var row in Devices.Where(r => r.Status is "Checking" or "Found")) row.Mark("Stale", "Scan stopped before this device was checked");
            StatusText.Text = "Stopped · completed results retained";
        }
        catch (Exception ex)
        {
            foreach (var row in Devices.Where(r => r.Status is "Checking" or "Found")) row.Mark("Stale", ex.Message);
            StatusText.Text = "Operation failed: " + ex.Message;
        }
        finally
        {
            activeTask = null; operation.Dispose(); operation = null; busy = false;
            ScanProgress.IsIndeterminate = false; SetBusy(); UpdateCounts();
        }
    }
    private void SetBusy()
    {
        SettingsButton.IsEnabled = !busy;
        RangeControls.IsEnabled = AdaptersBox.IsEnabled = Advanced.IsEnabled = DiscoverButton.IsEnabled = !busy;
        DevicesGrid.IsReadOnly = busy;
        RefreshButton.IsEnabled = !busy && nativeReady && Devices.Count > 0;
        StopButton.IsEnabled = busy || MonitorBox.IsChecked == true;
        UpdateRangeSummary();
    }
    private void Stop_Click(object sender, RoutedEventArgs e) { MonitorBox.IsChecked = false; operation?.Cancel(); if (!busy) { reader.Dispose(); StatusText.Text = "Monitoring stopped"; StopButton.IsEnabled = false; } }
    private void Monitor_Unchecked(object sender, RoutedEventArgs e)
    {
        monitor.Stop();
        if (!closing) saved.AutoRefreshEnabled = false;
        if (!busy && StopButton is not null) StopButton.IsEnabled = false;
    }
    private async void Monitor_Checked(object sender, RoutedEventArgs e)
    {
        if (RefreshIntervalBox is null || !ApplyRefreshInterval()) { MonitorBox.IsChecked = false; return; }
        saved.AutoRefreshEnabled = true;
        monitor.Start();
        if (StopButton is not null) StopButton.IsEnabled = true;
        if (nativeReady && !busy && !trackingEditActive && !trackingSavePending && Devices.Count > 0)
            await StartOperationAsync(t => RefreshAsync(true, t, automatic: true));
    }
    private void RefreshInterval_LostFocus(object sender, RoutedEventArgs e) => ApplyRefreshInterval();
    private void RefreshInterval_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyRefreshInterval();
        Keyboard.ClearFocus();
        e.Handled = true;
    }
    private bool ApplyRefreshInterval()
    {
        if (!int.TryParse(RefreshIntervalBox.Text, out int seconds) || seconds is < 2 or > 3600)
        {
            RefreshIntervalBox.Text = saved.AutoRefreshSeconds.ToString();
            StatusText.Text = "Auto-refresh interval must be 2–3600 seconds.";
            return false;
        }
        if (seconds == saved.AutoRefreshSeconds) return true;
        saved.AutoRefreshSeconds = seconds;
        bool restart = monitor.IsEnabled;
        monitor.Stop();
        monitor.Interval = TimeSpan.FromSeconds(seconds);
        if (restart) monitor.Start();
        StatusText.Text = $"Auto-refresh interval set to {seconds} sec";
        return true;
    }
    private void Filter_Changed(object sender, TextChangedEventArgs e) => view?.Refresh();
    private void Device_Sorting(object sender, DataGridSortingEventArgs e)
    {
        if (e.Column.SortMemberPath != "IpSort") return;
        e.Handled = true;
        var direction = e.Column.SortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        view.SortDescriptions.Clear(); view.SortDescriptions.Add(new("IpSort", direction)); e.Column.SortDirection = direction;
    }
    private void UpdateCounts()
    {
        CountsText.Text = $"{Devices.Count} endpoints / routes · {Devices.Count(r => r.Status == "Online")} fully readable · {Devices.Count(r => r.Status != "Online")} need attention";
        EmptyState.Visibility = Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.IsEnabled = !busy && nativeReady && Devices.Count > 0;
    }

    private void AutoSizeStationColumns()
    {
        Dispatcher.BeginInvoke(() =>
        {
            DevicesGrid.UpdateLayout();
            foreach (var column in DevicesGrid.Columns.Where(c => c.Visibility == Visibility.Visible))
                column.Width = DataGridLength.SizeToCells;
            DevicesGrid.UpdateLayout();
        }, DispatcherPriority.ContextIdle);
    }
    private void ShowInfo(string message)
    {
        if (closing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            Trace.WriteLine("PLC Finder message suppressed during shutdown: " + message);
            return;
        }
        MessageBox.Show((Window?)overlay ?? this, message, "PLC Finder", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CaptureUiSettings()
    {
        saved.Ranges = Ranges.ToList(); saved.AdapterIp = (AdaptersBox.SelectedItem as Adapter)?.Ip ?? "";
        saved.Routes = RoutesBox.Text; saved.ProgramScope = ScopeBox.Text;
        saved.SoftwareFormat = SoftwareFormatBox.SelectedItem as string ?? "STRING";
        saved.GemFormat = GemFormatBox.SelectedItem as string ?? "STRING"; saved.InvertSimulation = InvertBox.IsChecked == true;
        saved.EchoAgentAddress = EchoAddressBox.Text.Trim(); saved.EchoAgentKey = ""; saved.EchoAgentCertificate = "";
        if (int.TryParse(RefreshIntervalBox.Text, out int refreshSeconds) && refreshSeconds is >= 2 and <= 3600)
            saved.AutoRefreshSeconds = refreshSeconds;
        if (int.TryParse(ThresholdBox.Text, out int threshold)) saved.LargeScanThreshold = threshold;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closing) return;

        // Let WPF finish this first close request. Waiting for an in-flight PLC read here
        // made the user click Close again when a controller did not honor cancellation.
        closing = true;
        overlay?.Close();
        if (taskbarWidget is not null) BroadcastTaskbarMode(false);
        StopTaskbarMode();
        taskbarPeerWatch.Stop();
        try
        {
            monitor.Stop(); MonitorBox.IsChecked = false; operation?.Cancel();
            autoScan.Stop();
            if (activeTask is null) reader.Dispose(); // Active reads unwind on cancellation; never destroy their native handles concurrently.
            if (!smokeTest)
            {
                CaptureUiSettings();
                if (!SettingsStore.Save(saved))
                    Trace.WriteLine("PLC Finder could not save per-user settings during shutdown.");
            }
        }
        catch (Exception ex) { Trace.WriteLine("PLC Finder shutdown cleanup failed: " + ex); }
    }
}


