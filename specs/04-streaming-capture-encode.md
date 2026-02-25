# Wyre Stream — Streaming, Capture, Encode, Decode & Adaptive Rate Modules

**Part of repo:** `wyre-stream`
**License:** GPL v3

---

## Module List

```
Capture.Windows.dll     # DXGI/WGC capture, virtual display management
Capture.Linux.dll       # X11/DRM/V4L2 capture, vkms virtual display
Encode.dll              # FFmpeg encode pipeline, simulcast tier management
Decode.dll              # FFmpeg decode, jitter buffer, frame pacing
Stream.Session.dll      # StreamGrain, multi-client fanout, A→B strategy switching
Stream.Audio.dll        # audio capture, Opus encoding, per-app routing
Stream.AppWindow.dll    # single-window capture + child window tracking
AdaptiveRate.dll        # BBR probing, PID controller, feedback loop, polling rate
```

All media processing runs as **separate subprocesses** — never in-process. This provides crash isolation, exploit isolation, and better perf via OS process scheduling. Main app communicates with subprocesses via named pipes / stdin-stdout.

---

## Subprocess Architecture

Every media subprocess follows this pattern:

```csharp
public class MediaSubprocessManager : IHostedService
{
    private Process? _process;
    private readonly string _processId;
    private readonly string _friendlyName;  // "Video Encoder", "Screen Capture" etc

    private async Task OnProcessExited(int exitCode)
    {
        if (_stopRequested) return;  // expected exit

        // Unexpected — always prompt, never silent retry
        Bus.Publish(new SubprocessCrashedMessage(
            _processId, _friendlyName, exitCode, ReadLastStderr()));

        // UI shows prompt:
        // "{FriendlyName} stopped unexpectedly (exit code {exitCode})"
        // [Restart] [View Logs] [Don't Restart]
        // User choice awaited via TaskCompletionSource
    }
}
```

If user chooses "Don't Restart", that feature is disabled for the session. "View Logs" shows last N lines of subprocess stderr. "Restart" spawns fresh process.

---

## Capture.Windows

### Responsibility

Screen capture on Windows using DXGI OutputDuplication (via `Vortice.Windows`). Faster than WGC at high framerates — stays on GPU, no CPU readback needed.

### Monitor Enumeration

Enumerate via `EnumDisplayMonitors`. Return list of `MonitorDescriptor` including display name, resolution, refresh rate, is-virtual flag.

### DXGI OutputDuplication

```csharp
// Frame is a DirectX texture — hand directly to NVENC encoder
// Zero CPU readback at high framerates
public async Task<IDXGIResource> AcquireFrameAsync(CancellationToken ct)
{
    DXGI_OUTDUPL_FRAME_INFO info;
    _output.AcquireNextFrame(0, out info, out var resource);
    return resource;
}
```

### Virtual Display

For framerates exceeding physical monitor refresh rate, use `parsec-vdd` (open source virtual display driver, ships as optional install component). Create virtual monitor at arbitrary resolution and refresh rate. App runs on virtual display, captured at target framerate.

### Single Window Capture

For `Stream.AppWindow.dll` use: `GraphicsCaptureItem.TryCreateFromWindowId(hwnd)`. Track child windows via `EnumChildWindows`. Handle `WM_CHILDACTIVATE` for popup tracking.

### Error Codes

```
WYRE-CAPT-0001  Capture device not found
WYRE-CAPT-0002  Capture permission denied (OS level)
WYRE-CAPT-0003  Capture resolution unsupported
WYRE-CAPT-0004  Capture framerate unsupported
WYRE-CAPT-0005  Capture subprocess exited unexpectedly
WYRE-CAPT-W001  Capture framerate dropping below target
```

---

## Capture.Linux

### Responsibility

Screen capture on Linux. Supports X11 (via XDamage + XComposite) and partial Wayland support.

### X11 Capture

Use `XCompositeRedirectWindow` + `XDamage` events. Damage events report exactly which pixels changed — avoid full-frame captures. P/Invoke into `libX11`, `libXcomposite`, `libXdamage`, `libXfixes`.

