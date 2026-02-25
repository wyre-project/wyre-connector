# Wyre Stream — UI Modules

**Part of repo:** `wyre-stream`
**License:** GPL v3

---

## Module List

```
UI.Onboarding.dll       # first-launch wizard
UI.NodePairing.dll      # add node by short ID, fingerprint confirm
UI.StreamView.dll       # main stream window, PiP, zoom/pan, cursor overlay
UI.GuestLink.dll        # generate + share guest link, QR code display
UI.Settings.dll         # full settings tree + search
UI.Theme.dll            # dark/light/system theme, accent color picker
UI.Hotkeys.dll          # key binding, cheatsheet overlay, global hotkey hook
UI.MeshTopology.dll     # visual mesh map, node status, WoL button
UI.QrCode.dll           # QR code generation (used by GuestLink + Pairing)
UI.Localization.dll     # resource strings, language switching
```

All UI modules are Avalonia-based. They reference module interfaces via `IModuleContext` and communicate through the bus — never import other modules' dlls directly.

---

## UI.Onboarding

First-launch wizard. Shown when no prior config exists or user explicitly resets.

**Steps:**
1. Welcome — brief "what is Wyre" explanation, skip button
2. Name your node — set friendly name and short node ID (random pre-filled, editable)
3. Discovery — configure mesh passkey and central server URL (shows default, editable)
4. Test connection — ping central, test LAN discovery
5. Done — optionally launch stream immediately

Skip button on every step after step 1. State saved progressively so partial completion doesn't lose work.

---

## UI.NodePairing

**Add node by short ID:**

```
┌─────────────────────────────────────────────┐
│ Add Node                                    │
├─────────────────────────────────────────────┤
│ Enter the short ID of the node to add:      │
│                                             │
│  [ ABC123        ]                          │
│                                             │
│                    [Find Node]  [Cancel]    │
└─────────────────────────────────────────────┘
```

On find: discovery resolves the node (LAN mDNS or central lookup). Initiates Noise XX handshake. Derives fingerprint wordlist.

**Fingerprint confirmation:**

```
┌─────────────────────────────────────────────┐
│ Confirm Node Identity                       │
├─────────────────────────────────────────────┤
│ Ask the owner of "John's Desktop" to        │
│ confirm their fingerprint matches:          │
│                                             │
│     vivid · table · cargo · seven           │
│                                             │
│ Only confirm if you spoke with them         │
│ directly or verified through a trusted      │
│ channel.                                    │
│                                             │
│ [Confirm & Add]              [Cancel]       │
└─────────────────────────────────────────────┘
```

Fingerprint is 4 words from BIP39-style wordlist derived from SHA256 of peer's public key. Large enough to be distinctive, short enough to read aloud in 5 seconds.

**URL scheme handler:**
```
wyreconnector://pair/ABC123  →  opens this dialog pre-filled
```

---

## UI.StreamView

Main stream window. The primary Wyre Stream UI surface.

### Frame Rendering

`WriteableBitmap` with direct pixel buffer access for decoded frames. Target 60fps UI refresh minimum. Frame delivery from decoder runs on separate high-priority thread — rendering thread picks up latest frame.

```csharp
// Render loop — runs on dedicated thread
while (!_ct.IsCancellationRequested)
{
    var frame = _frameQueue.Dequeue();
    using var fb = _bitmap.Lock();
    frame.CopyTo(fb.Address, fb.RowBytes);
    Dispatcher.UIThread.Post(() => _image.InvalidateVisual());
    // Hold last frame if queue empty — never show black screen
}
```

### Layouts

- **Fullscreen** — single host fills entire display
- **Windowed** — resizable window, aspect-ratio-locked
- **Picture-in-Picture** — small overlay window, always on top, draggable
- **Side-by-side** — for multi-monitor hosts, split view

Toggle between layouts without disconnecting.

### Zoom & Pan

