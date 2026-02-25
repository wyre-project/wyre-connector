# Wyre Stream — Input, Clipboard, Observability, Chat & Power User Modules

**Part of repo:** `wyre-stream`
**License:** GPL v3

---

## Input Modules

```
Input.Capture.dll       # raw input capture, platform shims
Input.Inject.dll        # input injection, platform shims
Input.Arbiter.dll       # InputArbiterGrain, cursor priority, merge mode
Input.Keyboard.dll      # keyboard layout translation (toggleable)
Input.Controller.dll    # gamepad capture + injection, own ACL
Input.HostPriority.dll  # host physical input always has priority over all clients
```

---

### Input.Capture

Raw input capture — platform-specific, thin P/Invoke shims.

**Windows:** `GetRawInputData` from Raw Input API. Captures relative mouse movement, keyboard scancodes, at up to 1000Hz.

**Linux (X11):** `/dev/input/eventX` — read evdev events directly. Requires user in `input` group (set by installer). Mouse gives relative movement natively.

**Linux (Wayland):** `libei` (input emulation) — has almost no C# bindings yet, write thin P/Invoke directly. Fallback to X11 if libei unavailable.

All captured input published as `RawInputEvent` on the bus:

```csharp
public record RawInputEvent(
    InputType Type,           // Mouse, Keyboard, Controller
    RawMouseDelta? MouseDelta,
    KeyEvent? Key,
    DateTimeOffset CapturedAt
);
```

---

### Input.Inject

Input injection — platform-specific P/Invoke shims behind `IInputInjector` interface.

**Windows:**
```csharp
// Relative mouse injection
[DllImport("user32.dll")]
static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

// MOUSEEVENTF_MOVE is already relative
var input = new INPUT { type = INPUT_MOUSE };
input.mi.dwFlags = MOUSEEVENTF_MOVE;
input.mi.dx = deltaX;
input.mi.dy = deltaY;
SendInput(1, [input], Marshal.SizeOf<INPUT>());
```

**Linux:** Write to `/dev/uinput` via `FileStream` + `ioctl` P/Invoke. Requires user in `input` group. Create virtual device on startup, emit relative mouse events.

---

### Input.Arbiter

Orleans grain managing input priority across multiple clients.

```csharp
public interface IInputArbiterGrain : IGrainWithStringKey  // key = sessionId
{
    Task SubmitInputEventAsync(string clientId, RawInputEvent evt);
    Task<string?> GetCurrentOwnerAsync();
    Task ReleaseControlAsync(string clientId);
}
```

**Latest-Mover Priority Mode (default):**

```csharp
private string? _currentOwner;
private DateTime _lastMoveTime;
private readonly TimeSpan _idleTimeout = TimeSpan.FromMilliseconds(150); // configurable

public Task SubmitInputEventAsync(string clientId, RawInputEvent evt)
{
    var now = DateTime.UtcNow;

    bool ownerIdle = now - _lastMoveTime > _idleTimeout;

    if (_currentOwner is null || ownerIdle || _currentOwner == clientId)
    {
        if (_currentOwner != clientId)
        {
            _currentOwner = clientId;
            // Broadcast ownership change — all clients update cursor indicator
            this.GetStreamProvider("Default")
                .GetStream<InputOwnerChangedEvent>(StreamId.Create("input", sessionId))
                .OnNextAsync(new InputOwnerChangedEvent(clientId));
        }
        _lastMoveTime = now;
        Bus.Publish(new InputEventReadyMessage(evt));
    }
    // Non-owner during active ownership → discard

    return Task.CompletedTask;
}
```

**Weighted Merge Mode (optional, config):**

```csharp
private Vector2 MergeDeltas(
    IEnumerable<(string clientId, Vector2 delta, DateTime time)> inputs)
{
    var now = DateTime.UtcNow;
    var weighted = inputs.Select(i => {
        double age = (now - i.time).TotalMilliseconds;
        double weight = Math.Exp(-age / 50.0);  // 50ms half-life exponential decay
        return i.delta * (float)weight;
    });
    return weighted.Aggregate(Vector2.Zero, (a, b) => a + b);
}
```

**Cursor Mode config:**
```toml
[input]
cursor_mode = "latest_mover"   # or "weighted_merge"
cursor_idle_timeout_ms = 150
```

---

### Input.Keyboard

Keyboard layout translation — translates key positions between host and client layouts so typing feels natural on the client side regardless of what layout the host uses.

Toggleable, default ON. Configurable per-session in ACL UI.

```toml
[input.keyboard]
layout_translation = true   # default on
```

