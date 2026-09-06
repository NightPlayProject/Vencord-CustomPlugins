# Custom Vencord Manager

`VencordCustomManager` is the Windows installer/updater for this distribution.

## What it does

- reads `update-manifest.json` from the public repository
- downloads the latest release package over HTTPS
- verifies the package SHA-256 before extraction
- installs into `%LOCALAPPDATA%\NightPlayProject\VencordCustomPlugins\current`
- closes and restarts Discord when required
- keeps rollback backups during updates
- repairs or uninstalls through the official Vencord installer CLI
- preserves `%APPDATA%\Vencord\plugins`
- supports self-updates through the optional `manager` section of the manifest

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