Mouse wheel to zoom, middle-click drag to pan. Useful when host has 4K and client has smaller display. Reset zoom via double-click or hotkey.

```csharp
private double _zoom = 1.0;
private Vector2 _panOffset = Vector2.Zero;

// Apply transform to the render target
_renderTransform = new MatrixTransform(
    Matrix.CreateScale(_zoom, _zoom) *
    Matrix.CreateTranslation(_panOffset.X, _panOffset.Y));
```

### Cursor Overlay

Each connected client gets an assigned color (auto-assigned from palette, user-changeable). Cursors rendered as colored overlays on the stream. Options per session:

- Show host cursor only
- Show all client cursors (colored per client)
- Show only my cursor
- Hide all cursors (host renders own)

When a client loses input priority, their cursor dims/fades.

### Reconnect Banner

On stream death, overlay rendered on top of last held frame:

```
┌─────────────────────────────────────────────────────────┐
│  ⚠  Stream disconnected — reconnecting (attempt 3/10)  │
│     Next retry in 4 seconds...          [Disconnect]   │
└─────────────────────────────────────────────────────────┘
```

Semi-transparent, non-blocking. Last frame remains visible underneath.

### Connection Quality Indicator

Small persistent indicator in corner of stream window. Color: green/yellow/red. Hover to see exact numbers (RTT, packet loss, bitrate, FPS).

---

## UI.GuestLink

Generate and share time-limited guest links.

```
┌─────────────────────────────────────────────┐
│ Create Guest Link                           │
├─────────────────────────────────────────────┤
│ Expires in:  [ 1 hour ▼ ]                  │
│ Max uses:    [ 1       ]                    │
│ Label:       [ Support session  ]           │
│                                             │
│ Permissions:                                │
│  ☑ View screen    ☐ Input control           │
│  ☐ File transfer  ☐ Clipboard               │
│  ☐ Chat           ☐ Microphone              │
│                                             │
│                     [Create Link]           │
├─────────────────────────────────────────────┤
│ wyrestream://guest/abc...def                │
│                                             │
│ [Copy Link]  [Show QR]  [Send via Chat]     │
└─────────────────────────────────────────────┘
```

QR code display via `UI.QrCode.dll`. Scanning opens Wyre Stream on mobile/another machine and connects directly.

Active guest links shown in a list — revocable at any time.

---

## UI.Settings

Full settings tree with search. Every module contributes its own settings section via `ISettingsContributor` (from wyre-sdk). `UI.Settings.dll` discovers all contributors and assembles the tree — has zero knowledge of what modules exist.

### Settings Tree Structure

```
General
  ├── Node name and ID
  ├── Startup behavior (autostart, start hidden)
  └── Theme (→ delegates to UI.Theme)

Discovery
  ├── Central server URL
  └── LAN discovery toggle

Stream
  ├── Max FPS (slider, 1–280+)
  ├── Max bitrate
  ├── Default resolution scaling
  └── Audio settings

Input
  ├── Cursor mode (latest mover / weighted merge)
  ├── Cursor idle timeout (ms)
  ├── Keyboard layout translation (toggle)
  └── Controller passthrough (toggle)

Security
  ├── Default client permissions
  └── Auto-approve settings

Advanced (hidden unless "Show advanced" toggle on)
  ├── Debug mode
  ├── Encoder settings (codec, preset)
  ├── Network (custom STUN/TURN)
  └── Subprocess restart behavior

[Module Name] (each installed module adds section here)
```

### Search

Input field filters all settings labels and descriptions in real time. Matching settings highlighted, non-matching collapsed. Uses simple string matching — no fuzzy search needed at this scale.

---

## UI.Theme

Dark/light/system theme with accent color picker.

