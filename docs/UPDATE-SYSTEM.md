# Update System

`update-manifest.json` is the stable-channel source of truth for installed clients.

The updater should:

1. Fetch the manifest from the repository's raw `main` branch.
2. Compare `version` against the locally installed distribution version.
3. Download `assets.windows_release.url` only when a newer version exists.
4. Verify the downloaded file against `assets.windows_release.sha256` before extracting anything.
5. Ask Discord to close, then wait until Discord processes have exited.
6. Back up the current custom Vencord folder before replacement.
7. Replace the release files atomically where practical.
8. Preserve `%APPDATA%\Vencord\plugins` and user settings.
9. Roll back the backup if verification or installation fails.
10. Restart Discord only after a successful update.

The release version, URLs and hashes must be updated together for every publication.

## Manager updates

The manifest can also contain a top-level `manager` object with its own version, download URL and SHA-256. When that version is newer than the running manager, the UI offers a manager update. The replacement executable is downloaded and verified first, then a short-lived helper replaces the manager after the current process exits and starts the new build.

The manager executable itself is published as a GitHub Release asset; it is not committed to the repository.
