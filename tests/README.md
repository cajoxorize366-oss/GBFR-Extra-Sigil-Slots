# Smoke-test harnesses

These command-line harnesses validate the native ABI and the pure managed
integration paths without starting the game. They load DLLs only from an
already-built repository output directory and use isolated directories under
the system temporary folder.

Build the product and run every harness from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\build-release.ps1
powershell -ExecutionPolicy Bypass -File .\tests\run-smoke-tests.ps1
```

Pass `-Configuration Debug` to the runner after building Debug. The harness
projects themselves build as Release utilities; generated `bin` and `obj`
directories are ignored by Git.

The harnesses cover:

- strict `VirtualSlotCount` normalization and hidden-slot cleanup;
- packed ABI v9 sizes and transactional preset-reference updates;
- keyboard/mouse versus HID/controller Raw Input classification.
- event-driven frontend wake-up, key-repeat suppression, and closed-frame sleeping.
