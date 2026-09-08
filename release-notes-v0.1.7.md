# Custom Vencord Manager v0.1.7

- Makes dashboard startup feel effectively immediate by beginning WebView2 environment initialization at the application level before the manager window is constructed.
- Replaces the generic startup loader with a ReUI/shadcn-style skeleton that mirrors the final sidebar, release card, version/trust area, actions, progress surface, and lower cards.
- Keeps the legacy WPF dashboard hidden during healthy startup and reserves it for genuine WebView2 failure fallback.
- Injects the initial native manager state earlier so React can render the real selected-client/version state with less handoff work.
- Removes Framer Motion from the production frontend and replaces the small remaining effects with lightweight CSS transitions/keyframes, reducing the JavaScript bundle substantially while preserving restrained motion and reduced-motion behavior.
- Keeps the fixed 1040×800 non-resizable window and all existing Stable/PTB/Canary isolation, verification, rollback, recovery, self-update, and safety behavior.

Live Discord mutation testing for this release is restricted to Discord PTB while Stable is in active use.
