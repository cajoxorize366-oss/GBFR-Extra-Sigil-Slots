# GBFR Extra Sigil Slots — Reloaded-II migration

This project is the ReShade-free frontend for the ER 2.0.2 configurable extra-sigil mod.

Runtime dependency boundary:

- Reloaded-II
- Reloaded II Shared Lib: Reloaded.Hooks (`reloaded.sharedlib.hooks`)
- `Reloaded.Imgui.Hook` with its Direct3D 11 implementation

It does not call or depend on ReShade or Luma. The verified ReShade add-on remains frozen only as a behavioral reference.

Runtime split:

- `GBFR.ExtraSigilSlots20.Reloaded.dll`: Reloaded-II lifecycle, Direct3D 11 ImGui UI, hotkey and keyboard/mouse capture with controller pass-through.
- `GBFR.ExtraSigilSlots20.Native.dll`: ER 2.0.2 preflight, SafetyHook detours, inventory snapshot, per-character selections and same-thread native status rebuild.

The native core exposes a fixed packed C ABI and never renders a UI. The managed frontend never reads or writes game memory itself.