For single-window capture: `XQueryTree` to track window hierarchy, `XDamage` for change detection.

### Wayland

`wlr-export-dmabuf-unstable` for wlroots-based compositors (Sway etc). GNOME requires shell extension. Detect compositor and report clearly if unsupported:

```
WYRE-CAPT-0006  Wayland capture unsupported on this compositor
```

On unsupported Wayland, suggest using XWayland or switching to X11 session.

### Virtual Display (Linux)

`drm_vkms` kernel module (upstream since kernel 5.8). Load via `modprobe vkms`. Create virtual KMS display at arbitrary refresh rate.

---

## Encode

### Responsibility

FFmpeg-based video encoding. Runs as subprocess. Supports hardware (NVENC/VAAPI) with software fallback. Manages simulcast tiers.

### Codec Config (H.264, low latency)

```
NVENC (NVIDIA):
-c:v h264_nvenc -preset p1 -tune ll -rc cbr -delay 0 -zerolatency 1 -bf 0

VAAPI (Linux/AMD/Intel):
-c:v h264_vaapi -bf 0 -compression_level 0

Software fallback (libx264):
-c:v libx264 -preset ultrafast -tune zerolatency -bf 0
-x264-params "nal-hrd=cbr:force-cfr=1"
```

No B-frames (`-bf 0`) — they add latency. CBR for predictable streaming. Hardware encoder preferred, auto-detected at startup, fall back to software with `WYRE-ENCD-W002` warning.

### Simulcast Tiers

```csharp
public enum StreamTier { High, Medium, Low }

// Tiers are lazy — only activated when a client needs them
public class SimulcastManager
{
    private readonly HashSet<StreamTier> _activeTiers = new();

    public async Task EnsureTierActiveAsync(StreamTier tier)
    {
        if (_activeTiers.Contains(tier)) return;
        await StartEncoderForTierAsync(tier);
        _activeTiers.Add(tier);
    }

    public async Task DeactivateTierIfUnusedAsync(StreamTier tier)
    {
        if (!_clientTiers.Values.Any(t => t == tier))
            await StopEncoderForTierAsync(tier);
    }
}
```

### Error Codes

```
WYRE-ENCD-0001  Encoder initialization failed
WYRE-ENCD-0002  Encoder hardware unavailable — falling back to software
WYRE-ENCD-0003  Encoder bitrate out of range
WYRE-ENCD-0004  Encoder fps out of range
WYRE-ENCD-0005  Encoder subprocess exited unexpectedly
WYRE-ENCD-W001  Encode time exceeding frame budget
WYRE-ENCD-W002  Hardware encoder unavailable — using software
```

---

## Decode

### Responsibility

FFmpeg-based video decoding. Runs as subprocess. Jitter buffer with dynamic depth. Frame pacing for smooth delivery.

### Jitter Buffer

```csharp
public class JitterBuffer
{
    private readonly SortedDictionary<uint, EncodedFrame> _frames = new();
    private double _targetDepthMs;

    public void UpdateJitter(double measuredJitterMs)
    {
        _targetDepthMs = Math.Clamp(
            measuredJitterMs * 1.5,
            minValue: 1000.0 / MaxFps,  // minimum: one frame duration
            maxValue: 50.0);             // cap at 50ms to preserve interactivity
    }
}
```

### Frame Pacing

Dedicated high-priority render thread pinned to core:
```csharp
Thread.CurrentThread.Priority = ThreadPriority.Highest;
// ProcessorAffinity set via ProcessThread
```

Pre-allocate all frame buffers in pool. Use `NativeMemory.AlignedAlloc` for decode output buffer (64-byte aligned for SIMD). Never allocate on the render path.

### Last Frame Hold on Stream Death

```csharp
private WriteableBitmap? _lastFrame;
private StreamState _state = StreamState.Connected;

// On stream death: keep rendering _lastFrame
// Show reconnecting banner overlay
// Polly reconnect with backoff
// On reconnect: resume from last-held frame into live
```

