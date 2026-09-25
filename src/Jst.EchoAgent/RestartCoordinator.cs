using System.Collections.Concurrent;
using Jst.PlcFinder.Core;

namespace Jst.EchoAgent;

internal interface IEchoBackend : IDisposable
{
    Task<EchoControllerInfo[]> ListControllersAsync(CancellationToken token);
    Task<EchoControllerInfo> ReadControllerAsync(Guid id, CancellationToken token);
    Task SetEnabledAsync(Guid id, bool enabled, CancellationToken token);
}

internal sealed class RestartCoordinator(IEchoBackend backend)
{
    private readonly ConcurrentDictionary<Guid, EchoRestartJob> jobs = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> locks = new();
    private readonly ConcurrentDictionary<Guid, Task> running = new();
    private readonly ConcurrentDictionary<Guid, Guid> activeControllers = new();

    public EchoRestartJob Start(Guid controllerId, string controllerName)
    {
        var job = new EchoRestartJob(Guid.NewGuid(), controllerId, controllerName, "Queued", "Waiting to restart", false, false, DateTimeOffset.UtcNow);
        if (!activeControllers.TryAdd(controllerId, job.Id)) throw new InvalidOperationException("This controller already has a reset in progress.");
        jobs[job.Id] = job;
        running[job.Id] = Task.Run(() => ExecuteAsync(job));
        return job;
    }

    public bool TryGet(Guid id, out EchoRestartJob job) => jobs.TryGetValue(id, out job!);
    public async Task WaitForRunningAsync(TimeSpan timeout)
    {
        Task all = Task.WhenAll(running.Values);
        try { await all.WaitAsync(timeout).ConfigureAwait(false); } catch { }
    }

    private async Task ExecuteAsync(EchoRestartJob initial)
    {
        var gate = locks.GetOrAdd(initial.ControllerId, _ => new(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        bool offRequested = false;
        try
        {
            var controller = await backend.ReadControllerAsync(initial.ControllerId, CancellationToken.None).ConfigureAwait(false);
            if (!controller.IsEnabled) throw new InvalidOperationException("Controller is already off. It was not changed.");
            Update(initial.Id, "TurningOff", "Turning controller off", false, false);
            offRequested = true;
            await backend.SetEnabledAsync(initial.ControllerId, false, CancellationToken.None).ConfigureAwait(false);
            await WaitForState(initial.Id, false, "Waiting for controller to turn off", TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            Update(initial.Id, "Off", "Controller is off; waiting before restart", false, false);
            await Task.Delay(2000).ConfigureAwait(false);
            await TurnOnWithRetries(initial.Id).ConfigureAwait(false);
            Update(initial.Id, "Complete", "Controller is back on", true, true);
        }
        catch (Exception ex)
        {
            if (offRequested)
            {
                try { await TurnOnWithRetries(initial.Id).ConfigureAwait(false); }
                catch (Exception recovery) { ex = new InvalidOperationException(ex.Message + " Recovery failed; controller may be off: " + recovery.Message, ex); }
            }
            Update(initial.Id, "Failed", ex.Message, true, false);
        }
        finally
        {
            gate.Release(); running.TryRemove(initial.Id, out _); activeControllers.TryRemove(initial.ControllerId, out _);
        }
    }

    private async Task TurnOnWithRetries(Guid jobId)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                Update(jobId, "TurningOn", $"Turning controller on (attempt {attempt})", false, false);
                await backend.SetEnabledAsync(jobs[jobId].ControllerId, true, CancellationToken.None).ConfigureAwait(false);
                await WaitForState(jobId, true, "Waiting for controller to turn on", TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) { last = ex; await Task.Delay(1000).ConfigureAwait(false); }
        }
        throw new InvalidOperationException("Could not turn the controller back on after three attempts: " + last?.Message, last);
    }

    private async Task WaitForState(Guid jobId, bool enabled, string message, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            Update(jobId, enabled ? "TurningOn" : "TurningOff", message, false, false);
            if ((await backend.ReadControllerAsync(jobs[jobId].ControllerId, CancellationToken.None).ConfigureAwait(false)).IsEnabled == enabled) return;
            await Task.Delay(500).ConfigureAwait(false);
        }
        throw new TimeoutException($"Echo did not confirm the controller was {(enabled ? "on" : "off")} within {timeout.TotalSeconds:N0} seconds.");
    }

    private void Update(Guid id, string status, string message, bool complete, bool success) =>
        jobs.AddOrUpdate(id, _ => throw new InvalidOperationException(), (_, old) => old with
        { Status = status, Message = message, Complete = complete, Success = success, UpdatedAt = DateTimeOffset.UtcNow });
}
