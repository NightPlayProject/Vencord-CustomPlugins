# v1.0.1

> **WITHDRAWN.** This release's payload ZIP was packaged incorrectly and omitted `dist/package.json`, so manager integrity verification rejected it. The broken install ZIP has been removed. Use v1.0.0 until the corrected v1.0.2 release is available.

The intended payload changes were:

- OrionQuests updated from **v4.10.12** to **v4.10.13** (upstream `8eab919d`).
- Fixes the unrunnable-quest rescan loop: quests this Discord client cannot drive are skipped for the rest of that Orion run instead of being rediscovered every cycle.
- Adds explicit handling and messaging for `ACHIEVEMENT_IN_GAME` quests that require the real linked game/achievement rather than Discord activity automation.
- Tracks completed, blocked, and failed quest outcomes separately so Orion no longer reports a failed or skipped-only run as "all completed".
- Keeps the local Vencord integration/lint customizations used by this distribution.
- Vencord itself remains current at **1.15.4 / `0e40e433`**; there was no newer upstream Vencord commit to merge for this release.
- Custom Vencord Manager remains **v0.1.4**; no manager binary changes were required.
