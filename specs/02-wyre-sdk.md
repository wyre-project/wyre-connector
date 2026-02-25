# wyre-sdk — WyreConnector.Sdk

**Repo:** `wyre-sdk`
**License:** LGPL v3
**NuGet:** `WyreConnector.Sdk`
**Language:** C# / .NET 9

---

## Purpose

The SDK that third-party developers (and all first-party modules) use to build client-side modules and root modules for Wyre Connector. This is the only thing a module author needs to reference — they never reference `wyre-connector` directly.

LGPL v3 means module authors can keep their modules any license, including closed source, as long as they don't modify the SDK itself without sharing changes.

---

## What It Contains

### Re-exports from Core.dll

```csharp
// These are re-exported so module authors only need one NuGet reference
public interface IMessageBus { ... }       // see 01-wyre-connector.md
public record WyreError { ... }
public record WyreResult<T> { ... }
public static class WyreDebug { ... }
```

### Re-exports from ModuleHost.dll

```csharp
public interface IModule { ... }
public interface IRootModule { ... }
public interface IModuleContext { ... }
public interface IModuleHost { ... }
public record ModuleLoadResult { ... }
// all lifecycle types
```

### Message Base Types

Common message marker interfaces that all modules use:

```csharp
namespace Wyre.Sdk.Messages;

// Marker — all bus messages should implement this for tracing
public interface IWyreMessage { }

// Marker — messages that carry a WyreError
public interface IWyreErrorMessage : IWyreMessage
{
    WyreError Error { get; }
}

// Config reload — published by connector on config file change
public record ConfigReloadedMessage(AppConfig Config) : IWyreMessage;
public record ConfigReloadFailedMessage(string ParseError) : IWyreMessage;

// Subprocess events — published by any module managing subprocesses
public record SubprocessCrashedMessage(
    string ProcessId,
    string FriendlyName,
    int ExitCode,
    string? LastStderr
) : IWyreMessage;

// Generic error event — any module can publish this
public record WyreErrorEvent(WyreError Error) : IWyreMessage;
```

### Module Base Class (optional helper)

Module authors don't have to use this but it reduces boilerplate:

```csharp
public abstract class WyreModuleBase : IModule
{
    protected IMessageBus Bus { get; private set; } = null!;
    protected ILogger Logger { get; private set; } = null!;
    protected IConfiguration Config { get; private set; } = null!;
    protected string DataPath { get; private set; } = null!;
    protected IServiceProvider Services { get; private set; } = null!;

    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract Version Version { get; }
    public virtual string[] RequiredModules => [];
    public virtual string[] OptionalModules => [];

    public virtual Task InitializeAsync(IModuleContext ctx)
    {
        Bus = ctx.Bus;
        Logger = ctx.Logger;
        Config = ctx.Config;
        DataPath = ctx.DataPath;
        Services = ctx.Provider;
        return OnInitializeAsync(ctx);
    }

    protected abstract Task OnInitializeAsync(IModuleContext ctx);

    public virtual Task ShutdownAsync() => Task.CompletedTask;

    protected void Emit(WyreError error)
        => Bus.Publish(new WyreErrorEvent(error));

    protected WyreError Error(string code, string message, string? detail = null)
        => new() { Code = code, Type = WyreCodeType.Error, Message = message, Detail = detail };

    protected WyreError Warning(string code, string message, string? detail = null)
        => new() { Code = code, Type = WyreCodeType.Warning, Message = message, Detail = detail };
}
```

### Contributor Interfaces

These allow modules to self-register capabilities into the host app without knowing about each other. All defined here so both the host (e.g. UI modules) and contributors (e.g. Stream modules) share the same interface.

```csharp
// REST API — each module contributes its own endpoints
public interface IRestApiContributor
{
    void MapEndpoints(IEndpointRouteBuilder app);
}

// Settings UI — each module contributes its own settings panel
public interface ISettingsContributor
{
    string SectionId { get; }
    string SectionName { get; }
    Control CreatePanel();  // Avalonia Control
}

// Mesh topology UI — modules can add info to the node topology view
public interface ITopologyContributor
{
    IEnumerable<TopologyBadge> GetBadgesForNode(string nodeId);
}

public record TopologyBadge(string Label, string Color, string? Tooltip);

// Hooks — modules declare which events they emit that hooks can listen to
public interface IHookEventSource
{
    IReadOnlyList<HookEventDefinition> Events { get; }
}

public record HookEventDefinition(
    string EventId,        // e.g. "wyre.file.transfer.complete"
    string DisplayName,
    string Description,
    IReadOnlyDictionary<string, string> PayloadSchema   // key → type description
);
```

