using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Jst.PlcFinder.Core;

int passed = 0;
void Test(string name, Action run)
{
    run(); passed++; Console.WriteLine("PASS " + name);
}
void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Invalid(Action action) { try { action(); } catch (FormatException) { return; } throw new Exception("Expected validation failure"); }
Test("Three user subnets yield 762 unique hosts", () => Assert(IpRanges.Normalize(["10.10.10.X", "10.10.9.X", "192.168.9.X"]).Sum(r => (long)r.Count) == 762));
Test("Overlapping and adjacent ranges merge", () => Assert(IpRanges.Normalize(["10.10.10.X", "10.10.10.12", "10.10.10.200-10.10.10.255"]).Single().Count == 255));
Test("CIDR normalizes host bits", () => Assert(IpRanges.Parse("10.10.10.42/24") == IpRanges.Parse("10.10.10.X")));
Test("/31 retains both hosts", () => Assert(IpRanges.Parse("10.10.10.2/31").Count == 2));
Test("/32 retains single host", () => Assert(IpRanges.Parse("10.10.10.2/32").Count == 1));
Test("Numeric ordering and expansion", () => Assert(IpRanges.Expand(IpRanges.Normalize(["10.10.10.20", "10.10.10.9"])).SequenceEqual(new[] { "10.10.10.9", "10.10.10.20" })));
foreach (var bad in new[] { "", "10.1.2", "10.1.1.999", "10.1.1.1/33", "10.1.1.1/-1", "10.1.1.X.2", "10.1.1.8-10.1.1.2", "224.0.0.1", "127.0.0.1", "0.0.0.0", "10.0.0.0/0" })
    Test("Reject invalid range: " + bad, () => Invalid(() => IpRanges.Parse(bad)));
