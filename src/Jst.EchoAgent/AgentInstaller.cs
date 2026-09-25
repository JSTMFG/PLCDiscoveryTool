using Microsoft.Win32;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Jst.EchoAgent;

internal static class AgentInstaller
{
    private const string ServiceName = "JstEchoResetAgent";
    private const string FirewallName = "JST Echo Reset Agent";
    private static readonly Guid AppId = new("4a85fb43-427a-4ef6-baf3-74d9ea58d837");

    public static void Install(string? requestedHost)
    {
        if (!Directory.Exists(EchoSdkBackend.DefaultSdkPath))
            throw new InvalidOperationException("FactoryTalk Logix Echo API files were not found. Install Logix Echo and its SDK components first.");

        string installDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "JST", "Echo Reset Agent");
        Directory.CreateDirectory(installDirectory);
        string installedExe = Path.Combine(installDirectory, "JST Echo Reset Agent.exe");
        string currentExe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the current executable path.");
        if (!Path.GetFullPath(currentExe).Equals(Path.GetFullPath(installedExe), StringComparison.OrdinalIgnoreCase))
            File.Copy(currentExe, installedExe, true);

        string host = requestedHost ?? Dns.GetHostName();
        var certificate = CreateCertificate(host);
        string apiKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using (var key = Registry.LocalMachine.CreateSubKey(AgentConfig.RegistryPath))
        {
            key.SetValue("Port", AgentConfig.DefaultPort, RegistryValueKind.DWord);
            key.SetValue("EchoApiPort", 46520, RegistryValueKind.DWord);
            key.SetValue("ApiKey", apiKey);
            key.SetValue("CertificateThumbprint", certificate.Thumbprint);
            key.SetValue("SdkPath", EchoSdkBackend.DefaultSdkPath);
            key.SetValue("RequirePairing", 1, RegistryValueKind.DWord);
        }

        Run("netsh", "http", "delete", "sslcert", $"ipport=0.0.0.0:{AgentConfig.DefaultPort}", allowFailure: true);
        Run("netsh", "http", "add", "sslcert", $"ipport=0.0.0.0:{AgentConfig.DefaultPort}", $"certhash={certificate.Thumbprint}", $"appid={{{AppId}}}", "certstorename=MY");
        Run("netsh", "http", "delete", "urlacl", $"url=https://+:{AgentConfig.DefaultPort}/jst-echo/", allowFailure: true);
        Run("netsh", "http", "add", "urlacl", $"url=https://+:{AgentConfig.DefaultPort}/jst-echo/", "user=NT AUTHORITY\\SYSTEM");
        Run("netsh", "advfirewall", "firewall", "delete", "rule", $"name={FirewallName}", allowFailure: true);
        Run("netsh", "advfirewall", "firewall", "add", "rule", $"name={FirewallName}", "dir=in", "action=allow", "protocol=TCP", $"localport={AgentConfig.DefaultPort}", "profile=domain,private");
        Run("sc.exe", "stop", ServiceName, allowFailure: true);
        Run("sc.exe", "delete", ServiceName, allowFailure: true);
        Run("sc.exe", "create", ServiceName, "binPath=", $"\"{installedExe}\" --service", "start=", "auto", "DisplayName=", "JST Logix Echo Reset Agent");
        Run("sc.exe", "description", ServiceName, "Allows authenticated restart of individually identified FactoryTalk Logix Echo controllers.");
        Run("sc.exe", "start", ServiceName);

        Console.WriteLine("JST Echo Reset Agent installed. Enter these values in PLC Finder > Advanced connection options:");
        Console.WriteLine($"Agent address: https://{host}:{AgentConfig.DefaultPort}/jst-echo/");
        Console.WriteLine($"Pairing key: {apiKey}");
        Console.WriteLine($"Certificate thumbprint: {certificate.Thumbprint}");
        Console.WriteLine("Save these values securely; the pairing key is not displayed again.");
    }

    public static void Uninstall()
    {
        AgentConfig? config = null;
        try { config = AgentConfig.Load(); } catch { }
        Run("sc.exe", "stop", ServiceName, allowFailure: true);
        Run("sc.exe", "delete", ServiceName, allowFailure: true);
        Run("netsh", "advfirewall", "firewall", "delete", "rule", $"name={FirewallName}", allowFailure: true);
        Run("netsh", "http", "delete", "urlacl", $"url=https://+:{config?.Port ?? AgentConfig.DefaultPort}/jst-echo/", allowFailure: true);
        Run("netsh", "http", "delete", "sslcert", $"ipport=0.0.0.0:{config?.Port ?? AgentConfig.DefaultPort}", allowFailure: true);
        if (config is not null)
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            foreach (var cert in store.Certificates.Find(X509FindType.FindByThumbprint, config.CertificateThumbprint, false)) store.Remove(cert);
        }
        Registry.LocalMachine.DeleteSubKeyTree(AgentConfig.RegistryPath, false);
        Console.WriteLine("JST Echo Reset Agent service and configuration removed. The installed executable folder can now be deleted.");
    }

    private static X509Certificate2 CreateCertificate(string host)
    {
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest($"CN=JST Echo Reset Agent ({host})", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new("1.3.6.1.5.5.7.3.1")], true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(Dns.GetHostName());
        if (IPAddress.TryParse(host, out var specified)) san.AddIpAddress(specified); else san.AddDnsName(host);
        foreach (var address in Dns.GetHostAddresses(Dns.GetHostName()).Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)) san.AddIpAddress(address);
        request.CertificateExtensions.Add(san.Build());
        using var temporary = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        var persisted = X509CertificateLoader.LoadPkcs12(temporary.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite); store.Add(persisted);
        return persisted;
    }

    private static void Run(string file, params string[] arguments) => Run(file, arguments, false);
    private static void Run(string file, string[] arguments, bool allowFailure)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {file}.");
        string output = process.StandardOutput.ReadToEnd(); string error = process.StandardError.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0 && !allowFailure) throw new InvalidOperationException($"{file} failed: {error}{output}".Trim());
    }

    private static void Run(string file, string a1, string a2, string a3, string a4, bool allowFailure) => Run(file, [a1, a2, a3, a4], allowFailure);
    private static void Run(string file, string a1, string a2, string a3, string a4, string a5, bool allowFailure) => Run(file, [a1, a2, a3, a4, a5], allowFailure);
    private static void Run(string file, string a1, string a2, bool allowFailure) => Run(file, [a1, a2], allowFailure);
}
