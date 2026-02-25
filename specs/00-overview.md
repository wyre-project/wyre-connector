# Wyre — Full System Overview

> Complete technical reference for engineers and AI building the Wyre ecosystem.

## What Is Wyre

Wyre is an open, modular, mesh-networked remote desktop and file sharing ecosystem. It is built around a single runtime binary (`wyre-connector`) on top of which root modules are installed to provide actual functionality. Think of it like a plugin host — the host does nothing alone, but everything builds on it.

## Repository Map

| Repo | License | Purpose |
|---|---|---|
| `wyre-connector` | GPL v3 | The only runtime binary. Core.dll, ModuleHost.dll, module loading, hash validation |
| `wyre-stream` | GPL v3 | Remote desktop root module. Capture, encode, decode, input, ACL, chat, adaptive rate |
| `wyre-files` | GPL v3 | File sharing root module. Standalone app, extends Stream when both installed |
| `wyre-relay` | GPL v3 | Standalone NAT traversal relay + Wake-on-LAN |
| `wyre-connector-central` | GPL v3 | Standalone central discovery server, dumb authenticated router |
| `wyre-sdk` | LGPL v3 | Client module development SDK (IModule, IRootModule, IModuleContext, IMessageBus) |
| `wyre-server-sdk` | LGPL v3 | Server module development SDK (IServerModule, IServerModuleContext) |
| `wyre-installer-sdk` | LGPL v3 | Shared installer logic, Connector detection, shortcut creation |
| `wyre-modules-community` | CC0 | Awesome list of community modules |
| `wyre-docs` | CC BY 4.0 | All documentation |
| `wyre-site` | ARR | Website at wyre.zombidev.me |
| `.github` | None | Org profile, issue templates, FUNDING.yml, reusable CI workflows |

## Architecture Layers

```
wyre-connector.exe              ← the only binary
    │
    ├── ModuleHost.dll          ← module lifecycle, hash validation, error screens
    │       │
    │       └── Core.dll        ← message bus, frozen forever ~200 lines
    │
    └── [root module].dll       ← e.g. WyreStream.Root.dll
            │
            └── [sub modules]   ← e.g. Capture.Windows.dll, Stream.Session.dll
                    │
                    └── [child root modules] ← infinitely nestable
```

## Layer Rules (enforced by NetArchTest in CI)

```
Core.dll            → references nothing
ModuleHost.dll      → references Core.dll only
*.Messages.dll      → references nothing (pure records)
Module Y.dll        → references Core.dll + any *.Messages.dll
Module X.dll        → NEVER references Module Y.dll directly
```

## URL Schemes

```
wyreconnector://    ← platform level (pairing, mesh, node actions)
wyrestream://       ← stream level (guest links, session invites, connect)
```

## Hosting

```
wyre.zombidev.me                ← site, downloads, docs, module list
central.wyre.zombidev.me        ← default central server
status.zombidev.me              ← uptime monitoring
```

## Technology Stack

| Concern | Technology |
|---|---|
| Runtime | .NET 10, C# |
| UI | Avalonia + ReactiveUI.Avalonia |
| Mesh / distributed state | Microsoft.Orleans 10.0.1 |
| Control plane | Grpc.AspNetCore + Grpc.Tools |
| LAN discovery | Makaretu.Dns |
| P2P crypto | Sodium.Core (Noise XX) |
| WebRTC / RTP | SIPSorcery (latest) |
| Media encode/decode | SIPSorceryMedia.FFmpeg + FFmpeg binaries |
| Audio codec | Concentus (pure C#) |
| GPU capture (Windows) | Vortice.Windows (DXGI) |
| Chat | Microsoft.AspNetCore.SignalR |
| Persistence | EF Core + SQLite |
| Config | Tomlyn 0.20.0 (TOML v1.0) |
| Resilience | Polly |
| Validation | FluentValidation |
| Logging | Serilog |
| Debug charts | LiveChartsCore.SkiaSharpView.Avalonia (debug only) |
| Serialization | MessagePack-CSharp |
| Architecture tests | NetArchTest.Rules |
| Fuzzing | SharpFuzz |

## Error Code Format

```
WYRE-{COMPONENT}-{NUMBER}       ← errors
WYRE-{COMPONENT}-W{NUMBER}      ← warnings
WYRE-{COMPONENT}-U{NUMBER}      ← user popups / prompts
```

Components: `CONN`, `MESH`, `CENT`, `STRM`, `AUTH`, `ACL`, `TRAN`, `FILE`, `INPT`, `ENCD`, `DCOD`, `CAPT`, `RELY`, `INTG`

## Security Model

```
✓ ECDsa signed integrity.toml — hash chain App → ModuleHost → Core → modules
✓ Noise XX for all peer-to-peer connections
✓ HKDF derived keys per mesh (central learns nothing about mesh contents)
✓ All media processing runs in separate subprocesses
✓ Input bounds validation on all remote data
✓ Parser fuzzing via SharpFuzz
✓ Key rotation always requires explicit user approval — no auto-accept ever
✓ Per-client ACLs, hot-reloadable while connected
```

## Licenses

```
Runnable apps / runtime     → GPL v3
SDKs / installers           → LGPL v3
Community module index      → CC0
Documentation               → CC BY 4.0
Website                     → All Rights Reserved
```
