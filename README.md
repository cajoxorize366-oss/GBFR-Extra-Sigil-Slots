# GBFR Extra Sigil Slots

Reloaded-II version of the 20-sigil-slot mod for Granblue Fantasy: Relink ER 2.0.2.

The repository contains a C++ native hook and a C# Reloaded-II loader, but they are packaged as one Reloaded-II mod.

The compact selector opens with `F8`, supports Simplified Chinese and English (including Chinese IME input), displays the current character by name, and reports the complete valid physical-sigil scan count separately from the filtered picker match count. Version 0.4.1 provides named multi-character presets, usage filters, body-slot conflict reporting, and working confirmation dialogs for sigils already assigned to body or virtual extension slots.

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

The installable archive is generated at:

```text
dist\GBFR-Extra-Sigil-Slots-0.4.1.zip
```

Extract the `GBFR.ExtraSigilSlots20.Reloaded` folder from the ZIP into Reloaded-II's `Mods` directory. The package intentionally omits both `GBFR-ExtraSigilSlots20.ini` and the runtime-created `GBFR-ExtraSigilSlots20.presets.json`, so updating the mod does not overwrite existing selections or named presets.
