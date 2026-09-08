# Custom Vencord Manager v0.1.5

Manager-only update for the existing Custom Vencord distribution v1.0.2.

- Rebuilds the manager dashboard as a hybrid native WPF + embedded React/Tailwind/WebView2 interface.
- Adopts a ReUI-inspired inverted-black visual system with neutral surfaces, borders, controls, and native title-bar chrome.
- Simplifies the sidebar to the client selector and delivery-verification surfaces.
- Improves default 1040x800 layout spacing, responsive card sizing, wrapping, and client-selector proportions.
- Locks the manager window to its intended 1040x800 size and removes maximize/resize behavior.
- Polishes the native WPF fallback, confirmation dialogs, focus rings, tooltips, scrollbars, progress surfaces, and semantic status colors so fallback UI matches the React dashboard.
- Keeps C# as the sole authority for Stable/PTB/Canary selection, installation state, recovery, integrity verification, rollback, Discord process handling, and self-update safety.
- Keeps all selected-client isolation guarantees from v0.1.4; this release does not change the Custom Vencord distribution payload, which remains v1.0.2.
- Builds the React frontend in GitHub Actions before the self-contained single-file .NET publish so friends still receive one manager EXE and do not need Node.js, npm, Git, or .NET installed.
