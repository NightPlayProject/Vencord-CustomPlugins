# Vencord Custom Plugins

A Windows distribution of a custom Vencord fork with runtime JavaScript plugin loading, selected built-in plugins, and a dedicated updater/installer application.

## Recommended install: Custom Vencord Manager

Download **`VencordCustomManager.exe`** from the latest GitHub Release and run it. The manager is a self-contained Windows application; your friends do not need Git, Node.js, pnpm, or the .NET runtime installed.

The manager can:

- install the latest verified Custom Vencord release
- verify an existing local Vencord installation before contacting GitHub
- recover manager state when Discord is already injected with the managed build
- check GitHub for distribution and manager updates
- verify downloads with SHA-256 before installation
- back up the current build and roll back if an update fails
- repair an installation after Discord/client changes
- target Discord Stable, PTB, Canary, or auto-detect the installed client
- open the runtime plugin and managed-install folders
- uninstall Custom Vencord while preserving `%APPDATA%\Vencord\plugins`

Each Discord client now has its own managed Vencord payload under:

`%LOCALAPPDATA%\NightPlayProject\VencordCustomPlugins\clients\stable`

`%LOCALAPPDATA%\NightPlayProject\VencordCustomPlugins\clients\ptb`

`%LOCALAPPDATA%\NightPlayProject\VencordCustomPlugins\clients\canary`

The old `current` directory is kept only as a migration source for v0.1.0-v0.1.2 installs until no Discord client still references it.

Once the manager is installed, future releases are discovered through `update-manifest.json`; users should not need you to resend ZIP files.

## Current release

- Distribution: **v1.0.0**
- Custom Vencord Manager: **v0.1.3**
- Vencord base: **1.15.4** (`0e40e433`)
- OrionQuests: **v4.10.12**
- Runtime custom-plugin loader: included
- NitroSniper: included

## Manual install

The original ZIP flow is still available as a fallback:

1. Download `Vencord-CustomPlugins-release.zip` from the latest GitHub Release.
2. Extract it to a permanent folder.
3. Fully quit Discord.
4. Run `install.bat`.
5. Start Discord and enable the plugins you want in Vencord settings.

Do not move a manually installed extracted folder afterward; Discord loads that build from its install location.

## Custom plugins

Runtime `.js` plugins can be placed in:

`%APPDATA%\Vencord\plugins`

Restart Discord and enable the plugin from Vencord Settings -> Plugins.

## Source and licensing

The modified Vencord source corresponding to each binary release is published as `Vencord-CustomPlugins-source.zip` in the same GitHub Release. The manager source is maintained directly in this repository under `manager/`.

Vencord is licensed under GPL-3.0. Third-party plugins retain their respective licenses. See the source archive for license files and attribution.

## Important notice

Vencord and client modifications are not endorsed by Discord. Automated quest completion and automated gift redemption may violate Discord's Terms of Service and can put an account at risk. Use at your own discretion.