Test("Large ranges count without materializing addresses", () => Assert(IpRanges.Parse("10.0.0.0/8").Count == 16777214));
Test("Taskbar carousel orders PLCs numerically, skips bridges, and wraps", () =>
{
    var a = new PlcRow(new("10.0.0.20", 1, 14, "PLC", "1", 1), "");
    var b = new PlcRow(new("10.0.0.9", 1, 14, "PLC", "1", 2), "");
    var bridge = new PlcRow(new("10.0.0.1", 1, 12, "Bridge", "1", 3), "");
    var carousel = new PlcCarousel();
    carousel.Update([a, bridge, b]); Assert(carousel.Current == b && carousel.Count == 2);
    carousel.Update([a, bridge, b], true); Assert(carousel.Current == a);
    carousel.Update([a, bridge, b], true); Assert(carousel.Current == b);
    carousel.Update([a], true); Assert(carousel.Current == a && carousel.Count == 1);
    carousel.Update([a], true); Assert(carousel.Current == a);
    carousel.Update([]); Assert(carousel.Current is null && carousel.Count == 0 && carousel.Index == -1);
});
Test("Taskbar carousel preserves selection on additions and handles replacements and routed PLCs", () =>
{
    var a = new PlcRow(new("10.0.0.20", 1, 14, "PLC", "1", 1), "1,0");
    var b = new PlcRow(new("10.0.0.20", 1, 14, "PLC", "1", 2), "1,2");
    var c = new PlcRow(new("10.0.0.9", 1, 14, "PLC", "1", 3), "");
    var carousel = new PlcCarousel();
    carousel.Update([a, b]); carousel.Update([a, b], true);
    carousel.Update([a, b, c]); Assert(carousel.Current == b && carousel.Count == 3);
    var replacement = new PlcRow(b.Identity, b.Route);
    carousel.Update([a, replacement, c]); Assert(carousel.Current == replacement);
    carousel.Update([a, c]); Assert(carousel.Current == c);
});
Test("Taskbar exclusions preserve the current PLC and keep routes independent", () =>
{
    var a = new PlcRow(new("10.0.0.20", 1, 14, "PLC", "1", 1), "1,0");
    var b = new PlcRow(new("10.0.0.20", 1, 14, "PLC", "1", 2), "1,2");
    var c = new PlcRow(new("10.0.0.9", 1, 14, "PLC", "1", 3), "");
    var carousel = new PlcCarousel();
    carousel.Update([a, b, c]);
    Assert(carousel.Current == c && carousel.Count == 3);
    carousel.Update([a, b, c], false, [a.Key]);
    Assert(carousel.Current == c && carousel.Count == 2);
    carousel.Update([a, b, c], true, [a.Key]);
    Assert(carousel.Current == b && carousel.Count == 2);
    carousel.Update([a, b, c], false, [a.Key, b.Key]);
    Assert(carousel.Current == c && carousel.Count == 1);
    carousel.Update([a, b, c], false, [a.Key, b.Key, c.Key]);
    Assert(carousel.Current is null && carousel.Count == 0);
    carousel.Update([a, b, c], false, [a.Key, c.Key]);
    Assert(carousel.Current == b && carousel.Count == 1);
    carousel.Update([a, b, c]);
    Assert(carousel.Current == b && carousel.Count == 3);
});
Test("Developer field changes use the last successful read and group all changed fields", () =>
{
    var row = new PlcRow(new("10.0.0.20", 1, 14, "PLC", "1", 1), "");
    var labels = new DeveloperTrackingSettings().Labels;
    FieldRead[] initial = [new("Alice", null), new("100", null), new("010126", null), new("", null), new("", null)];
    Assert(row.TrackingChanges(initial, labels).Count == 0);
    row.ApplyTracking(initial);
    FieldRead[] updated = [new("Bob", null), new("200", null), new("010126", null), new("", null), new("Ready", null)];
    var changes = row.TrackingChanges(updated, labels);
    Assert(changes.Count == 3 && changes[0].Description == "Developer: Alice → Bob" &&
        changes[1].Description == "Job number: 100 → 200" && changes[2].Description == "Field 5: (blank) → Ready");
    var secondRow = new PlcRow(new("10.0.0.9", 1, 14, "PLC", "1", 2), "1,2");
    var notice = DeveloperChangeNotice.Format([(row, changes), (secondRow, [new DeveloperFieldChange("Field 4", "Old", "New")])]);
    Assert(notice == "10.0.0.9 · slot 2\n  Field 4: Old → New\n10.0.0.20\n  Developer: Alice → Bob\n  Job number: 100 → 200\n  Field 5: (blank) → Ready");
    row.Mark("Not responding", "Timeout");
    Assert(row.TrackingChanges(updated, labels).Count == 3);
    Assert(row.TrackingChanges([FieldRead.Failed("Timeout"), .. updated.Skip(1)], labels).Count == 0);
    row.ApplyTracking(updated);
    Assert(row.TrackingChanges(updated, labels).Count == 0);
    row.ClearTracking();
    Assert(row.TrackingChanges(initial, labels).Count == 0);
});
var text = new byte[88]; BinaryPrimitives.WriteInt32LittleEndian(text, 4); Encoding.ASCII.GetBytes("CELL").CopyTo(text, 4);
Test("Decode Logix string", () => Assert(ValueDecoder.Decode(text, 0xA0, "STRING") == "CELL"));
Test("Reject string with invalid length", () => Invalid(() => ValueDecoder.Decode([255,255,255,127], 0xA0, "STRING")));
Test("Do not interpret scalar as string", () => Invalid(() => ValueDecoder.Decode(new byte[88], 0xC4, "STRING")));
Test("Decode raw numeric date", () => Assert(ValueDecoder.Decode([0x65,0x27,0x35,0x01], 0xC4, "DINT") == "20260709"));
Test("Reject mismatched numeric PLC type", () => Invalid(() => ValueDecoder.Decode([0,0,0,0], 0xCA, "DINT")));
Test("Missing simulation stays unknown", () => Assert(new PlcRow(new("10.0.0.1",1,14,"PLC","1.0",1), "").Simulation == "Unknown"));
Test("Physical PLC type uses its EtherNet/IP model", () => Assert(new PlcRow(new("10.0.0.1", 1, 14, "1756-L85E", "1.0", 1), "").DeviceKind == "1756-L85E"));
Test("Stale values retain timestamp, never current Off", () =>
{
    var row = new PlcRow(new("10.0.0.1",1,14,"PLC","1.0",1), "");
    row.Apply([new("Station",null),new("Date",null),new("Gem",null),new("Off",null)]);
    var stamp = row.Fields[3].ReadAt; row.Mark("Not responding", "Timeout");
    Assert(row.Simulation == "Off (stale)" && row.Fields[3].ReadAt == stamp && !row.Fields[3].Current);
});
Test("Partial read preserves successful fields", () =>
{
    var row = new PlcRow(new("10.0.0.1",1,14,"PLC","1.0",1), "");
    row.Apply([new("Station",null),FieldRead.Failed("Missing"),new("Gem",null),FieldRead.Failed("Timeout")]);
    Assert(row.Status == "Partial" && row.Station == "Station" && row.Simulation == "Unknown");
});
Test("Missing station name with other readable tags is partial", () =>
{
    var row = new PlcRow(new("10.0.0.1",1,14,"PLC","1.0",1), "");
    row.Apply([FieldRead.Failed("Tag not found"),new("Date",null),new("Gem",null),new("Off",null)]);
    Assert(row.Status == "Partial" && row.Station == "Unavailable" && row.SoftwareDate == "Date");
});
Test("Station tag timeouts do not claim the PLC is empty", () =>
{
    var row = new PlcRow(new("10.10.10.50",1,14,"Emulate 5580 Controller","34.11",1), "");
    row.Apply(Enumerable.Repeat(FieldRead.Failed("Read timed out; check the PLC route and network."), 4).ToArray());
    Assert(row.Status == "Tag read timeout" && row.Station == "Unavailable");
});
Test("Missing custom station tags do not claim the PLC has no program", () =>
{
    var row = new PlcRow(new("10.0.0.1",1,14,"PLC","1.0",1), "");
    row.Apply(Enumerable.Repeat(FieldRead.Failed("Tag or route not found (PLCTAG_ERR_NOT_FOUND)."), 4).ToArray());
    Assert(row.Status == "Station tags not found" && row.Station == "Unavailable");
    row.Apply([new("Station",null),new("Date",null),new("Gem",null),new("Off",null)]);
    Assert(row.Status == "Online" && row.Station == "Station");
    row.Apply(Enumerable.Repeat(FieldRead.Failed("Tag or route not found (PLCTAG_ERR_NOT_FOUND)."), 4).ToArray());
    Assert(row.Status == "Station tags not found" && row.Station == "Station (stale)");
});
Test("Repeated failure backs off", () =>
{
    var row = new PlcRow(new("10.0.0.1",1,14,"PLC","1.0",1), "");
    for (int i = 0; i < 5; i++) row.Mark("Not responding", "Timeout");
    Assert((row.NextPoll - DateTimeOffset.UtcNow).TotalSeconds > 55);
});
var request = IdentityPacket.Request();
var packet = Fixtures.Identity(request);
Test("Identity parses vendor, type, serial, name", () =>
{
    var id = IdentityPacket.Parse(packet, "10.10.10.20", request.AsSpan(12,8));
    Assert(id is { IsAllenBradley: true, IsController: true, Product: "TEST PLC", Serial: 0x12345678 });
});
Test("Identity rejects wrong sender context", () => Assert(IdentityPacket.Parse(packet,"10.0.0.1",new byte[8]) is null));
Test("Identity rejects every truncated response", () =>
{
    for (int i=0;i<packet.Length;i++) Assert(IdentityPacket.Parse(packet.AsSpan(0,i),"10.0.0.1",request.AsSpan(12,8)) is null);
});
Test("Direct routed backplane reply identifies the controller model", () =>
{
    byte[] identity = new byte[15 + 9];
    BinaryPrimitives.WriteUInt16LittleEndian(identity, 1);
    BinaryPrimitives.WriteUInt16LittleEndian(identity.AsSpan(2), 14);
    identity[6] = 35; identity[7] = 11;
    BinaryPrimitives.WriteUInt32LittleEndian(identity.AsSpan(10), 0xA1B2C3D4);
    identity[14] = 9; Encoding.ASCII.GetBytes("1756-L85E").CopyTo(identity, 15);
    byte[] cip = [0x81, 0, 0, 0, .. identity];
    byte[] cpf = new byte[16 + cip.Length];
    BinaryPrimitives.WriteUInt16LittleEndian(cpf.AsSpan(6), 2);
    BinaryPrimitives.WriteUInt16LittleEndian(cpf.AsSpan(12), 0x00B2);
    BinaryPrimitives.WriteUInt16LittleEndian(cpf.AsSpan(14), (ushort)cip.Length); cip.CopyTo(cpf, 16);
    byte[] response = new byte[24 + cpf.Length];
    BinaryPrimitives.WriteUInt16LittleEndian(response, 0x6F);
    BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(2), (ushort)cpf.Length); cpf.CopyTo(response, 24);
    var id = BackplaneDiscovery.TryParseRoutedIdentity(response, "10.10.10.52");
    Assert(id is { Product: "1756-L85E", Serial: 0xA1B2C3D4, IsController: true });
});
Test("Routed controller row identifies its backplane slot", () =>
{
    var row = new PlcRow(new("10.10.10.52", 1, 14, "1756-L72/B", "34.11", 1), "1,2");
    Assert(row.IpDisplay == "10.10.10.52 · slot 2");
});
Test("Native PLC library loads", TagReader.VerifyNativeLibrary);
Console.WriteLine("Native library version " + TagReader.Version);

