# Wyre — Security Model

---

## Trust Chain

```
CI private key (in CI secrets, never in repo)
    │ signs
    ▼
integrity.toml (ECDsa signed manifest of all hashes)
    │
    ├── App.exe reads integrity.toml
    │   verifies signature with embedded public key
    │   checks ModuleHost.dll hash
    │       FAIL → bare platform dialog (no Avalonia), write log, hard stop
    │       PASS ▼
    │
    ├── ModuleHost.dll checks Core.dll hash
    │       FAIL → ModuleHost error screen, write log, hard stop
    │       PASS ▼
    │
    ├── ModuleHost checks each built-in module hash
    │       MISMATCH → Avalonia prompt (WYRE-INTG-U001)
    │                  user: Load / Skip / Abort
    │
    └── ModuleHost checks external module hashes
            UNKNOWN  → prompt (WYRE-CONN-U003): Load+Remember / Load Once / Skip
            CHANGED  → prompt (WYRE-CONN-U004): Re-approve / Skip
            MATCH    → silent load
```

No module ever loads without passing through this chain.

---

## integrity.toml

```toml
[signature]
public_key = "base64(ECDsa SubjectPublicKeyInfo)"
signed_at = "2025-01-01T00:00:00Z"
signature = "base64(ECDsa signature over canonical payload)"

[critical]
"ModuleHost.dll" = "sha256:abc123..."

[core]
"Core.dll" = "sha256:def456..."

[builtin]
"Mesh.Core.dll" = "sha256:..."
# ... all built-in module hashes

[external]
"CommunityModule.dll" = { hash = "sha256:...", approved_at = "...", approved_by_user = true }
```

Signature covers all hash entries via canonical deterministic serialization. Tampering with any entry invalidates the signature. External section populated at runtime (never at build time).

---

## Mesh Key Derivation (Zero-Knowledge)

The central server never sees the mesh secret. All keys derived client-side:

```csharp
byte[] meshSecret = Convert.FromBase64String(userProvidedPasskey);

// Server stores authKey (derived once on first registration, not the secret)
byte[] meshId = HKDF.DeriveKey(HashAlgorithmName.SHA256, meshSecret,
    outputLength: 32, label: "mesh-id"u8);

byte[] authKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, meshSecret,
    outputLength: 32, label: "mesh-auth"u8);

// Never sent to server — used only for encrypting descriptors
byte[] encryptionKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, meshSecret,
    outputLength: 32, label: "mesh-enc"u8);

// For TURN credential generation
byte[] turnKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, meshSecret,
    outputLength: 32, label: "turn-credentials"u8);
```

Central server only stores:
- Opaque mesh IDs (SHA256 hashes)
- HMAC auth keys (derived, not the secret)
- Encrypted blobs it cannot decrypt
- IP addresses (unavoidable for rendezvous)

---

## Noise XX Peer-to-Peer

After central-assisted discovery, all communication is direct peer-to-peer via Noise Protocol XX pattern using `Sodium.Core`:

```
Initiator                               Responder
    │                                       │
    │──── e ────────────────────────────►   │
    │                                       │
    │   ◄──── e, ee, s, es ──────────────   │
    │                                       │
    │──── s, se ────────────────────────►   │
    │                                       │
    │◄══════ Encrypted channel ══════════►  │
```

Both sides are mutually authenticated. Central server is completely out of the picture after this handshake.

---

## Key Rotation Rules

**Absolute rules — no exceptions:**

1. Invalid signature on rotation announcement → silently drop, log `WYRE-AUTH-0003`, never prompt
2. Valid signature → ALWAYS prompt user, NEVER auto-accept
3. Currently connected peer rotates → prompt immediately
4. Offline peer reconnects with new key → hold in `Pending_KeyRotationApproval`, no data flows
5. User rejects → send `KeyRotationRejectedMessage` back, inform rotating node's user

**Why no auto-accept:** A legitimate key rotation is rare and expected (user has to initiate it deliberately). An unexpected rotation prompt could indicate compromise. User verification is cheap and prevents impersonation.

---

## Remote Input Security

All input events arriving from remote clients:

