# Wyre Installers — wyre-installer-sdk + All First-Party Installers

**Repos:**
- `wyre-installer-sdk` — LGPL v3
- `wyre-connector-installer` — LGPL v3
- `wyre-stream-installer` — LGPL v3
- `wyre-files-installer` — LGPL v3
- `wyre-relay-installer` — LGPL v3
- `wyre-central-installer` — LGPL v3

---

## Self-Installing Binary Pattern

All Wyre installers are self-installing single binaries. Run directly — no separate setup.exe. The binary IS the installer.

```
wyre-stream-installer-win-x64.exe
    → run with no args     = interactive GUI installer
    → run with --install   = same as no args (explicit)
    → run with --silent    = install with defaults, no prompts
    → run with --uninstall = uninstall
```

Relay and central installers are even simpler — they just copy the binary to a chosen location. No system integration needed.

---

## wyre-installer-sdk

**NuGet:** `WyreInstaller.Sdk`

Shared logic used by ALL first-party and community installers. Community root module authors use this to build their own installers.

### Core API

```csharp
namespace Wyre.Installer.Sdk;

public static class WyreInstallerContext
{
    // Detection
    public static bool IsConnectorInstalled();
    public static Version? GetInstalledConnectorVersion();
    public static string? GetConnectorInstallPath();

    // Installation
    public static Task<InstallResult> InstallConnectorAsync(
        string installPath,
        IProgress<InstallProgress> progress,
        CancellationToken ct = default);

    public static Task<InstallResult> InstallModulesAsync(
        string[] moduleDllPaths,
        string connectorModulesPath,
        IProgress<InstallProgress> progress,
        CancellationToken ct = default);

    // Shortcuts
    public static Task CreateShortcutAsync(ShortcutConfig config);
    public static Task RemoveShortcutAsync(string shortcutName);

    // Platform setup
    public static Task RegisterUriSchemeAsync(string scheme, string exePath);
    public static Task RegisterAutostartAsync(string appName, string exePath, string[] args);
    public static Task RemoveAutostartAsync(string appName);
    public static Task AddFirewallRuleAsync(string ruleName, string exePath);
    public static Task RemoveFirewallRuleAsync(string ruleName);

    // Uninstall
    public static Task<UninstallResult> UninstallModulesAsync(string[] moduleIds);
    public static Task<UninstallResult> UninstallConnectorAsync();
}

public record ShortcutConfig(
    string Name,                    // "Wyre Stream"
    string TargetExe,               // path to wyre-connector.exe
    string[]? Args,                 // ["--root", "wyrestream"]
    string? IconPath,
    ShortcutLocation Location       // Desktop, StartMenu, Both
);

public record InstallProgress(
    string CurrentStep,
    int ProgressPercent,
    string? Detail
);
```

### Embedded Assets

Installer binaries embed the actual app binaries as embedded resources. Extracted on install:

```xml
<ItemGroup>
    <EmbeddedResource Include="Assets\win-x64\wyre-connector.exe"
                      LogicalName="assets.win-x64.connector" />
    <EmbeddedResource Include="Assets\linux-x64\wyre-connector"
                      LogicalName="assets.linux-x64.connector" />
    <EmbeddedResource Include="Assets\modules\Mesh.Core.dll"
                      LogicalName="assets.modules.mesh-core" />
    <!-- etc -->
</ItemGroup>
```

Extraction helper:
```csharp
public static async Task ExtractEmbeddedAsset(
    string logicalName,
    string destinationPath)
{
    var assembly = Assembly.GetExecutingAssembly();
    await using var stream = assembly.GetManifestResourceStream(logicalName)
        ?? throw new InvalidOperationException($"Asset not found: {logicalName}");

    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
    await using var file = File.Create(destinationPath);
    await stream.CopyToAsync(file);

    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        File.SetUnixFileMode(destinationPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
}
```

### Update System

```csharp
public class WyreUpdater
{
    private const string ManifestUrl = "https://wyre.zombidev.me/releases/manifest.json";

    public static async Task<UpdateCheckResult> CheckForUpdateAsync(
        string component,  // "connector", "stream", "relay", "central"
        Version currentVersion)
    {
        var manifest = await FetchManifestAsync();
        var latest = manifest.Components[component].Version;
        return new UpdateCheckResult(
            HasUpdate: latest > currentVersion,
            LatestVersion: latest,
            ReleaseNotes: manifest.Components[component].ReleaseNotes,
            DownloadUrl: manifest.Components[component].Downloads[PlatformKey()]);
    }

    public static async Task UpdateAsync(
        UpdateCheckResult update,
        IProgress<InstallProgress> progress)
    {
        // 1. Download to temp
        var tempPath = await DownloadWithProgressAsync(update.DownloadUrl, progress);

        // 2. Verify checksum against manifest (manifest signed with ECDsa)
        VerifyChecksum(tempPath, update.ExpectedChecksum);

        // 3. Write update script (can't replace running binary directly)
        // 4. Launch update script, exit current process
        // Update script: wait for current process to exit, copy new binary, relaunch
        await WriteAndLaunchUpdateScriptAsync(tempPath);
        Environment.Exit(0);
    }
}
```

