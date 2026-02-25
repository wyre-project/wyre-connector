# wyre-relay — Wyre Stream Relay

**Repo:** `wyre-relay`
**License:** GPL v3
**Language:** C# / .NET 9

---

## Purpose

Standalone NAT traversal relay and Wake-on-LAN forwarder. Does NOT require Wyre Connector. Self-contained binary.

Primary use cases:
- Forward RTP/gRPC between two nodes that cannot directly reach each other (NAT traversal)
- Forward Wake-on-LAN magic packets across subnets
- Always-on low-power node on LAN (Raspberry Pi, NAS, old laptop)

---

## What It Is NOT

- Not a full Wyre node — no streaming, no capture, no input
- No Orleans — stateless by design
- No Avalonia UI complexity — one window, three buttons
- No installer — drop anywhere, run it

---

## Startup Behavior

```
Config exists at ./relay.toml?
    NO  → create default relay.toml in current directory → start immediately with defaults
    YES → start immediately with current config
```

No first-run special case. No prompts. No editor opens. User edits config via "Open Config" button whenever they want. Config changes hot-reloaded automatically.

---

## The Window

```
┌─────────────────────────────────────────────┐
│ Wyre Stream Relay                      [─][X]│
├─────────────────────────────────────────────┤
│                                             │
│              ● Relay Active                 │
│                                             │
│  [Enable Relay]   [Disable Relay]           │
│                                             │
│  [Open Config]    [Open Logs]               │
│                                             │
│  ─────────────────────────────────────────  │
│                                             │
│                    [Exit]                   │
│                                             │
└─────────────────────────────────────────────┘
```

**Always starts active** — relay is enabled immediately on every launch. No "remember last state" logic.

**Status indicator:** Green dot = active, grey = inactive, red = error.

**Enable/Disable:** Toggle relay on/off during runtime. Host can disable temporarily without exiting.

**Open Config:** Opens `relay.toml` in system default text editor.
- Windows: `notepad.exe relay.toml`
- Linux: `xdg-open relay.toml` (respects user's default editor)

**Open Logs:** Opens log file in system default text editor or shows last N lines in a simple scrollable dialog.

**Exit:** Clean shutdown. Relay.toml NOT rewritten on exit (no state persistence).

---

## Tray Behavior

Close button [X] → hides to tray, relay continues running.

Tray icon:
- Single click → restore window
- Tray menu: **Open**, **Exit**

Exit from tray = same as Exit button = clean shutdown.

Tray tooltip: "Wyre Stream Relay — Active" or "Wyre Stream Relay — Inactive"

---

## Config (relay.toml)

Created on first run with these defaults:

```toml
[relay]
listen_port = 9000
max_connections = 50

[mesh]
central_server = "https://central.wyre.zombidev.me"
mesh_passkey = ""         # set this to join a mesh

[wol]
enabled = true
allowed_subnets = []      # empty = allow all (restrict for safety)
```

Hot-reloaded via `FileSystemWatcher` with 500ms debounce. Bad TOML: keep running with last valid config, show error indicator on status dot (orange dot + tooltip "Config error — using last valid config").

---

## Relay Service

Core UDP/RTP forwarding logic. Stateless — no Orleans needed.

```csharp
public class RelayService : IHostedService
{
    // Maintains list of active forwarding sessions
    // Each session: (nodeA endpoint) ↔ (nodeB endpoint)
    // Forwards all UDP packets between the two endpoints
    // Sessions time out after configurable idle period

    public IObservable<RelayState> State { get; }  // Active, Inactive, Error
}

public enum RelayState { Active, Inactive, Error }
```

### Authentication

Only authenticated mesh members can use the relay. Authentication uses the same HMAC proof as the central server — relay verifies mesh membership before accepting any forwarding session.

Uses `Sodium.Core` for HMAC verification. No mesh secret stored — only the derived `authKey` (same as central server model).

### gRPC Control Channel

Kestrel hosts a minimal gRPC service for relay control:

```protobuf
service WyreRelay {
    // Request a forwarding session between two authenticated peers
    rpc RequestForwarding(ForwardingRequest) returns (ForwardingResponse);

    // WoL relay — send magic packet on behalf of remote node
    rpc ForwardWakeOnLan(WolRequest) returns (WolResponse);

    // Capability advertisement
    rpc GetCapabilities(CapabilitiesRequest) returns (CapabilitiesResponse);
}
```

---

## Wake-on-LAN Service

```csharp
public class WolService
{
    public WyreResult<Unit> SendMagicPacket(PhysicalAddress mac, string? subnetCidr = null)
    {
        // Check subnet is in allowed_subnets (if configured)
        if (!IsSubnetAllowed(subnetCidr))
            return WyreResult<Unit>.Fail(new WyreError
            {
                Code = "WYRE-RELY-0003",
                Type = WyreCodeType.Error,
                Message = "WoL subnet not in allowed list"
            });

        var payload = BuildMagicPacket(mac);

        // Send to subnet broadcast + global broadcast
        SendUdpBroadcast(payload, subnetCidr ?? "255.255.255.255", port: 9);

        return WyreResult<Unit>.Ok(Unit.Default);
    }

    private static byte[] BuildMagicPacket(PhysicalAddress mac)
    {
        var payload = new byte[102];
        Array.Fill(payload, (byte)0xFF, 0, 6);
        var macBytes = mac.GetAddressBytes();
        for (int i = 1; i <= 16; i++)
            macBytes.CopyTo(payload, i * 6);
        return payload;
    }
}
```

---

## Mesh Registration

Announces relay capabilities to the central server so Wyre Stream nodes know it exists and can route through it.

Node descriptor advertised:
```csharp
new RelayNodeDescriptor
{
    NodeId = _config.NodeId ?? GenerateRelayNodeId(),
    Capabilities = NodeCapability.Relay | NodeCapability.WakeOnLan,
    MaxConnections = _config.Relay.MaxConnections,
    ListenPort = _config.Relay.ListenPort
}
```

Heartbeat to central every 2 minutes to keep registration alive.

---

## Error Codes

```
WYRE-RELY-0001  Relay at capacity (max_connections reached)
WYRE-RELY-0002  Relay mesh not authenticated
WYRE-RELY-0003  Relay WoL subnet not in allowed list
WYRE-RELY-0004  Relay target unreachable
WYRE-RELY-W001  Relay connection unstable
```

---

## Build / Distribution

```bash
# Single-file, self-contained, no installer
dotnet publish -c Release --runtime win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:PublishTrimmed=true -o dist/win-x64

dotnet publish -c Release --runtime linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:PublishTrimmed=true -o dist/linux-x64
```

Output: single ~20MB binary. Drop anywhere, run it. relay.toml created in the same directory as the binary on first run.

---

## NuGet Dependencies

```xml
<PackageReference Include="Avalonia" />
<PackageReference Include="Avalonia.ReactiveUI" />
<PackageReference Include="Microsoft.Extensions.Hosting" />
<PackageReference Include="Grpc.AspNetCore" />
<PackageReference Include="Sodium.Core" />
<PackageReference Include="Tomlyn" Version="0.20.0" />
<PackageReference Include="Serilog" />
<PackageReference Include="Polly" />
```

Note: No `WyreConnector.Sdk` dependency — the relay is completely independent of the module system.