Implementation: maintain a mapping table from USB HID scancode → layout-adjusted scancode for common layouts (QWERTY, AZERTY, QWERTZ, Dvorak, Colemak). Apply translation at inject time, not capture time.

---

### Input.Controller

Gamepad capture and injection with its own independent ACL entry (`INPUT_CONTROLLER`).

**Windows capture:** XInput via P/Invoke, or raw HID via `HidLibrary`.
**Windows injection:** `Nefarius.ViGEm.Client` NuGet — creates virtual Xbox controller or DS4.
**Linux capture:** Read from `/dev/input/js*` or evdev.
**Linux injection:** `uinput` virtual gamepad device.

Each client's controller appears as a separate virtual device on the host. Multiple clients can each have a controller simultaneously if ACL permits.

---

### Input.HostPriority

When a client has input focus, optionally block physical input on the host machine to prevent conflicts between local and remote input. Configurable, default OFF (host always has priority regardless — this blocks LOCAL input when remote is active, which is an advanced use case).

```toml
[input]
block_host_input_when_client_active = false
```

---

## Clipboard.Sync

Syncs clipboard across mesh nodes. Text, images, and files (when Wyre Files is installed).

Uses `TextCopy` for OS clipboard access. Subscribes to OS clipboard change notifications. On change: encrypt content, publish to `clipboard-sync` channel on central. Peers receive and apply to their local clipboard.

ACL permission: `CLIPBOARD`. No sync if not permitted.

```csharp
public record ClipboardChangedMessage(
    string FromNodeId,
    ClipboardContentType ContentType,  // Text, Image, Files
    byte[] EncryptedContent
);
```

File clipboard sync publishes `ClipboardFilesReadyMessage` which `Wyre Files` subscribes to for actual transfer handling. Wyre Stream itself does not transfer files — it only handles the clipboard signal.

---

## Observability Modules

```
Audit.Log.dll           # input event journal, session events, append-only SQLite
History.Connections.dll # connection history UI + persistence
Notifications.dll       # join/leave toasts, bandwidth warnings, consent indicator
Diagnostics.Network.dll # debug overlay, LiveCharts2 (debug mode only)
CrashReporter.dll       # exception collector, log snapshot, clipboard/file export
```

---

### Audit.Log

Append-only journal of all significant events. Uses SQLite via EF Core. Never deletes entries — only appends.

```csharp
public record AuditEntry
{
    public long Id { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string NodeId { get; init; }
    public string? SessionId { get; init; }
    public string EventCode { get; init; }   // e.g. "WYRE-STRM-0001"
    public string EventType { get; init; }   // human readable category
    public string Description { get; init; }
    public string? UserId { get; init; }
    public Dictionary<string, object?>? Metadata { get; init; }
}
```

Subscribes to messages on the bus — anything worth auditing is published as a message and Audit.Log subscribes without the publishing module knowing:

```csharp
ctx.Bus.Subscribe<ClientConnectedMessage>(msg => _journal.WriteAsync(...));
ctx.Bus.Subscribe<ClientDisconnectedMessage>(msg => _journal.WriteAsync(...));
ctx.Bus.Subscribe<AclChangedMessage>(msg => _journal.WriteAsync(...));
ctx.Bus.Subscribe<SessionLockedMessage>(msg => _journal.WriteAsync(...));
ctx.Bus.Subscribe<KeyRotationApprovedMessage>(msg => _journal.WriteAsync(...));
ctx.Bus.Subscribe<KeyRotationRejectedMessage>(msg => _journal.WriteAsync(...));
ctx.Bus.Subscribe<WyreErrorEvent>(msg =>  // log all errors
{
    if (msg.Error.Type == WyreCodeType.Error)
        _journal.WriteAsync(...);
});
// etc — subscribe to everything worth auditing
```

---

### Notifications

Subscribes to bus events and shows non-intrusive toasts. All notifications configurable in settings.

```csharp
ctx.Bus.Subscribe<ClientConnectedMessage>(msg =>
    _toast.Show($"{msg.NodeId} connected", ToastType.Info, duration: 3000));

ctx.Bus.Subscribe<ClientDisconnectedMessage>(msg =>
    _toast.Show($"{msg.NodeId} disconnected", ToastType.Info, duration: 2000));

ctx.Bus.Subscribe<StreamHealthReport>(msg =>
{
    if (msg.PacketLossPercent > 5.0)
        _toast.Show("Stream quality reduced — network conditions poor",
            ToastType.Warning, code: "WYRE-STRM-W001");
});

// Recording consent indicator — non-dismissable, always visible while recording
ctx.Bus.Subscribe<RecordingStartedMessage>(msg =>
    _overlayService.ShowPersistent("● REC", OverlayPosition.TopRight));

ctx.Bus.Subscribe<RecordingStoppedMessage>(msg =>
    _overlayService.HidePersistent());
```

