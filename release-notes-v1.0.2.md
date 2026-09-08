# v1.0.2

Corrected payload release replacing withdrawn v1.0.1.

- OrionQuests updates from **v4.10.12** to **v4.10.13** (upstream `8eab919d`).
- Fixes the unrunnable-quest rescan loop by marking quests this client cannot drive as skipped for the rest of that Orion run.
- Adds explicit handling for `ACHIEVEMENT_IN_GAME` quests that require the real linked game/achievement.
- Tracks completed, blocked, and failed quest outcomes separately so skipped/failed runs are not reported as fully completed.
- Preserves this distribution's Vencord-specific Orion integration and lint customizations.
- Vencord itself remains current at **1.15.4 / `0e40e433`**; no newer Vencord upstream commit exists for this release.
- Corrects the withdrawn v1.0.1 packaging defect by using the intended **standalone, updater-disabled** Vencord build and including the manager-required non-empty `dist/package.json`.
- The exact release ZIP is preflighted against the manager's complete integrity-file contract and tested through a real **v1.0.0 -> v1.0.2** Canary update, verification, uninstall, and byte-for-byte restore cycle before publication.
- Custom Vencord Manager is now **v0.1.7**. v0.1.5 introduced the approved React/Tailwind + WebView2 dashboard and ReUI-inspired inverted-black visuals; v0.1.6 removed the normal-startup legacy WPF flash; v0.1.7 prewarms the WebView2 environment earlier, replaces the generic loader with a layout-matched ReUI/shadcn skeleton, injects native state earlier, and removes Framer Motion from the production frontend in favor of lighter CSS transitions. The fixed 1040x800 window, native failure fallback, manager backend, and per-client isolation semantics remain unchanged.
