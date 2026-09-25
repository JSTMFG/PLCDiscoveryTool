using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Jst.PlcFinder.Core;

public record RoutedControllerIdentity(DeviceIdentity Identity, string Route);

/// <summary>Reads the EtherNet/IP Identity object through a ControlLogix backplane route.</summary>
public sealed class BackplaneDiscovery
{
    // The largest standard 1756 chassis has slots 0 through 16.
    private const int SlotCount = 17;

    public async Task<IReadOnlyList<RoutedControllerIdentity>> DiscoverAsync(DeviceIdentity bridge, CancellationToken token)
    {
        var found = new System.Collections.Concurrent.ConcurrentBag<RoutedControllerIdentity>();
        await Parallel.ForEachAsync(Enumerable.Range(0, SlotCount), new ParallelOptions
        {
            MaxDegreeOfParallelism = 6,
            CancellationToken = token
        }, async (slot, ct) =>
        {
            var identity = await ProbeSlotAsync(bridge.Ip, slot, ct).ConfigureAwait(false);
            if (identity is not null && identity.IsAllenBradley && identity.IsController)
                found.Add(new(identity, $"1,{slot}"));
        }).ConfigureAwait(false);
        return found.OrderBy(x => byte.Parse(x.Route[2..])).ToArray();
    }

    internal static async Task<DeviceIdentity?> ProbeSlotAsync(string ip, int slot, CancellationToken token, int port = 44818)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(900);
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Parse(ip), port, timeout.Token).ConfigureAwait(false);
            using var stream = client.GetStream();
            uint session = await RegisterSessionAsync(stream, timeout.Token).ConfigureAwait(false);
            try
            {
                await SendAsync(stream, session, BuildRoutedIdentityRequest(slot), timeout.Token).ConfigureAwait(false);
                var response = await ReceiveAsync(stream, timeout.Token).ConfigureAwait(false);
                return TryParseRoutedIdentity(response, ip);
            }
            finally
            {
                try { await SendAsync(stream, session, [], CancellationToken.None, 0x66).ConfigureAwait(false); } catch { }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (SocketException) { return null; }
        catch (IOException) { return null; }
    }

    internal static DeviceIdentity? TryParseRoutedIdentity(ReadOnlySpan<byte> packet, string ip)
    {
        if (packet.Length < 32 || BinaryPrimitives.ReadUInt16LittleEndian(packet) != 0x6F ||
            BinaryPrimitives.ReadUInt32LittleEndian(packet[8..]) != 0) return null;
        int payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(packet[2..]);
        if (24 + payloadLength > packet.Length || payloadLength < 8) return null;
        var cpf = packet.Slice(24, payloadLength);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(cpf[6..]);
        int offset = 8;
        ReadOnlySpan<byte> cip = [];
        for (int item = 0; item < count; item++)
        {
            if (offset + 4 > cpf.Length) return null;
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(cpf[offset..]);
            int length = BinaryPrimitives.ReadUInt16LittleEndian(cpf[(offset + 2)..]);
            offset += 4;
            if (offset + length > cpf.Length) return null;
            if (type == 0x00B2) cip = cpf.Slice(offset, length);
            offset += length;
        }
        if (cip.Length < 4 || cip[2] != 0) return null;
        // A 1756-ENBT returns the routed Identity reply (0x81) directly. Some
        // bridges retain the outer Unconnected Send reply (0xD2) around it.
        int reply = 0;
        if (cip[0] == 0xD2)
        {
            reply = 4 + cip[3] * 2;
            if (reply + 4 > cip.Length) return null;
        }
        if (cip[reply] != 0x81 || cip[reply + 2] != 0) return null;
        int identity = reply + 4 + cip[reply + 3] * 2;
        if (identity + 15 > cip.Length) return null;
        ushort vendor = BinaryPrimitives.ReadUInt16LittleEndian(cip[identity..]);
        ushort typeCode = BinaryPrimitives.ReadUInt16LittleEndian(cip[(identity + 2)..]);
        byte revisionMajor = cip[identity + 6]; byte revisionMinor = cip[identity + 7];
        uint serial = BinaryPrimitives.ReadUInt32LittleEndian(cip[(identity + 10)..]);
        int nameLength = cip[identity + 14];
        if (identity + 15 + nameLength > cip.Length) return null;
        string product = System.Text.Encoding.ASCII.GetString(cip.Slice(identity + 15, nameLength));
        return new(ip, vendor, typeCode, product, $"{revisionMajor}.{revisionMinor}", serial);
    }

    private static async Task<uint> RegisterSessionAsync(NetworkStream stream, CancellationToken token)
    {
        await SendAsync(stream, 0, [1, 0, 0, 0], token, 0x65).ConfigureAwait(false);
        var response = await ReceiveAsync(stream, token).ConfigureAwait(false);
        if (response.Length < 24 || BinaryPrimitives.ReadUInt16LittleEndian(response) != 0x65 || BinaryPrimitives.ReadUInt32LittleEndian(response[8..]) != 0)
            throw new IOException("The EtherNet/IP bridge rejected session registration.");
        return BinaryPrimitives.ReadUInt32LittleEndian(response[4..]);
    }

    private static byte[] BuildRoutedIdentityRequest(int slot)
    {
        // Unconnected Send to Connection Manager, with Get_Attributes_All for Identity
        // object class 1 / instance 1 routed to backplane port 1 and the requested slot.
        byte[] cip = [0x52, 0x02, 0x20, 0x06, 0x24, 0x01, 0x0A, 0x05,
            0x06, 0x00, // embedded Get_Attributes_All request length, in bytes
            0x01, 0x02, 0x20, 0x01, 0x24, 0x01,
            0x01, 0x00, 0x01, (byte)slot];
        byte[] cpf = new byte[16 + cip.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(cpf.AsSpan(6), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(cpf.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(cpf.AsSpan(12), 0x00B2);
        BinaryPrimitives.WriteUInt16LittleEndian(cpf.AsSpan(14), (ushort)cip.Length);
        cip.CopyTo(cpf, 16);
        return cpf;
    }

    private static async Task SendAsync(NetworkStream stream, uint session, byte[] payload, CancellationToken token, ushort command = 0x6F)
    {
        byte[] packet = new byte[24 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, command);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), session);
        RandomNumberGenerator.Fill(packet.AsSpan(12, 8));
        payload.CopyTo(packet, 24);
        await stream.WriteAsync(packet, token).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReceiveAsync(NetworkStream stream, CancellationToken token)
    {
        byte[] header = new byte[24]; await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
        byte[] packet = new byte[24 + length]; header.CopyTo(packet, 0);
        if (length > 0) await stream.ReadExactlyAsync(packet.AsMemory(24, length), token).ConfigureAwait(false);
        return packet;
    }
}
