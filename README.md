# GBFR Extra Sigil Slots

Reloaded-II configurable extra-sigil-slot mod for Granblue Fantasy: Relink ER 2.0.2.

The repository contains a C++ native hook and a C# Reloaded-II loader, but they are packaged as one Reloaded-II mod.

The compact selector opens with `F8`, supports Simplified Chinese and English (including Chinese IME input), displays the current character by name, and reports the complete valid physical-sigil scan count separately from the filtered picker match count. Version 0.5.0 provides 1–24 configurable virtual slots, named multi-character presets, usage filters, body-slot conflict reporting, and confirmed ownership transfers.

## Virtual slot count

Set `VirtualSlotCount` in `GBFR-ExtraSigilSlotsNumConfig.ini`, then restart the game. The default is `8` and the supported range is `1` through `24`.

- A missing, empty, non-numeric, signed, negative, or otherwise malformed value is rewritten to `8`.
- `0` is rewritten to `1`.
- A value above `24` is rewritten to `24`.
- Reducing the count clears and rewrites selections beyond the new active range so hidden slots cannot keep inventory sigils reserved.

## Build and package

Requirements:

- Windows x64
- Visual Studio 2022 Build Tools with MSVC v143 and a Windows SDK
- .NET 8 SDK
- PowerShell 5.1 or newer

Run from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\build-release.ps1
```

The script defaults to `Release`, `x64`, and version `0.5.0`. These defaults can
be overridden explicitly, for example with `-Configuration Debug` or
`-Version 0.5.0-test`.

The installable archive is generated at:

```text
dist\GBFR-Extra-Sigil-Slots-0.5.0.zip
```

Extract the `GBFR.ExtraSigilSlots20.Reloaded` folder from the ZIP into Reloaded-II's `Mods` directory. The package includes a default `GBFR-ExtraSigilSlotsNumConfig.ini` next to the native DLL so the slot count can be edited immediately. Back up and restore your existing INI when updating if you want to keep its settings; rename a legacy `GBFR-ExtraSigilSlots20.ini` backup to the new filename before restoring it. The runtime-created `GBFR-ExtraSigilSlots20.presets.json` remains excluded so named presets are not overwritten.

## Development

- [Native architecture and refactor plan](docs/native-architecture.md)
- [Smoke-test harnesses](tests/README.md)
