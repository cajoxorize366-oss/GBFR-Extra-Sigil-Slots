using System.Threading;

namespace GBFR.ExtraSigilSlots.Reloaded;

/// <summary>
/// Bridges Win32 input events to the Present thread without touching ImGui.
/// The graphics hook stays installed, but a closed overlay does not start an
/// ImGui frame unless a toggle request is waiting to be consumed.
/// </summary>
internal static class FrontendOverlayGate
{
    private const int DefaultToggleKey = 0x77; // F8
    private const uint WmKeyDown = 0x0100;
    private const uint WmSysKeyDown = 0x0104;
    private const long PreviousKeyStateMask = 1L << 30;

    private static int s_windowOpen;
    private static int s_pendingToggleCount;
    private static int s_toggleKey = DefaultToggleKey;

    internal static bool ShouldRenderFrame =>
        Volatile.Read(ref s_windowOpen) != 0 ||
        Volatile.Read(ref s_pendingToggleCount) != 0;

    internal static bool IsOpen => Volatile.Read(ref s_windowOpen) != 0;

    internal static int CurrentToggleKey => Volatile.Read(ref s_toggleKey);

    internal static void SetToggleKey(int virtualKey)
    {
        Volatile.Write(
            ref s_toggleKey,
            virtualKey is >= 1 and <= 255 ? virtualKey : DefaultToggleKey);
    }

    internal static void SetOpen(bool open) =>
        Volatile.Write(ref s_windowOpen, open ? 1 : 0);

    internal static bool ObserveWindowMessage(
        uint message,
        IntPtr wParam,
        IntPtr lParam)
    {
        if (message is not WmKeyDown and not WmSysKeyDown ||
            unchecked((int)wParam.ToInt64()) != CurrentToggleKey ||
            (lParam.ToInt64() & PreviousKeyStateMask) != 0)
        {
            return false;
        }

        Interlocked.Increment(ref s_pendingToggleCount);
        return true;
    }

    internal static bool ConsumeToggleRequest() =>
        (Interlocked.Exchange(ref s_pendingToggleCount, 0) & 1) != 0;

    internal static void ForceClosed()
    {
        Volatile.Write(ref s_windowOpen, 0);
        Interlocked.Exchange(ref s_pendingToggleCount, 0);
    }
}
