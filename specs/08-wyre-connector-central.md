# wyre-connector-central — Wyre Connector Central

**Repo:** `wyre-connector-central`
**License:** GPL v3
**Language:** C# / .NET 9

---

## Purpose

Standalone central discovery server. Acts as a **dumb authenticated router** — it stores and forwards encrypted blobs between mesh members but cannot read the contents of anything it routes.

Does NOT require Wyre Connector. Self-contained binary. "Connector" is in the name because it's part of the Wyre Connector platform, not because it depends on the binary.

---

## Core Philosophy

The server has exactly one thing it understands: **mesh authentication**.

Everything else is opaque blobs routed to named channels. The server learns:
- That a mesh with a given (opaque) ID exists
- That some number of nodes are members
- That blobs are being exchanged on named channels

The server does NOT learn:
- What's in any blob (all encrypted with mesh-derived keys)
- Who any node actually is (node descriptors are encrypted)
- What the named channels contain

---

## Startup Behavior

Same as relay — no special first-run flow:

```
Config exists at ./central.toml?
    NO  → create default central.toml in current directory → start immediately
    YES → start immediately with current config
```

Config hot-reloaded via `FileSystemWatcher` + 500ms debounce.

---

## The Window

```
┌─────────────────────────────────────────────┐
│ Wyre Connector Central                 [─][X]│
├─────────────────────────────────────────────┤
│                                             │
│              ● Central Active               │
│                                             │
│  Meshes:        14                          │
│  Connections:   37                          │
│  Channels:      892                         │
│  Modules:       turn                        │
│                                             │
│  [Open Config]        [Open Logs]           │
│                                             │
│  ─────────────────────────────────────────  │
│                                             │
│                    [Exit]                   │
│                                             │
└─────────────────────────────────────────────┘
```

Always-on when window is open. Close → tray. Exit button or tray Exit → clean shutdown.

Stats update every second. Stats are purely in-memory counters — no logging of what meshes/nodes exist.

---

## Config (central.toml)

```toml
[server]
port = 8080

[routing]
allow_all_channels = true     # false = use allowed_channels whitelist
allowed_channels = [          # only used if allow_all_channels = false
    "discovery",
    "key-rotation",
    "mesh-gossip",
    "stream-sessions",
    "guest-tokens"
]
max_blob_size_kb = 512
max_channels_per_mesh = 64
max_blobs_per_channel = 100
default_ttl_seconds = 300     # blobs expire if not refreshed
max_meshes = 10000

[server_modules]
enabled = []                  # e.g. ["turn"]
```

---

## HTTP API

Minimal ASP.NET Core — no controllers, minimal API only.

### Mesh Registration / Heartbeat

```
POST /mesh/{meshId}/register
Body: {
    "nonce": "base64",
    "ephemeral_pub_key": "base64",
    "encrypted_descriptor": "base64",
    "hmac": "base64(HMAC(authKey, nonce + ephemeralPubKey))"
}
Response: 200 OK | 401 Unauthorized
```

Server verifies HMAC using the stored `authKey` for this mesh. On first registration, derives and stores `authKey` from the HMAC proof. TTL: 5 minutes. Nodes re-register every 2 minutes.

### Peer Discovery

```
POST /mesh/{meshId}/discover
Body: {
    "nonce": "base64",
    "ephemeral_pub_key": "base64",
    "hmac": "base64"
}
Response: {
    "peers": [
        {
            "ephemeral_pub_key": "base64",
            "encrypted_descriptor": "base64"
        }
    ]
}
```

Returns all encrypted peer descriptors except the requesting node's own. Server cannot read these — clients decrypt locally.

### Channel Publishing

```
POST /mesh/{meshId}/channel/{channelName}/publish
Body: {
    "encrypted_blob": "base64",
    "ttl_seconds": 300,
    "nonce": "base64",
    "hmac": "base64"
}
Response: 200 OK | 401 | 403 (channel not whitelisted) | 413 (blob too large)
```

### WebSocket (persistent connection)

```
GET /mesh/{meshId}/ws?nonce={nonce}&hmac={hmac}
→ Upgrade to WebSocket
```

After upgrade, server pushes `CentralEvent` JSON messages for:

```json
{
    "sequence_number": 1847,
    "mesh_id": "base64",
    "event_type": "peer_registered",
    "encrypted_payload": "base64",
    "timestamp": "2025-01-01T00:00:00Z"
}
```

Event types:
```
peer_registered         # new node joined mesh
peer_left               # node TTL expired or explicit leave
channel_message         # new blob published on a subscribed channel
policy_changed          # mesh creator updated mesh policy (encrypted)
server_module_changed   # server module loaded/unloaded
```

On reconnect, client sends `{ "resume_from_sequence": 1847 }`. Server replays events since that sequence (buffered up to 5 minutes). If gap exceeds buffer, send `{ "event_type": "full_fetch_required" }` and client does fresh discovery call.

### Capabilities

```
GET /capabilities
Response: {
    "routing": true,
    "server_modules": ["turn"],
    "channel_policy": {
        "allow_all": true,
        "allowed_channels": []
    },
    "max_blob_size_kb": 512,
    "version": "1.0.0"
}
```

### Health Check

```
GET /health
Response: { "status": "ok", "timestamp": "...", "uptime_seconds": 3600 }
```

Used by Railway health check and status.zombidev.me monitoring.

---

## In-Memory State

