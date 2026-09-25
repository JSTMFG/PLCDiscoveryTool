using Microsoft.Win32;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Jst.EchoAgent;

internal sealed record AgentConfig(int Port, int EchoApiPort, string ApiKey, string CertificateThumbprint, string SdkPath, bool RequirePairing)
{
    public const string RegistryPath = @"SOFTWARE\JST\EchoResetAgent";
    public const string PortableRegistryPath = @"SOFTWARE\JST\EchoResetAgentPortable";
    public const int DefaultPort = 47443;
    public string Prefix => $"https://+:{Port}/jst-echo/";

    public static AgentConfig Load()
    {
        using var key = Registry.LocalMachine.OpenSubKey(RegistryPath) ??
            throw new InvalidOperationException("The Echo agent is not installed. Run --install from an elevated terminal.");
        return new(Convert.ToInt32(key.GetValue("Port", DefaultPort)), Convert.ToInt32(key.GetValue("EchoApiPort", 46520)),
            key.GetValue("ApiKey") as string ?? "", key.GetValue("CertificateThumbprint") as string ?? "",
            key.GetValue("SdkPath") as string ?? EchoSdkBackend.DefaultSdkPath,
            Convert.ToInt32(key.GetValue("RequirePairing", 1)) != 0);
    }

    public static AgentConfig LoadOrCreatePortable(string? requestedHost)
    {
        if (!Directory.Exists(EchoSdkBackend.DefaultSdkPath))
            throw new InvalidOperationException("FactoryTalk Logix Echo API files were not found for this Windows account.");
        using var key = Registry.CurrentUser.CreateSubKey(PortableRegistryPath);
        string host = requestedHost ?? Dns.GetHostName();
        string apiKey = key.GetValue("ApiKey") as string ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        string thumbprint = key.GetValue("CertificateThumbprint") as string ?? "";
        if (FindCertificate(thumbprint) is null)
        {
            using var certificate = CreatePortableCertificate(host);
            thumbprint = certificate.Thumbprint;
        }
        key.SetValue("Port", DefaultPort, RegistryValueKind.DWord);
        key.SetValue("EchoApiPort", 46520, RegistryValueKind.DWord);
        key.SetValue("ApiKey", apiKey);
        key.SetValue("CertificateThumbprint", thumbprint);
        key.SetValue("SdkPath", EchoSdkBackend.DefaultSdkPath);
        key.SetValue("RequirePairing", 0, RegistryValueKind.DWord);
        return new(DefaultPort, 46520, apiKey, thumbprint, EchoSdkBackend.DefaultSdkPath, false);
    }

    public static X509Certificate2? FindCertificate(string thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint)) return null;
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false)
            .OfType<X509Certificate2>().FirstOrDefault(c => c.HasPrivateKey);
    }

    private static X509Certificate2 CreatePortableCertificate(string host)
    {
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest($"CN=JST Echo Reset Agent ({host})", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new("1.3.6.1.5.5.7.3.1")], true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(Dns.GetHostName());
        if (IPAddress.TryParse(host, out var address)) san.AddIpAddress(address); else san.AddDnsName(host);
        request.CertificateExtensions.Add(san.Build());
        using var temporary = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        var persisted = X509CertificateLoader.LoadPkcs12(temporary.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite); store.Add(persisted);
        return persisted;
    }
}
