# wyre-files — Wyre Files

**Repo:** `wyre-files`
**License:** GPL v3
**Language:** C# / .NET 9

---

## Purpose

Standalone file sharing application built on Wyre Connector. Works completely independently as a LAN/mesh file sharing tool. When both Wyre Files and Wyre Stream are installed on the same Connector instance, it transparently extends Stream with file transfer capabilities — via bus messages only, no direct module coupling.

---

## Module List

```
WyreFiles.Root.dll              # root module — owns the UI and app lifecycle
FileTransfer.Server.dll         # Kestrel HTTP server, HTTP 206, tusdotnet
FileTransfer.Client.dll         # download client, progress tracking, resume
FileTransfer.DragDrop.dll       # handles FileTransferRequestedMessage from Stream
FileTransfer.BandwidthCap.dll   # rate limiter, bandwidth negotiation
Clipboard.Files.dll             # file clipboard sync across mesh
UI.FileTransfer.dll             # file browser, transfer queue UI
```

Each has a companion `*.Messages.dll`.

---

## Standalone UI

When run alone (no Stream installed or no stream session active), shows a full file sharing interface:

```
┌─────────────────────────────────────────────────────────────────┐
│ Wyre Files                                               [─][□][X]│
├────────────────────┬────────────────────────────────────────────┤
│ Nodes              │  Files from John's Desktop                  │
│                    │                                             │
│ ● My Node          │  📁 Documents/                              │
│ ● John's Desktop   │  📁 Downloads/                              │
│ ○ Sarah's Laptop   │  📄 report.pdf              2.4 MB          │
│   (offline)        │  📷 photo.jpg               8.1 MB          │
│                    │  📦 archive.zip             156 MB          │
│                    │                                             │
│                    │  [Download Selected]  [Send File...]        │
├────────────────────┼────────────────────────────────────────────┤
│ Transfers          │                                             │
│                    │  ↓ report.pdf from John's Desktop  ████ 87% │
│                    │  ↑ photo.png to Sarah's Laptop     ██░░ 34% │
└────────────────────┴────────────────────────────────────────────┘
```

---

## Stream Integration (Bus-Based, Zero Coupling)

Wyre Files subscribes to messages that Wyre Stream publishes. Wyre Stream has no knowledge of Wyre Files existing.

### What Wyre Stream publishes

```csharp
// In Stream.DragDrop.Messages.dll (part of wyre-stream)
public record FileTransferRequestedMessage(
    string SessionId,
    string FromNodeId,
    string FileName,
    long FileSizeBytes,
    string[]? FilePaths        // paths on host
);

// In Clipboard.Sync.Messages.dll (part of wyre-stream)
public record ClipboardFilesReadyMessage(
    string FromNodeId,
    string[] FilePaths
);
```

### What Wyre Files subscribes to

```csharp
// In FileTransfer.DragDrop.dll
ctx.Bus.Subscribe<FileTransferRequestedMessage>(async msg =>
{
    // Check ACL (DRAG_DROP permission)
    if (!await _acl.IsPermittedAsync(msg.FromNodeId, Permission.DRAG_DROP))
    {
        Bus.Publish(new FileTransferRejectedMessage(msg.SessionId, "Permission denied"));
        return;
    }

    // Check host approval setting
    if (!_config.AutoApprove)
    {
        var approved = await ShowApprovalPromptAsync(msg);
        if (!approved)
        {
            Bus.Publish(new FileTransferRejectedMessage(msg.SessionId, "Rejected by host"));
            return;
        }
    }

    // Start the actual transfer
    await _transferService.StartTransferAsync(msg);
});

ctx.Bus.Subscribe<ClipboardFilesReadyMessage>(async msg =>
    await _transferService.StartTransferAsync(msg));
```

### What Wyre Files publishes (Stream subscribes)

```csharp
// In FileTransfer.Client.Messages.dll (part of wyre-files)
public record FileTransferProgressMessage(
    string TransferId,
    string FileName,
    long BytesTransferred,
    long TotalBytes,
    double SpeedBytesPerSecond
);

public record FileTransferCompleteMessage(
    string TransferId,
    string FileName,
    string LocalPath,
    TimeSpan Duration
);

public record FileTransferRejectedMessage(
    string TransferId,
    string Reason
);
```

