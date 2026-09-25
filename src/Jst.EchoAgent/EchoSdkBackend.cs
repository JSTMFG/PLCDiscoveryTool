using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Jst.PlcFinder.Core;

namespace Jst.EchoAgent;

internal sealed class EchoSdkBackend : IEchoBackend
{
    public const string DefaultSdkPath = @"C:\Program Files (x86)\Rockwell Software\FactoryTalk Logix Echo\LogixDesigner";
    private readonly AgentConfig config;
    private readonly AssemblyLoadContext loadContext;
    private readonly Assembly clientAssembly;
    private readonly Assembly interfacesAssembly;
    private readonly object client;
    private readonly MethodInfo listChassis;
    private readonly MethodInfo listControllers;
    private readonly MethodInfo readController;
    private readonly MethodInfo updateController;
    private readonly SemaphoreSlim calls = new(1, 1);

    public EchoSdkBackend(AgentConfig config)
    {
        this.config = config;
        if (!Directory.Exists(config.SdkPath)) throw new InvalidOperationException($"Echo SDK folder not found: {config.SdkPath}");
        NativeLibrary.SetDllImportResolver(typeof(EchoSdkBackend).Assembly, (_, _, _) => IntPtr.Zero);
        Environment.SetEnvironmentVariable("PATH", config.SdkPath + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
        loadContext = new EchoSdkLoadContext(config.SdkPath);
        interfacesAssembly = loadContext.LoadFromAssemblyPath(Path.Combine(config.SdkPath, "RockwellAutomation.FactoryTalkLogixEcho.Api.Interfaces.dll"));
        clientAssembly = loadContext.LoadFromAssemblyPath(Path.Combine(config.SdkPath, "RockwellAutomation.FactoryTalkLogixEcho.Api.Client.dll"));
        var factory = clientAssembly.GetType("RockwellAutomation.FactoryTalkLogixEcho.Api.Client.ClientFactory", true)!;
        var create = factory.GetMethod("GetServiceApiClientV2", BindingFlags.Public | BindingFlags.Static) ??
            factory.GetMethod("GetServiceApiClientV1", BindingFlags.Public | BindingFlags.Static) ??
            throw new InvalidOperationException("The installed Echo SDK has no supported Service API client.");
        client = create.Invoke(null, ["127.0.0.1", config.EchoApiPort]) ?? throw new InvalidOperationException("Could not create the Echo Service API client.");
        listChassis = client.GetType().GetMethod("ListChassis", Type.EmptyTypes) ?? throw Missing("ListChassis");
        listControllers = client.GetType().GetMethod("ListControllers", [typeof(Guid)]) ?? throw Missing("ListControllers");
        readController = client.GetType().GetMethod("ReadController", [typeof(Guid)]) ?? throw Missing("ReadController");
        updateController = client.GetType().GetMethod("UpdateController") ?? throw Missing("UpdateController");
    }

    public async Task<EchoControllerInfo[]> ListControllersAsync(CancellationToken token)
    {
        await calls.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var chassisResult = await InvokeAsync(listChassis, client, [], token).ConfigureAwait(false);
            var chassisIds = ((System.Collections.IEnumerable)chassisResult!).Cast<object>()
                .Select(x => x.GetType().GetProperty("ChassisGuid")?.GetValue(x))
                .OfType<Guid>();
            var controllers = new Dictionary<Guid, EchoControllerInfo>();
            foreach (Guid chassisId in EchoInventoryScopes.All(chassisIds))
            {
                var result = await InvokeAsync(listControllers, client, [chassisId], token).ConfigureAwait(false);
                foreach (object value in (System.Collections.IEnumerable)result!)
                {
                    var controller = ConvertController(value);
                    if (controller.Id != Guid.Empty) controllers[controller.Id] = controller;
                }
            }
            return controllers.Values.ToArray();
        }
        finally { calls.Release(); }
    }

