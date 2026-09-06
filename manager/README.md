# Custom Vencord Manager

`VencordCustomManager` is the Windows installer/updater for this distribution.

## What it does

- reads `update-manifest.json` from the public repository
- downloads the latest release package over HTTPS
- verifies the package SHA-256 before extraction
- verifies the selected Discord installation before contacting GitHub
- detects existing Vencord injections and whether they point to this manager's build
- installs separate payloads into `%LOCALAPPDATA%\NightPlayProject\VencordCustomPlugins\clients\stable|ptb|canary`
- closes and restarts only the selected Discord client when required
- keeps per-client rollback backups during updates
- injects/repairs/uninstalls using the same small `app.asar` patch method used by the Vencord installer
- verifies the Discord injection target again before reporting success
- detects Discord Stable, PTB, and Canary independently and shows a live status for each client
- keeps install/update/repair/uninstall isolated to the explicitly selected Discord client
- refuses destructive Auto-mode actions when multiple Discord clients are installed
- migrates legacy v0.1.0-v0.1.2 shared-payload installs one client at a time
- journals patch/unpatch operations and recovers interrupted `app.asar` rename states before allowing more mutations
- verifies per-file SHA-256 fingerprints for managed payload files before treating them as healthy
- keeps state/metadata writes atomic with rollback copies
- uses eased wheel scrolling for the dashboard and activity log
- preserves `%APPDATA%\Vencord\plugins`
- supports self-updates through the optional `manager` section of the manifest

## Recovery

If the manager is interrupted during install/update/repair/attach/uninstall, the next launch detects the per-client pending-operation journal before normal state recovery. It closes only that Discord client, restores or completes the exact recorded `app-*\resources` transaction, verifies the result, and only then clears the journal and restarts the client.

New installs write `.manager-install.json` beside each client payload with per-file SHA-256 fingerprints, so local corruption is not treated as a verified installation merely because a version string or `patcher.js` exists.

## UI

The manager uses a fully custom WPF shell: custom title bar/window controls, custom segmented client selector, custom cards/buttons/progress surfaces, custom scrollbar styling, and in-app modal overlays instead of Windows `MessageBox` dialogs.

## Build

```powershell
dotnet build VencordCustomManager.sln -c Release
dotnet publish VencordCustomManager\VencordCustomManager.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:EnableCompressionInSingleFile=true `
  -o publish
```

The release artifact is `publish\VencordCustomManager.exe`.
