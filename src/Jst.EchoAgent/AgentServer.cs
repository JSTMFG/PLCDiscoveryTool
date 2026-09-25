using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Jst.PlcFinder.Core;

namespace Jst.EchoAgent;

internal sealed class AgentServer : IDisposable
{
    private readonly AgentConfig config;
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly IEchoBackend backend;
    private readonly RestartCoordinator restarts;
    private readonly ConcurrentDictionary<string, long> nonces = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public AgentServer(AgentConfig config, IEchoBackend? backend = null)
    {
        this.config = config;
        this.backend = backend ?? new EchoSdkBackend(config);
        restarts = new(this.backend);
        listener.Prefixes.Add(config.Prefix);
    }

    public async Task RunAsync()
    {
        listener.Start(); AgentLog.Write($"Listening on {config.Prefix}");
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync().WaitAsync(stopping.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (HttpListenerException) when (stopping.IsCancellationRequested) { break; }
                _ = Task.Run(() => HandleAsync(context));
            }
        }
        finally { await restarts.WaitForRunningAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            byte[] body = await ReadBodyAsync(context.Request).ConfigureAwait(false);
            if (!Authenticate(context.Request, body))
            {
                await WriteAsync(context.Response, 401, new { error = "Authentication failed." }).ConfigureAwait(false); return;
            }

            string path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            string method = context.Request.HttpMethod;
            if (method == "GET" && path.Equals("/jst-echo/api/controllers", StringComparison.OrdinalIgnoreCase))
            {
                var controllers = await backend.ListControllersAsync(stopping.Token).ConfigureAwait(false);
                await WriteAsync(context.Response, 200, new EchoInventory(DateTimeOffset.UtcNow, Environment.MachineName, controllers)).ConfigureAwait(false); return;
            }
            if (method == "POST" && TryControllerRestart(path, out Guid controllerId))
            {
                var controller = (await backend.ListControllersAsync(stopping.Token).ConfigureAwait(false)).SingleOrDefault(c => c.Id == controllerId);
                if (controller is null) { await WriteAsync(context.Response, 404, new { error = "Controller is not in the local Echo inventory." }).ConfigureAwait(false); return; }
                EchoRestartJob job;
                try { job = restarts.Start(controller.Id, controller.Name); }
                catch (InvalidOperationException ex) { await WriteAsync(context.Response, 409, new { error = ex.Message }).ConfigureAwait(false); return; }
                AgentLog.Write($"Restart requested from {context.Request.RemoteEndPoint}: {controller.Name} ({controller.Id:D})");
                await WriteAsync(context.Response, 202, job).ConfigureAwait(false); return;
            }
            if (method == "GET" && TryJob(path, out Guid jobId))
            {
                if (!restarts.TryGet(jobId, out var job)) { await WriteAsync(context.Response, 404, new { error = "Reset job not found." }).ConfigureAwait(false); return; }
                await WriteAsync(context.Response, 200, job).ConfigureAwait(false); return;
            }
            await WriteAsync(context.Response, 404, new { error = "Endpoint not found." }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { TryClose(context.Response); }
        catch (Exception ex)
        {
            AgentLog.Write("Request failed: " + ex, true);
            try { await WriteAsync(context.Response, 500, new { error = ex.Message }).ConfigureAwait(false); } catch { TryClose(context.Response); }
        }
    }

    private bool Authenticate(HttpListenerRequest request, byte[] body)
    {
        if (!config.RequirePairing) return true;
        if (!long.TryParse(request.Headers["X-JST-Timestamp"], out long timestamp) ||
            string.IsNullOrWhiteSpace(request.Headers["X-JST-Nonce"]) || string.IsNullOrWhiteSpace(request.Headers["X-JST-Signature"])) return false;
        if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestamp) > 300) return false;
        string nonce = request.Headers["X-JST-Nonce"]!;
        if (!nonces.TryAdd(nonce, timestamp)) return false;
        foreach (var old in nonces.Where(x => Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - x.Value) > 600).Select(x => x.Key).Take(100)) nonces.TryRemove(old, out _);
        return EchoAuthentication.Verify(config.ApiKey, timestamp, nonce, request.HttpMethod,
            request.RawUrl?.Split('?', 2)[0] ?? "", body, request.Headers["X-JST-Signature"]!);
    }

    private static async Task<byte[]> ReadBodyAsync(HttpListenerRequest request)
    {
        if (request.ContentLength64 > 4096) throw new InvalidOperationException("Request body is too large.");
        using var memory = new MemoryStream();
        await request.InputStream.CopyToAsync(memory).ConfigureAwait(false);
        if (memory.Length > 4096) throw new InvalidOperationException("Request body is too large.");
        return memory.ToArray();
    }

    private static bool TryControllerRestart(string path, out Guid id)
    {
        id = Guid.Empty;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 5 && parts[0] == "jst-echo" && parts[1] == "api" && parts[2] == "controllers" &&
            parts[4] == "restart" && Guid.TryParse(parts[3], out id);
    }
    private static bool TryJob(string path, out Guid id)
    {
        id = Guid.Empty;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 4 && parts[0] == "jst-echo" && parts[1] == "api" && parts[2] == "jobs" && Guid.TryParse(parts[3], out id);
    }
    private static async Task WriteAsync(HttpListenerResponse response, int status, object value)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        response.StatusCode = status; response.ContentType = "application/json"; response.ContentLength64 = json.Length;
        await response.OutputStream.WriteAsync(json).ConfigureAwait(false); response.Close();
    }
    private static void TryClose(HttpListenerResponse response) { try { response.Close(); } catch { } }
    public void Stop() { if (stopping.IsCancellationRequested) return; stopping.Cancel(); listener.Stop(); }
    public void Dispose() { Stop(); listener.Close(); backend.Dispose(); stopping.Dispose(); }
}
