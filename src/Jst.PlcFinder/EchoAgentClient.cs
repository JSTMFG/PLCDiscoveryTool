using System.Net.Http.Headers;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Jst.PlcFinder.Core;

namespace Jst.PlcFinder;

public sealed class EchoAgentClient : IDisposable
{
    private readonly HttpClient http;
    private readonly string key;
    private readonly bool usePairing;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public EchoAgentClient(string address, string key, string certificateThumbprint)
    {
        if (!Uri.TryCreate(address.TrimEnd('/') + "/", UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new FormatException("Echo agent address must be an HTTPS URL.");
        usePairing = !string.IsNullOrWhiteSpace(key);
        if (usePairing) _ = Convert.FromBase64String(key);
        string expected = Normalize(certificateThumbprint);
        var handler = new HttpClientHandler
        {
            // Portable trusted-network mode accepts the agent's local self-signed TLS certificate.
            // A configured thumbprint remains pinned for legacy installed agents.
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null && (expected.Length == 0 || Normalize(certificate.GetCertHashString()) == expected)
        };
        http = new(handler) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(15) };
        this.key = key;
    }

    public Task<EchoInventory> GetInventoryAsync(CancellationToken token) => SendAsync<EchoInventory>(HttpMethod.Get, "api/controllers", token);

    public Task<EchoRestartJob> StartRestartAsync(Guid controllerId, CancellationToken token) =>
        SendAsync<EchoRestartJob>(HttpMethod.Post, $"api/controllers/{controllerId:D}/restart", token);

    public Task<EchoRestartJob> GetJobAsync(Guid jobId, CancellationToken token) =>
        SendAsync<EchoRestartJob>(HttpMethod.Get, $"api/jobs/{jobId:D}", token);

    private async Task<T> SendAsync<T>(HttpMethod method, string relative, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, relative);
        var body = Array.Empty<byte>();
        if (usePairing)
        {
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string nonce = Guid.NewGuid().ToString("N");
            string path = "/jst-echo/" + relative;
            request.Headers.Add("X-JST-Timestamp", timestamp.ToString());
            request.Headers.Add("X-JST-Nonce", nonce);
            request.Headers.Add("X-JST-Signature", EchoAuthentication.Sign(key, timestamp, nonce, method.Method, path, body));
        }
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, token).ConfigureAwait(false);
        string content = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Echo agent returned {(int)response.StatusCode}: {content.Trim()}");
        return JsonSerializer.Deserialize<T>(content, Json) ?? throw new InvalidOperationException("Echo agent returned an empty response.");
    }

    private static string Normalize(string value) => value.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
    public void Dispose() => http.Dispose();
}