### Error Codes

```
WYRE-DCOD-0001  Decoder initialization failed
WYRE-DCOD-0002  Decoder frame corrupted
WYRE-DCOD-0003  Decoder jitter buffer overflow
WYRE-DCOD-0004  Decoder subprocess exited unexpectedly
```

---

## Stream.Session

### Responsibility

Orleans grain managing the full lifecycle of a streaming session. Multi-client fanout. Single-stream vs simulcast strategy switching.

### StreamGrain

```csharp
public interface IStreamGrain : IGrainWithStringKey  // key = sessionId
{
    Task<StreamDescriptor> StartCaptureAsync(CaptureConfig config);
    Task AddClientAsync(string clientId, StreamTier initialTier);
    Task RemoveClientAsync(string clientId);
    Task SwitchClientTierAsync(string clientId, StreamTier newTier);
    Task ReportHealthAsync(string clientId, StreamHealthReport report);
    Task StopAsync();
}
```

### A→B Strategy Switching

```csharp
private async Task EvaluateStrategyAsync()
{
    if (_clientHealth.Count < 2) return;

    var bitrates = _clientHealth.Values
        .Select(h => h.ReceivedBitrate)
        .OrderBy(b => b)
        .ToList();

    double ratio = bitrates.First() / bitrates.Last();

    // Hysteresis band — prevents oscillation
    bool shouldSimulcast = ratio < 0.15;   // 85% degradation trigger
    bool canCollapse = ratio > 0.25;        // recovery threshold

    if (shouldSimulcast && _strategy == EncodingStrategy.SingleStream)
        await SwitchToSimulcastAsync();
    else if (canCollapse && _strategy == EncodingStrategy.Simulcast)
        await SwitchToSingleStreamAsync();
}
```

### Client Health Feedback

Each client reports every ~500ms:

```csharp
public record StreamHealthReport
{
    public double ReceivedBitrateBps { get; init; }
    public double PacketLossPercent { get; init; }
    public double DecodeTimeMs { get; init; }
    public int RenderQueueDepth { get; init; }
    public double RequestedBitrateBps { get; init; }  // what client wants
}
```

### Session Resumption

Grain keeps session state alive for 60 seconds after client disconnects. Reconnect within window = seamless resume. After 60s grain deactivates, reconnect = fresh session.

### Error Codes

```
WYRE-STRM-0001  Stream session not found
WYRE-STRM-0002  Stream max clients reached
WYRE-STRM-0003  Stream encoder failed
WYRE-STRM-0004  Stream decoder failed
WYRE-STRM-0005  Stream capture failed
WYRE-STRM-0006  Stream subprocess crashed
WYRE-STRM-0007  Stream subprocess restart rejected by user
WYRE-STRM-0008  Simulcast tier unavailable
WYRE-STRM-0009  Adaptive rate controller error
WYRE-STRM-0010  Virtual display unavailable
WYRE-STRM-W001  Bitrate reduced due to network conditions
WYRE-STRM-W002  Switching to simulcast — client quality gap >85%
WYRE-STRM-W003  Encoder falling back to software
WYRE-STRM-U001  Subprocess crashed — restart?
WYRE-STRM-U002  Recording started — consent indicator
```

---

## Stream.Audio

### Responsibility

Audio capture, Opus encoding via Concentus, per-application audio routing, fan-out to all clients.

### Per-App Audio Routing

Windows: WASAPI session enumeration — enumerate all active audio sessions, let host choose which apps to capture. Linux: PipeWire node targeting — query available audio nodes, capture specific ones.

Always encode once, fan-out same Opus stream to all clients. Audio bitrate is negligible so no per-client encoding.

### Audio Mixing (Multi-Client Mic)

Each client's microphone shows up as a separate audio device on the host — not mixed, not merged. Host can choose to listen to any or all. Implementation: each client's Opus audio stream decoded and injected as a separate virtual audio device via:
- Windows: Virtual Audio Cable approach or WASAPI virtual device
- Linux: PipeWire virtual source per client

