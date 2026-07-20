namespace GBFR.ExtraSigilSlots.Reloaded;

internal static class WindowInputClassifier
{
    internal static bool IsAlwaysCaptured(uint message)
    {
        const uint WmNcMouseFirst = 0x00A0;
        const uint WmNcMouseLast = 0x00AD;
        const uint WmGestureFirst = 0x0119;
        const uint WmGestureLast = 0x011A;
        const uint WmKeyFirst = 0x0100;
        const uint WmKeyLast = 0x0109;
        const uint WmImeStartComposition = 0x010D;
        const uint WmImeEndComposition = 0x010E;
        const uint WmImeComposition = 0x010F;
        const uint WmMouseFirst = 0x0200;
        const uint WmMouseLast = 0x020E;
        const uint WmTouch = 0x0240;
        const uint WmPointerFirst = 0x0241;
        const uint WmPointerLast = 0x024F;
        const uint WmImeChar = 0x0286;
        const uint WmHotkey = 0x0312;
        const uint WmAppCommand = 0x0319;

        return message is >= WmNcMouseFirst and <= WmNcMouseLast ||
            message is >= WmGestureFirst and <= WmGestureLast ||
            message is >= WmKeyFirst and <= WmKeyLast ||
            message is WmImeStartComposition or WmImeEndComposition or WmImeComposition ||
            message is >= WmMouseFirst and <= WmMouseLast ||
            message == WmTouch ||
            message is >= WmPointerFirst and <= WmPointerLast ||
            message == WmImeChar ||
            message == WmHotkey ||
            message == WmAppCommand;
    }
}
