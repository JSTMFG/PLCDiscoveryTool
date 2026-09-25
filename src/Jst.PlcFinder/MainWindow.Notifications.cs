using System.Diagnostics;
using System.IO;
using Jst.PlcFinder.Core;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Jst.PlcFinder;

public partial class MainWindow
{
    private readonly ConnectivityTransitionTracker connectivityTransitions = new();
    private NotificationSettings CurrentNotificationSettings => saved.Notifications ??= new();

    private void NotifyConnectivityChanges(IEnumerable<PlcRow> rows)
    {
        var offline = new List<PlcRow>();
        var recovered = new List<PlcRow>();
        foreach (var row in rows)
        {
            switch (connectivityTransitions.Observe(row.Key, row.Status))
            {
                case ConnectivityTransition.StoppedResponding: offline.Add(row); break;
                case ConnectivityTransition.RespondingAgain: recovered.Add(row); break;
            }
        }
        ShowOfflineDevices(offline);
        ShowRecoveredDevices(recovered);
    }

    private bool ShowNewDevices(IReadOnlyList<PlcRow> rows)
    {
        if (rows.Count == 0 || !CurrentNotificationSettings.NewDevices || smokeTest && !notificationSmokeTest || closing) return false;
        try
        {
            var details = string.Join("\n", rows.Take(5).Select(row =>
            {
                string name = row.Fields[0].Value ?? row.Identity.Product;
                return $"{name} · {row.IpDisplay}";
            }));
            if (rows.Count > 5) details += $"\nAnd {rows.Count - 5} more devices";
            new ToastContentBuilder()
                .AddText(rows.Count == 1 ? "New device found" : $"{rows.Count} new devices found")
                .AddText("Automatic network scan completed. Expand for details.")
                .AddVisualChild(new AdaptiveGroup
                {
                    Children =
                    {
                        new AdaptiveSubgroup
                        {
                            Children = { new AdaptiveText { Text = details, HintWrap = true } }
                        }
                    }
                })
                .Show();
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine("Could not show new device notification: " + ex);
            return false;
        }
    }

    private bool ShowOfflineDevices(IReadOnlyList<PlcRow> rows)
    {
        if (rows.Count == 0 || !CurrentNotificationSettings.StoppedResponding || smokeTest && !notificationSmokeTest || closing) return false;
        try
        {
            var details = rows.Take(5).Select(row =>
                $"{(string.IsNullOrWhiteSpace(row.Fields[0].Value) ? row.IpDisplay : row.Fields[0].Value + " · " + row.IpDisplay)} stopped responding");
            var message = string.Join("\n", details);
            if (rows.Count > 5) message += $"\nAnd {rows.Count - 5} more devices";
            new ToastContentBuilder()
                .AddText(rows.Count == 1 ? "PLC not responding" : $"{rows.Count} PLCs not responding")
                .AddText(message)
                .Show();
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine("Could not show offline device notification: " + ex);
            return false;
        }
    }

    private bool ShowRecoveredDevices(IReadOnlyList<PlcRow> rows)
    {
        if (rows.Count == 0 || !CurrentNotificationSettings.RespondingAgain || smokeTest && !notificationSmokeTest || closing) return false;
        try
        {
            var details = rows.Take(5).Select(row =>
                $"{(string.IsNullOrWhiteSpace(row.Fields[0].Value) ? row.IpDisplay : row.Fields[0].Value + " · " + row.IpDisplay)} · {row.Status}");
            var message = string.Join("\n", details);
            if (rows.Count > 5) message += $"\nAnd {rows.Count - 5} more devices";
            new ToastContentBuilder()
                .AddText(rows.Count == 1 ? "PLC responding again" : $"{rows.Count} PLCs responding again")
                .AddText(message)
                .Show();
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine("Could not show recovery notification: " + ex);
            return false;
        }
    }

    private bool ShowDeveloperChanges(IReadOnlyList<(PlcRow Row, IReadOnlyList<DeveloperFieldChange> Changes)> changedRows)
    {
        if (!CurrentNotificationSettings.DeveloperChanges || smokeTest && !notificationSmokeTest || closing) return false;
        try
        {
            int fieldCount = changedRows.Sum(entry => entry.Changes.Count);
            new ToastContentBuilder()
                .AddText("Developer fields changed")
                .AddText($"{fieldCount} field{(fieldCount == 1 ? "" : "s")} changed on {changedRows.Count} PLC{(changedRows.Count == 1 ? "" : "s")}. Expand for details.")
                .AddVisualChild(new AdaptiveGroup
                {
                    Children =
                    {
                        new AdaptiveSubgroup
                        {
                            Children = { new AdaptiveText { Text = DeveloperChangeNotice.Format(changedRows), HintWrap = true } }
                        }
                    }
                })
                .Show();
            return true;
        }
        catch (Exception ex)
        {
            // Notifications should not interrupt a successful PLC refresh.
            Trace.WriteLine("Could not show developer change notification: " + ex);
            return false;
        }
    }

    private void SmokeNotification()
    {
        var row = new PlcRow(new("10.0.0.20", 1, 14, "TEST PLC", "1", 1), "");
        bool found = ShowNewDevices([row]);
        row.Mark("Not responding", "Test outage");
        bool offline = ShowOfflineDevices([row]);
        row.Apply([new("TEST PLC", null), new("1", null), new("1", null), new("OFF", null)]);
        bool recovered = ShowRecoveredDevices([row]);
        bool changes = ShowDeveloperChanges([(row,
            [new DeveloperFieldChange("Developer", "Alice", "Bob"),
             new DeveloperFieldChange("Job number", "100", "200")])]);
        bool sent = found && offline && recovered && changes;
        Directory.CreateDirectory("artifacts");
        File.WriteAllText("artifacts/notification-smoke-test.txt", sent
            ? "PASS: Windows accepted new-device, offline, recovery, and developer-change notifications.\n"
            : $"FAIL: Notification acceptance: new={found}, offline={offline}, recovery={recovered}, developer={changes}.\n");
        System.Windows.Application.Current.Shutdown(sent ? 0 : 1);
    }
}
