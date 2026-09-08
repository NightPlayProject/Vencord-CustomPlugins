# Update System

`update-manifest.json` is the stable-channel source of truth for installed clients.

The updater should:

1. Inspect the selected local Discord install **before any GitHub request**.
2. Detect whether Discord is already patched and read the injected `app.asar` target.
3. Recover manager state when that target is the manager-owned `dist\patcher.js`.
4. Fetch the manifest from the repository's raw `main` branch.
5. Compare `version` against the locally installed distribution version.
6. Download `assets.windows_release.url` only when a newer version exists.
7. Verify the downloaded file against `assets.windows_release.sha256` before extracting anything.
8. Resolve Auto to one exact Discord client; if multiple clients exist, require an explicit Stable/PTB/Canary selection.
9. Ask only the selected Discord client to close, then wait until that client's processes have exited.
10. Write a client-specific pending-operation journal before the first file/injection mutation.
11. Back up only the selected client's custom Vencord folder before replacement.
12. Replace the selected client's release files atomically where practical.
13. Preserve `%APPDATA%\Vencord\plugins` and user settings.
14. Ensure only the selected Discord client's injected `app.asar` points to that client's managed `dist\patcher.js`.
15. Verify `_app.asar`, the exact patch target, and the managed payload SHA-256 fingerprints before reporting success.
16. Roll back the selected client's managed-file backup and exact previous injection target if verification or installation fails.
17. Recover interrupted `app.asar` rename states from the journal after a crash/power loss and fail closed if recovery cannot be verified.
18. Restart only the selected Discord client, and only after a successful operation or verified rollback/recovery.

The release version, URLs and hashes must be updated together for every publication.

## Distribution release preflight

Build the payload from the Vencord source tree with the declared pnpm version and the exact distribution flags:

```powershell
npx -y pnpm@11.9.0 build --standalone --disable-updater
```

The release archive must also contain a non-empty `dist\package.json` (the current distribution uses `{}`). The Vencord build itself does not create this file.

Before any GitHub release is published or the manifest is changed, validate the exact ZIP that will be uploaded:

```powershell
.\scripts\validate-distribution.ps1 `
  -ZipPath <release-zip> `
  -ExpectedOrion <orion-version> `
  -ExpectedVencordVersion <vencord-version> `
  -ExpectedVencordCommit <vencord-commit>
```

This preflight mirrors the manager's managed-payload integrity file set, rejects empty or missing files, rejects accidental `dist\dist` nesting, parses `dist\package.json`, and checks the payload README metadata. A release must not be advertised until this validation and an install/verify/uninstall isolation test both pass.

## Manager updates

The manifest can also contain a top-level `manager` object with its own version, download URL and SHA-256. When that version is newer than the running manager, the UI offers a manager update. The replacement executable is downloaded and verified first, then a short-lived helper replaces the manager after the current process exits and starts the new build.

The manager executable itself is published as a GitHub Release asset; it is not committed to the repository.

## Multiple Discord clients

Discord Stable, PTB, and Canary are probed, stored, backed up, patched, repaired and uninstalled independently. New manager-owned payloads live under `%LOCALAPPDATA%\NightPlayProject\VencordCustomPlugins\clients\<branch>`.

- A client can be manager-owned, patched by another Vencord install, unpatched, or not installed.
- Install/update/repair/uninstall operates only on the explicitly selected client.
- Auto mode can perform mutation only when exactly one Discord client is installed; otherwise the user must choose Stable, PTB or Canary.
- The legacy `current` payload from manager v0.1.0-v0.1.2 is preserved as a rollback/migration dependency until no live injection or pending journal needs it.
- Each client has independent state, metadata, payload integrity hashes, backups and pending-operation journal.
- The UI refreshes all three client statuses after selection changes and after install/update/repair/uninstall operations.
