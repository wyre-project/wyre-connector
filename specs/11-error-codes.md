# Wyre — Complete Error Code Reference

All error codes follow the format:
```
WYRE-{COMPONENT}-{NUMBER}      ← errors
WYRE-{COMPONENT}-W{NUMBER}     ← warnings
WYRE-{COMPONENT}-U{NUMBER}     ← user prompts / popups
```

Every code is logged in debug output and written to the audit log regardless of whether it is displayed to the user.

Hooks can auto-respond to any `U` code via config:
```toml
[hooks.auto_respond]
"WYRE-FILE-U001" = "approve"
```

---

## CONN — Connector / Module System

| Code | Type | Message |
|---|---|---|
| WYRE-CONN-0001 | Error | Module not found |
| WYRE-CONN-0002 | Error | Module hash mismatch |
| WYRE-CONN-0003 | Error | Module dependency missing |
| WYRE-CONN-0004 | Error | Module circular dependency |
| WYRE-CONN-0005 | Error | Module initialization failed |
| WYRE-CONN-0006 | Error | Module incompatible version |
| WYRE-CONN-0007 | Error | Root module already loaded |
| WYRE-CONN-0008 | Error | ModuleHost integrity failure |
| WYRE-CONN-U001 | Popup | No root modules installed — install one to get started |
| WYRE-CONN-U002 | Popup | Module hash mismatch — load anyway? |
| WYRE-CONN-U003 | Popup | New external module — approve? |
| WYRE-CONN-U004 | Popup | External module changed — re-approve? |
| WYRE-CONN-U005 | Popup | Connector required — install now? |

---

## INTG — Integrity / Hash Validation

| Code | Type | Message |
|---|---|---|
| WYRE-INTG-0001 | Error | integrity.toml not found |
| WYRE-INTG-0002 | Error | integrity.toml corrupted |
| WYRE-INTG-0003 | Error | integrity.toml signature mismatch |
| WYRE-INTG-0004 | Error | ModuleHost.dll not found |
| WYRE-INTG-0005 | Error | ModuleHost.dll hash mismatch |
| WYRE-INTG-0006 | Error | Core.dll hash mismatch |
| WYRE-INTG-0007 | Error | Built-in module hash mismatch |
| WYRE-INTG-W001 | Warning | External module hash not yet approved |
| WYRE-INTG-U001 | Popup | Built-in module modified — load / skip / abort? |

---

## MESH — Mesh Networking

| Code | Type | Message |
|---|---|---|
| WYRE-MESH-0001 | Error | Mesh not found |
| WYRE-MESH-0002 | Error | Mesh passkey invalid |
| WYRE-MESH-0003 | Error | Mesh policy violation — module not allowed |
| WYRE-MESH-0004 | Error | Mesh policy changed — module now disallowed |
| WYRE-MESH-0005 | Error | Mesh creator key invalid |
| WYRE-MESH-0006 | Error | Mesh full — max nodes reached |
| WYRE-MESH-0007 | Error | Mesh channel not allowed by central policy |
| WYRE-MESH-0008 | Error | Module restriction — join rejected |
| WYRE-MESH-W001 | Warning | Mesh peer connection degraded |
| WYRE-MESH-W002 | Warning | Mesh peer latency high |
| WYRE-MESH-U001 | Popup | Key rotation received — accept new key? |
| WYRE-MESH-U002 | Popup | Mesh policy changed — disable module to stay connected? |
| WYRE-MESH-U003 | Popup | Module restricted — disable to join mesh? |

---

## CENT — Central Server

| Code | Type | Message |
|---|---|---|
| WYRE-CENT-0001 | Error | Central unreachable |
| WYRE-CENT-0002 | Error | Central authentication failed |
| WYRE-CENT-0003 | Error | Central HMAC invalid |
| WYRE-CENT-0004 | Error | Central WebSocket disconnected |
| WYRE-CENT-0005 | Error | Central sequence gap — replaying missed events |
| WYRE-CENT-0006 | Error | Central replay TTL exceeded — full state fetch required |
| WYRE-CENT-0007 | Error | Central channel not whitelisted |
| WYRE-CENT-0008 | Error | Central blob size exceeded |
| WYRE-CENT-0009 | Error | Central max channels per mesh exceeded |
| WYRE-CENT-0010 | Error | Central server module not available |
| WYRE-CENT-W001 | Warning | Central WebSocket reconnecting |
| WYRE-CENT-W002 | Warning | Central sequence gap detected |

