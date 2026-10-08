# v1.0.5

- Updates Vencord to **1.15.10 / `718c8672`**.
- Updates the complete OrionQuests port to **v4.11.5 / `38b6f84`**, including Orb-only filtering, current Orb balances/payouts, companion events, and scheduler diagnostics.
- Corrects the previous manifest/plugin version mismatch; validates the actual bundled Orion version.
- Adapts NitroSniper notifications to the updated Vencord toast API.
- Orion regression tests: **134 passed**.
- Keeps **NitroSniper** bundled.
- Keeps the runtime custom-plugin loader and existing desktop/native integrations.
- Rebuilds the standalone updater-disabled distribution from the current customized source.
- Validates the exact release ZIP with `scripts/validate-distribution.ps1` before publishing.
- Custom Vencord Manager remains **v0.1.7**.
