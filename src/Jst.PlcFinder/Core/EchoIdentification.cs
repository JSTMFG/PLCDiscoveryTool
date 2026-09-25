namespace Jst.PlcFinder.Core;

public static class EchoIdentification
{
    public static IReadOnlyList<(string Ip, EchoControllerInfo Controller)> UniqueDirectEndpoints(
        IReadOnlyCollection<EchoControllerInfo> controllers, IReadOnlyCollection<IpInterval>? scannedRanges = null)
    {
        return controllers
            .SelectMany(c => c.IpAddresses.Select(ip => (Ip: ip.Trim(), Controller: c)))
            .Where(x => System.Net.IPAddress.TryParse(x.Ip, out var address) &&
                        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .GroupBy(x => x.Ip, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => x.Controller.Id).Distinct().Count() == 1)
            .Select(g => g.First())
            .Where(x => scannedRanges is null || scannedRanges.Any(range =>
            {
                uint ip = IpRanges.Number(x.Ip);
                return ip >= range.First && ip <= range.Last;
            }))
            .ToArray();
    }

    public static EchoControllerInfo? Match(PlcRow row, IReadOnlyCollection<EchoControllerInfo> controllers)
    {
        static bool HasIdentity(EchoControllerInfo c, PlcRow row) => c.SerialNumber != 0 &&
            c.SerialNumber == row.Identity.Serial && c.IpAddresses.Any(x => string.Equals(x, row.Ip, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrEmpty(row.Route))
        {
            var direct = controllers.Where(c => HasIdentity(c, row)).ToArray();
            return direct.Length == 1 ? direct[0] : null;
        }

        var route = row.Route.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (route.Length != 2 || route[0] != "1" || !uint.TryParse(route[1], out uint slot)) return null;

        var chassisAtAddress = controllers.Where(c => c.IpAddresses.Any(ip => string.Equals(ip, row.Ip, StringComparison.OrdinalIgnoreCase)) && c.ChassisId != Guid.Empty)
            .Select(c => c.ChassisId).Distinct().ToHashSet();
        var routed = controllers.Where(c => c.ChassisId != Guid.Empty && chassisAtAddress.Contains(c.ChassisId) && c.Slot == slot &&
            c.SerialNumber != 0 && c.SerialNumber == row.Identity.Serial).ToArray();
        return routed.Length == 1 ? routed[0] : null;
    }
}
