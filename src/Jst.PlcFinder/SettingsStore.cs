using Microsoft.Win32;
using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using Jst.PlcFinder.Core;

namespace Jst.PlcFinder;

public class UserSettings
{
    public List<SearchRange> Ranges { get; set; } = Defaults();
    public string AdapterIp { get; set; } = "";
    public string Routes { get; set; } = "";
    public string ProgramScope { get; set; } = "";
    public string SoftwareFormat { get; set; } = "STRING";
    public string GemFormat { get; set; } = "STRING";
    public bool InvertSimulation { get; set; }
    public bool AutoRefreshEnabled { get; set; }
    public int AutoRefreshSeconds { get; set; } = 5;
    public string StartupMode { get; set; } = "Main";
    public string Appearance { get; set; } = "Light";
    public bool ScanOnStartup { get; set; }
    public bool AutoScanEnabled { get; set; }
    public int AutoScanSeconds { get; set; } = 300;
    public int LargeScanThreshold { get; set; } = 4096;
    public string EchoAgentAddress { get; set; } = "https://10.10.10.200:47443/jst-echo/";
    public string EchoAgentKey { get; set; } = "";
    public string EchoAgentCertificate { get; set; } = "";
    public DeveloperTrackingSettings DeveloperTracking { get; set; } = new();
    public Dictionary<string, bool> VisibleColumns { get; set; } = new();
    public OverlaySettings Overlay { get; set; } = new();
    public TaskbarSettings Taskbar { get; set; } = new();
    public NotificationSettings Notifications { get; set; } = new();
    public static List<SearchRange> Defaults() => [new() { Range = "10.10.10.X" }, new() { Range = "10.10.9.X" }, new() { Range = "192.168.9.X" }];
}

public class NotificationSettings
{
    public bool StoppedResponding { get; set; } = true;
    public bool RespondingAgain { get; set; } = true;
    public bool NewDevices { get; set; } = true;
    public bool DeveloperChanges { get; set; } = true;
}

public class TaskbarSettings
{
    public List<string> HiddenPlcKeys { get; set; } = [];
    public string DisplayDeviceName { get; set; } = "";
    public int ScrollSeconds { get; set; } = 5;
    public bool AutoRefreshEnabled { get; set; }
    public int AutoRefreshSeconds { get; set; } = 5;
    public bool DockBesideClock { get; set; } = true;
    public int PositionPercent { get; set; } = 100;
    public string ColorScheme { get; set; } = ModeColors.Default;
}

public class OverlaySettings
{
    public bool AlwaysOnTop { get; set; }
    public int OpacityPercent { get; set; } = 80;
    public string ColorScheme { get; set; } = ModeColors.Default;
    public Dictionary<string, bool> VisibleFields { get; set; } = new();
    public bool IsVisible(string key) => VisibleFields?.GetValueOrDefault(key, key is "ip" or "station" or "tracking0")
        ?? (key is "ip" or "station" or "tracking0");
}

public static class SettingsStore
{
    // Per-user settings keep the distributed executable free of sidecar files.
    private const string Key = @"Software\JST\PlcFinder";
    public static UserSettings Load()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            var data = key?.GetValue("Settings") as string;
            if (data is null) return new();
            if (data.StartsWith("dpapi:", StringComparison.Ordinal))
                data = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(data[6..]), null, DataProtectionScope.CurrentUser));
            return JsonSerializer.Deserialize<UserSettings>(data) ?? new();
        }
        catch { return new(); }
    }
    public static bool Save(UserSettings settings)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            byte[] protectedData = ProtectedData.Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(settings)), null, DataProtectionScope.CurrentUser);
            key.SetValue("Settings", "dpapi:" + Convert.ToBase64String(protectedData));
            return true;
        }
        catch { return false; }
    }
}