Host activity indicator: detect local mouse/keyboard activity on host when clients are connected and show subtle indicator on client side ("Host is active locally").

---

### Diagnostics.Network

**Only activated when `debug.enabled = true` in config.** Otherwise this module does zero work (check flag in `OnInitializeAsync` and return immediately if disabled).

Shows LiveCharts2 overlay in corner of stream window:
- Bitrate graph (encode bitrate vs received bitrate per client)
- FPS graph (target vs actual)
- Latency / RTT graph
- Packet loss %
- Encode time per frame
- Decode time per frame
- Jitter buffer depth
- Current encoding strategy (A or B)
- Active simulcast tiers
- Input polling rate

All data sourced from messages already flowing on the bus — this module just visualizes them.

Also activates `OrleansDashboard` (local web UI at `localhost:8888`) for grain diagnostics.

---

### CrashReporter

Registered as fallback handler for unhandled exceptions NOT caught by ModuleHost's crash fallback (i.e. exceptions originating within module code after successful load).

```csharp
public class CrashReporterModule : WyreModuleBase
{
    protected override Task OnInitializeAsync(IModuleContext ctx)
    {
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        return Task.CompletedTask;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var report = BuildReport(e.ExceptionObject as Exception);
        // Collect: OS version, connector version, all module versions,
        // last 100 audit log entries, last N log lines, grain state snapshot

        // Show Avalonia dialog:
        // "Wyre encountered an unexpected error."
        // [Copy to Clipboard]  [Save to File]  [Close]
        // No automatic telemetry — fully opt-in
    }
}
```

---

## Chat

```
Chat.dll    # SignalR hub, ChatHistoryGrain, @mentions, ACL gating
```

### SignalR Hub

Hosted in Kestrel alongside everything else. One SignalR group per session (all clients connected to one host), one per mesh (all nodes in mesh).

```csharp
public class MeshChatHub : Hub
{
    public async Task SendToSession(string sessionId, ChatMessage message)
        => await Clients.Group(sessionId).SendAsync("ReceiveMessage", message);

    public async Task SendToMesh(ChatMessage message)
        => await Clients.Group($"mesh:{message.MeshId}").SendAsync("ReceiveMessage", message);

    public async Task SendDirect(string targetNodeId, ChatMessage message)
        => await Clients.User(targetNodeId).SendAsync("ReceiveMessage", message);
}
```

### ChatHistoryGrain

Stores last N messages per session. When client connects mid-session, replay history. Persisted in SQLite via EF Core.

```csharp
public record ChatMessage
{
    public string Id { get; init; }
    public string SenderNodeId { get; init; }
    public string SenderName { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string Content { get; init; }
    public ChatMessageType Type { get; init; }  // Text, System, FileTransferNotification
    public string? ReplyToId { get; init; }
}
```

### @Mentions

Parse `@nodeId` in message content. Notify targeted node via `MentionReceivedMessage` on bus. Notifications module subscribes and shows highlight toast.

### ACL

Chat gated on `CHAT` permission. Clients without `CHAT` permission cannot send or receive messages. Checked in hub before every message dispatch.

---

## Power User Modules

```
Hooks.dll   # script/executable on events, TOML configured
RestApi.dll # localhost HTTP REST, self-registering endpoints
Cli.dll     # command parser, headless control via RestApi
```

---

### Hooks

Executes local scripts/executables when bus events occur. Events hookable defined by modules implementing `IHookEventSource` (from wyre-sdk).

```toml
[hooks]
on_client_connect = "/scripts/notify.sh"
on_file_received = "/scripts/move-downloads.sh"
on_stream_start = ""         # empty = disabled
```

Each hook receives event data as JSON via stdin or environment variables. Max execution time configurable, process killed if exceeded.

Hook auto-response to popup codes:
```toml
[hooks.auto_respond]
"WYRE-FILE-U001" = "approve"   # auto-approve all incoming file transfers
"WYRE-FILE-U002" = "reject"    # auto-reject all drag-drop attempts
```

---

### RestApi

Localhost HTTP REST API mirroring all node state. Each module self-registers endpoints via `IRestApiContributor` (from wyre-sdk). RestApi.dll discovers all contributors and maps their endpoints — has zero knowledge of what modules exist.

```csharp
public class RestApiModule : WyreModuleBase
{
    protected override Task OnInitializeAsync(IModuleContext ctx)
    {
        var contributors = ctx.Provider.GetServices<IRestApiContributor>();
        foreach (var c in contributors)
            c.MapEndpoints(_app);
        return Task.CompletedTask;
    }
}
```

