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
8. Ask Discord to close, then wait until Discord processes have exited.
9. Back up the current custom Vencord folder before replacement.
10. Replace the release files atomically where practical.
11. Preserve `%APPDATA%\Vencord\plugins` and user settings.
12. Ensure Discord's injected `app.asar` points to the manager-owned `dist\patcher.js`.
13. Verify `_app.asar`, the patch target, and the managed patcher file before reporting success.
14. Roll back the managed-file backup and previous injection target if verification or installation fails.
15. Restart Discord only after a successful verified update.

The release version, URLs and hashes must be updated together for every publication.

## Manager updates

The manifest can also contain a top-level `manager` object with its own version, download URL and SHA-256. When that version is newer than the running manager, the UI offers a manager update. The replacement executable is downloaded and verified first, then a short-lived helper replaces the manager after the current process exits and starts the new build.

The manager executable itself is published as a GitHub Release asset; it is not committed to the repository.
