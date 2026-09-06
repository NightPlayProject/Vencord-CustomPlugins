# v1.0.0

Initial public distribution release.

- Custom Vencord fork based on Vencord 1.15.4 / commit `0e40e433`.
- Runtime JavaScript custom-plugin loader.
- OrionQuests updated to v4.10.12, including the heartbeat watchdog fixes.
- NitroSniper included as a built-in plugin.
- Added the first public **Custom Vencord Manager v0.1.0** for one-click install, updates, repair and uninstall.
- Manager downloads are SHA-256 verified and distribution updates keep rollback backups.
- Manager UI uses a custom desktop shell, segmented Discord-channel controls and in-app confirmation/error dialogs instead of stock Windows dialogs.
- Added manager self-update metadata so future manager builds can replace themselves from GitHub Releases.

## Manager v0.1.1

- Verifies the selected local Discord installation before contacting GitHub.
- Detects and recovers manager-owned Custom Vencord installs even when `manager-state.json` was never written.
- Replaces the hanging external Vencord Installer CLI step with the same small `app.asar` injection format used by Vencord itself.
- Verifies the Discord patch target after install/update/repair before showing success.
- Adds explicit **Installation verified**, **Repair verified**, and **Uninstall verified** completion dialogs.

## Manager v0.1.2

- Tracks Discord Stable, PTB, and Canary independently instead of reusing one client's install status for another.
- Adds live per-client badges for manager-owned Custom Vencord, another Vencord install, no Custom Vencord, or Discord not found.
- Refreshes the main action/status immediately when switching Discord clients.
- Reuses the shared managed payload when adding the same release to another Discord client instead of downloading it again.
- Uninstalling one Discord channel no longer removes shared managed files while another channel still uses them.
- Shared-build updates re-verify every Discord client that was already using the manager-owned build.
- Adds eased mouse-wheel scrolling and smoother touch/trackpad panning for the dashboard and activity log.
- Writes local install metadata so interrupted installs can be recovered more reliably on the next launch.

## Manager v0.1.3

- Replaces the v0.1.2 shared-payload architecture with fully isolated Stable/PTB/Canary payloads, state, metadata and backups.
- Install, update, repair and uninstall now stop/restart and modify only the explicitly selected Discord client.
- Auto mode requires an explicit client selection when more than one Discord desktop client is installed.
- Migrates legacy shared `current` installs one client at a time without deleting another client's rollback dependency.
- Adds durable pending-operation journals for install/update/repair, fast attach and uninstall.
- Recovers hard-crash/power-loss states between `app.asar`, `_app.asar`, `app.asar.manager-new` and `app.asar.manager-old` rename steps.
- Captures the exact `app-*\\resources` directory after Discord closes and uses that exact directory for rollback instead of re-resolving a possibly newer Discord app folder.
- Adds atomic manager-state and install-metadata writes with backup copies.
- Adds per-file SHA-256 fingerprints for the managed Vencord payload and refuses to mark a corrupted payload as verified.
- Keeps a Discord client closed when rollback/recovery cannot be verified instead of restarting a potentially broken installation.
- Adds a single-instance manager guard and blocks closing the manager while a critical Vencord mutation is active.
- Removes automatic example-plugin writes from install/repair so a single-client operation does not silently change the shared runtime plugins folder.
