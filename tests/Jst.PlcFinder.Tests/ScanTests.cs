using System.Net;
using System.Net.Sockets;
using Jst.PlcFinder.Core;

static class ScanTests
{
    public static async Task RunAsync(Action<string, Action> test)
    {
        void Check(bool value) { if (!value) throw new Exception("Scan regression"); }
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int prepared = 0, active = 0, maximum = 0, readCount = 0;
        var run = ScanPipeline.RunAsync(Enumerable.Range(0, 100).Select(x => x.ToString()),
            (ip, ct) => Task.FromResult<DeviceIdentity?>(new(ip, 1, 14, "PLC", "1", 1)),
            (id, ct) =>
            {
                if (Interlocked.Increment(ref prepared) == 100) discovered.TrySetResult();
                return Task.FromResult<IReadOnlyList<PlcRow>>([new(id, "")]);
            }, async (row, ct) =>
            {
                int n = Interlocked.Increment(ref active);
                Interlocked.Exchange(ref maximum, Math.Max(n, maximum));
                try { await gate.Task.WaitAsync(ct); Interlocked.Increment(ref readCount); }
                finally { Interlocked.Decrement(ref active); }
            }, null, CancellationToken.None);
        try
        {
            await discovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            test("Discovery continues while tag reads are blocked", () => Check(prepared == 100 && !run.IsCompleted));
        }
        finally { gate.TrySetResult(); }
        await run;
        test("Pipeline reads every queued controller with at most four readers", () => Check(readCount == 100 && maximum <= 4 && active == 0));

        using (var cancel = new CancellationTokenSource())
        {
            var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocked = ScanPipeline.RunAsync(Enumerable.Range(0, 1000).Select(x => x.ToString()),
                (ip, ct) => Task.FromResult<DeviceIdentity?>(new(ip, 1, 14, "PLC", "1", 1)),
                (id, ct) => Task.FromResult<IReadOnlyList<PlcRow>>([new(id, "")]),
                async (row, ct) => { reading.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }, null, cancel.Token);
            await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100); // Allow the bounded queue to fill.
            cancel.Cancel();
            bool cancelled = false;
            try { await blocked.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { cancelled = true; }
            test("Stop drains a full queue without deadlocking", () => Check(cancelled));
        }

        UdpClient? udp = null; TcpListener? listener = null; int port = 0;
        for (int attempt = 0; attempt < 50 && udp is null; attempt++)
        {
            port = Random.Shared.Next(20000, 60000);
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port); listener.Start();
                udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            }
            catch (SocketException) { listener?.Stop(); listener = null; udp?.Dispose(); udp = null; }
        }
        if (udp is null || listener is null) throw new InvalidOperationException("Could not reserve a loopback TCP/UDP test port.");
        using (udp)
        using (listener)
        {
        var udpTask = Task.Run(async () =>
        {
            var req = await udp.ReceiveAsync(); await Task.Delay(400);
            await udp.SendAsync(Fixtures.Identity(req.Buffer), req.RemoteEndPoint);
        });
        var tcpTask = Task.Run(async () => { using var client = await listener.AcceptTcpClientAsync(); });
        var result = await new Discovery().ProbeAsync("127.0.0.1", IPAddress.Loopback, CancellationToken.None, port);
        await Task.WhenAll(udpTask, tcpTask).WaitAsync(TimeSpan.FromSeconds(3));
        test("Failed TCP fallback still accepts a delayed UDP reply", () => Check(result?.Product == "TEST PLC"));
        }
    }
}
