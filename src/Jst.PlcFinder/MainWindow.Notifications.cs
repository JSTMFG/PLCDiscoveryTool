using System.Diagnostics;
using System.IO;
using Jst.PlcFinder.Core;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Jst.PlcFinder;

public partial class MainWindow
{
    private readonly OfflineTransitionTracker offlineTransitions = new();

    private bool ShowNewDevices(IReadOnlyList<PlcRow> rows)
    {
        if (rows.Count == 0 || smokeTest || closing) return false;
        try
        {
            var details = string.Join("\n", rows.Select(row =>
            {
                string name = row.Fields[0].Value ?? row.Identity.Product;
                return $"{name} · {row.IpDisplay}";
            }));
            new ToastContentBuilder()
                .AddText(rows.Count == 1 ? "New device found" : $"{rows.Count} new devices found")
                .AddText("Automatic network scan completed. Expand for all devices.")
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
        if (rows.Count == 0 || smokeTest || closing) return false;
        try
        {
            var details = rows.Take(5).Select(row =>
                $"{(string.IsNullOrWhiteSpace(row.Fields[0].Value) ? row.IpDisplay : row.Fields[0].Value + " · " + row.IpDisplay)} stopped responding");
            var message = string.Join("\n", details);
            if (rows.Count > 5) message += $"\nAnd {rows.Count - 5} more devices";
            new ToastContentBuilder()
                .AddText(rows.Count == 1 ? "PLC offline" : $"{rows.Count} PLCs offline")
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

    private bool ShowDeveloperChanges(IReadOnlyList<(PlcRow Row, IReadOnlyList<DeveloperFieldChange> Changes)> changedRows)
    {
        if (smokeTest && !notificationSmokeTest || closing) return false;
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
        bool sent = ShowDeveloperChanges([(row,
            [new DeveloperFieldChange("Developer", "Alice", "Bob"),
             new DeveloperFieldChange("Job number", "100", "200")])]);
        Directory.CreateDirectory("artifacts");
        File.WriteAllText("artifacts/notification-smoke-test.txt", sent
            ? "PASS: Windows accepted a grouped developer-change notification.\n"
            : "FAIL: Windows did not accept the developer-change notification.\n");
        System.Windows.Application.Current.Shutdown(sent ? 0 : 1);
    }
}
