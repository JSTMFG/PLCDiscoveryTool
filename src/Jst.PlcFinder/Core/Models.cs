using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Jst.PlcFinder.Core;

public class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    { if (!EqualityComparer<T>.Default.Equals(field, value)) { field = value; Changed(property); } }
}

public class SearchRange : Observable
{
    private bool enabled;
    private string range = "";
    public bool Enabled { get => enabled; set => Set(ref enabled, value); }
    public string Range { get => range; set => Set(ref range, value); }
}

public record DeviceIdentity(string Ip, ushort Vendor, ushort DeviceType, string Product, string Revision, uint Serial)
{
    public bool IsAllenBradley => Vendor == 1;
    public bool IsController => DeviceType == 14;
    public bool IsBridge => DeviceType == 12;
}

public record FieldRead(string? Value, string? Error)
{
    public bool Success => Error is null && Value is not null;
    public static FieldRead Failed(string message) => new(null, message);
}

public class FieldState
{
    public string? Value { get; private set; }
    public string? Error { get; private set; } = "Not read";
    public DateTimeOffset? ReadAt { get; private set; }
    public bool Current => Error is null && ReadAt.HasValue;
    public string Display => Value is null ? "Unavailable" : Current ? Value : $"{Value} (stale)";
    public void Apply(FieldRead read, DateTimeOffset time)
    {
        Error = read.Error;
        if (read.Success) { Value = read.Value; ReadAt = time; }
    }
    public void Invalidate(string reason) => Error = reason;
}

public class PlcRow : Observable
{
    public DeviceIdentity Identity { get; private set; }
    public string Ip => Identity.Ip;
    public string IpDisplay => Route.Split(',') is ["1", var slot] ? $"{Ip} · slot {slot}" : Ip;
    public uint IpSort => IpRanges.Number(Ip);
    public string Route { get; }
    public string RouteLabel => string.IsNullOrEmpty(Route) ? "Direct" : Route;
    public string Key => $"{Ip}|{Route}";
    public FieldState[] Fields { get; } = Enumerable.Range(0, 4).Select(_ => new FieldState()).ToArray();
    public string[] TrackingValues { get; private set; } = ["Not read", "Not read", "Not read", "Not read", "Not read"];
    private string[]? lastSuccessfulTrackingValues;
    public bool TrackingAvailable { get; private set; }
    public IReadOnlyList<DeveloperFieldChange> TrackingChanges(FieldRead[] reads, string[] labels)
    {
        if (lastSuccessfulTrackingValues is null || reads.Length != 5 || !reads.All(r => r.Success)) return [];
        return Enumerable.Range(0, 5)
            .Where(i => !string.Equals(lastSuccessfulTrackingValues[i], reads[i].Value ?? "", StringComparison.Ordinal))
            .Select(i => new DeveloperFieldChange(labels[i], lastSuccessfulTrackingValues[i], reads[i].Value ?? ""))
            .ToArray();
    }
    public void ApplyTracking(FieldRead[] reads)
    {
        TrackingAvailable = reads.Length == 5 && reads.All(r => r.Success);
        TrackingValues = reads.All(r => r.Success) ? reads.Select(r => r.Value ?? "").ToArray()
            : Enumerable.Repeat("Developer tracking not installed", 5).ToArray();
        if (TrackingAvailable) lastSuccessfulTrackingValues = TrackingValues.ToArray();
        Changed(nameof(TrackingValues));
    }
    public void ClearTracking()
    {
        TrackingAvailable = false;
        lastSuccessfulTrackingValues = null;
        TrackingValues = Enumerable.Repeat("Not read", 5).ToArray();
        Changed(nameof(TrackingValues));
    }
    public string Station => IsEchoInventoryOnly ? EchoControllerName : Fields[0].Display;
    public string SoftwareDate => Fields[1].Display;
    public string GemDate => Fields[2].Display;
    public string Simulation => Fields[3].Value is null ? "Unknown" : Fields[3].Display;
    public string Status { get; private set; } = "Found";
    public Guid? EchoControllerId { get; private set; }
    public string EchoControllerName { get; private set; } = "";
    public bool IsEchoInventoryOnly { get; private set; }
    public bool IsEcho => EchoControllerId.HasValue;
    // The product name comes from the controller's EtherNet/IP identity response and is
    // the controller model (for example, 1756-L85E or 1769-L33ER).
    public string DeviceKind => IsEcho ? "Echo" : string.IsNullOrWhiteSpace(Identity.Product) ? "Unknown" : Identity.Product;
    public string Updated => Fields.Where(f => f.ReadAt.HasValue).Select(f => f.ReadAt).Max()?.ToLocalTime().ToString("HH:mm:ss") ?? "—";
    public int Failures { get; private set; }
    public DateTimeOffset NextPoll { get; private set; }
    public string Details => $"{Identity.Product}  •  firmware {Identity.Revision}  •  serial {Identity.Serial:X8}  •  endpoint {Ip}  •  route {RouteLabel}\n" +
        string.Join("\n", Fields.Select((f, i) => $"{TagSettings.Names[i]}: {f.Error ?? "Read OK"}  |  last success: {f.ReadAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "never"}"));
    private bool HasTagReadTimeout => Fields.Any(f => f.Error?.Contains("timed out", StringComparison.OrdinalIgnoreCase) == true);
    public string StatusToolTip => (Status switch
    {
        "Online" => "The controller responded and all station tags were read.",
        "Partial" when IsEcho && HasTagReadTimeout => "The Echo controller responded, but a tag read timed out. A reset is likely required. Use Reset PLC, then Refresh.",
        "Partial" => "The controller responded, but one or more station tags could not be read.",
        "Station tags not found" => "The controller responded, but the configured station tags or route were not found. Check the program scope and controller route.",
        "Tag read timeout" when IsEcho => "Tag reads timed out on this Echo controller. A reset is likely required. Use Reset PLC, then Refresh.",
        "Tag read timeout" => "The controller was discovered, but a tag read timed out. Check connectivity and try Refresh.",
        "Tag read failed" => "The controller was discovered, but its station tags could not be read. See the tag errors below.",
        "Route mismatch" => "Windows would send tag requests through a different adapter. Choose Automatic or correct the network route.",
        "Not responding" => "This address did not answer the most recent scan.",
        "No controller found" => "The Ethernet bridge answered, but no controller was found in the scanned chassis slots. Try a manual route in Advanced.",
        "Unsupported" => "This device does not support the station tag reads used by this app.",
        "Echo not responding" => "The Echo agent lists this controller, but it has not answered EtherNet/IP discovery.",
        "Echo disabled" => "The Echo agent reports this controller is disabled.",
        "Stale" => "The last scan or refresh stopped before this result could be checked again.",
        _ => Status
    }) + "\n\n" + Details;

