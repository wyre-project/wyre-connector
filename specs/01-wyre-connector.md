# wyre-connector — Core.dll + ModuleHost.dll

**Repo:** `wyre-connector`
**License:** GPL v3
**Language:** C# / .NET 9

---

## Purpose

The only runtime binary in the Wyre ecosystem. Does nothing alone — shows a simple UI telling the user to install a root module. Everything else in the Wyre ecosystem is built on top of this.

Contains two foundational dlls:

- `Core.dll` — the message bus. Frozen forever. ~200 lines. Never edited after v1.
- `ModuleHost.dll` — module lifecycle, hash validation, error screens, crash fallback, dependency resolution.

---

## Core.dll

### Responsibility

The message bus. The only communication channel between all modules. Modules never import each other — they only communicate through the bus.

### Full Public Surface

```csharp
namespace Wyre.Core;

public interface IMessageBus
{
    // Fire and forget — all subscribers notified
    void Publish<T>(T message) where T : class;

    // Request/response — exactly one handler must be registered
    Task<TResponse> RequestAsync<TRequest, TResponse>(
        TRequest request,
        CancellationToken ct = default)
        where TRequest : class
        where TResponse : class;

    // Subscribe to published messages — returns IDisposable to unsubscribe
    IDisposable Subscribe<T>(Action<T> handler) where T : class;
    IDisposable Subscribe<T>(Func<T, Task> handler) where T : class;

    // Register a request handler — only one per TRequest type, throws if duplicate
    void Handle<TRequest, TResponse>(
        Func<TRequest, CancellationToken, Task<TResponse>> handler)
        where TRequest : class
        where TResponse : class;
}
```

### Implementation Notes

- Use `ConcurrentDictionary<Type, List<Delegate>>` for subscribers
- Snapshot subscriber list before iterating to prevent collection-modified exceptions
- Each subscriber is isolated — one throwing must not affect others
- Async subscribers are fire-and-forget with error logging, never awaited by publisher
- `RequestAsync` throws `InvalidOperationException` with the type name if no handler registered — never silently swallows
- Zero reflection at dispatch time — type safety enforced at compile time
- No MediatR, no external event bus library — this IS the bus

### Rate Limiting

No rate limiting on the bus. Trusted module environment. Complexity not worth it.

---

## ModuleHost.dll

### Responsibility