Example endpoints contributed by Stream.Session:
```
GET  /api/sessions              ← list active sessions
DELETE /api/sessions/{id}       ← disconnect session
GET  /api/sessions/{id}/clients ← list connected clients
```

All endpoints require localhost only — never bind to external interfaces.

---

### Cli

Command-line interface for headless control. Communicates with the running Connector via RestApi.

```bash
wyre-connector status
wyre-connector sessions list
wyre-connector sessions disconnect {sessionId}
wyre-connector mesh peers
wyre-connector mesh join {passkey}
wyre-connector module list
wyre-connector module enable {moduleId}
wyre-connector module disable {moduleId}
```

Delegates everything to RestApi — no direct internal access. This means Cli works even for external tooling that uses the same REST endpoints.

---

## Platform Modules

```
Platform.WakeOnLan.dll      # magic packet, relay routing, MAC cache
Platform.Tray.dll           # system tray icon, show/hide window
Platform.Autostart.dll      # OS autostart registration
Platform.Firewall.dll       # Windows firewall rule management
Platform.Elevation.dll      # UAC helper (Windows), pkexec (Linux)
Platform.WaylandFallback.dll # early detection + clear error message
```

### Platform.WakeOnLan

```csharp
public static void SendMagicPacket(PhysicalAddress mac, IPAddress broadcast)
{
    var payload = new byte[102];
    Array.Fill(payload, (byte)0xFF, 0, 6);
    var macBytes = mac.GetAddressBytes();
    for (int i = 1; i <= 16; i++)
        macBytes.CopyTo(payload, i * 6);

    using var client = new UdpClient();
    client.EnableBroadcast = true;
    client.Send(payload, payload.Length, new IPEndPoint(broadcast, 9));
}
```

MAC addresses cached in SQLite from last-known `NodeDescriptor`. If node is on different subnet, route WoL request through a relay node with WoL capability via bus request.

After sending magic packet, poll discovery for node to appear (up to 60s timeout).

### Platform.Tray

Close window → hide to tray (never kill process). Tray menu: Open, Exit. Single-click tray icon → show/restore window.

```csharp
window.Closing += (_, e) =>
{
    e.Cancel = true;
    window.Hide();
};
```

Exit only via tray menu or UI Exit button — closing the window never terminates the process.

### Platform.Autostart

```csharp
// Windows
Registry.CurrentUser
    .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true)!
    .SetValue("WyreConnector",
        $"\"{Environment.ProcessPath}\" --hidden --root wyrestream");

// Linux — systemd user service
// Write to ~/.config/systemd/user/wyre-connector.service
// ExecStart=wyre-connector --hidden --root wyrestream
// WantedBy=default.target
```

`--hidden` flag: starts with window hidden, tray icon still appears.

Also detects if launched by OS autostart and auto-applies hidden behavior without requiring the flag:
```csharp
bool IsStartingWithOS()
    => Environment.GetEnvironmentVariable("WYRE_AUTOSTART") == "1"
    || GetParentProcessName() is "systemd" or "launchd" or "explorer";
```

### Platform.WaylandFallback

Check at startup if running under Wayland without supported capture protocol. Show clear message BEFORE attempting any capture:

```
Wyre Stream detected you are running Wayland with a compositor
that does not support window capture (GNOME without shell extension).

Options:
• Switch to an X11 session
• Install the Wyre GNOME Shell Extension
• Use a wlroots-based compositor (Sway, Hyprland)

[Open Help]  [Continue Anyway]  [Exit]
```

---

## Session Recording Module

Part of observability, but detailed here:

When `SessionRecording` module starts recording:
1. Taps the encoded stream (already encoded — zero re-encoding cost)
2. Muxes into MP4 container via FFmpeg subprocess
3. Publishes `RecordingStartedMessage` on bus immediately
4. `Notifications.dll` subscribes → shows persistent "● REC" overlay on ALL connected clients
5. Overlay is non-dismissable while recording active

```csharp
ctx.Bus.Subscribe<RecordingStartedMessage>(msg =>
{
    // This runs on EVERY client — consent indicator always shown
    _overlay.ShowPersistent(
        content: "● REC",
        color: Colors.Red,
        position: OverlayPosition.TopRight,
        dismissable: false);
});
```

Storage: configurable path, configurable retention, exportable via UI.

---

## Error Codes

```
WYRE-INPT-0001  Input injection unavailable
WYRE-INPT-0002  Input device not found
WYRE-INPT-0003  uinput permission denied — user not in input group
WYRE-INPT-0004  Controller injection unavailable — ViGEm not installed
WYRE-INPT-0005  Input arbitration conflict
```
