# Custom Vencord Manager v0.1.6

- Removes the visible legacy WPF-dashboard flash during normal startup.
- Shows a lightweight ReUI-black startup surface while WebView2 initializes.
- Keeps the React dashboard hidden until it has mounted, received native manager state, and rendered that state.
- Reuses the extracted Web UI cache immediately on subsequent launches of the same manager build.
- Keeps the native WPF dashboard available only as a true WebView2/runtime failure fallback.
- Preserves the v0.1.5 progress-bar completion fix, fixed 1040×800 window, per-client isolation, recovery, rollback, verification, and self-update behavior.
