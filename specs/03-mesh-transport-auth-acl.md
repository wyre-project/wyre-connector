# Wyre Stream — Mesh, Transport, Discovery, Auth & ACL Modules

**Part of repo:** `wyre-stream`
**License:** GPL v3

These are the foundational modules that must load before anything else in Wyre Stream. They form the security and connectivity layer.

---

## Module List

```
Mesh.Core.dll               # node identity, short IDs, multi-mesh membership
Transport.Core.dll          # gRPC control plane, Noise XX, connection lifecycle
Discovery.Lan.dll           # mDNS advertise + browse
Discovery.Central.dll       # central server registration + peer fetch via WebSocket
Auth.Pairing.dll            # pairing handshake, fingerprint wordlist, short node IDs
Auth.GuestLinks.dll         # ephemeral mesh, time-limited tokens, cross-mesh
ACL.Engine.dll              # ClientPolicyGrain, permission evaluation, FluentValidation
ACL.UI.dll                  # in-session ACL editor panels (Avalonia views)
Session.Lock.dll            # session lock hotkey + client overlay
```

Each ships a companion `*.Messages.dll` with pure records.

---

## Mesh.Core

### Responsibility

Node identity, multi-mesh membership, mesh policy enforcement, key management.

### Node Identity

Each node has a short alphanumeric ID — random on first launch or user-settable (alphanumeric only, 6-12 chars). Stored in node config. Never changes unless user explicitly resets.

```csharp
public interface IMeshMembershipGrain : IGrainWithStringKey  // key = nodeId
{
    Task JoinMeshAsync(MeshCredentials credentials);
    Task LeaveMeshAsync(string meshId);
    Task<IReadOnlyList<MeshMembership>> GetActiveMeshesAsync();
    Task<GuestToken> GenerateGuestLinkAsync(GuestLinkConfig config);
    Task<NodeDescriptor> GetDescriptorAsync();
}

public record MeshCredentials(
    string MeshId,          // derived from mesh secret via HKDF
    byte[] AuthKey,         // HKDF("mesh-auth", meshSecret)
    byte[] EncryptionKey,   // HKDF("mesh-enc", meshSecret)
    byte[] TurnKey          // HKDF("turn-credentials", meshSecret)
);

public record NodeDescriptor
{
    public string NodeId { get; init; }
    public string[] LoadedRootModules { get; init; }  // e.g. ["wyrestream", "wyrefiles"]
    public Version ConnectorVersion { get; init; }
    public string[] MacAddresses { get; init; }       // for WoL
    public IPAddress? LastKnownIp { get; init; }
    // All encrypted when stored on central — central cannot read
}
```

### Multi-Mesh

A node can be a member of multiple meshes simultaneously. Each mesh has its own derived keys via HKDF. Each mesh gets its own Orleans cluster client instance. The `MeshMembershipGrain` tracks all active memberships.

### Mesh Policy

```csharp
public record MeshPolicy
{
    public string MeshId { get; init; }
    public string CreatorNodeId { get; init; }
    public MeshModuleRestriction ModuleRestriction { get; init; }
    public IReadOnlyList<string>? AllowedRootModules { get; init; } // null = all
    public int? MaxNodes { get; init; }
    public DateTimeOffset LastUpdated { get; init; }
    // signed by creator's ECDsa key
}

public enum MeshModuleRestriction { All, None, Whitelist }
```

Policy changes are gossiped to all mesh members signed by creator's key. On receiving a policy change where a currently loaded root module is now disallowed, publish `MeshPolicyModuleRestrictedMessage` — UI subscribes and shows the prompt.

### Key Rotation

```csharp
public record KeyRotationAnnouncement
{
    public string NodeId { get; init; }
    public string NodeName { get; init; }
    public byte[] OldPublicKey { get; init; }
    public byte[] NewPublicKey { get; init; }
    public DateTimeOffset RotatedAt { get; init; }
    public byte[] Signature { get; init; }  // signed by OLD key — proves legitimacy
}
```

Rules:
- Invalid signature → silently drop, never prompt (WYRE-AUTH-0003 logged internally)
- Valid signature → ALWAYS prompt user, never auto-accept
- Currently connected peer → prompt mid-session
- Offline peer reconnecting → hold in `Pending_KeyRotationApproval` state, no data flows until user decides
- Rejection → send `KeyRotationRejectedMessage` back so rotating node's user knows

Fingerprint display uses wordlist (human-memorable, verifiable over voice call), not raw hex.

### Messages (Mesh.Core.Messages.dll)

