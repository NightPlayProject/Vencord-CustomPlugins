# Custom Vencord Manager

`VencordCustomManager` is the Windows installer/updater for this distribution.

## What it does

- reads `update-manifest.json` from the public repository
- downloads the latest release package over HTTPS
- verifies the package SHA-256 before extraction
- verifies the selected Discord installation before contacting GitHub
- detects existing Vencord injections and whether they point to this manager's build
- installs into `%LOCALAPPDATA%\NightPlayProject\VencordCustomPlugins\current`
- closes and restarts Discord when required
- keeps rollback backups during updates
- injects/repairs/uninstalls using the same small `app.asar` patch method used by the Vencord installer
- verifies the Discord injection target again before reporting success
- detects Discord Stable, PTB, and Canary independently and shows a live status for each client
- reuses an already-current managed build when adding Custom Vencord to another Discord channel
- keeps the shared managed build when one Discord channel is uninstalled but another still uses it
- re-verifies every Discord channel using the shared build after a build update
- uses eased wheel scrolling for the dashboard and activity log
- preserves `%APPDATA%\Vencord\plugins`
- supports self-updates through the optional `manager` section of the manifest

## Recovery

If Discord was already patched successfully but the manager was interrupted before it could save `manager-state.json`, the next launch detects the local injection first. If it points to the manager-owned `dist\patcher.js`, the manager recovers its state from local metadata or from the installed package after the manifest is loaded.

New installs also write `.manager-install.json` inside the managed install folder before Discord is patched, so future interrupted installs can recover their distribution version without relying only on the external update state.

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
