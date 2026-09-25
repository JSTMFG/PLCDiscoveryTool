using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;

namespace Jst.PlcFinder.Core;

public readonly record struct IpInterval(uint First, uint Last)
{
    public ulong Count => (ulong)Last - First + 1;
}

public static class IpRanges
{
    public static uint Number(string input)
    {
        var parts = input.Trim().Split('.');
        if (parts.Length != 4 || parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit) || !byte.TryParse(p, out _)))
            throw new FormatException($"Invalid IPv4 address: {input}");
        return BinaryPrimitives.ReadUInt32BigEndian(parts.Select(byte.Parse).ToArray());
    }

    public static IPAddress Address(uint number)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, number);
        return new IPAddress(bytes);
    }

    public static IpInterval Parse(string text)
    {
        text = text.Trim();
        if (text.EndsWith(".X", StringComparison.OrdinalIgnoreCase) || text.EndsWith(".*"))
            text = text[..^1] + "0/24";
        uint first, last;
        if (text.Contains('/'))
        {
            var parts = text.Split('/');
            if (parts.Length != 2 || !int.TryParse(parts[1], out var bits) || bits < 0 || bits > 32)
                throw new FormatException($"Invalid subnet: {text}");
            var ip = Number(parts[0]);
            uint mask = bits == 0 ? 0 : uint.MaxValue << (32 - bits);
            first = ip & mask;
            last = first | ~mask;
            if (bits < 31) { first++; last--; }
        }
        else if (text.Contains('-'))
        {
            var parts = text.Split('-');
            if (parts.Length != 2) throw new FormatException($"Invalid range: {text}");
            first = Number(parts[0]); last = Number(parts[1]);
        }
        else first = last = Number(text);
        if (first > last) throw new FormatException($"Range ends before it starts: {text}");
        if (first == 0 || last >= 0xE0000000 || (first <= 0x7FFFFFFF && last >= 0x7F000000))
            throw new FormatException("Use unicast device addresses; unspecified, loopback, and multicast ranges are not supported.");
        return new(first, last);
    }

    public static IReadOnlyList<IpInterval> Normalize(IEnumerable<string> inputs)
    {
        var sorted = inputs.Select(Parse).OrderBy(r => r.First).ToList();
        var result = new List<IpInterval>();
        foreach (var current in sorted)
        {
            if (result.Count > 0 && (ulong)current.First <= (ulong)result[^1].Last + 1)
                result[^1] = new(result[^1].First, Math.Max(result[^1].Last, current.Last));
            else result.Add(current);
        }
        return result;
    }

    public static IEnumerable<string> Expand(IEnumerable<IpInterval> ranges)
    {
        foreach (var range in ranges)
            for (ulong ip = range.First; ip <= range.Last; ip++) yield return Address((uint)ip).ToString();
    }
}
