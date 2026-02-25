using System.Reactive.Subjects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wyre.Core.Messaging;
using Wyre.ModuleHost.Contracts;
using Wyre.ModuleHost.Loader;

namespace Wyre.ModuleHost;

public class ModuleHostImpl : IModuleHost
{
    private readonly IMessageBus _bus;
    private readonly IServiceProvider _provider;
    private readonly IConfiguration _config;
    private readonly ILogger<ModuleHostImpl> _logger;
    private readonly string _modulesPath;
    private readonly Version _hostVersion;

    private readonly Dictionary<string, (PluginLoadContext Alc, IModule Module)> _loaded = new();
    private readonly Dictionary<string, IModule> _failed = new();
    private readonly Subject<ModuleLifecycleEvent> _lifecycle = new();

    public IObservable<ModuleLifecycleEvent> Lifecycle => _lifecycle;
    public IReadOnlyList<IModule> All => _loaded.Values.Select(v => v.Module).ToList();
    public IReadOnlyList<IModule> Failed => _failed.Values.ToList();

    public ModuleHostImpl(
        IMessageBus bus,
        IServiceProvider provider,
        IConfiguration config,
        ILogger<ModuleHostImpl> logger,
        string modulesPath,
        Version hostVersion)
    {
        _bus = bus;
        _provider = provider;
        _config = config;
        _logger = logger;
        _modulesPath = modulesPath;
        _hostVersion = hostVersion;
    }

    public IModule? Get(string moduleId) => _loaded.TryGetValue(moduleId, out var val) ? val.Module : null;

    public async Task<ModuleLoadResult> LoadAsync(string dllPath)
    {
        var moduleId = Path.GetFileNameWithoutExtension(dllPath);
        _lifecycle.OnNext(new ModuleLifecycleEvent(moduleId, ModuleLifecycleStage.Loading));

        try
        {
            if (!File.Exists(dllPath))
            {
                return Fail(moduleId, ModuleLoadFailureReason.DllNotFound, $"DLL not found: {dllPath}");
            }

            var alc = new PluginLoadContext(dllPath);
            var assembly = alc.LoadFromAssemblyPath(dllPath);

            var moduleType = assembly.GetTypes()
                .FirstOrDefault(t => typeof(IModule).IsAssignableFrom(t) && !t.IsAbstract);

            if (moduleType == null)
            {
                alc.Unload();
                return Fail(moduleId, ModuleLoadFailureReason.NoModuleImplementation, "No IModule implementation found.");
            }

            var module = (IModule)Activator.CreateInstance(moduleType)!;
            moduleId = module.Id;

            // Check dependencies
            var missingDeps = module.RequiredModules.Where(req => !_loaded.ContainsKey(req)).ToArray();
            if (missingDeps.Any())
            {
                alc.Unload();
                return Fail(moduleId, ModuleLoadFailureReason.MissingDependency, "Missing dependencies.", missingDeps);
            }

            var ctx = new ModuleContextImpl(
                _bus,
                new ServiceCollection(),
                _provider,
                _config,
                _provider.GetRequiredService<ILoggerFactory>().CreateLogger(module.Name),
                module.Id,
                Path.Combine(_modulesPath, module.Id),
                _hostVersion
            );

            await module.InitializeAsync(ctx);
            _loaded[module.Id] = (alc, module);

            _lifecycle.OnNext(new ModuleLifecycleEvent(module.Id, ModuleLifecycleStage.Loaded));
            return new ModuleLoadResult(true, module.Id, module, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load module from {DllPath}", dllPath);
            return Fail(moduleId, ModuleLoadFailureReason.InitializationException, ex.Message, null, ex);
        }
    }

    public async Task<ModuleLoadResult> LoadDirectoryAsync(string modulesPath)
    {
        // In a real implementation, we would read manifests, resolve dependencies, and load in order.
        // For now, we just load all DLLs in the directory.
        var dlls = Directory.GetFiles(modulesPath, "*.dll");
        foreach (var dll in dlls)
        {
            await LoadAsync(dll);
        }
        return new ModuleLoadResult(true, "Directory", null, null);
    }

    public async Task UnloadAsync(string moduleId)
    {
        if (_loaded.TryGetValue(moduleId, out var val))
        {
            _lifecycle.OnNext(new ModuleLifecycleEvent(moduleId, ModuleLifecycleStage.Unloading));
            await val.Module.ShutdownAsync();
            _loaded.Remove(moduleId);
            val.Alc.Unload();
            _lifecycle.OnNext(new ModuleLifecycleEvent(moduleId, ModuleLifecycleStage.Unloaded));
        }
    }

    public async Task ReloadAsync(string moduleId)
    {
        if (!_loaded.TryGetValue(moduleId, out var val))
        {
            _logger.LogWarning("Cannot reload module {ModuleId} because it is not loaded.", moduleId);
            return;
        }

        var dllPath = val.Alc.Assemblies.FirstOrDefault()?.Location;
        if (string.IsNullOrEmpty(dllPath))
        {
            _logger.LogError("Cannot reload module {ModuleId} because its DLL path could not be determined.", moduleId);
            return;
        }

        await UnloadAsync(moduleId);
        
        // Wait a bit for the ALC to be collected and file locks to be released
        GC.Collect();
        GC.WaitForPendingFinalizers();
        
        await LoadAsync(dllPath);
    }

    private ModuleLoadResult Fail(string moduleId, ModuleLoadFailureReason reason, string message, string[]? missingDeps = null, Exception? ex = null)
    {
        var error = new ModuleLoadError(reason, message, ex, missingDeps);
        _lifecycle.OnNext(new ModuleLifecycleEvent(moduleId, ModuleLifecycleStage.FailedToLoad, error));
        return new ModuleLoadResult(false, moduleId, null, error);
    }

    private class ModuleContextImpl : IModuleContext
    {
        public IMessageBus Bus { get; }
        public IServiceCollection Services { get; }
        public IServiceProvider Provider { get; }
        public IConfiguration Config { get; }
        public ILogger Logger { get; }
        public string ModuleId { get; }
        public string DataPath { get; }
        public Version HostVersion { get; }

        public ModuleContextImpl(
            IMessageBus bus,
            IServiceCollection services,
            IServiceProvider provider,
            IConfiguration config,
            ILogger logger,
            string moduleId,
            string dataPath,
            Version hostVersion)
        {
            Bus = bus;
            Services = services;
            Provider = provider;
            Config = config;
            Logger = logger;
            ModuleId = moduleId;
            DataPath = dataPath;
            HostVersion = hostVersion;
        }
    }
}
