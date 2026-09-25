using System.Collections.Concurrent;
using System.Text.Json;
using Jst.PlcFinder.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;

namespace Jst.EchoAgent;

internal sealed class PortableAgentServer : IDisposable
{
    private readonly AgentConfig config;
    private readonly CancellationTokenSource stopping = new();
    private readonly IEchoBackend backend;
    private readonly RestartCoordinator restarts;
    private readonly ConcurrentDictionary<string, long> nonces = new();
    private WebApplication? app;

    public PortableAgentServer(AgentConfig config, IEchoBackend? backend = null)
    {
        this.config = config;
        this.backend = backend ?? new EchoSdkBackend(config);
        restarts = new(this.backend);
    }

    public async Task RunAsync()
    {
        using var certificate = AgentConfig.FindCertificate(config.CertificateThumbprint) ??
            throw new InvalidOperationException("The portable TLS certificate could not be loaded for this Windows account.");
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(config.Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
                listen.UseHttps(certificate);
            });
        });
        app = builder.Build();
        app.Run(HandleAsync);
        try { await app.RunAsync(stopping.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        finally { await restarts.WaitForRunningAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
    }

    private async Task HandleAsync(HttpContext context)
    {
        try
        {
            byte[] body = await ReadBodyAsync(context.Request).ConfigureAwait(false);
            if (!Authenticate(context.Request, body))
            {
                await WriteAsync(context, 401, new { error = "Authentication failed." }); return;
            }
            string path = context.Request.Path.Value?.TrimEnd('/') ?? "";
            string method = context.Request.Method;
            if (method == "GET" && path.Equals("/jst-echo/api/controllers", StringComparison.OrdinalIgnoreCase))
            {
                var controllers = await backend.ListControllersAsync(stopping.Token).ConfigureAwait(false);
                await WriteAsync(context, 200, new EchoInventory(DateTimeOffset.UtcNow, Environment.MachineName, controllers)); return;
            }
            if (method == "POST" && TryControllerRestart(path, out Guid controllerId))
            {
                var controller = (await backend.ListControllersAsync(stopping.Token).ConfigureAwait(false)).SingleOrDefault(c => c.Id == controllerId);
                if (controller is null) { await WriteAsync(context, 404, new { error = "Controller is not in the local Echo inventory." }); return; }
                EchoRestartJob job;
                try { job = restarts.Start(controller.Id, controller.Name); }
                catch (InvalidOperationException ex) { await WriteAsync(context, 409, new { error = ex.Message }); return; }
                AgentLog.Write($"Restart requested from {context.Connection.RemoteIpAddress}: {controller.Name} ({controller.Id:D})");
                await WriteAsync(context, 202, job); return;
            }
            if (method == "GET" && TryJob(path, out Guid jobId))
            {
                if (!restarts.TryGet(jobId, out var job)) { await WriteAsync(context, 404, new { error = "Reset job not found." }); return; }
                await WriteAsync(context, 200, job); return;
            }
            await WriteAsync(context, 404, new { error = "Endpoint not found." });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AgentLog.Write("Portable request failed: " + ex, true);
            if (!context.Response.HasStarted) await WriteAsync(context, 500, new { error = ex.Message });
        }
    }

    private bool Authenticate(HttpRequest request, byte[] body)
    {
        if (!config.RequirePairing) return true;
        if (!long.TryParse(request.Headers["X-JST-Timestamp"], out long timestamp) ||
            string.IsNullOrWhiteSpace(request.Headers["X-JST-Nonce"]) ||
            string.IsNullOrWhiteSpace(request.Headers["X-JST-Signature"])) return false;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - timestamp) > 300) return false;
        string nonce = request.Headers["X-JST-Nonce"]!;
        if (!nonces.TryAdd(nonce, timestamp)) return false;
        foreach (var old in nonces.Where(x => Math.Abs(now - x.Value) > 600).Select(x => x.Key).Take(100)) nonces.TryRemove(old, out _);
        return EchoAuthentication.Verify(config.ApiKey, timestamp, nonce, request.Method,
            request.Path.Value ?? "", body, request.Headers["X-JST-Signature"]!);
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request)
    {
        if (request.ContentLength > 4096) throw new InvalidOperationException("Request body is too large.");
        using var memory = new MemoryStream();
        await request.Body.CopyToAsync(memory).ConfigureAwait(false);
        if (memory.Length > 4096) throw new InvalidOperationException("Request body is too large.");
        return memory.ToArray();
    }

    private static bool TryControllerRestart(string path, out Guid id)
    {
        id = Guid.Empty; var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 5 && parts[0] == "jst-echo" && parts[1] == "api" && parts[2] == "controllers" &&
            parts[4] == "restart" && Guid.TryParse(parts[3], out id);
    }

    private static bool TryJob(string path, out Guid id)
    {
        id = Guid.Empty; var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 4 && parts[0] == "jst-echo" && parts[1] == "api" && parts[2] == "jobs" && Guid.TryParse(parts[3], out id);
    }

    private static async Task WriteAsync(HttpContext context, int status, object value)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, value, value.GetType(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)).ConfigureAwait(false);
    }

    public void Stop() { if (!stopping.IsCancellationRequested) stopping.Cancel(); }
    public void Dispose() { Stop(); app?.DisposeAsync().AsTask().GetAwaiter().GetResult(); backend.Dispose(); stopping.Dispose(); }
}