1. Must pass ACL check (`INPUT_CONTROL` permission)
2. Input arbiter controls which client's input is injected (latest-mover priority)
3. All injected input is logged in audit log with source node ID
4. Session lock immediately freezes all remote input regardless of ACL
5. Host always has physical input priority (Input.HostPriority module)

Input values are bounds-checked — a remote client cannot send `deltaX = INT_MAX` to cause unexpected behavior:

```csharp
public record RawMouseDelta
{
    private int _dx;
    public int Dx
    {
        get => _dx;
        init => _dx = Math.Clamp(value, -10000, 10000);  // sane bounds
    }
    // same for Dy
}
```

---

## Remote Data Bounds Validation

Every value arriving over the network is validated before use. Remote data is treated as adversarial even from authenticated peers:

```csharp
public record StreamNegotiation
{
    private int _requestedFps;
    public int RequestedFps
    {
        get => _requestedFps;
        init => _requestedFps = Math.Clamp(value, 1, 500);
    }

    private int _requestedBitrate;
    public int RequestedBitrateBps
    {
        get => _requestedBitrate;
        init => _requestedBitrate = Math.Clamp(value, 100_000, 500_000_000);
    }

    private string _sessionId = "";
    public string SessionId
    {
        get => _sessionId;
        init => _sessionId = value?.Length > 64
            ? throw new ArgumentException("SessionId too long")
            : value ?? "";
    }
}
```

Apply this pattern to ALL incoming gRPC message fields, ALL channel blobs after decryption, ALL file transfer headers.

---

## Subprocess Isolation

All media processing (capture, encode, decode) runs as separate OS processes:

```
Main process (UI + control plane)
    ├── Capture subprocess     ← if compromised, cannot access main process
    ├── Encoder subprocess     ← FFmpeg memory corruption isolated here
    └── Decoder subprocess     ← parsing untrusted network data isolated here
```

A memory corruption exploit in FFmpeg's H.264 decoder crashes the subprocess, not the main app. Main app sees pipe closed, prompts user to restart, continues.

This is the single highest-value security measure for the media processing attack surface.

---

## Parser Fuzzing

All parsers that process untrusted data (from remote peers) are fuzzed using `SharpFuzz`:

- RTP packet parser
- gRPC message deserialization
- File transfer header parser
- Central server blob deserialization (client side, after decryption)
- TOML config parser (for config received from remote)

Fuzz targets run in CI on every PR. Corpus maintained in repo.

```csharp
[FuzzTarget]
public static void FuzzRtpParser(ReadOnlySpan<byte> data)
{
    try
    {
        RtpPacket.TryParse(data, out _);
    }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
        // Expected — parser should throw cleanly, not crash
    }
}
```

---

## Session Recording Consent

When any session recording module starts recording, a non-dismissable consent indicator appears on ALL connected client streams:

- Clients cannot disable or dismiss this indicator
- It is rendered by `Notifications.dll` subscribing to `RecordingStartedMessage`
- Even if a client's UI module is customized, the indicator is injected at the stream overlay level

This is both a legal compliance measure (required in many jurisdictions) and a trust measure.

---

## What This Does NOT Prevent

Be honest with users about limitations:

| Attack | Protected? | Notes |
|---|---|---|
| DLL planting / sideloading | ✓ | Hash chain catches it |
| Module tampering on disk | ✓ | Hash chain catches it |
| Malicious external module injection | ✓ | User prompted on first load |
| integrity.toml tampering | ✓ | ECDsa signature prevents this |
| Process injection (remote code) | ✗ | OS-level attack, no app defense possible |
| Compromised FFmpeg in-process | ✓ (partial) | Subprocess isolation contains it |
| Supply chain attack on NuGet | ✓ (build-time) | Hash manifest catches different builds |
| Compromised CI / signing key | ✗ | Out of scope for app-level security |
| Memory corruption in FFmpeg | ✓ (partial) | Subprocess isolation limits blast radius |
| Malicious mesh peer sending crafted media | ✓ (partial) | Subprocess isolation + fuzz testing |
| Replay attacks on mesh messages | ✓ | Noise protocol provides replay protection |
| MITM on central server | ✓ (partial) | Noise XX authenticated, central can't MITM peers |