Update manifest format:
```json
{
    "signed_at": "2025-01-01T00:00:00Z",
    "signature": "base64(ECDsa signature)",
    "public_key": "base64(ECDsa public key)",
    "components": {
        "connector": {
            "version": "1.2.0",
            "release_notes": "...",
            "downloads": {
                "win-x64": "https://wyre.zombidev.me/releases/1.2.0/connector-win-x64.exe",
                "linux-x64": "https://wyre.zombidev.me/releases/1.2.0/connector-linux-x64"
            },
            "checksums": {
                "win-x64": "sha256:abc123...",
                "linux-x64": "sha256:def456..."
            }
        }
    }
}
```

---

## wyre-connector-installer

Installs the Wyre Connector runtime. Foundation for all other installers.

### Installer UI (Avalonia)

```
Step 1: Welcome
    "Wyre Connector is the runtime that powers all Wyre applications.
     It does nothing alone — install a Wyre application after this."
    [Next]  [Cancel]

Step 2: Install Location
    [ C:\Users\User\AppData\Local\WyreConnector\ ▼ ] [Browse...]
    Disk space required: 45 MB
    Available: 234 GB
    [Back]  [Install]

Step 3: Progress
    Installing Wyre Connector...
    ████████████░░░░  75%
    Extracting files...

Step 4: Done
    ✓ Wyre Connector installed successfully.
    [Finish]
```

### Post-Install Steps

**Windows:**
```csharp
// Register URI scheme: wyreconnector://
// Register in PATH (optional, prompted)
// Add Windows Defender exclusion (optional, prompted, requires elevation)
// Windows Firewall rule (auto, via elevation prompt)
```

**Linux:**
```csharp
// Write .desktop file to ~/.local/share/applications/
// Register URI scheme via xdg-mime
// Add user to 'input' group (for input injection)
// Write udev rule for /dev/uinput access
// Run: update-desktop-database
// Run: modprobe vkms (for virtual display)
```

### Elevation

For operations requiring elevation, spawn `wyre-elevate` helper:
- Windows: separate `wyre-elevate.exe` with manifest requesting elevation (UAC prompt)
- Linux: `pkexec wyre-elevate` (PolicyKit prompt)

Never run the entire installer elevated — only specific operations.

---

## wyre-stream-installer

Installs Wyre Stream root module + all stream modules on top of Connector.

### Connector Detection + Auto-Install

```csharp
var connectorInstalled = WyreInstallerContext.IsConnectorInstalled();

if (!connectorInstalled)
{
    var choice = await ShowPromptAsync(
        title: "Wyre Connector Required",
        message: "Wyre Stream requires Wyre Connector to run.\n" +
                 "Install Wyre Connector now?",
        buttons: ["Install Connector + Stream", "Cancel"],
        code: "WYRE-CONN-U005");

    if (choice == "Cancel") return;

    // Install Connector first (runs Connector installer steps silently)
    await WyreInstallerContext.InstallConnectorAsync(
        defaultPath, progress);
}

// Then install Stream modules into connector's modules folder
await WyreInstallerContext.InstallModulesAsync(streamModules, modulesPath, progress);

// Create "Wyre Stream" shortcut
await WyreInstallerContext.CreateShortcutAsync(new ShortcutConfig(
    Name: "Wyre Stream",
    TargetExe: connectorExePath,
    Args: ["--root", "wyrestream"],
    IconPath: streamIconPath,
    Location: ShortcutLocation.Both));

// Register URL scheme
await WyreInstallerContext.RegisterUriSchemeAsync("wyrestream", connectorExePath);
```

### Component Selection

```
Step: Choose Components
  ☑ Wyre Stream (required)
  ☑ Virtual Display Driver    (enables >60fps capture — requires admin)
  ☑ ViGEm Controller Driver   (enables gamepad passthrough — requires admin)
  ☑ Add to startup
```

Optional components prompt for elevation separately.

---

## wyre-files-installer

Same pattern as Stream installer. Detects + auto-installs Connector if missing. Installs Files root module. Creates "Wyre Files" shortcut pointing to `wyre-connector.exe --root wyrefiles`. Registers `wyrefiles://` URI scheme.

---

## wyre-relay-installer

Simpler — relay is standalone, no Connector needed.

```
Step 1: Welcome
Step 2: Install location (default: current directory or user-chosen path)
Step 3: Copy binary
Step 4: Create shortcut (optional)
Done
```

No elevation needed. No system integration beyond an optional shortcut. relay.toml created by the relay binary itself on first run — installer doesn't touch it.

---

## wyre-central-installer

Same as relay installer. Even simpler — most central deployments are server/Docker. The installer is mainly for users who want to run central on a desktop.

```
Step 1: Welcome
    "Wyre Connector Central is the discovery server for Wyre meshes.
     Most users don't need to install this — use the public central at
     central.wyre.zombidev.me or deploy via Docker."
    [Continue]  [Open Docker Guide]  [Cancel]

Step 2: Install location
Step 3: Copy binary
Step 4: Done
```

---

## Installer Scheme Summary

All Wyre installers follow the same scheme (enforced by `WyreInstaller.Sdk`):

```
1. Check Connector installed (if needed)
2. Prompt to install Connector if missing (with code WYRE-CONN-U005)
3. Extract embedded binaries/modules
4. Verify checksums of extracted files
5. Platform post-install steps
6. Create shortcuts
7. Register URI schemes
8. Done screen with optional launch
```

Community root module installers use this same scheme via `WyreInstaller.Sdk` — consistent experience for users regardless of which module they're installing.
