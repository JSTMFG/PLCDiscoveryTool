using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

// A small test fixture, not a production PLC implementation. Binds only to loopback.
sealed class FakePlc : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 44818);
    private readonly CancellationTokenSource stop = new();
    private readonly List<Task> clients = [];
    private Task? accept;
    public ConcurrentBag<byte> Services { get; } = [];
    public int Simulation { get; set; } = 4;
    public bool MissingGem { get; set; }
    public bool NumericSoftware { get; set; }
    public bool FloatSimulation { get; set; }
    public bool DelayReads { get; set; }
    public ConcurrentBag<string> Routes { get; } = [];
    public ConcurrentBag<string> TagPaths { get; } = [];
    public string[] DeveloperTracking { get; } = ["DEV_A", "JOB_100", "010126", "", ""];
    public Task StartAsync()
    {
        listener.Start();
        accept = Task.Run(async () =>
        {
            try { while (!stop.IsCancellationRequested) { var client = await listener.AcceptTcpClientAsync(stop.Token); clients.Add(ServeAsync(client)); } }
            catch (OperationCanceledException) { }
        });
        return Task.CompletedTask;
    }
    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                uint clientConnectionId = 0;
                while (!stop.IsCancellationRequested)
                {
                    var header = new byte[24]; await stream.ReadExactlyAsync(header, stop.Token);
                    int length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
                    var body = new byte[length]; await stream.ReadExactlyAsync(body, stop.Token);
                    ushort command = BinaryPrimitives.ReadUInt16LittleEndian(header);
                    byte[] reply;
                    if (command == 0x65) { reply = body; BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 0x1234); }
                    else if (command == 0x66) return;
                    else if (command is 0x6F or 0x70)
                    {
                        int offset = 8; byte[]? cip = null;
                        ushort sequence = 0;
                        int items = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(6));
                        for (int i = 0; i < items; i++)
                        {
                            int type = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset));
                            int size = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset + 2)); offset += 4;
                            if (type == 0xB2) cip = body.AsSpan(offset,size).ToArray();
                            if (type == 0xB1) { sequence = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset)); cip = body.AsSpan(offset + 2, size - 2).ToArray(); }
                            offset += size;
                        }
                        if (cip is null) throw new Exception("No unconnected data item");
                        byte[] response;
                        if (cip[0] is 0x54 or 0x5B)
                        {
                            clientConnectionId = BinaryPrimitives.ReadUInt32LittleEndian(cip.AsSpan(12));
                            response = new byte[30]; response[0] = (byte)(cip[0] | 0x80);
                            BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(4), 0x87654321);
                            cip.AsSpan(12,12).CopyTo(response.AsSpan(8));
                            cip.AsSpan(28,4).CopyTo(response.AsSpan(20));
                            cip.AsSpan(cip[0] == 0x5B ? 36 : 34,4).CopyTo(response.AsSpan(24));
                            int pathStart = cip[0] == 0x5B ? 46 : 42;
                            var path = cip.AsSpan(pathStart).ToArray();
                            if (!path.AsSpan(path.Length - 4).SequenceEqual(new byte[] {0x20,2,0x24,1})) throw new Exception("Unexpected Message Router path");
                            Routes.Add(Convert.ToHexString(path.AsSpan(0, path.Length - 4)));
                        }
                        else if (cip[0] == 0x4E && cip[2] == 0x20 && cip[3] == 6)
                        { response = new byte[14]; response[0] = 0xCE; cip.AsSpan(8,8).CopyTo(response.AsSpan(4)); }
                        else
                        {
                            if (DelayReads) await Task.Delay(5000, stop.Token);
                            response = ReadResponse(cip);
                        }
                        if (command == 0x70)
                        {
                            reply = new byte[22 + response.Length]; reply[6] = 2;
                            reply[8] = 0xA1; reply[10] = 4; BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(12), clientConnectionId);
                            reply[16] = 0xB1; BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(18), (ushort)(response.Length + 2));
                            BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(20), sequence); response.CopyTo(reply,22);
                        }
                        else
                        {
                            reply = new byte[16 + response.Length]; reply[6] = 2;
                            reply[12] = 0xB2; BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(14), (ushort)response.Length); response.CopyTo(reply, 16);
                        }
                    }
                    else throw new Exception($"Unexpected encapsulation command {command:X}");
                    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), (ushort)reply.Length);
                    await stream.WriteAsync(header, stop.Token); await stream.WriteAsync(reply, stop.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (EndOfStreamException) { }
            catch (IOException ex) when (stop.IsCancellationRequested || ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset }) { }
        }
    }
    private byte[] ReadResponse(byte[] cip)
    {
        byte service = cip[0]; Services.Add(service);
        int pathSize = cip[1] * 2;
        // Unconnected Send to the Connection Manager wraps a read when a route is specified.
        if (service == 0x52 && pathSize == 4 && cip[2] == 0x20 && cip[3] == 6)
        {
            int embeddedSize = BinaryPrimitives.ReadUInt16LittleEndian(cip.AsSpan(8));
            int routeOffset = 10 + embeddedSize + (embeddedSize % 2);
            int routeSize = routeOffset < cip.Length ? cip[routeOffset] * 2 : 0;
            Routes.Add(routeSize == 0 ? "" : Convert.ToHexString(cip.AsSpan(routeOffset + 2, routeSize)));
            return ReadResponse(cip.AsSpan(10, embeddedSize).ToArray());
        }
        if (service == 0x4D)
        {
            string writePath = Encoding.ASCII.GetString(cip, 2, pathSize);
            int index = TrackingIndex(writePath);
            if (index < 0) throw new Exception("Unexpected write tag");
            // Logix STRING writes include the four-byte type descriptor and a two-byte element count.
            int dataOffset = 2 + pathSize + 6;
            int length = BinaryPrimitives.ReadInt32LittleEndian(cip.AsSpan(dataOffset));
            if (length < 0 || dataOffset + 4 + length > cip.Length)
                throw new Exception($"Unexpected write payload offset={dataOffset} length={length}: {Convert.ToHexString(cip)}");
            DeveloperTracking[index] = Encoding.ASCII.GetString(cip, dataOffset + 4, length);
            return [0xCD, 0, 0, 0];
        }
        if (service is not (0x4C or 0x52)) throw new Exception($"Non-read service {service:X2}");
        string path = Encoding.ASCII.GetString(cip, 2, pathSize);
        TagPaths.Add(Convert.ToHexString(cip.AsSpan(2, pathSize)));
        if (path.Contains("EIBHOST") && MissingGem) return [(byte)(service | 0x80),0,5,0];
        bool simulation = path.Contains("OO");
        bool numericSoftware = path.Contains("SOFTWARE_DATE") && NumericSoftware;
        byte[] type = simulation ? [(byte)(FloatSimulation ? 0xCA : 0xC4),0] : numericSoftware ? [0xC4,0] : [0xA0,2,0xCE,0x0F];
        byte[] data;
        if (simulation) { data = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(data, Simulation); }
        else if (numericSoftware) { data = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(data, 20260921); }
        else
        {
            int trackingIndex = TrackingIndex(path);
            string value = trackingIndex >= 0 ? DeveloperTracking[trackingIndex] : path.Contains("SOFTWARE_DATE") ? "2026-09-21" : path.Contains("EIBHOST") ? "2026-09-01" : "FIXTURE_STATION";
            data = new byte[88]; BinaryPrimitives.WriteInt32LittleEndian(data, value.Length); Encoding.ASCII.GetBytes(value).CopyTo(data,4);
        }
        return new byte[] { (byte)(service | 0x80),0,0,0 }.Concat(type).Concat(data).ToArray();
    }
    private static int TrackingIndex(string path)
    {
        const string marker = "DEBUG_DEVELOPER";
        int start = path.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return -1;
        int element = path.IndexOf('\x28', start + marker.Length);
        if (element < 0) return 0; // Logix omits the element selector for [0].
        int digit = element + 1;
        return digit < path.Length && path[digit] is >= '\0' and <= '\x04' ? path[digit] : -1;
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); listener.Stop();
        if (accept is not null) await accept;
        await Task.WhenAll(clients); stop.Dispose();
    }
}