```csharp
public record NodeConnectedMessage(string NodeId, string MeshId);
public record NodeDisconnectedMessage(string NodeId, string MeshId, string? Reason);
public record MeshJoinedMessage(string MeshId);
public record MeshLeftMessage(string MeshId);
public record MeshPolicyChangedMessage(string MeshId, MeshPolicy NewPolicy);
public record MeshPolicyModuleRestrictedMessage(string MeshId, string[] DisallowedModules);
public record KeyRotationReceivedMessage(KeyRotationAnnouncement Announcement);
public record KeyRotationApprovedMessage(string NodeId, byte[] NewPublicKey);
public record KeyRotationRejectedMessage(string ByNodeId, string Reason);
```

---

## Transport.Core

### Responsibility

All network transport: gRPC control plane, Noise XX encrypted peer connections, connection lifecycle, protocol version negotiation.

### gRPC Control Plane

Hosted in Kestrel on the same instance as everything else. Bidirectional streaming RPC for the input relay channel.

```protobuf
syntax = "proto3";

service WyreControl {
    // Session negotiation
    rpc Negotiate(NegotiationRequest) returns (NegotiationResponse);

    // Input relay — bidirectional streaming, low latency
    rpc InputRelay(stream InputEvent) returns (stream InputAck);

    // Stream control
    rpc StartStream(StartStreamRequest) returns (StartStreamResponse);
    rpc StopStream(StopStreamRequest) returns (StopStreamResponse);

    // Health / feedback
    rpc ReportHealth(stream HealthReport) returns (stream HealthAck);
}
```

### Protocol Version Negotiation

Happens before any other communication. Each side advertises all loaded modules and their versions. They agree on the highest common protocol version per module:

```csharp
public record NegotiationHandshake
{
    public string NodeId { get; init; }
    public Version ConnectorVersion { get; init; }
    public Dictionary<string, Version> ModuleVersions { get; init; }
    public Dictionary<string, ProtocolRange> SupportedProtocols { get; init; }
}

public record ProtocolRange(Version Min, Version Max);

// No intersection for required module → hard reject with WYRE-TRAN-0006
// No intersection for optional module → that feature unavailable this session
```

### Noise XX Handshake

Uses `Sodium.Core` for Curve25519 keypairs and ChaCha20-Poly1305. After handshake both sides have authenticated encrypted channel. Central server is completely out of the loop from this point.

### Connection State Machine

```csharp
public enum PeerConnectionState
{
    Disconnected,
    Connecting,
    Pending_KeyRotationApproval,
    Negotiating,
    Connected,
    Reconnecting,
    Rejected
}
```

Auto-reconnect via Polly exponential backoff: 1s, 2s, 4s, 8s... up to 30s, max 10 attempts. After 10 failures → `Disconnected`, surface to user.

### Messages (Transport.Core.Messages.dll)

```csharp
public record PeerConnectionStateChangedMessage(
    string NodeId, PeerConnectionState OldState, PeerConnectionState NewState);
public record ProtocolNegotiatedMessage(string NodeId, Dictionary<string, Version> AgeedVersions);
public record ReconnectingMessage(string NodeId, int Attempt, TimeSpan NextRetryIn);
public record ReconnectSucceededMessage(string NodeId);
public record ReconnectFailedMessage(string NodeId, string Reason);
```

---

## Discovery.Lan

### Responsibility

mDNS advertising and browsing on LAN. Nodes find each other without manual IP entry.

Uses `Makaretu.Dns`. Advertises service type `_wyre._tcp.local`. Service record includes node ID, connector version, loaded root modules (as TXT records).

On discovery, publish `LanNodeDiscoveredMessage`. On loss, publish `LanNodeLostMessage`.

---

## Discovery.Central

### Responsibility

Registration with and peer discovery from the central server. Maintains a persistent WebSocket connection to central for push events.

### Crypto (Zero-Knowledge)

```csharp
// Both sides derive these independently from the shared mesh secret
// Server never sees the secret

byte[] meshId = HKDF.DeriveKey(HashAlgorithmName.SHA256, meshSecret,
    outputLength: 32, label: "mesh-id"u8);

byte[] authKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, meshSecret,
    outputLength: 32, label: "mesh-auth"u8);

byte[] encryptionKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, meshSecret,
    outputLength: 32, label: "mesh-enc"u8);
```

Node descriptor encrypted with `encryptionKey` before sending to central. Central stores blobs it cannot read.

### WebSocket Connection

One persistent WebSocket per mesh membership. Reconnects with Polly backoff.

Push events from central:
```csharp
// All arrive as CentralEvent with sequence numbers
public record CentralEvent
{
    public long SequenceNumber { get; init; }
    public string MeshId { get; init; }
    public string EventType { get; init; }
    public byte[] EncryptedPayload { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
```