```csharp
public enum AppTheme { Dark, Light, System }

// Applied reactively — change takes effect immediately, no restart
public class ThemeService
{
    public void Apply(AppTheme theme, Color accentColor)
    {
        Application.Current!.RequestedThemeVariant = theme switch
        {
            AppTheme.Dark   => ThemeVariant.Dark,
            AppTheme.Light  => ThemeVariant.Light,
            _               => ThemeVariant.Default  // system
        };

        // Update accent color resource
        Application.Current.Resources["SystemAccentColor"] = accentColor;
    }
}
```

Accent color picker: standard color wheel + preset swatches. Default: Wyre brand color.

---

## UI.Hotkeys

Global hotkey registration and binding UI.

**Default hotkeys:**

| Action | Default | Scope |
|---|---|---|
| Toggle input capture | Ctrl+Alt+Z | Global |
| Switch input priority | Ctrl+Alt+X | Global |
| Toggle fullscreen | F11 | Window |
| Toggle recording | Ctrl+Alt+R | Global |
| Disconnect session | Ctrl+Alt+D | Global |
| Show cheatsheet | Ctrl+Alt+/ | Global |
| Lock session | Ctrl+Alt+L | Global |

All hotkeys rebindable in UI. Conflicts detected and warned.

**Windows:** `RegisterHotKey` P/Invoke from `user32.dll` for global hotkeys.
**Linux:** `XGrabKey` P/Invoke (`libX11`) for X11, fallback to window-scoped on Wayland.

**Cheatsheet overlay:** Semi-transparent full-screen overlay showing all active hotkeys. Triggered by hotkey. Dismissed by any key press or click.

---

## UI.MeshTopology

Visual mesh map showing all nodes in all active meshes.

Each node shown as a card:
- Node name + short ID
- Connection status (green/yellow/red dot)
- Loaded root modules (icons)
- Latency to this node
- Last seen timestamp (if offline)
- WoL button (if offline + WoL module installed)
- Connect button (if Wyre Stream root module loaded on that node)

Nodes colored/grouped by mesh when in multiple meshes.

Topology contributors (via `ITopologyContributor` from wyre-sdk) add badges to nodes — e.g. Wyre Files adds a file-sharing badge, Wyre Stream adds a stream-available badge.

---

## UI.QrCode

QR code generation from arbitrary string data. Used by:
- `UI.GuestLink.dll` — encode guest link URL
- `UI.NodePairing.dll` — encode pairing URL

Uses a pure C# QR code library (e.g. `QRCoder` NuGet — MIT licensed). Renders as `WriteableBitmap` or SVG.

---

## UI.Localization

`Microsoft.Extensions.Localization` + Avalonia resource dictionary pattern.

```csharp
// All user-facing strings via localization key — never hardcoded
public static class L
{
    public static string Get(string key, params object[] args)
        => _localizer[key, args];
}

// Usage in ViewModels
StatusText = L.Get("stream.status.reconnecting", attempt, maxAttempts);
```

Shipped languages: English (en). Community translations via `wyre-docs` repo PRs (CC BY 4.0 license on docs makes translations freely contribuable).

Language selection in `UI.Settings`. Change takes effect immediately via reactive binding — no restart.

---

## Avalonia Patterns Used Throughout

### ReactiveUI ViewModel pattern

```csharp
public class StreamViewModel : ReactiveObject
{
    [Reactive] public bool IsConnected { get; private set; }
    [Reactive] public string StatusText { get; private set; } = "";

    // Commands
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
}
```

### Bus → UI thread marshaling

All bus subscriptions that update UI properties must marshal to UI thread:

```csharp
ctx.Bus.Subscribe<StreamStateChangedMessage>(msg =>
    Dispatcher.UIThread.Post(() =>
        ViewModel.IsConnected = msg.NewState == StreamState.Connected));
```

Or use ReactiveUI's scheduler:

```csharp
ctx.Bus.Subscribe<StreamStateChangedMessage>(async msg =>
{
    await Dispatcher.UIThread.InvokeAsync(() =>
        ViewModel.IsConnected = msg.NewState == StreamState.Connected);
});
```
