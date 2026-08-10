# GBFR Extra Sigil Slots Standalone

Windows x64 desktop controller built with Rust, Tauri, React, and TypeScript. It
finds a running `granblue_fantasy_relink.exe`, injects the bundled Native Agent,
and exposes the v0.8.7 slot, inventory, language, count, and preset workflows in
an external window. It does not install the old in-game ImGui/input frontend.

## Build

From the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\build-standalone.ps1
```

The script builds the Native Agent, stages its DLL and TSV resources, runs the
frontend and Rust checks, then creates an NSIS installer under
`src-tauri\target\release\bundle\nsis`.

The controller and game must run at the same Windows integrity level. A process
that already loaded the Reloaded-II Native Core is rejected instead of receiving
a second copy. Closing the controller does not unload active game hooks; restart
the game to remove them.