---

## AUTH — Authentication / Pairing

| Code | Type | Message |
|---|---|---|
| WYRE-AUTH-0001 | Error | Node ID not found |
| WYRE-AUTH-0002 | Error | Pairing fingerprint rejected |
| WYRE-AUTH-0003 | Error | Key rotation signature invalid (silently dropped, logged only) |
| WYRE-AUTH-0004 | Error | Key rotation rejected by peer |
| WYRE-AUTH-0005 | Error | Guest link expired |
| WYRE-AUTH-0006 | Error | Guest link max uses reached |
| WYRE-AUTH-0007 | Error | Guest link invalid token |
| WYRE-AUTH-0008 | Error | Noise handshake failed |
| WYRE-AUTH-0009 | Error | Peer public key changed — key rotation required |
| WYRE-AUTH-U001 | Popup | Pairing request — confirm fingerprint |
| WYRE-AUTH-U002 | Popup | Unknown peer — trust and connect? |

---

## ACL — Access Control

| Code | Type | Message |
|---|---|---|
| WYRE-ACL-0001 | Error | Permission denied — FILE_READ |
| WYRE-ACL-0002 | Error | Permission denied — FILE_WRITE |
| WYRE-ACL-0003 | Error | Permission denied — CLIPBOARD |
| WYRE-ACL-0004 | Error | Permission denied — INPUT_CONTROL |
| WYRE-ACL-0005 | Error | Permission denied — INPUT_CONTROLLER |
| WYRE-ACL-0006 | Error | Permission denied — SCREEN_VIEW |
| WYRE-ACL-0007 | Error | Permission denied — MICROPHONE |
| WYRE-ACL-0008 | Error | Permission denied — CAMERA |
| WYRE-ACL-0009 | Error | Permission denied — DRAG_DROP |
| WYRE-ACL-0010 | Error | Permission denied — CHAT |
| WYRE-ACL-0011 | Error | Permission denied — APP_LAUNCH |
| WYRE-ACL-0012 | Error | Permission denied — VIEW_SPECIFIC_MONITORS |
| WYRE-ACL-0013 | Error | Permission denied — VIEW_SPECIFIC_PROCESSES |

---

## TRAN — Transport / Connection

| Code | Type | Message |
|---|---|---|
| WYRE-TRAN-0001 | Error | Connection timeout |
| WYRE-TRAN-0002 | Error | Connection refused |
| WYRE-TRAN-0003 | Error | Peer unreachable |
| WYRE-TRAN-0004 | Error | TURN credentials failed |
| WYRE-TRAN-0005 | Error | ICE negotiation failed |
| WYRE-TRAN-0006 | Error | Protocol version mismatch — no common version |
| WYRE-TRAN-0007 | Error | Module version incompatible |
| WYRE-TRAN-0008 | Error | gRPC channel failed |
| WYRE-TRAN-0009 | Error | WebSocket unexpected close |

---

## STRM — Stream Session

| Code | Type | Message |
|---|---|---|
| WYRE-STRM-0001 | Error | Stream session not found |
| WYRE-STRM-0002 | Error | Stream max clients reached |
| WYRE-STRM-0003 | Error | Stream encoder failed |
| WYRE-STRM-0004 | Error | Stream decoder failed |
| WYRE-STRM-0005 | Error | Stream capture failed |
| WYRE-STRM-0006 | Error | Stream subprocess crashed |
| WYRE-STRM-0007 | Error | Stream subprocess restart rejected by user |
| WYRE-STRM-0008 | Error | Simulcast tier unavailable |
| WYRE-STRM-0009 | Error | Adaptive rate controller error |
| WYRE-STRM-0010 | Error | Virtual display unavailable |
| WYRE-STRM-W001 | Warning | Bitrate reduced due to network conditions |
| WYRE-STRM-W002 | Warning | Switching to simulcast — client quality gap exceeded 85% |
| WYRE-STRM-W003 | Warning | Encoder falling back to software — no hardware encoder |
| WYRE-STRM-U001 | Popup | Subprocess crashed — restart? |
| WYRE-STRM-U002 | Popup | Recording started — consent indicator (non-dismissable) |

---

## ENCD — Encoder

