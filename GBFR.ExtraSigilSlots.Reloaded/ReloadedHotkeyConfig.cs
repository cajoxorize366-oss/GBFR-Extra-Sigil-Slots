using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace GBFR.ExtraSigilSlots.Reloaded;

public enum OverlayHotkey
{
    F1 = 0x70,
    F2 = 0x71,
    F3 = 0x72,
    F4 = 0x73,
    F5 = 0x74,
    F6 = 0x75,
    F7 = 0x76,
    F8 = 0x77,
    F9 = 0x78,
    F10 = 0x79,
    F11 = 0x7A,
    F12 = 0x7B,

    [Display(Name = "Insert")]
    Insert = 0x2D,

    [Display(Name = "Delete")]
    Delete = 0x2E,

    [Display(Name = "Home")]
    Home = 0x24,

    [Display(Name = "End")]
    End = 0x23,

    [Display(Name = "Page Up")]
    PageUp = 0x21,

    [Display(Name = "Page Down")]
    PageDown = 0x22,

    [Display(Name = "Pause")]
    Pause = 0x13,

    [Display(Name = "Scroll Lock")]
    ScrollLock = 0x91,

    [Display(Name = "NumPad 0")]
    NumPad0 = 0x60,

    [Display(Name = "NumPad 1")]
    NumPad1 = 0x61,

    [Display(Name = "NumPad 2")]
    NumPad2 = 0x62,

    [Display(Name = "NumPad 3")]
    NumPad3 = 0x63,

    [Display(Name = "NumPad 4")]
    NumPad4 = 0x64,

    [Display(Name = "NumPad 5")]
    NumPad5 = 0x65,

    [Display(Name = "NumPad 6")]
    NumPad6 = 0x66,

    [Display(Name = "NumPad 7")]
    NumPad7 = 0x67,

    [Display(Name = "NumPad 8")]
    NumPad8 = 0x68,

    [Display(Name = "NumPad 9")]
    NumPad9 = 0x69,
}

public sealed class HotkeyConfig : ReloadedConfigurable<HotkeyConfig>
{
    internal const string FileName = "HotkeyConfig.json";
    internal const string ConfigurationName = "Hotkey / 快捷键";

    [Category("Input / 输入")]
    [DisplayName("Overlay Hotkey / 菜单快捷键")]
    [Description(
        "Opens or closes the extra-sigil menu. Changes made in Reloaded-II " +
        "apply while the game is running. / 打开或关闭扩展因子菜单；在 Reloaded-II 中修改后会实时生效。")]
    [DefaultValue(OverlayHotkey.F8)]
    public OverlayHotkey MenuHotkey { get; set; } = OverlayHotkey.F8;

    internal int VirtualKey => (int)MenuHotkey;

    internal string KeyName => Enum.GetName(MenuHotkey) ?? nameof(OverlayHotkey.F8);

    internal static OverlayHotkey FromVirtualKey(int virtualKey) =>
        Enum.IsDefined(typeof(OverlayHotkey), virtualKey)
            ? (OverlayHotkey)virtualKey
            : OverlayHotkey.F8;

    protected override void Normalize()
    {
        MenuHotkey = FromVirtualKey((int)MenuHotkey);
    }
}