Everything about the module system:
- Loading modules from `.dll` files via `AssemblyLoadContext`
- Dependency graph resolution (Kahn's algorithm — topological sort with cycle detection)
- Hash / integrity validation before any ALC load
- Error screen for module load failures
- Crash fallback screen for unhandled exceptions
- Hot-unload via collectible ALC
- Lifecycle observable for the app shell

### Key Interfaces

```csharp
namespace Wyre.ModuleHost;

// What a module is
public interface IModule
{
    string Id { get; }
    string Name { get; }
    Version Version { get; }
    string[] RequiredModules { get; }   // module IDs, not type refs
    string[] OptionalModules { get; }   // load order hint only

    Task InitializeAsync(IModuleContext ctx);
    Task ShutdownAsync();
}

// Root modules — can be the entry point of a standalone experience
public interface IRootModule : IModule
{
    Task RunAsync(IModuleHost host, string[] args, CancellationToken ct);
    IReadOnlyList<string> ChildRootModules { get; }  // can spawn child root modules
    bool HasUI { get; }
    string BinaryName { get; }     // e.g. "wyre-stream"
    string DisplayName { get; }    // e.g. "Wyre Stream"
}

// What a module gets
public interface IModuleContext
{
    IMessageBus Bus { get; }
    IServiceCollection Services { get; }
    IServiceProvider Provider { get; }
    IConfiguration Config { get; }
    ILogger Logger { get; }
    string ModuleId { get; }
    string DataPath { get; }         // isolated per-module storage folder
    Version HostVersion { get; }     // Connector version, for compat checks
}

// The loader
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

### Module Manifest

Each module ships a `.module.toml` alongside its dll:

```toml
[module]
id = "wyrestream.capture.windows"
name = "Wyre Stream — Windows Capture"
version = "1.0.0"
capabilities = ["HasUI", "HasGrain", "HasService"]

[module.requires]
modules = ["wyrestream.encode", "wyrestream.acl.engine"]

[module.optional]
modules = ["wyrestream.diagnostics.network"]
```

### Dependency Resolution

Kahn's algorithm — topological sort. If `sorted.Count != manifests.Count` after the sort, a cycle exists. Report exactly which modules form the cycle. Check this BEFORE loading any ALC.

### Load Result Types

```csharp
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
    string[]? MissingDependencies
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

public enum ModuleLifecycleStage
{
    Discovered,
    DependenciesResolved,
    Loading,
    Loaded,
    FailedToLoad,
    Unloading,
    Unloaded,
    Crashed
}
```

### Error Screen (Avalonia)

Shown automatically when any module fails to load. Non-modal — app runs with modules that did load. Shows per-module error with options:

- **View Details** — expands stack trace + manifest info
- **Retry** — calls `IModuleHost.ReloadAsync(moduleId)`
- **Disable** — writes module ID to disabled list in config, won't load next launch

### Crash Fallback

Registered BEFORE any modules load via `AppDomain.CurrentDomain.UnhandledException`. If Avalonia dispatcher is alive, show Avalonia crash window. Otherwise write `crash-{timestamp}.txt` next to the exe. Never depends on any module.

---

## Integrity / Hash Validation Chain

```
App.exe
  └── verifies ModuleHost.dll hash via integrity.toml
        └── ModuleHost verifies Core.dll hash
              └── ModuleHost verifies built-in module hashes
                    └── ModuleHost handles external module hashes
```

### integrity.toml Format

```toml
[signature]
public_key = "base64(ECDsa SubjectPublicKeyInfo)"
signed_at = "2025-01-01T00:00:00Z"
signature = "base64(ECDsa signature over all hashes)"

[critical]
"ModuleHost.dll" = "sha256:abc123..."

[core]
"Core.dll" = "sha256:def456..."

[builtin]
"Mesh.Core.dll" = "sha256:..."
"Transport.Core.dll" = "sha256:..."
# ... all built-in modules

[external]
"SomeCommunityModule.dll" = { hash = "sha256:...", approved_at = "2025-06-01T12:00:00Z", approved_by_user = true }
```

The ECDsa signature covers ALL hash entries. Tampering with any hash invalidates the signature.

### App.exe Validation (only ModuleHost)

```csharp
// App.exe validates ONLY ModuleHost.dll before touching it
// Everything else is ModuleHost's job
public static IntegrityResult ValidateModuleHost()
{
    // 1. Load and parse integrity.toml
    // 2. Verify ECDsa signature over entire manifest
    // 3. Compute SHA256 of ModuleHost.dll
    // 4. Compare against manifest.Critical["ModuleHost.dll"]
    // Hard stop if any step fails
}
```

### App's Fallback Error Screen (No Avalonia)

Platform native dialogs only — no framework dependency:

```csharp
// Windows: P/Invoke MessageBoxW from user32.dll
// Linux: try zenity, then kdialog, then stderr
// Always write integrity-failure-{timestamp}.txt next to exe regardless
```

Error codes for integrity failures:

```
WYRE-INTG-0001  integrity.toml not found
WYRE-INTG-0002  integrity.toml corrupted
WYRE-INTG-0003  integrity.toml signature mismatch
WYRE-INTG-0004  ModuleHost.dll not found
WYRE-INTG-0005  ModuleHost.dll hash mismatch
WYRE-INTG-0006  Core.dll hash mismatch
WYRE-INTG-0007  Built-in module hash mismatch — user prompted
WYRE-INTG-0008  External module hash unknown — user prompted
WYRE-INTG-0009  External module hash changed — user prompted
```

### ModuleHost Validation Logic

| Module type | Hash match | Action |
|---|---|---|
| Core.dll | ✓ | Continue |
| Core.dll | ✗ | Hard stop — ModuleHost error screen |
| Built-in | ✓ | Load silently |
| Built-in | ✗ | Prompt: Load / Skip / Abort (WYRE-INTG-U001) |
| External | ✓ known hash | Load silently |
| External | ✗ changed hash | Prompt: Re-approve / Skip (WYRE-CONN-U004) |
| External | unknown | Prompt: Load+Remember / Load Once / Skip (WYRE-CONN-U003) |

### Integrity Manifest Generator (build-time)

Runs as post-build MSBuild task or in CI publish pipeline. Signs with private ECDsa key stored in CI secrets (never in repo). Outputs `integrity.toml`.

---

## Wyre Connector UI (No Root Modules Installed)

Simple Avalonia window shown when no root modules are loaded:

```
┌─────────────────────────────────────────────┐
│ Wyre Connector                              │
├─────────────────────────────────────────────┤
│                                             │
│   No root modules installed.               │
│                                             │
│   Install a root module to get started:    │
│   → wyre.zombidev.me/modules               │
│                                             │
│                    [Exit]                   │
└─────────────────────────────────────────────┘
```

---

## Launch Flags

```
wyre-connector.exe                          ← normal launch
wyre-connector.exe --root wyrestream        ← launch specific root module
wyre-connector.exe --hidden                 ← start hidden to tray
wyre-connector.exe --install                ← run installer
wyre-connector.exe --update                 ← run updater
wyre-connector.exe --uninstall              ← run uninstaller
```

`--hidden` also activates automatically when launched by OS autostart mechanism (detected via parent process or marker flag).

---

## Config

All config is TOML via Tomlyn 0.20.0. Lives at:

- Windows: `%LOCALAPPDATA%\wyre-connector\config.toml`
- Linux: `~/.config/wyre-connector/config.toml`

Hot-reloaded via `FileSystemWatcher` with 500ms debounce. Bad TOML keeps current config and shows error indicator — never crashes.

```toml
[connector]
disabled_modules = []        # module IDs to skip on load

[discovery]
central_server = "https://central.wyre.zombidev.me"  # blank = LAN/mDNS only

[logging]
level = "Warning"

[debug]
enabled = false

[debug.output]
console = true
file = true
overlay = true
bus_trace = false
perf_counters = true
grain_diagnostics = true

[debug.components]
mesh = true
transport = true
stream = true
input = false
encoder = true
decoder = true
capture = true
acl = false
bus = false
```

---

## WyreError — Universal Error Type

Lives in `Core.dll`. Used everywhere.

```csharp
public enum WyreCodeType { Error, Warning, Popup }

public record WyreError
{
    public required string Code { get; init; }        // "WYRE-MESH-W001"
    public required WyreCodeType Type { get; init; }
    public required string Message { get; init; }
    public string? Detail { get; init; }
    public string? NodeId { get; init; }
    public string? SessionId { get; init; }
    public Exception? Exception { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

public record WyreResult<T>
{
    public bool Success { get; init; }
    public T? Value { get; init; }
    public WyreError? Error { get; init; }

    public static WyreResult<T> Ok(T value) => new() { Success = true, Value = value };
    public static WyreResult<T> Fail(WyreError error) => new() { Success = false, Error = error };

    public WyreResult<TOut> Map<TOut>(Func<T, TOut> mapper) =>
        Success ? WyreResult<TOut>.Ok(mapper(Value!)) : WyreResult<TOut>.Fail(Error!);
}
```

Never throw across module boundaries. Always return `WyreResult<T>`.

---

## Debug System

```csharp
public static class WyreDebug
{
    public static bool Enabled { get; private set; }

    [Conditional("DEBUG_ENABLED")]
    public static void Log(string component, string message,
        [CallerMemberName] string caller = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0) { ... }

    public static IDisposable Time(string component, string operation)
        => Enabled ? new DebugTimer(component, operation) : NullDisposable.Instance;

    public static void TraceMessage<T>(T message) where T : class { ... }
}
```

Debug output format:
```
[15:42:03.847] [MESH    ] [MeshCore.ConnectAsync:142      ] Connecting to node ABC123
[15:42:03.855] [BUS     ] [MessageBus.Publish:34          ] → NodeConnectedMessage: {...}
```

Controlled entirely by config hot-reload. No restart needed to enable/disable.

---

## Architecture Test Requirements

All enforced in CI via `NetArchTest.Rules`:

1. `Core.dll` references nothing external
2. `ModuleHost.dll` references only `Core.dll`
3. `*.Messages.dll` references nothing
4. No module dll references any other module dll (only Core + Messages dlls)

---

## NuGet Dependencies

```xml
<PackageReference Include="Avalonia" />
<PackageReference Include="Avalonia.ReactiveUI" />
<PackageReference Include="Microsoft.Extensions.Hosting" />
<PackageReference Include="Microsoft.Extensions.DependencyInjection" />
<PackageReference Include="Tomlyn" Version="0.20.0" />
<PackageReference Include="Serilog" />
<PackageReference Include="Serilog.Extensions.Logging" />
<PackageReference Include="NetArchTest.Rules" /> <!-- test project only -->
<PackageReference Include="Sodium.Core" />        <!-- ECDsa signing -->
```

---

## Build / Publish

```bash
# Self-contained single file, trimmed, platform specific
dotnet publish -c Release \
  --runtime win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:PublishTrimmed=true

dotnet publish -c Release \
  --runtime linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:PublishTrimmed=true
```

Post-publish: run `IntegrityManifestGenerator` to produce signed `integrity.toml` alongside the binary. CI private key used for signing — never committed to repo.
