# Vencord Custom Plugins

A Windows distribution of a custom Vencord fork with runtime JavaScript plugin loading plus selected built-in plugins.

## Current release

- Distribution: **v1.0.0**
- Vencord base: **1.15.4** (`0e40e433`)
- OrionQuests: **v4.10.12**
- Runtime custom-plugin loader: included
- NitroSniper: included

## Install

1. Download `Vencord-CustomPlugins-release.zip` from the latest GitHub Release.
2. Extract it to a permanent folder.
3. Fully quit Discord.
4. Run `install.bat`.
5. Start Discord and enable the plugins you want in Vencord settings.

Do not move the extracted folder after installing; Discord loads the custom Vencord build from that location.

## Updates

This repository exposes `update-manifest.json` for the upcoming **Custom Vencord Manager** application. The manager will compare the installed distribution version to the manifest, download the matching release asset, verify its SHA-256 hash, back up the current installation, and update it.

Until the manager is published, users can update manually by downloading the latest release ZIP and reinstalling it.

## Custom plugins

Runtime `.js` plugins can be placed in:

`%APPDATA%\Vencord\plugins`

Restart Discord and enable the plugin from Vencord Settings -> Plugins.

## Source and licensing

The modified Vencord source corresponding to each binary release is published as `Vencord-CustomPlugins-source.zip` in the same GitHub Release.

Vencord is licensed under GPL-3.0. Third-party plugins retain their respective licenses. See the source archive for license files and attribution.

## Important notice

Vencord and client modifications are not endorsed by Discord. Automated quest completion and automated gift redemption may violate Discord's Terms of Service and can put an account at risk. Use at your own discretion.