---

## Stream.AppWindow

### Responsibility

Single-window sharing. Only that window and its children are captured and streamed.

Windows:
- Use `GraphicsCaptureItem.TryCreateFromWindowId(hwnd)` 
- Track children via `EnumChildWindows`
- Handle `WM_CHILDACTIVATE` for popups

Linux (X11):
- `XCompositeRedirectWindow` on the target window
- `XQueryTree` for child tracking
- `XDamage` for efficient change detection — only re-encode changed regions

Each forwarded window renders into a borderless, always-on-top Avalonia window on the client side. From user perspective it looks like a native window. Illusion breaks for GPU-heavy apps and apps using OS-native drag-and-drop.

---

## AdaptiveRate

### Responsibility

Closed-loop control system for bitrate, framerate, and input polling rate. BBR-style probing, PID controller for steady-state, per-client feedback integration.

### Rate Controller State Machine

```csharp
public enum RateState { Probing, Steady, Recovering, Flooded }

public class AdaptiveRateController : IHostedService
{
    // User-configurable bounds
    public int MaxFps { get; set; } = 280;
    public int MinFps { get; set; } = 24;
    public int MaxBitrateBps { get; set; } = 100_000_000;
    public int MinBitrateBps { get; set; } = 500_000;

    // Measured per client
    private double _availableBandwidth;
    private double _rttMs;
    private double _packetLossPercent;
    private double _decodeTimeMs;
    private double _jitter;
    private int _renderQueueDepth;
}
```

### PID Controller

```csharp
public class PidController
{
    private readonly double _kp;
    private readonly double _ki;
    private readonly double _kd;
    private double _integral;
    private double _lastError;

    // Starting values: kp=0.8, ki=0.1, kd=0.05 — tune empirically
    public double Update(double error, double dt)
    {
        _integral += error * dt;
        double derivative = (error - _lastError) / dt;
        _lastError = error;
        return _kp * error + _ki * _integral + _kd * derivative;
    }
}
```

### BBR-style Probing

Periodically send probe bursts slightly above current rate to find ceiling without sustained congestion. Track one-way delay gradient (rising gradient = congestion before packet loss appears). Reduce rate proactively on rising gradient.

### Adaptive Input Polling

```csharp
private int CalculateOptimalPollingHz()
{
    // Never poll faster than one event per RTT/2
    double minIntervalMs = Math.Max(_rttMs / 2.0, 1000.0 / MaxPollingHz);
    int networkLimitedHz = (int)(1000.0 / minIntervalMs);
    return Math.Min(networkLimitedHz, _receiverReportedMaxInputHz);
}
```

Use `System.Threading.PeriodicTimer` for the polling loop — doesn't drift.

### GC Pressure

At 280fps, 280 frames/second through the pipeline. All frame buffers pooled via `System.Buffers.ArrayPool<byte>`. Entire pipeline via `System.IO.Pipelines` — zero allocation on hot path. Decode output buffer pinned via `NativeMemory.AlignedAlloc`.

---

## Buffer Strategy

```csharp
// Rent on receive, return after decode
var buffer = ArrayPool<byte>.Shared.Rent(estimatedSize);
try
{
    // process frame
}
finally
{
    ArrayPool<byte>.Shared.Return(buffer);
}

// Decode output — pre-allocated, 4K worst case, never touched by GC
private unsafe readonly byte* _decodeOutput =
    (byte*)NativeMemory.AlignedAlloc(3840 * 2160 * 4, 64);
```

---

## NuGet Dependencies

```xml
<PackageReference Include="WyreConnector.Sdk" />
<PackageReference Include="Microsoft.Orleans.Sdk" Version="9.2.1" />
<PackageReference Include="SIPSorcery" />
<PackageReference Include="SIPSorceryMedia.FFmpeg" />
<PackageReference Include="Concentus" />
<PackageReference Include="Vortice.Windows" />          <!-- Windows only -->
<PackageReference Include="FFmpeg.AutoGen" />
<PackageReference Include="Polly" />
```
