# v1.0.6

- Updates Vencord to **1.15.11 / `32c90633`**.
- Retains the latest OrionQuests port at **v4.11.5 / `38b6f84`**, including Orb-only filtering, companion events, and scheduler diagnostics.
- Validates the bundled Orion version and upstream source pin.
- Updates NitroSniper test-webhook notifications for Vencord 1.15.11's toast API.
- Orion regression tests: **134 passed**.
- Keeps **NitroSniper** bundled.
- Keeps the runtime custom-plugin loader and existing desktop/native integrations.
- Rebuilds the standalone updater-disabled distribution from the current customized source.
- Validates the exact release ZIP with `scripts/validate-distribution.ps1` before publishing.
- **Manager-only update (October 16, 2026):** Custom Vencord Manager **v0.1.8** now uses the transferred `RealChroma/Vencord-CustomPlugins` repository for update checks and release links. The Vencord distribution and OrionQuests versions are unchanged.
- Manager **v0.1.7** remains available as a previous asset; the update manifest now offers **v0.1.8**.
