using System.Threading.Channels;

namespace Jst.PlcFinder.Core;

public record ScanProgress(int CheckedAddresses, int FoundEndpoints, int QueuedControllers, int ReadControllers);

public static class ScanPipeline
{
    public const int DiscoveryConcurrency = 32;
    public const int ReadConcurrency = 4;
    public const int QueueCapacity = 128;

    public static async Task RunAsync(
        IEnumerable<string> addresses,
        Func<string, CancellationToken, Task<DeviceIdentity?>> probe,
        Func<DeviceIdentity, CancellationToken, Task<IReadOnlyList<PlcRow>>> prepare,
        Func<PlcRow, CancellationToken, Task> read,
        IProgress<ScanProgress>? progress,
        CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var queue = Channel.CreateBounded<PlcRow>(new BoundedChannelOptions(QueueCapacity)
        { FullMode = BoundedChannelFullMode.Wait, SingleWriter = false, SingleReader = false });
        int checkedCount = 0, foundCount = 0, queuedCount = 0, readCount = 0;
        long lastReport = 0;
        var progressLock = new object();

        void Report(bool force = false)
        {
            lock (progressLock)
            {
                long now = Environment.TickCount64;
                if (!force && now - lastReport < 100) return;
                lastReport = now;
                progress?.Report(new(Volatile.Read(ref checkedCount), Volatile.Read(ref foundCount),
                    Volatile.Read(ref queuedCount), Volatile.Read(ref readCount)));
            }
        }

        async Task GuardAsync(Func<Task> run)
        {
            try { await run().ConfigureAwait(false); }
            catch { stop.Cancel(); throw; }
        }

        var consumers = Enumerable.Range(0, ReadConcurrency).Select(_ => GuardAsync(async () =>
        {
            await foreach (var row in queue.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
            {
                await read(row, stop.Token).ConfigureAwait(false);
                Interlocked.Increment(ref readCount);
                Report();
            }
        })).ToArray();

        var producer = GuardAsync(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(addresses, new ParallelOptions
                { MaxDegreeOfParallelism = DiscoveryConcurrency, CancellationToken = stop.Token }, async (ip, ct) =>
                {
                    var identity = await probe(ip, ct).ConfigureAwait(false);
                    Interlocked.Increment(ref checkedCount);
                    if (identity?.IsAllenBradley == true)
                    {
                        Interlocked.Increment(ref foundCount);
                        // Show found rows immediately, even while earlier tag reads are slow.
                        var rows = await prepare(identity, ct).ConfigureAwait(false);
                        foreach (var row in rows)
                        {
                            Interlocked.Increment(ref queuedCount);
                            await queue.Writer.WriteAsync(row, ct).ConfigureAwait(false);
                        }
                    }
                    Report();
                }).ConfigureAwait(false);
            }
            finally { queue.Writer.TryComplete(); }
        });

        await Task.WhenAll(consumers.Append(producer)).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        Report(force: true);
    }
}