    public PlcRow(DeviceIdentity identity, string route, bool echoInventoryOnly = false)
    { Identity = identity; Route = route; IsEchoInventoryOnly = echoInventoryOnly; }
    public void ConfirmIdentity(DeviceIdentity identity)
    {
        Identity = identity;
        IsEchoInventoryOnly = false;
        Changed("");
    }
    public void SetEcho(EchoControllerInfo? controller)
    {
        EchoControllerId = controller?.Id;
        EchoControllerName = controller?.Name ?? "";
        Changed("");
    }
    public void Apply(FieldRead[] reads)
    {
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 4; i++) Fields[i].Apply(reads[i], now);
        // Missing application-specific tags cannot establish whether a program is loaded.
        Status = reads.All(f => f.Success) ? "Online" :
            reads.Any(f => f.Success) ? "Partial" :
            reads.All(f => f.Error?.StartsWith("Tag or route not found (", StringComparison.OrdinalIgnoreCase) == true) ? "Station tags not found" :
            reads.Any(f => f.Error?.Contains("timed out", StringComparison.OrdinalIgnoreCase) == true) ? "Tag read timeout" :
            "Tag read failed";
        Failures = reads.Any(f => f.Success) ? 0 : Failures + 1;
        NextPoll = now.AddSeconds(Math.Min(60, 5 * Math.Pow(2, Math.Min(Failures, 4))));
        Changed("");
    }
    public void Mark(string status, string error)
    {
        TrackingAvailable = false;
        Status = status;
        foreach (var f in Fields) f.Invalidate(error);
        TrackingValues = TrackingValues.Select(v => v.EndsWith(" (stale)") ? v : v + " (stale)").ToArray();
        Failures++;
        NextPoll = DateTimeOffset.UtcNow.AddSeconds(Math.Min(60, 5 * Math.Pow(2, Math.Min(Failures, 4))));
        Changed("");
    }
    public void MarkEchoInventory(EchoControllerInfo controller)
    {
        SetEcho(controller);
        Status = controller.IsEnabled ? "Echo not responding" : "Echo disabled";
        foreach (var f in Fields) f.Invalidate("Listed by the Echo server; no EtherNet/IP reply has been received.");
        NextPoll = DateTimeOffset.UtcNow;
        Changed("");
    }
}

public record TagSettings(string ProgramScope = "", string SoftwareFormat = "STRING", string GemFormat = "STRING", bool InvertSimulation = false)
{
    public static readonly string[] Names = ["STATION.NAME", "STATION.SOFTWARE_DATE", "STATION.EIBHOST[2]", "STATION.OO.2"];
    public string FullName(int index) => string.IsNullOrWhiteSpace(ProgramScope) ? Names[index] : $"Program:{ProgramScope}.{Names[index]}";
}

public record DeveloperTrackingSettings(
    string DeveloperTag = "DEBUG_DEVELOPER[0]",
    string JobNumberTag = "DEBUG_DEVELOPER[1]",
    string LastClaimedDateTag = "DEBUG_DEVELOPER[2]",
    string Field4Tag = "DEBUG_DEVELOPER[3]",
    string Field5Tag = "DEBUG_DEVELOPER[4]",
    string DeveloperLabel = "Developer",
    string JobNumberLabel = "Job number",
    string LastClaimedDateLabel = "Last claimed date",
    string Field4Label = "Field 4",
    string Field5Label = "Field 5")
{
    public string[] Tags => [DeveloperTag, JobNumberTag, LastClaimedDateTag, Field4Tag, Field5Tag];
    public string[] Labels => [DeveloperLabel, JobNumberLabel, LastClaimedDateLabel, Field4Label, Field5Label];
    public static bool IsValidTag(string tag) =>
        System.Text.RegularExpressions.Regex.IsMatch(tag, @"^(?:Program:[A-Za-z_][A-Za-z0-9_]{0,39}\.)?[A-Za-z_][A-Za-z0-9_]*(?:\[[0-9]+\])?(?:\.[A-Za-z_][A-Za-z0-9_]*(?:\[[0-9]+\])?)*$");
}