    public async Task<EchoControllerInfo> ReadControllerAsync(Guid id, CancellationToken token)
    {
        await calls.WaitAsync(token).ConfigureAwait(false);
        try { return ConvertController((await InvokeAsync(readController, client, [id], token).ConfigureAwait(false))!); }
        finally { calls.Release(); }
    }

    public async Task SetEnabledAsync(Guid id, bool enabled, CancellationToken token)
    {
        await calls.WaitAsync(token).ConfigureAwait(false);
        try
        {
            object current = (await InvokeAsync(readController, client, [id], token).ConfigureAwait(false))!;
            var updateType = interfacesAssembly.GetType("RockwellAutomation.FactoryTalkLogixEcho.Api.Interfaces.ControllerUpdate", true)!;
            object update = Activator.CreateInstance(updateType)!;
            foreach (var target in updateType.GetProperties().Where(p => p.CanWrite))
            {
                string sourceName = target.Name == "Name" ? "ControllerName" : target.Name;
                var source = current.GetType().GetProperty(sourceName);
                if (source is not null && target.PropertyType.IsAssignableFrom(source.PropertyType)) target.SetValue(update, source.GetValue(current));
            }
            updateType.GetProperty("IsEnabled")!.SetValue(update, enabled);
            await InvokeAsync(updateController, client, [update], token).ConfigureAwait(false);
        }
        finally { calls.Release(); }
    }

    private static EchoControllerInfo ConvertController(object value)
    {
        var type = value.GetType();
        T Get<T>(string name, T fallback = default!) => type.GetProperty(name)?.GetValue(value) is T result ? result : fallback;
        var ips = new List<string>();
        foreach (string property in new[] { "IPConfigurationData", "IPConfigurationDataSecondary" })
        {
            object? config = type.GetProperty(property)?.GetValue(value);
            if (config?.GetType().GetProperty("Address")?.GetValue(config) is System.Net.IPAddress address &&
                !address.Equals(System.Net.IPAddress.Any) && !address.Equals(System.Net.IPAddress.None)) ips.Add(address.ToString());
        }
        bool enabled = Get("IsEnabled", false);
        bool faulted = Get("IsFaulted", false);
        string state = !enabled ? "Off" : faulted ? "Faulted" : "On";
        return new(Get("ControllerGuid", Guid.Empty), Get("ControllerName", "Unnamed"), ips.Distinct().ToArray(),
            Get("ChassisGuid", Guid.Empty), Get("ChassisName", ""), Get("Slot", 0u), Get("SerialNumber", 0u), enabled, state);
    }

    private static async Task<object?> InvokeAsync(MethodInfo method, object target, object?[] arguments, CancellationToken token)
    {
        object taskObject = method.Invoke(target, arguments) ?? throw new InvalidOperationException($"Echo API {method.Name} returned no task.");
        var task = (Task)taskObject;
        await task.WaitAsync(token).ConfigureAwait(false);
        return taskObject.GetType().GetProperty("Result")?.GetValue(taskObject);
    }

    private static MissingMethodException Missing(string method) => new($"The installed Echo SDK does not provide {method}.");
    public void Dispose()
    {
        if (client is IDisposable disposable) disposable.Dispose();
        calls.Dispose();
        loadContext.Unload();
    }

    private sealed class EchoSdkLoadContext(string directory) : AssemblyLoadContext("JST Echo SDK", true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name?.StartsWith("System.", StringComparison.Ordinal) == true ||
                assemblyName.Name?.StartsWith("Microsoft.", StringComparison.Ordinal) == true) return null;
            string candidate = Path.Combine(directory, assemblyName.Name + ".dll");
            return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
        }
        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            foreach (string candidate in new[] { Path.Combine(directory, unmanagedDllName), Path.Combine(directory, unmanagedDllName + ".dll") })
                if (File.Exists(candidate)) return LoadUnmanagedDllFromPath(candidate);
            return IntPtr.Zero;
        }
    }
}