```csharp
// Entire server state — no database
public class MeshRegistry
{
    // meshId → authKey (derived from HMAC proof on first registration)
    private ConcurrentDictionary<string, byte[]> _meshAuthKeys = new();

    // meshId → list of registered nodes with TTL
    private ConcurrentDictionary<string, List<RegisteredNode>> _meshNodes = new();

    // meshId → channelName → list of blobs with TTL
    private ConcurrentDictionary<string, ConcurrentDictionary<string, List<TimestampedBlob>>> _channels = new();

    // meshId → event sequence + buffer
    private ConcurrentDictionary<string, EventBuffer> _eventBuffers = new();

    // meshId → list of active WebSocket connections
    private ConcurrentDictionary<string, List<WebSocket>> _connections = new();
}
```

No database. All state in memory. Nodes re-register every 2 minutes — TTL expiry is the cleanup mechanism. Server restart = all nodes re-register within 2 minutes, no data lost (node descriptors are owned by the nodes, not the server).

### TTL Cleanup Service

```csharp
public class RegistryCleanupService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(ct))
        {
            _registry.PurgeExpiredEntries();
            // Remove nodes past TTL, blobs past TTL, empty meshes
        }
    }
}
```

---

## Server Module System

Optional capabilities added as dlls in `./modules/` folder. Loaded by ModuleHost-lite (simplified — no hash validation complexity of full Connector, just basic load + interface check).

```csharp
public interface IServerModule
{
    string Id { get; }
    string Name { get; }
    Version Version { get; }

    // Gets access to router + ASP.NET app builder
    Task InitializeAsync(IServerModuleContext ctx);
    Task ShutdownAsync();
}

public interface IServerModuleContext
{
    IEndpointRouteBuilder Router { get; }
    IConfiguration Config { get; }
    ILogger Logger { get; }
    IMeshRegistry Registry { get; }  // read-only access to mesh state
}
```

### Built-in Optional: TURN Module

When `enabled = ["turn"]` in config, TURN module provides TURN credentials for STUN/TURN-based NAT traversal (symmetric NAT case).

```
POST /mesh/{meshId}/turn/credentials
Body: { "nonce": "base64", "hmac": "base64" }
Response: {
    "username": "timestamp:nodeId",
    "password": "base64(HMAC-SHA1(turnKey, username))",
    "ttl": 86400,
    "uris": ["turn:central.wyre.zombidev.me:3478"]
}
```

Credentials derived from mesh's TURN key (HKDF("turn-credentials", meshSecret)) — standard TURN long-term credential mechanism. Server module handles actual UDP TURN relay on port 3478.

---

## Dockerfile

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 3478/udp    # TURN (if module enabled)

FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /app/publish \
    --runtime linux-musl-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:PublishTrimmed=true

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["./WyreConnectorCentral"]
```

### railway.toml

```toml
[build]
builder = "dockerfile"

[deploy]
healthcheckPath = "/health"
healthcheckTimeout = 10
restartPolicyType = "on_failure"
```

Railway reads `PORT` env var automatically. In `Program.cs`:
```csharp
builder.WebHost.UseUrls(
    $"http://+:{Environment.GetEnvironmentVariable("PORT") ?? "8080"}");
```

### docker-compose.yml (for self-hosters)

```yaml
version: "3.9"
services:
  wyre-central:
    image: ghcr.io/wyre-project/wyre-connector-central:latest
    ports:
      - "8080:8080"
      - "3478:3478/udp"    # only needed if TURN module enabled
    volumes:
      - ./central.toml:/app/central.toml
      - ./modules:/app/modules
    restart: unless-stopped
    healthcheck:
      test: ["CMD", "wget", "-q", "--spider", "http://localhost:8080/health"]
      interval: 30s
      timeout: 10s
      retries: 3
```

---

## Security Notes

- All HMAC verification uses constant-time comparison (`CryptographicOperations.FixedTimeEquals`)
- Rate limiting per mesh ID: max 100 requests/minute per mesh (prevents abuse of shared infrastructure)
- No IP logging — server logs operation counts only, not which IPs registered
- Encrypted descriptors: server stores bytes it cannot decrypt, never tries to
- Mesh IDs are opaque hashes — server cannot correlate mesh IDs to real-world identities

---

## Error Codes

```
WYRE-CENT-0001  Central unreachable (client-side)
WYRE-CENT-0002  Central authentication failed
WYRE-CENT-0003  Central HMAC invalid
WYRE-CENT-0004  Central WebSocket disconnected
WYRE-CENT-0005  Central sequence gap — replaying missed events
WYRE-CENT-0006  Central replay TTL exceeded — full state fetch
WYRE-CENT-0007  Central channel not whitelisted
WYRE-CENT-0008  Central blob size exceeded
WYRE-CENT-0009  Central max channels per mesh exceeded
WYRE-CENT-0010  Central server module unavailable
WYRE-CENT-W001  Central WebSocket reconnecting
WYRE-CENT-W002  Central sequence gap detected
```

---

## NuGet Dependencies

```xml
<PackageReference Include="Avalonia" />
<PackageReference Include="Avalonia.ReactiveUI" />
<PackageReference Include="Microsoft.AspNetCore" />
<PackageReference Include="Microsoft.Extensions.Hosting" />
<PackageReference Include="Sodium.Core" />
<PackageReference Include="Tomlyn" Version="0.20.0" />
<PackageReference Include="Serilog" />
<PackageReference Include="Serilog.AspNetCore" />
```
