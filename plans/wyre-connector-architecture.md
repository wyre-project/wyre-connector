# Wyre Connector Architecture Plan

## Overview

Wyre Connector is the foundational runtime binary for the entire Wyre ecosystem. It is useless on its own but becomes powerful when root modules (like Wyre Stream, Wyre Files) are installed on top of it.

## Project Structure

```
wyre-connector/
├── src/
│   ├── Core/                          # Core.dll - frozen message bus
│   │   ├── Core.csproj
│   │   ├── IMessageBus.cs
│   │   ├── MessageBus.cs
│   │   ├── WyreError.cs
│   │   └── WyreResult.cs
│   │
│   ├── ModuleHost/                    # ModuleHost.dll - module system
│   │   ├── ModuleHost.csproj
│   │   ├── IModule.cs
│   │   ├── IRootModule.cs
│   │   ├── IModuleContext.cs
│   │   ├── IModuleHost.cs
│   │   ├── ModuleLoader.cs
│   │   ├── DependencyResolver.cs
│   │   ├── IntegrityValidator.cs
│   │   ├── ModuleErrorWindow.axaml
│   │   └── CrashFallback.cs
│   │
│   ├── Connector/                     # Main executable
│   │   ├── Connector.csproj
│   │   ├── Program.cs
│   │   ├── App.axaml
│   │   ├── MainWindow.axaml
│   │   ├── NoRootModulesView.axaml
│   │   ├── TrayIcon.cs
│   │   └── ConfigWatcher.cs
│   │
│   └── Directory.Build.props
│
├── tests/
│   ├── Core.Tests/
│   └── Architecture.Tests/            # NetArchTest rules
│
├── integrity.toml                     # Hash manifest (generated at build)
├── connector.toml                     # Runtime config
└── WyreConnector.sln
```

## Core.dll - The Message Bus

**Purpose**: The frozen foundation. Never changes after v1. ~200 lines max.

### Interfaces

```csharp
// IMessageBus.cs
namespace Wyre.Core;

public interface IMessageBus
{
    // Fire and forget
    void Publish<T>(T message) where T : class;
    
    // Request/response pattern
    Task<TResponse> RequestAsync<TRequest, TResponse>(
        TRequest request, 
        CancellationToken ct = default)
        where TRequest : class
        where TResponse : class;
    
    // Subscribe to messages
    IDisposable Subscribe<T>(Action<T> handler) where T : class;
    IDisposable Subscribe<T>(Func<T, Task> handler) where T : class;
    
    // Register request handler
    void Handle<TRequest, TResponse>(
        Func<TRequest, CancellationToken, Task<TResponse>> handler)
        where TRequest : class
        where TResponse : class;
}
```

### Error System

```csharp
// WyreError.cs
namespace Wyre.Core;

public enum WyreCodeType { Error, Warning, Popup }

public record WyreError
{
    public required string Code { get; init; }        // "WYRE-CONN-0001"
    public required WyreCodeType Type { get; init; }
    public required string Message { get; init; }
    public string? Detail { get; init; }
    public string? NodeId { get; init; }
    public string? SessionId { get; init; }
    public Exception? Exception { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

// WyreResult.cs
public record WyreResult<T>
{
    public bool Success { get; init; }
    public T? Value { get; init; }
    public WyreError? Error { get; init; }
    
    public static WyreResult<T> Ok(T value) => new() { Success = true, Value = value };
    public static WyreResult<T> Fail(WyreError error) => new() { Success = false, Error = error };
}
```

## ModuleHost.dll - Module System

**Purpose**: Module lifecycle, dependency resolution, hash validation, error screens.

### Module Interfaces

```csharp
// IModule.cs
namespace Wyre.ModuleHost;

public interface IModule
{
    string Id { get; }
    string Name { get; }
    Version Version { get; }
    string[] RequiredModules { get; }   // IDs only
    string[] OptionalModules { get; }
    
    Task InitializeAsync(IModuleContext ctx);
    Task ShutdownAsync();
}

// IRootModule.cs
public interface IRootModule : IModule
{
    Task RunAsync(IModuleHost host, string[] args, CancellationToken ct);
    IReadOnlyList<string> ChildRootModules { get; }
}

// IModuleContext.cs
public interface IModuleContext
{
    IMessageBus Bus { get; }
    IServiceCollection Services { get; }
    IServiceProvider Provider { get; }
    IConfiguration Config { get; }
    ILogger Logger { get; }
    string ModuleId { get; }
    string DataPath { get; }
    Version HostVersion { get; }
}

// IModuleHost.cs
public interface IModuleHost
{
    Task<ModuleLoadResult> LoadAsync(string dllPath);
    Task<ModuleLoadResult> LoadDirectoryAsync(string modulesPath);
    Task UnloadAsync(string moduleId);
    Task ReloadAsync(string moduleId);
    
    IModule? Get(string moduleId);
    IReadOnlyList<IModule> All { get; }
    IReadOnlyList<IModule> Failed { get; }
    
    IObservable<ModuleLifecycleEvent> Lifecycle { get; }
}
```

### Dependency Resolution

Uses Kahn's algorithm for topological sort with cycle detection:

```csharp
// DependencyResolver.cs
internal static class DependencyResolver
{
    public static DependencyResolutionResult Resolve(
        IReadOnlyList<ModuleManifest> manifests)
    {
        // Build graph
        // Topological sort
        // Detect cycles
        // Return load order
    }
}
```

