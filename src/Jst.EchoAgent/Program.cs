using System.ServiceProcess;

namespace Jst.EchoAgent;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Contains("--install", StringComparer.OrdinalIgnoreCase))
            {
                AgentInstaller.Install(Argument(args, "--host"));
                return 0;
            }
            if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
            {
                AgentInstaller.Uninstall();
                return 0;
            }
            if (args.Contains("--validate-sdk", StringComparer.OrdinalIgnoreCase))
            {
                var config = args.Contains("--portable", StringComparer.OrdinalIgnoreCase)
                    ? AgentConfig.LoadOrCreatePortable(Argument(args, "--host")) : AgentConfig.Load();
                using var backend = new EchoSdkBackend(config);
                foreach (var controller in await backend.ListControllersAsync(CancellationToken.None))
                    Console.WriteLine($"{controller.Id:D}  {controller.Name}  {string.Join(',', controller.IpAddresses)}  slot {controller.Slot}  {controller.State}");
                return 0;
            }
            if (args.Contains("--portable", StringComparer.OrdinalIgnoreCase))
            {
                string host = Argument(args, "--host") ?? System.Net.Dns.GetHostName();
                var config = AgentConfig.LoadOrCreatePortable(host);
                using var server = new PortableAgentServer(config);
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; server.Stop(); };
                Console.WriteLine("JST Echo Reset Agent portable mode (no administrator rights required)");
                Console.WriteLine($"Agent address: https://{host}:{config.Port}/jst-echo/");
                Console.WriteLine("Trusted-network mode is enabled. PLC Finder connects without a pairing key.");
                Console.WriteLine("Keep this window open. Disconnecting RDP is okay; signing out stops the agent. Press Ctrl+C to stop.");
                await server.RunAsync();
                return 0;
            }
            if (args.Contains("--console", StringComparer.OrdinalIgnoreCase))
            {
                using var server = new AgentServer(AgentConfig.Load());
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; server.Stop(); };
                Console.WriteLine("JST Echo Reset Agent running. Press Ctrl+C to stop.");
                await server.RunAsync();
                return 0;
            }
            if (args.Contains("--service", StringComparer.OrdinalIgnoreCase) || !Environment.UserInteractive)
            {
                ServiceBase.Run(new EchoWindowsService());
                return 0;
            }

            Console.WriteLine("JST Echo Reset Agent\n\nWithout administrator rights:\n  JST Echo Reset Agent.exe --portable --host 10.10.10.200\n\nWith administrator rights:\n  JST Echo Reset Agent.exe --install --host 10.10.10.200\n\nOther commands:\n  --validate-sdk --portable   Read the local Echo inventory as the current user\n  --console                   Run an installed configuration interactively\n  --uninstall                 Remove the Windows service");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string? Argument(string[] args, string name)
    {
        int index = Array.FindIndex(args, x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

internal sealed class EchoWindowsService : ServiceBase
{
    private AgentServer? server;
    private Task? running;
    public EchoWindowsService() => ServiceName = "JstEchoResetAgent";
    protected override void OnStart(string[] args)
    {
        server = new(AgentConfig.Load());
        running = server.RunAsync();
    }
    protected override void OnStop()
    {
        server?.Stop();
        try { running?.Wait(TimeSpan.FromSeconds(30)); } catch { }
        server?.Dispose();
    }
}