On reconnect, send `resume_from_sequence` to replay missed events. If gap exceeds TTL (5 minutes), do full state fetch instead.

### Named Channels

Any module can publish/subscribe to named channels on central (Tier 1 routing):

```
POST /mesh/{meshId}/channel/{channelName}/publish
GET  /mesh/{meshId}/channel/{channelName}/subscribe   ← via WebSocket push
POST /mesh/{meshId}/channel/{channelName}/latest
```

Channel name is arbitrary — modules define their own. Central routes blobs it cannot read.

### Messages (Discovery.Central.Messages.dll)

```csharp
public record CentralConnectedMessage(string MeshId, string ServerUrl);
public record CentralDisconnectedMessage(string MeshId, string? Reason);
public record CentralReconnectingMessage(string MeshId, int Attempt);
public record PeerRegisteredMessage(string MeshId, string NodeId, byte[] EncryptedDescriptor);
public record PeerLeftMessage(string MeshId, string NodeId);
public record ChannelMessageReceivedMessage(string MeshId, string Channel, byte[] EncryptedBlob);
```

---

## Auth.Pairing

### Responsibility

Node pairing via short IDs, fingerprint confirmation, trust store management.

### Flow

```
User enters peer's short node ID (e.g. "ABC123")
    → Discovery finds the node via LAN or central
    → Noise XX handshake to get their public key
    → Derive fingerprint wordlist from their public key
    → Show fingerprint to both users
    → Both users confirm match verbally or visually
    → Trust stored in local trust store (SQLite via EF Core)
    → Future connections: compare stored key, prompt only on mismatch
```

### Fingerprint

Wordlist-based fingerprint — 4 words derived from SHA256 of public key mapped to a fixed word list (BIP39 style). Example: `vivid-table-cargo-seven`. Short enough to read aloud, distinctive enough to catch tampering.

### URL Scheme Handler

```
wyreconnector://pair/ABC123     → opens pairing UI pre-filled with node ID
```

---

## Auth.GuestLinks

### Responsibility

Ephemeral mesh generation, time-limited guest tokens, cross-mesh access.

### Ephemeral Mesh

When host generates a guest link it creates a single-use ephemeral mesh:

```csharp
public record GuestLinkConfig(
    TimeSpan Expiry,
    int MaxUses,
    NodePolicy Policy,
    string? Label          // "Support session", "John's link" etc
);
```

Ephemeral mesh ID and keys derived from guest token. Host joins the ephemeral mesh temporarily. Guest connects using the link — no existing mesh membership required. After guest disconnects or token expires, ephemeral mesh registration dropped.

### URL Scheme

```
wyrestream://guest/{base64(ephemeralMeshId)}:{base64(encryptedToken)}
```

Token decrypts to session policy. Mesh ID is opaque to central.

---

## ACL.Engine

### Responsibility

Permission evaluation, policy storage, hot-reload while connected, FluentValidation of all policy changes.

### Permissions

```csharp
public enum Permission
{
    FILE_READ,
    FILE_WRITE,
    CLIPBOARD,
    INPUT_CONTROL,
    INPUT_CONTROLLER,
    SCREEN_VIEW,
    VIEW_SPECIFIC_MONITORS,
    VIEW_SPECIFIC_PROCESSES,
    VIEW_SPECIFIC_WINDOWS,
    HIDE_TASKBAR,
    HIDE_NOTIFICATIONS,
    APP_LAUNCH,
    MICROPHONE,
    CAMERA,
    DRAG_DROP,
    CHAT,
    RECORD_SESSION
}

public record ViewPermissions
{
    public List<string>? AllowedMonitorIds { get; init; }   // null = all
    public List<string>? AllowedProcessNames { get; init; } // null = all
    public List<string>? AllowedWindowHandles { get; init; }
    public bool HideTaskbar { get; init; }
    public bool HideNotifications { get; init; }
}
```

### Interface

```csharp
public interface IAclEngine
{
    ValueTask<bool> IsPermittedAsync(string nodeId, Permission permission);
    Task<NodePolicy> GetPolicyAsync(string nodeId);
    Task SetPolicyAsync(string nodeId, NodePolicy policy, string authorizedBy);
    IObservable<PolicyChangedEvent> PolicyChanges { get; }
}
```

Policy changes published via `PolicyChangedEvent` on the Orleans stream — active sessions subscribe and react immediately without reconnect.

Pre-connection policies stored in SQLite via EF Core. Loaded into `ClientPolicyGrain` when node connects.

### ACL.UI

Avalonia views for the in-session ACL editor. Only references `ACL.Engine` via `IAclEngine` interface from the SDK. Hosts can edit per-client policies while clients are connected — changes take effect immediately.

