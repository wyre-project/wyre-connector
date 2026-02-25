using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wyre.Core.Messaging;

namespace Wyre.ModuleHost.Contracts;

public interface IModule
{
    string Id { get; }
    string Name { get; }
    Version Version { get; }
    string[] RequiredModules { get; }   // IDs, not type refs
    string[] OptionalModules { get; }   // load order hint, not hard requirement
    
    Task InitializeAsync(IModuleContext ctx);
    Task ShutdownAsync();
}

public interface IRootModule : IModule
{
    Task RunAsync(IModuleHost host, string[] args, CancellationToken ct);
    
    // A root module can spawn child root modules
    // giving them their own isolated ModuleHost scope
    IReadOnlyList<string> ChildRootModules { get; }
}

public interface IModuleContext
{
    IMessageBus Bus { get; }
    IServiceCollection Services { get; }
    IServiceProvider Provider { get; }
    IConfiguration Config { get; }
    ILogger Logger { get; }
    string ModuleId { get; }
    string DataPath { get; }            // isolated per-module storage folder
    Version HostVersion { get; }        // app version, for compat checks
}

public interface IModuleHost
{
    Task<ModuleLoadResult> LoadAsync(string dllPath);
    Task<ModuleLoadResult> LoadDirectoryAsync(string modulesPath);
    Task UnloadAsync(string moduleId);
    Task ReloadAsync(string moduleId);
    
    IModule? Get(string moduleId);
    IReadOnlyList<IModule> All { get; }
    IReadOnlyList<IModule> Failed { get; }  // loaded but errored
    
    IObservable<ModuleLifecycleEvent> Lifecycle { get; }
}

public record ModuleLoadResult(
    bool Success,
    string ModuleId,
    IModule? Module,
    ModuleLoadError? Error
);

public record ModuleLoadError(
    ModuleLoadFailureReason Reason,
    string Message,
    Exception? Exception,
    string[]? MissingDependencies  // populated when Reason == MissingDependency
);

public enum ModuleLoadFailureReason
{
    DllNotFound,
    NoModuleImplementation,
    MissingDependency,
    CircularDependency,
    IncompatibleVersion,
    InitializationException,
    ManifestInvalid
}

public record ModuleLifecycleEvent(
    string ModuleId,
    ModuleLifecycleStage Stage,
    ModuleLoadError? Error = null
);

public enum ModuleLifecycleStage
{
    Discovered,
    DependenciesResolved,
    Loading,
    Loaded,
    FailedToLoad,
    Unloading,
    Unloaded,
    Crashed      // threw after successful load
}
