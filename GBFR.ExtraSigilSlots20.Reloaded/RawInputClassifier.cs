namespace GBFR.ExtraSigilSlots20.Reloaded;

internal static class RawInputClassifier
{
    internal static bool IsKeyboardOrMouse(uint type) => type is 0 or 1;
}
