using Jst.EchoAgent;
using Jst.PlcFinder.Core;

static class EchoTests
{
    public static async Task RunAsync(Action<string, Action> test)
    {
        void Check(bool value, string message = "Echo regression") { if (!value) throw new Exception(message); }
        var chassis = Guid.NewGuid();
        var directId = Guid.NewGuid(); var routedId = Guid.NewGuid();
        EchoControllerInfo[] inventory =
        [
            new(directId, "Direct Echo", ["10.10.10.200"], chassis, "Echo Chassis", 0, 1, true, "On"),
            new(routedId, "Slot 2 Echo", [], chassis, "Echo Chassis", 2, 2, true, "On")
        ];
        var direct = new PlcRow(new("10.10.10.200", 1, 14, "PLC", "1", 1), "");
        // Routed discovery reads the controller's own identity through the chassis gateway.
        var routed = new PlcRow(new("10.10.10.200", 1, 14, "PLC", "1", 2), "1,2");
        var physical = new PlcRow(new("10.10.10.25", 1, 14, "PLC", "1", 3), "");
        test("Direct Echo controller is identified only from agent inventory", () => Check(EchoIdentification.Match(direct, inventory)?.Id == directId));
        test("Routed Echo controller is identified by chassis and slot", () => Check(EchoIdentification.Match(routed, inventory)?.Id == routedId));
        test("Routed Echo matching rejects a different controller serial", () => Check(EchoIdentification.Match(
            new PlcRow(new("10.10.10.200", 1, 14, "PLC", "1", 999), "1,2"), inventory) is null));
        test("Physical PLC without an inventory match is never marked Echo", () => Check(EchoIdentification.Match(physical, inventory) is null));
        test("Ambiguous Echo identity is rejected", () => Check(EchoIdentification.Match(direct, [.. inventory, new(Guid.NewGuid(), "Duplicate", ["10.10.10.200"], Guid.NewGuid(), "Other", 0, 1, true, "On")]) is null));
        test("Matching IP without matching serial is not identified as Echo", () => Check(EchoIdentification.Match(
            new PlcRow(new("10.10.10.200", 1, 14, "PLC", "1", 999), ""), inventory) is null));
        test("Echo tag timeout tooltips recommend a reset", () =>
        {
            direct.SetEcho(inventory[0]);
            direct.Apply(Enumerable.Repeat(FieldRead.Failed("Read timed out"), 4).ToArray());
            Check(direct.Status == "Tag read timeout" && direct.StatusToolTip.Contains("reset is likely required", StringComparison.OrdinalIgnoreCase));
            direct.Apply([new FieldRead("Station", null), FieldRead.Failed("Read timed out"),
                new FieldRead("Gem", null), new FieldRead("Off", null)]);
            Check(direct.Status == "Partial" && direct.StatusToolTip.Contains("reset is likely required", StringComparison.OrdinalIgnoreCase));
            physical.Apply(Enumerable.Repeat(FieldRead.Failed("Read timed out"), 4).ToArray());
            Check(!physical.StatusToolTip.Contains("reset is likely required", StringComparison.OrdinalIgnoreCase));
        });
        test("Echo inventory queries the root and every distinct chassis", () =>
        {
            var second = Guid.NewGuid();
            Check(EchoInventoryScopes.All([chassis, second, chassis]).SequenceEqual([Guid.Empty, chassis, second]));
        });
        test("Unique Echo IP remains available when EtherNet/IP discovery is down", () => Check(
            EchoIdentification.UniqueDirectEndpoints(inventory).Single().Controller.Id == directId));
        test("Echo inventory rows require a scan of their subnet", () =>
        {
            var otherSubnet = IpRanges.Normalize(["10.10.9.X"]);
            Check(EchoIdentification.UniqueDirectEndpoints(inventory, otherSubnet).Count == 0);
            var scannedSubnet = IpRanges.Normalize(["10.10.10.X"]);
            Check(EchoIdentification.UniqueDirectEndpoints(inventory, scannedSubnet).Single().Controller.Id == directId);
        });
        test("Echo inventory from a previous scan is excluded after scanning another subnet", () =>
        {
            var currentScan = IpRanges.Normalize(["10.10.10.X"]).ToList();
            Check(EchoIdentification.UniqueDirectEndpoints(inventory, currentScan).Count == 1);
            currentScan.Clear();
            currentScan.AddRange(IpRanges.Normalize(["10.10.9.X"]));
            Check(EchoIdentification.UniqueDirectEndpoints(inventory, currentScan).Count == 0);
        });
        test("Duplicate Echo IP is not exposed as a reset target", () => Check(
            EchoIdentification.UniqueDirectEndpoints([.. inventory,
                new(Guid.NewGuid(), "Duplicate IP", ["10.10.10.200"], Guid.Empty, "", 0, 3, true, "On")]).Count == 0));
        test("Echo inventory row uses controller name until PLC responds", () =>
        {
            var row = new PlcRow(new("10.10.10.49", 1, 14, "FactoryTalk Logix Echo", "—", 49), "", true);
            row.MarkEchoInventory(new(Guid.NewGuid(), "ECHO_49", ["10.10.10.49"], Guid.Empty, "", 0, 49, true, "On"));
            Check(row.IsEcho && row.IsEchoInventoryOnly && row.Station == "ECHO_49" && row.Status == "Echo not responding");
            row.ConfirmIdentity(new("10.10.10.49", 1, 14, "1756-L85E", "36.11", 49));
            Check(!row.IsEchoInventoryOnly);
        });

        string key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        string signature = EchoAuthentication.Sign(key, 123, "nonce", "POST", "/jst-echo/api/controllers/id/restart", []);
        test("Agent request signatures verify and reject changed paths", () => Check(
            EchoAuthentication.Verify(key, 123, "nonce", "POST", "/jst-echo/api/controllers/id/restart", [], signature) &&
            !EchoAuthentication.Verify(key, 123, "nonce", "POST", "/jst-echo/api/controllers/other/restart", [], signature)));

        using var fake = new FakeBackend(inventory[0]);
        var coordinator = new RestartCoordinator(fake);
        var job = coordinator.Start(directId, "Direct Echo");
        EchoRestartJob current;
        do
        {
            await Task.Delay(25);
            Check(coordinator.TryGet(job.Id, out current!));
        } while (!current.Complete);
        test("Reset transaction explicitly turns one Echo controller off and on", () =>
            Check(current.Success && fake.Commands.SequenceEqual(new[] { false, true })));
        using var uncertain = new FakeBackend(inventory[0], throwAfterOff: true);
        var recovery = new RestartCoordinator(uncertain);
        var recoveryJob = recovery.Start(directId, "Recovery fixture");
        await recovery.WaitForRunningAsync(TimeSpan.FromSeconds(10));
        test("Reset recovers when SDK throws after turning the controller off", () =>
            Check(recovery.TryGet(recoveryJob.Id, out var result) && result.Complete && !result.Success &&
                uncertain.Commands.SequenceEqual(new[] { false, true })));
    }

    private sealed class FakeBackend(EchoControllerInfo controller, bool throwAfterOff = false) : IEchoBackend
    {
        private EchoControllerInfo value = controller;
        public List<bool> Commands { get; } = [];
        public Task<EchoControllerInfo[]> ListControllersAsync(CancellationToken token) => Task.FromResult(new[] { value });
        public Task<EchoControllerInfo> ReadControllerAsync(Guid id, CancellationToken token) => Task.FromResult(value);
        public Task SetEnabledAsync(Guid id, bool enabled, CancellationToken token)
        {
            Commands.Add(enabled); value = value with { IsEnabled = enabled, State = enabled ? "On" : "Off" };
            if (!enabled && throwAfterOff) throw new IOException("Fixture: state changed but SDK reply failed.");
            return Task.CompletedTask;
        }
        public void Dispose() { }
    }
}