| Code | Type | Message |
|---|---|---|
| WYRE-ENCD-0001 | Error | Encoder initialization failed |
| WYRE-ENCD-0002 | Error | Encoder hardware unavailable — falling back to software |
| WYRE-ENCD-0003 | Error | Encoder bitrate out of range |
| WYRE-ENCD-0004 | Error | Encoder fps out of range |
| WYRE-ENCD-0005 | Error | Encoder subprocess exited unexpectedly |
| WYRE-ENCD-W001 | Warning | Encode time exceeding frame budget |
| WYRE-ENCD-W002 | Warning | Hardware encoder unavailable — using software |

---

## DCOD — Decoder

| Code | Type | Message |
|---|---|---|
| WYRE-DCOD-0001 | Error | Decoder initialization failed |
| WYRE-DCOD-0002 | Error | Decoder frame corrupted |
| WYRE-DCOD-0003 | Error | Decoder jitter buffer overflow |
| WYRE-DCOD-0004 | Error | Decoder subprocess exited unexpectedly |

---

## CAPT — Capture

| Code | Type | Message |
|---|---|---|
| WYRE-CAPT-0001 | Error | Capture device not found |
| WYRE-CAPT-0002 | Error | Capture permission denied (OS level) |
| WYRE-CAPT-0003 | Error | Capture resolution unsupported |
| WYRE-CAPT-0004 | Error | Capture framerate unsupported |
| WYRE-CAPT-0005 | Error | Capture subprocess exited unexpectedly |
| WYRE-CAPT-0006 | Error | Wayland capture unsupported on this compositor |
| WYRE-CAPT-W001 | Warning | Capture framerate dropping below target |

---

## INPT — Input

| Code | Type | Message |
|---|---|---|
| WYRE-INPT-0001 | Error | Input injection unavailable |
| WYRE-INPT-0002 | Error | Input device not found |
| WYRE-INPT-0003 | Error | uinput permission denied — user not in input group |
| WYRE-INPT-0004 | Error | Controller injection unavailable — ViGEm not installed |
| WYRE-INPT-0005 | Error | Input arbitration conflict |

---

## FILE — File Transfer

| Code | Type | Message |
|---|---|---|
| WYRE-FILE-0001 | Error | Transfer rejected by host |
| WYRE-FILE-0002 | Error | Transfer approval timeout |
| WYRE-FILE-0003 | Error | Transfer interrupted |
| WYRE-FILE-0004 | Error | Transfer checksum mismatch |
| WYRE-FILE-0005 | Error | Transfer bandwidth cap exceeded |
| WYRE-FILE-0006 | Error | Transfer destination full |
| WYRE-FILE-U001 | Popup | Incoming file transfer — approve? |
| WYRE-FILE-U002 | Popup | Incoming drag-drop — approve? |

---

## RELY — Relay

| Code | Type | Message |
|---|---|---|
| WYRE-RELY-0001 | Error | Relay at capacity |
| WYRE-RELY-0002 | Error | Relay mesh not authenticated |
| WYRE-RELY-0003 | Error | Relay WoL subnet not in allowed list |
| WYRE-RELY-0004 | Error | Relay target unreachable |
| WYRE-RELY-W001 | Warning | Relay connection unstable |

---

## Adding New Error Codes

When adding a new module or new error condition:

1. Choose the correct component prefix (add a new one if truly a new subsystem)
2. Errors: next sequential number in that component's error range
3. Warnings: next sequential `W` number
4. Popups: next sequential `U` number
5. Add to this reference file
6. Add to `wyre-docs` with full description, cause, and fix
7. Use `WyreError` record — never throw raw exceptions across module boundaries
8. Emit via `Bus.Publish(new WyreErrorEvent(error))` so any module can observe

### WyreError construction

```csharp
var error = new WyreError
{
    Code = "WYRE-STRM-0006",
    Type = WyreCodeType.Error,
    Message = "Stream subprocess crashed",
    Detail = $"Process '{friendlyName}' exited with code {exitCode}",
    SessionId = sessionId,
    Metadata = new Dictionary<string, object?>
    {
        ["process_name"] = friendlyName,
        ["exit_code"] = exitCode,
        ["last_stderr"] = lastStderr?[..Math.Min(500, lastStderr.Length)]
    }
};

Bus.Publish(new WyreErrorEvent(error));
```
