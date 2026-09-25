using System.Windows;
using System.Windows.Controls;
using Jst.PlcFinder.Core;

namespace Jst.PlcFinder;

public partial class MainWindow
{
    private EchoInventory? echoInventory;

    private EchoAgentClient CreateEchoClient() => new(EchoAddressBox.Text.Trim(), "", "");

    private async Task<bool> RefreshEchoInventoryAsync(CancellationToken token, bool quiet)
    {
        try
        {
            using var client = CreateEchoClient();
            var inventory = await client.GetInventoryAsync(token);
            ApplyEchoInventory(inventory);
            EchoStatusText.Text = $"Connected · {inventory.Controllers.Length} Echo controllers · {inventory.GeneratedAt.ToLocalTime():HH:mm:ss}";
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ApplyEchoInventory(null);
            EchoStatusText.Text = "Unavailable · " + ex.Message;
            if (!quiet) ShowInfo("Could not connect to the Echo reset agent.\n\n" + ex.Message);
            return false;
        }
    }

    private void ApplyEchoInventory(EchoInventory? inventory)
    {
        echoInventory = inventory;
        var inventoryRows = Devices.Where(r => r.IsEchoInventoryOnly).ToArray();
        foreach (var row in Devices.Where(r => !r.IsEchoInventoryOnly))
            row.SetEcho(inventory is null ? null : EchoIdentification.Match(row, inventory.Controllers));
        if (inventory is null)
        {
            foreach (var stale in inventoryRows) Devices.Remove(stale);
        }
        else if (scannedRanges.Count > 0)
        {
            var represented = Devices.Where(r => !r.IsEchoInventoryOnly && r.EchoControllerId.HasValue)
                .Select(r => r.EchoControllerId!.Value).ToHashSet();
            var retained = new HashSet<PlcRow>();
            foreach (var endpoint in EchoIdentification.UniqueDirectEndpoints(inventory.Controllers, scannedRanges)
                         .Where(x => !represented.Contains(x.Controller.Id)))
            {
                var controller = endpoint.Controller;
                var row = inventoryRows.FirstOrDefault(r => r.Ip.Equals(endpoint.Ip, StringComparison.OrdinalIgnoreCase) &&
                                                            r.EchoControllerId == controller.Id);
                if (row is null)
                {
                    var identity = new DeviceIdentity(endpoint.Ip, 1, 14, "FactoryTalk Logix Echo", "—", controller.SerialNumber);
                    row = new PlcRow(identity, "", true);
                    Devices.Add(row);
                }
                row.MarkEchoInventory(controller);
                retained.Add(row);
                represented.Add(controller.Id);
            }
            foreach (var stale in inventoryRows.Where(r => !retained.Contains(r))) Devices.Remove(stale);
        }
        UpdateCounts();
        view?.Refresh();
        SetBusy();
    }

    private async void EchoConnect_Click(object sender, RoutedEventArgs e)
    {
        EchoConnectButton.IsEnabled = false;
        try
        {
            await StartOperationAsync(async token =>
            {
            if (await RefreshEchoInventoryAsync(token, false))
            {
                CaptureUiSettings();
                if (!SettingsStore.Save(saved))
                    EchoStatusText.Text += " · connected, but settings could not be saved";
            }
            });
        }
        finally { EchoConnectButton.IsEnabled = !busy; }
    }

    private async void ResetEchoRow_Click(object sender, RoutedEventArgs e)
    {
        if (busy || sender is not FrameworkElement { DataContext: PlcRow selected }) return;
        DevicesGrid.SelectedItem = selected;
        await StartOperationAsync(token => ResetEchoAsync(selected, token));
    }

    private async Task ResetEchoAsync(PlcRow selected, CancellationToken token)
    {
        if (!await RefreshEchoInventoryAsync(token, false) ||
            selected.EchoControllerId is not Guid controllerId || echoInventory is null) return;
        var controller = echoInventory.Controllers.SingleOrDefault(c => c.Id == controllerId);
        if (controller is null)
        {
            selected.SetEcho(null); SetBusy();
            ShowInfo("This device is no longer present in the live Echo inventory. It was not restarted.");
            return;
        }
        string route = selected.Route.Length == 0 ? "Direct" : selected.Route;
        if (MessageBox.Show(this,
            $"Restart Echo controller '{controller.Name}'?\n\nIP: {selected.Ip}\nRoute: {route}\nChassis: {controller.ChassisName}\nSlot: {controller.Slot}\n\nThe emulated controller will briefly turn off, then the server agent will turn it back on.",
            "Reset Echo PLC", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        {
            using var client = CreateEchoClient();
            var job = await client.StartRestartAsync(controllerId, token);
            while (!job.Complete)
            {
                StatusText.Text = $"Echo reset · {job.Message}";
                await Task.Delay(500, token);
                job = await client.GetJobAsync(job.Id, token);
            }
            if (!job.Success) throw new InvalidOperationException(job.Message);

            StatusText.Text = "Echo reset complete · verifying EtherNet/IP";
            reader.Dispose(); // Discard sessions created before the emulated controller restarted.
            DeviceIdentity? identity = null;
            for (int attempt = 0; attempt < 40 && identity is null; attempt++)
            {
                var segments = selected.Route.Split(',');
                identity = segments is ["1", var slotText] && byte.TryParse(slotText, out var slot)
                    ? await BackplaneDiscovery.ProbeSlotAsync(selected.Ip, slot, token)
                    : await discovery.ProbeAsync(selected.Ip, (AdaptersBox.SelectedItem as Adapter)?.Address, token);
                if (identity is null) await Task.Delay(500, token);
            }
            if (identity is null)
            {
                selected.Mark("Not responding", "Echo reports the controller is on, but EtherNet/IP did not answer after reset.");
                NotifyConnectivityChanges([selected]);
                StatusText.Text = "Echo reset complete · EtherNet/IP is not responding";
                return;
            }
            if (identity.Serial != selected.Identity.Serial)
            {
                selected.Mark("Device changed", "A controller with a different serial number answered after the Echo reset. Scan again before taking another action.");
                StatusText.Text = "Echo reset complete · a different device answered at this address";
                return;
            }
            selected.ConfirmIdentity(identity);
            await ReadRowAsync(selected, ReadTagSettings(), token);
            NotifyConnectivityChanges([selected]);
            StatusText.Text = "Echo reset complete · PLC responding";
            await RefreshEchoInventoryAsync(token, true);
        }
    }
}
