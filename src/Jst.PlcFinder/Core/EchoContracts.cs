using System.Security.Cryptography;
using System.Text;

namespace Jst.PlcFinder.Core;

public record EchoControllerInfo(Guid Id, string Name, string[] IpAddresses, Guid ChassisId,
    string ChassisName, uint Slot, uint SerialNumber, bool IsEnabled, string State);

public record EchoInventory(DateTimeOffset GeneratedAt, string Server, EchoControllerInfo[] Controllers);

public static class EchoInventoryScopes
{
    public static Guid[] All(IEnumerable<Guid> chassisIds) =>
        chassisIds.Prepend(Guid.Empty).Distinct().ToArray();
}

public record EchoRestartJob(Guid Id, Guid ControllerId, string ControllerName, string Status,
    string Message, bool Complete, bool Success, DateTimeOffset UpdatedAt);

public static class EchoAuthentication
{
    public static string Sign(string base64Key, long timestamp, string nonce, string method, string rawPath, ReadOnlySpan<byte> body)
    {
        var bodyHash = Convert.ToHexString(SHA256.HashData(body));
        var canonical = $"{timestamp}\n{nonce}\n{method.ToUpperInvariant()}\n{rawPath}\n{bodyHash}";
        using var hmac = new HMACSHA256(Convert.FromBase64String(base64Key));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
    }

    public static bool Verify(string base64Key, long timestamp, string nonce, string method, string rawPath,
        ReadOnlySpan<byte> body, string signature)
    {
        try
        {
            var expected = Convert.FromBase64String(Sign(base64Key, timestamp, nonce, method, rawPath, body));
            var actual = Convert.FromBase64String(signature);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException) { return false; }
    }
}
