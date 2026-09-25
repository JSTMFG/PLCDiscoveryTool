namespace Jst.PlcFinder.Core;

public static class ScanChanges
{
    public static HashSet<string> KnownNetworkDevices(IEnumerable<PlcRow> rows) =>
        rows.Where(row => !row.IsEchoInventoryOnly)
            .Select(Fingerprint)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<PlcRow> NewNetworkDevices(
        IReadOnlySet<string> known, IEnumerable<PlcRow> scannedRows) =>
        scannedRows.Where(row => !row.IsEchoInventoryOnly && !known.Contains(Fingerprint(row)))
            .OrderBy(row => row.IpSort).ThenBy(row => row.Route, StringComparer.Ordinal)
            .ToArray();

    private static string Fingerprint(PlcRow row) => $"{row.Key}|{row.Identity.Serial}";
}