Wyre Stream's `Notifications.dll` subscribes to these and shows toasts. Wyre Stream's `UI.StreamView.dll` subscribes and shows a progress indicator in the stream overlay.

---

## When Stream is NOT installed

If `FileTransferRequestedMessage` is never published (no Stream), Wyre Files works standalone via its own UI — user manually selects files and target nodes. The bus integration is additive, not required.

---

## FileTransfer.Server

HTTP server for file serving. Uses Kestrel + `Microsoft.AspNetCore.StaticFiles` for HTTP 206 range request support (seekable video playback, resumable downloads).

```csharp
app.MapGet("/files/{nodeId}/{**filePath}", async (
    string nodeId,
    string filePath,
    HttpContext ctx) =>
{
    // ACL check: FILE_READ permission for requesting node
    if (!await _acl.IsPermittedAsync(requestingNodeId, Permission.FILE_READ))
        return Results.Forbid();

    var fullPath = ResolveAndValidatePath(nodeId, filePath);
    return Results.File(fullPath, enableRangeProcessing: true);
});
```

For resumable uploads: `tusdotnet` middleware. Handles large file transfers that survive network interruptions.

---

## FileTransfer.BandwidthCap

Rate limiter ensuring file transfers don't starve the stream.

```toml
[files.transfer]
max_bandwidth_percent = 50   # use at most 50% of available bandwidth for transfers
                             # "highest between client and host" negotiated at connect time
```

Negotiation at connect time: both sides report their max transfer bandwidth, use the higher of the two values (as specified in design). Applied via a token bucket rate limiter wrapping the download stream.

---

## Host Approval Popup

When a client attempts a file transfer or drag-drop and auto-approve is NOT set in ACL:

```
┌─────────────────────────────────────────────┐
│ Incoming File Transfer          WYRE-FILE-U001│
├─────────────────────────────────────────────┤
│ John's Desktop wants to send:               │
│                                             │
│   📄 report.pdf   (2.4 MB)                  │
│                                             │
│ From: John's Desktop  (node: ABC123)        │
│                                             │
│ [Approve]              [Reject]             │
└─────────────────────────────────────────────┘
```

Hooks can auto-respond to `WYRE-FILE-U001` — power users can configure auto-approve for specific nodes.

---

## Clipboard.Files

Extends `Clipboard.Sync` (from wyre-stream) with file clipboard support. When clipboard contains files:
- Encrypts file metadata (paths, sizes)
- Publishes to `clipboard-sync` channel on central
- Peers receive → show notification "Clipboard contains N files from John's Desktop"
- Peer clicks "Get files" → triggers `FileTransferRequestedMessage`

Full file content is NOT sent via the clipboard channel (that's just for signaling). Actual file transfer goes through the HTTP server.

---

## NuGet Dependencies

```xml
<PackageReference Include="WyreConnector.Sdk" />
<PackageReference Include="Microsoft.Orleans.Sdk" Version="9.2.1" />
<PackageReference Include="Microsoft.AspNetCore" />
<PackageReference Include="tusdotnet" />
<PackageReference Include="TextCopy" />
<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" />
<PackageReference Include="Avalonia" />
<PackageReference Include="Avalonia.ReactiveUI" />
<PackageReference Include="Polly" />
<PackageReference Include="Tomlyn" Version="0.20.0" />
<PackageReference Include="Serilog" />
```

---

## Error Codes

```
WYRE-FILE-0001  Transfer rejected by host
WYRE-FILE-0002  Transfer approval timeout
WYRE-FILE-0003  Transfer interrupted
WYRE-FILE-0004  Transfer checksum mismatch
WYRE-FILE-0005  Transfer bandwidth cap exceeded
WYRE-FILE-0006  Transfer destination full
WYRE-FILE-U001  Incoming file transfer — approve?
WYRE-FILE-U002  Incoming drag-drop — approve?
```