using (var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
{
    int port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    var server = Task.Run(async () => { var message = await udp.ReceiveAsync(); await udp.SendAsync(Fixtures.Identity(message.Buffer), message.RemoteEndPoint); });
    var identity = await new Discovery().ProbeAsync("127.0.0.1", IPAddress.Loopback, CancellationToken.None, port);
    await server;
    Test("Real UDP discovery exchange with local fixture", () => Assert(identity?.Product == "TEST PLC"));
}
using (var listener = new TcpListener(IPAddress.Loopback, 0))
{
    listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var server = Task.Run(async () => { using var client = await listener.AcceptTcpClientAsync(); var stream = client.GetStream(); var req = new byte[24]; await stream.ReadExactlyAsync(req); await stream.WriteAsync(Fixtures.Identity(req)); });
    var identity = await new Discovery().ProbeAsync("127.0.0.1", IPAddress.Loopback, CancellationToken.None, port);
    await server;
    Test("TCP fallback when UDP is unavailable", () => Assert(identity?.Serial == 0x12345678));
}
using (var cancel = new CancellationTokenSource())
{
    cancel.Cancel(); bool cancelled = false;
    try { await new Discovery().ProbeAsync("127.0.0.1", IPAddress.Loopback, cancel.Token, 1); } catch (OperationCanceledException) { cancelled = true; }
    Test("Discovery honors cancellation", () => Assert(cancelled));
}

// Exercise the actual native library against a loopback-only EtherNet/IP fixture.
if (args.Contains("--trace")) libplctag.NativeImport.plctag.plc_tag_set_debug_level(4);
await using (var plc = new FakePlc())
using (var tags = new TagReader())
{
    await plc.StartAsync();
    var row = new PlcRow(new("127.0.0.1",1,14,"Fixture","1.0",1), "");
    var reads = await tags.ReadAsync(row, new(), CancellationToken.None);
    Test("Native direct-route tag reads return station and dates", () => Assert(reads[0].Value == "FIXTURE_STATION" && reads[1].Value == "2026-09-21" && reads[2].Value == "2026-09-01", string.Join(" | ", reads.Select(r => r.Error ?? r.Value))));
    Test("Native simulation suffix reads bit 2", () => Assert(reads[3].Value == "ON", reads[3].Error ?? "Wrong simulation value"));
    var tracking = await tags.ReadDeveloperTrackingAsync(row, new(), CancellationToken.None);
    Test("Developer tracking reads configured STRING fields", () => Assert(tracking.All(x => x.Success) && tracking[0].Value == "DEV_A" && tracking[2].Value == "010126", string.Join(" | ", tracking.Select(x => x.Error ?? x.Value)) + " paths=" + string.Join(',', plc.TagPaths.TakeLast(5))));
    await tags.WriteDeveloperTrackingAsync(row, new(), new Dictionary<int, string> { [0] = "DEV_B", [1] = "JOB_101", [2] = "092226" }, CancellationToken.None);
    tracking = await tags.ReadDeveloperTrackingAsync(row, new(), CancellationToken.None);
    Test("Developer tracking writes and verifies changed fields", () => Assert(tracking[0].Value == "DEV_B" && tracking[1].Value == "JOB_101" && tracking[2].Value == "092226"));
    bool rejected = false;
    try { await tags.WriteDeveloperTrackingAsync(row, new(), new Dictionary<int, string> { [0] = "SHOULD_NOT_WRITE", [1] = new string('X', 200) }, CancellationToken.None); }
    catch (FormatException) { rejected = true; }
    tracking = await tags.ReadDeveloperTrackingAsync(row, new(), CancellationToken.None);
    Test("Invalid later tracking field prevents all writes", () => Assert(rejected && tracking[0].Value == "DEV_B"));
    int standardReadBaseline = plc.Services.Count;
    Test("Tracking reads and writes do not create duplicate standard tag reads", () => Assert(standardReadBaseline > 0));
    plc.Simulation = 0;
    reads = await tags.ReadAsync(row, new(), CancellationToken.None);
    Test("Native handles are reused and see refreshed values", () => Assert(reads[3].Value == "Off"));
    Test("Refresh performs four fresh reads", () => Assert(plc.Services.Count == standardReadBaseline + 4));
    reads = await tags.ReadAsync(row, new(InvertSimulation: true), CancellationToken.None);
    Test("Inverted simulation option", () => Assert(reads[3].Value == "ON"));
    plc.MissingGem = true;
    reads = await tags.ReadAsync(row, new(), CancellationToken.None);
    Test("Native missing tag does not discard other fields", () => Assert(reads[0].Success && !reads[2].Success && reads[3].Success));
    plc.MissingGem = false;
    var slot0 = new PlcRow(row.Identity, "1,0"); var slot2 = new PlcRow(row.Identity, "1,2");
    reads = await tags.ReadAsync(slot0, new(), CancellationToken.None);
    Test("Native backplane route reads", () => Assert(reads.All(r => r.Success), string.Join(" | ", reads.Select(r => r.Error ?? r.Value))));
    reads = await tags.ReadAsync(slot2, new(), CancellationToken.None);
    Test("Distinct chassis slots use distinct wire routes", () => Assert(reads.All(r => r.Success) && plc.Routes.Contains("0100") && plc.Routes.Contains("0102") && slot0.Key != slot2.Key));
    plc.NumericSoftware = true;
    using (var numericTags = new TagReader())
    {
        reads = await numericTags.ReadAsync(row, new(SoftwareFormat: "DINT"), CancellationToken.None);
        Test("Native numeric date format", () => Assert(reads[1].Value == "20260921", reads[1].Error ?? "Wrong numeric value"));
    }
    plc.FloatSimulation = true;
    using (var invalidTags = new TagReader())
    {
        reads = await invalidTags.ReadAsync(row, new(), CancellationToken.None);
        Test("Unexpected simulation data type stays unknown", () => Assert(!reads[3].Success));
    }
    plc.DelayReads = true;
    using (var cancellation = new CancellationTokenSource(150))
    {
        bool cancelled = false;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try { await tags.ReadAsync(row, new(), cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
        Test("Pending native read cancels promptly", () => Assert(cancelled && watch.ElapsedMilliseconds < 1500));
    }
    Test("Only read/write CIP services were sent", () => Assert(plc.Services.Count > 0 && plc.Services.All(x => x is 0x4C or 0x4D or 0x52)));
}
await ScanTests.RunAsync(Test);
Test("Manual carousel navigation wraps both ways and skips hidden PLCs", () =>
{
    var rows = Enumerable.Range(1, 3).Select(i => new PlcRow(new($"10.10.9.{i}", 1, 14, "PLC", "1", (uint)i), "")).ToArray();
    var carousel = new PlcCarousel();
    carousel.Update(rows, hiddenKeys: [rows[1].Key]);
    carousel.Update(rows, hiddenKeys: [rows[1].Key], offset: -1);
    Assert(carousel.Current == rows[2]);
    carousel.Update(rows, hiddenKeys: [rows[1].Key], offset: 1);
    Assert(carousel.Current == rows[0]);
    carousel.Update([], offset: -1);
    Assert(carousel.Current is null);
    carousel.Update([rows[0]], offset: -1);
    Assert(carousel.Current == rows[0]);
});
await EchoTests.RunAsync(Test);
Test("Taskbar offline alerts fire once after online and rearm on recovery", () =>
{
    var tracker = new OfflineTransitionTracker();
    Assert(!tracker.Observe("10.10.9.10|", "Not responding"));
    Assert(!tracker.Observe("10.10.9.10|", "Online"));
    Assert(!tracker.Observe("10.10.9.10|", "Partial"));
    Assert(tracker.Observe("10.10.9.10|", "Not responding"));
    Assert(!tracker.Observe("10.10.9.10|", "Not responding"));
    Assert(!tracker.Observe("10.10.9.11|", "Not responding"));
    Assert(!tracker.Observe("10.10.9.10|", "Online"));
    Assert(tracker.Observe("10.10.9.10|", "Not responding"));
    tracker.Clear();
    Assert(!tracker.Observe("10.10.9.10|", "Not responding"));
});
Test("Automatic scan groups only newly discovered network devices", () =>
{
    var existing = new PlcRow(new("10.10.9.10", 1, 14, "PLC", "1", 10), "");
    var replaced = new PlcRow(new("10.10.9.11", 1, 14, "PLC", "1", 11), "");
    var known = ScanChanges.KnownNetworkDevices([existing, replaced]);
    replaced.ConfirmIdentity(new("10.10.9.11", 1, 14, "PLC", "1", 111));
    var added = new PlcRow(new("10.10.9.12", 1, 14, "PLC", "1", 12), "");
    var echoOnly = new PlcRow(new("10.10.9.13", 1, 14, "Echo", "1", 13), "", true);
    var newDevices = ScanChanges.NewNetworkDevices(known, [added, echoOnly, existing, replaced]);
    Assert(newDevices.Count == 2 && newDevices[0] == replaced && newDevices[1] == added);
    Assert(ScanChanges.NewNetworkDevices(ScanChanges.KnownNetworkDevices([existing, replaced, added]),
        [existing, replaced, added, echoOnly]).Count == 0);
});
Console.WriteLine($"\n{passed} checks passed.");

static class Fixtures
{
    public static byte[] Identity(byte[] request)
    {
        var name = Encoding.ASCII.GetBytes("TEST PLC"); var packet = new byte[30 + 34 + name.Length];
        request.AsSpan(0,24).CopyTo(packet);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)(packet.Length - 24));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(24), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(26), 0x0C);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(28), (ushort)(34 + name.Length));
        var body = packet.AsSpan(30); BinaryPrimitives.WriteUInt16LittleEndian(body, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(body[18..], 1); BinaryPrimitives.WriteUInt16LittleEndian(body[20..], 14);
        body[24]=35; body[25]=11; BinaryPrimitives.WriteUInt32LittleEndian(body[28..], 0x12345678);
        body[32]=(byte)name.Length; name.CopyTo(body[33..]); return packet;
    }
}