### Integrity Validation

```csharp
// IntegrityValidator.cs
public class IntegrityValidator
{
    // Validates hashes against integrity.toml
    // Critical (ModuleHost, Core) → hard error
    // Built-in modules → soft error, user chooses
    // External modules → prompt on first load, store hash
}
```

## Integrity Chain

```
CI signs integrity.toml with private ECDsa key
    │
App.exe verifies integrity.toml signature
    │
    ├── ModuleHost.dll hash mismatch?
    │       → bare platform dialog, hard stop
    │
ModuleHost verifies Core.dll
    │       → mismatch: error screen, hard stop
    │
ModuleHost verifies built-in modules
    │       → mismatch: prompt → load / skip / abort
    │
ModuleHost verifies external modules
            → unknown:  prompt → approve+store / load once / skip
            → changed:  prompt → re-approve / skip
            → match:    silent, load
```

## integrity.toml Structure

```toml
[signature]
public_key = "base64(ECDsa public key)"
signed_at = "2025-01-01T00:00:00Z"
signature = "base64(ECDsa signature)"

[critical]
"ModuleHost.dll" = "sha256:abc123..."

[core]
"Core.dll" = "sha256:def456..."

[builtin]
# Built-in modules if any

[external]
# Populated on first approved load
```

## Error Codes

### Connector Errors (WYRE-CONN-XXXX)

| Code | Type | Description |
|------|------|-------------|
| WYRE-CONN-0001 | Error | Module not found |
| WYRE-CONN-0002 | Error | Module hash mismatch |
| WYRE-CONN-0003 | Error | Module dependency missing |
| WYRE-CONN-0004 | Error | Module circular dependency |
| WYRE-CONN-0005 | Error | Module initialization failed |
| WYRE-CONN-0006 | Error | Module incompatible version |
| WYRE-CONN-0007 | Error | Root module already loaded |
| WYRE-CONN-0008 | Error | ModuleHost integrity failure |
| WYRE-CONN-U001 | Popup | No root modules installed |
| WYRE-CONN-U002 | Popup | Module hash mismatch - load anyway? |
| WYRE-CONN-U003 | Popup | New external module - approve? |
| WYRE-CONN-U004 | Popup | External module changed - re-approve? |

### Integrity Errors (WYRE-INTG-XXXX)

| Code | Type | Description |
|------|------|-------------|
| WYRE-INTG-0001 | Error | integrity.toml not found |
| WYRE-INTG-0002 | Error | integrity.toml corrupted |
| WYRE-INTG-0003 | Error | integrity.toml signature mismatch |
| WYRE-INTG-0004 | Error | ModuleHost.dll hash mismatch |
| WYRE-INTG-0005 | Error | Core.dll hash mismatch |
| WYRE-INTG-0006 | Error | Built-in module hash mismatch |
| WYRE-INTG-0007 | Error | External module hash changed |

## Main Connector UI

When no root modules are installed:

```
┌─────────────────────────────────────────────┐
│ Wyre Connector                              │
├─────────────────────────────────────────────┤
│                                             │
│   No root modules installed.                │
│                                             │
│   Install a root module to get started:     │
│   → wyre.zombidev.me/modules               │
│                                             │
│                    [Exit]                   │
└─────────────────────────────────────────────┘
```

## Module Rules (Enforced by Architecture Tests)

```
Core.dll          → references nothing
ModuleHost.dll    → references Core.dll only
*.Messages.dll    → references nothing
Module X.dll      → references Core.dll + any *.Messages.dll
Module X.dll      → NEVER references Module Y.dll
```

## Configuration (connector.toml)

```toml
[connector]
version = "1.0.0"

[modules]
directory = "modules"
auto_discover = true

[debug]
enabled = false

[debug.output]
console = true
file = true
```

## Hot-Reload Support

FileSystemWatcher monitors connector.toml:
- 500ms debounce
- On valid change: publish ConfigReloadedMessage on bus
- On invalid TOML: show error indicator, keep current config

## Tray Icon

- Close window → minimize to tray
- Tray menu: Open, Exit
- Exit from tray or UI button → clean shutdown

## Dependencies

| Package | Purpose |
|---------|---------|
| Avalonia | UI framework |
| ReactiveUI | MVVM |
| Tomlyn | TOML config parsing |
| Serilog | Logging |
| NetArchTest.Rules | Architecture tests |

## Build Output

```
bin/
├── wyre-connector.exe (or just wyre-connector on Linux)
├── Core.dll
├── ModuleHost.dll
├── integrity.toml
├── connector.toml (default, created on first run)
└── modules/ (empty, for root modules)
```

## Implementation Order

1. **Core.dll** - Message bus, error types, result types
2. **ModuleHost.dll** - Module interfaces, loader, dependency resolver, integrity validator
3. **Architecture.Tests** - Enforce module rules
4. **Connector executable** - Avalonia UI, tray, config hot-reload
5. **Error handling** - Error codes, debug output, crash fallback

## Next Steps

After Wyre Connector is complete:
1. Build WyreConnector.Sdk (LGPL v3) - separate repo
2. Build Wyre Stream root module - separate repo
3. Build Wyre Files root module - separate repo
4. Build installers - separate repo