---

## Session.Lock

### Responsibility

Instant session lock — freezes all client input passthrough but keeps stream alive.

Clients see "Session Locked" overlay. Host can lock via hotkey or UI button. Host can unlock the same way. ACL check required — only permitted clients can request unlock.

Lock state stored in `SessionLockGrain`. All `InputArbiterGrain` instances check lock state before injecting any input.

---

## Error Codes

```
WYRE-MESH-0001  Mesh not found
WYRE-MESH-0002  Mesh passkey invalid
WYRE-MESH-0003  Mesh policy violation — module not allowed
WYRE-MESH-0004  Mesh policy changed — module now disallowed
WYRE-MESH-0005  Mesh creator key invalid
WYRE-MESH-0006  Mesh full
WYRE-MESH-0007  Mesh channel not allowed by central policy
WYRE-MESH-0008  Module restriction — join rejected
WYRE-MESH-W001  Mesh peer connection degraded
WYRE-MESH-W002  Mesh peer latency high
WYRE-MESH-U001  Key rotation received — accept new key?
WYRE-MESH-U002  Mesh policy changed — disable module to stay connected?
WYRE-MESH-U003  Module restricted — disable to join mesh?

WYRE-AUTH-0001  Node ID not found
WYRE-AUTH-0002  Pairing fingerprint rejected
WYRE-AUTH-0003  Key rotation signature invalid (silent drop)
WYRE-AUTH-0004  Key rotation rejected by peer
WYRE-AUTH-0005  Guest link expired
WYRE-AUTH-0006  Guest link max uses reached
WYRE-AUTH-0007  Guest link invalid token
WYRE-AUTH-0008  Noise handshake failed
WYRE-AUTH-0009  Peer public key changed — rotation required
WYRE-AUTH-U001  Pairing request — confirm fingerprint
WYRE-AUTH-U002  Unknown peer — trust and connect?

WYRE-ACL-0001   Permission denied — FILE_READ
WYRE-ACL-0002   Permission denied — FILE_WRITE
WYRE-ACL-0003   Permission denied — CLIPBOARD
WYRE-ACL-0004   Permission denied — INPUT_CONTROL
WYRE-ACL-0005   Permission denied — INPUT_CONTROLLER
WYRE-ACL-0006   Permission denied — SCREEN_VIEW
WYRE-ACL-0007   Permission denied — MICROPHONE
WYRE-ACL-0008   Permission denied — CAMERA
WYRE-ACL-0009   Permission denied — DRAG_DROP
WYRE-ACL-0010   Permission denied — CHAT
WYRE-ACL-0011   Permission denied — APP_LAUNCH
WYRE-ACL-0012   Permission denied — VIEW_SPECIFIC_MONITORS
WYRE-ACL-0013   Permission denied — VIEW_SPECIFIC_PROCESSES

WYRE-TRAN-0001  Connection timeout
WYRE-TRAN-0002  Connection refused
WYRE-TRAN-0003  Peer unreachable
WYRE-TRAN-0004  TURN credentials failed
WYRE-TRAN-0005  ICE negotiation failed
WYRE-TRAN-0006  Protocol version mismatch
WYRE-TRAN-0007  Module version incompatible
WYRE-TRAN-0008  gRPC channel failed
WYRE-TRAN-0009  WebSocket unexpected close
WYRE-CENT-0001  Central unreachable
WYRE-CENT-0002  Central authentication failed
WYRE-CENT-0003  Central HMAC invalid
WYRE-CENT-0004  Central WebSocket disconnected
WYRE-CENT-0005  Central sequence gap — replaying
WYRE-CENT-0006  Central replay TTL exceeded — full fetch
WYRE-CENT-0007  Central channel not whitelisted
WYRE-CENT-0008  Central blob size exceeded
WYRE-CENT-0009  Central max channels exceeded
WYRE-CENT-0010  Central server module unavailable
WYRE-CENT-W001  Central WebSocket reconnecting
WYRE-CENT-W002  Central sequence gap detected
```

---

## NuGet Dependencies

```xml
<PackageReference Include="WyreConnector.Sdk" />
<PackageReference Include="Microsoft.Orleans.Sdk" Version="9.2.1" />
<PackageReference Include="Grpc.AspNetCore" />
<PackageReference Include="Grpc.Tools" />
<PackageReference Include="Makaretu.Dns" />
<PackageReference Include="Sodium.Core" />
<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" />
<PackageReference Include="FluentValidation" />
<PackageReference Include="Polly" />
<PackageReference Include="Polly.Extensions.Http" />
```