### Module Manifest Attribute

Optional attribute for modules that prefer C# attributes over `.module.toml`:

```csharp
[AttributeUsage(AttributeTargets.Class)]
public class WyreModuleAttribute : Attribute
{
    public string Id { get; }
    public string Name { get; }
    public string Version { get; }
    public string[] Requires { get; init; } = [];
    public string[] Optional { get; init; } = [];

    public WyreModuleAttribute(string id, string name, string version)
    {
        Id = id;
        Name = name;
        Version = version;
    }
}

// Usage:
[WyreModule("wyrestream.capture.windows", "Windows Capture", "1.0.0",
    Requires = ["wyrestream.encode"])]
public class WindowsCaptureModule : WyreModuleBase { ... }
```

---

## *.Messages.dll Pattern

Every module that publishes messages to the bus ships a companion `*.Messages.dll` containing only pure records. Module authors follow this pattern:

```
MyModule.dll            ← business logic, references WyreConnector.Sdk
MyModule.Messages.dll   ← pure records, references nothing
```

```csharp
// MyModule.Messages.dll — zero dependencies
namespace MyCompany.MyModule.Messages;

public record MyModuleStartedMessage(string ModuleId, DateTimeOffset StartedAt);
public record MyModuleStoppedMessage(string ModuleId, string? Reason);
public record MyModuleDataMessage(string Key, byte[] Payload);
```

Other modules that want to react to these events only reference `MyModule.Messages.dll`, never `MyModule.dll`.

---

## Cross-Module Communication Patterns

### Pattern 1 — Publish/Subscribe (events)

```csharp
// Publisher (in MyModule.dll)
ctx.Bus.Publish(new MyModuleStartedMessage(Id, DateTimeOffset.UtcNow));

// Subscriber (in AnotherModule.dll — only refs MyModule.Messages.dll)
ctx.Bus.Subscribe<MyModuleStartedMessage>(msg =>
{
    // react to event
});
```

### Pattern 2 — Request/Response (queries)

```csharp
// In MyModule.Messages.dll
public record GetMyDataRequest(string Key);
public record GetMyDataResponse(byte[]? Data, bool Found);

// Handler registration (in MyModule.dll)
ctx.Bus.Handle<GetMyDataRequest, GetMyDataResponse>(async (req, ct) =>
{
    var data = await _store.GetAsync(req.Key, ct);
    return new GetMyDataResponse(data, data is not null);
});

// Caller (in AnotherModule.dll — only refs MyModule.Messages.dll)
var response = await ctx.Bus.RequestAsync<GetMyDataRequest, GetMyDataResponse>(
    new GetMyDataRequest("mykey"));

if (response.Found)
    // use response.Data
```

### Pattern 3 — Graceful degradation when module absent

```csharp
// Check if a handler is registered before calling
// RequestAsync throws InvalidOperationException if no handler found
// So catch it for optional features:

try
{
    var response = await ctx.Bus.RequestAsync<GetFileTransferStatusRequest,
        GetFileTransferStatusResponse>(new());
    // Wyre Files is installed, use it
}
catch (InvalidOperationException)
{
    // Wyre Files not installed — show "Install Wyre Files" message instead
}
```

---

## NuGet Package Structure

```
WyreConnector.Sdk/
    ├── Wyre.Core.dll              (dependency)
    ├── Wyre.ModuleHost.dll        (dependency)
    ├── Wyre.Sdk.dll               (this package)
    └── Wyre.Sdk.xml               (XML docs)
```

Targets `netstandard2.1` and `net9.0`.

---

## Versioning

The SDK version must be kept in sync with the Connector version. Breaking changes to SDK interfaces require a major version bump. Module manifests declare a minimum SDK version:

```toml
[module]
min_sdk_version = "1.0.0"
```

ModuleHost rejects modules whose `min_sdk_version` is higher than the installed Connector version with error `WYRE-CONN-0006` (IncompatibleVersion).
