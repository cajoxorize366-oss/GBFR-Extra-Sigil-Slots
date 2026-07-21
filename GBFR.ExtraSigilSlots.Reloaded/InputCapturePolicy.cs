namespace GBFR.ExtraSigilSlots.Reloaded;

internal static class InputCapturePolicy
{
    internal static bool ShouldCaptureWindowMessages(
        bool menuOpen,
        bool nativeBarrierActive)
    {
        // Native polling and Win32 window-message capture have different release
        // rules. The native barrier may drain held input after the menu closes,
        // but WndProc must immediately pass every message back to the game/OS.
        _ = nativeBarrierActive;
        return menuOpen;
    }
}
