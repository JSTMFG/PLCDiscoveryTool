using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.IO;

namespace Jst.PlcFinder.Core;

public static class IdentityPacket
{
    public static byte[] Request()
    {
        var packet = new byte[24];
        packet[0] = 0x63;
        RandomNumberGenerator.Fill(packet.AsSpan(12, 8));
        return packet;
    }

    public static DeviceIdentity? Parse(ReadOnlySpan<byte> data, string endpoint, ReadOnlySpan<byte> context)
    {
        if (data.Length < 26 || BinaryPrimitives.ReadUInt16LittleEndian(data) != 0x63 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) != 0 || !data.Slice(12, 8).SequenceEqual(context)) return null;
        int end = 24 + BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        if (end > data.Length || end < 26) return null;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(data[24..]);
        int offset = 26;
        for (int item = 0; item < count; item++)
        {
            if (offset + 4 > end) return null;
            int type = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
            int length = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 2)..]);
            offset += 4;
            if (offset + length > end) return null;
            if (type == 0x0C && length >= 34)
            {
                var body = data.Slice(offset, length);
                int nameLength = body[32];
                if (33 + nameLength + 1 > length) return null;
                return new(endpoint, BinaryPrimitives.ReadUInt16LittleEndian(body[18..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(body[20..]),
                    Encoding.ASCII.GetString(body.Slice(33, nameLength)), $"{body[24]}.{body[25]}",
                    BinaryPrimitives.ReadUInt32LittleEndian(body[28..]));
            }
            offset += length;
        }
        return null;
    }
}

public sealed class Discovery
{
    public const int UdpTimeoutMs = 900;
    public const int TcpTimeoutMs = 1200;
    public const int TcpHeadStartMs = 150;
    public const int ProbeBudgetMs = TcpHeadStartMs + TcpTimeoutMs;

    // No ping prerequisite: devices often disable ICMP while allowing EtherNet/IP.
    public async Task<DeviceIdentity?> ProbeAsync(string ip, IPAddress? local, CancellationToken token, int port = 44818)
    {
        token.ThrowIfCancellationRequested();
        var request = IdentityPacket.Request();
        using var race = CancellationTokenSource.CreateLinkedTokenSource(token);
        var udp = ProbeUdpAsync(ip, local, request, race.Token, port);
        var tcp = ProbeTcpAfterHeadStartAsync();
        try
        {
            var first = await Task.WhenAny(udp, tcp).ConfigureAwait(false);
            var result = await first.ConfigureAwait(false);
            if (result is null)
                result = await (first == udp ? tcp : udp).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            // Drain both branches before disposing their sockets/CTS. No orphaned probes
            // remain after a winner is found or the user presses Stop.
            race.Cancel();
            try { await Task.WhenAll(udp, tcp).ConfigureAwait(false); }
            catch (OperationCanceledException) when (race.IsCancellationRequested) { }
        }

        async Task<DeviceIdentity?> ProbeTcpAfterHeadStartAsync()
        {
            await Task.WhenAny(udp, Task.Delay(TcpHeadStartMs, race.Token)).ConfigureAwait(false);
            race.Token.ThrowIfCancellationRequested();
            // Fast UDP devices need no TCP connection. An immediate UDP rejection
            // starts fallback immediately; a silent UDP socket gets only a head start.
            if (udp.IsCompletedSuccessfully && udp.Result is not null) return udp.Result;
            return await ProbeTcpAsync(ip, local, request, race.Token, port).ConfigureAwait(false);
        }
    }

    internal static async Task<DeviceIdentity?> ProbeUdpAsync(string ip, IPAddress? local, byte[] request, CancellationToken token, int port)
    {
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(UdpTimeoutMs);
            try
            {
                using var udp = new UdpClient(new IPEndPoint(local ?? IPAddress.Any, 0));
                udp.Connect(IPAddress.Parse(ip), port);
                await udp.SendAsync(request, timeout.Token);
                while (true)
                {
                    var reply = await udp.ReceiveAsync(timeout.Token);
                    var result = IdentityPacket.Parse(reply.Buffer, ip, request.AsSpan(12, 8));
                    if (result is not null) return result;
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (SocketException) { }
        }
        return null;
    }

    internal static async Task<DeviceIdentity?> ProbeTcpAsync(string ip, IPAddress? local, byte[] request, CancellationToken token, int port)
    {
        using var tcpTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        tcpTimeout.CancelAfter(TcpTimeoutMs);
        try
        {
            using var tcp = new TcpClient(new IPEndPoint(local ?? IPAddress.Any, 0));
            await tcp.ConnectAsync(IPAddress.Parse(ip), port, tcpTimeout.Token);
            var stream = tcp.GetStream();
            await stream.WriteAsync(request, tcpTimeout.Token);
            var header = new byte[24];
            await stream.ReadExactlyAsync(header, tcpTimeout.Token);
            int length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
            if (length > 4096) return null;
            var response = new byte[24 + length];
            header.CopyTo(response, 0);
            await stream.ReadExactlyAsync(response.AsMemory(24), tcpTimeout.Token);
            return IdentityPacket.Parse(response, ip, request.AsSpan(12, 8));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (SocketException) { return null; }
        catch (IOException) { return null; }
    }

    public async Task<IReadOnlyList<DeviceIdentity>> BroadcastAsync(IPAddress local, IPAddress broadcast, CancellationToken token)
    {
        var found = new Dictionary<string, DeviceIdentity>();
        using var udp = new UdpClient(new IPEndPoint(local, 0)) { EnableBroadcast = true };
        var request = IdentityPacket.Request();
        await udp.SendAsync(request, new IPEndPoint(broadcast, 44818), token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(1800);
        try
        {
            while (true)
            {
                var response = await udp.ReceiveAsync(timeout.Token);
                var identity = IdentityPacket.Parse(response.Buffer, response.RemoteEndPoint.Address.ToString(), request.AsSpan(12, 8));
                if (identity is not null) found[identity.Ip] = identity;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        return found.Values.ToArray();
    }
}
