using DearImguiSharp;
using Reloaded.Imgui.Hook;
using System.Runtime.InteropServices;
using System.Text;

namespace GBFR.ExtraSigilSlots20.Reloaded;

internal sealed unsafe class SigilOverlayUi : IDisposable
{
    private const int NativeSlotCount = 13;
    private const int ImGuiCondFirstUseEver = 1 << 2;
    private const int ImGuiWindowFlagsNoCollapse = 1 << 5;
    private const int ImGuiWindowFlagsNoSavedSettings = 1 << 8;
    private const int MaxInventoryCount = 5100;

    private readonly Action<bool> _setInputCapture;
    private readonly Action<string> _log;
    private readonly List<NativeCore.InventoryView> _inventory = [];
    private readonly Dictionary<uint, NativeCore.InventoryView> _inventoryBySlot = [];
    private readonly List<int> _filteredIndices = [];
    private readonly byte[] _searchBuffer = new byte[160];
    private readonly byte[] _labelBuffer = new byte[1024];
    private readonly ImVec2 _zeroSize = MakeVec2(0.0f, 0.0f);
    private readonly ImVec2 _windowPosition = MakeVec2(0.0f, 0.0f);
    private readonly ImVec2 _windowSize = MakeVec2(620.0f, 500.0f);
    private readonly ImVec2 _buttonSize = MakeVec2(-45.0f, 0.0f);
    private readonly ImVec2 _pickerSize = MakeVec2(900.0f, 620.0f);
    private readonly ImVec2 _childSize = MakeVec2(0.0f, -42.0f);
    private readonly ImVec4 _successColor = MakeVec4(0.35f, 1.0f, 0.45f, 1.0f);
    private readonly ImVec4 _errorColor = MakeVec4(1.0f, 0.35f, 0.25f, 1.0f);
    private readonly ImVec4 _captureColor = MakeVec4(1.0f, 0.8f, 0.2f, 1.0f);

    private NativeCore.RuntimeState _state;
    private uint[] _selection = new uint[NativeCore.VirtualSlotCount];
    private uint _selectionCharacterHash;
    private bool _windowOpen;
    private bool _toggleWasDown;
    private bool _captureKey;
    private bool _captureWaitForRelease;
    private int _pickerSlot = -1;
    private bool _pickerOpen = true;
    private int _lastLoggedInventoryCount = -1;
    private bool _hasSavedClipRect;
    private NativeRect _savedClipRect;
    private IntPtr _savedCaptureWindow;
    private bool _disposed;

    internal SigilOverlayUi(Action<bool> setInputCapture, Action<string> log)
    {
        _setInputCapture = setInputCapture;
        _log = log;
    }

    internal void RenderFrame()
    {
        if (_disposed)
            return;

        NativeCore.Tick();
        if (!NativeCore.TryGetState(out _state))
        {
            SetWindowOpen(false);
            return;
        }

        PollHotkey();
        _setInputCapture(_windowOpen);
        ImGui.GetIO().MouseDrawCursor = _windowOpen;
        if (!_windowOpen)
            return;
        MaintainReleasedMouse();

        if (_inventory.Count == 0 || NativeCore.IsInventoryDirty())
            RefreshInventory();

        uint characterHash = _state.EffectiveCharacterHash;
        bool canEdit = characterHash != 0 && _state.EditAllowed != 0;
        if (characterHash != _selectionCharacterHash)
            LoadSelection(characterHash);

        ImVec2 display = ImGui.GetIO().DisplaySize;
        _windowPosition.X = Math.Max(0.0f, display.X * 0.69f);
        _windowPosition.Y = Math.Max(0.0f, display.Y * 0.54f);
        ImGui.SetNextWindowPos(_windowPosition, ImGuiCondFirstUseEver, _zeroSize);
        ImGui.SetNextWindowSize(_windowSize, ImGuiCondFirstUseEver);

        bool open = _windowOpen;
        if (!ImGui.Begin(
                "GBFR Extra Sigil Slots (12 + 8)##Reloaded",
                ref open,
                ImGuiWindowFlagsNoCollapse))
        {
            ImGui.End();
            SetWindowOpen(open);
            return;
        }
        SetWindowOpen(open);

        string runtimeMessage = NativeCore.GetRuntimeMessage();
        ImGui.TextColored(
            _state.RuntimeMessageIsError != 0 ? _errorColor : _successColor,
            string.IsNullOrEmpty(runtimeMessage) ? "Native core has no status message." : runtimeMessage
        );

        if (_state.UiSelectedCharacterHash != 0)
        {
            ImGui.Text($"Equipment Q/E selected character: 0x{_state.UiSelectedCharacterHash:X8}");
        }
        else
        {
            ImGui.TextWrapped(
                "Waiting for the equipment-selected character. Open Equipment and switch character once."
            );
        }

        ImGui.TextDisabled(
            $"Native status 0x{_state.LastRebuiltCharacterHash:X8}, context {_state.LastContextMode}; " +
            $"Present TID {_state.OverlayThreadId}, owner TID {_state.OwnerThreadId}."
        );
        ImGui.TextDisabled(
            $"Authorized {_state.AuthorizedStatusCount}; last 0x{_state.AuthorizedCharacterHash:X8} " +
            $"@ 0x{_state.AuthorizedStatusAddress:X}; UI/source mode {_state.UiMode}/{_state.SourceMode}."
        );
        string editSession = _state.EditSessionState switch
        {
            1 => "equipment editable",
            2 => "mission locked",
            3 => "free-training editable",
            _ => "unknown locked",
        };
        ImGui.TextDisabled(
            $"Edit session: {editSession}; observed local 0x{_state.ObservedCharacterHash:X8} " +
            $"@ 0x{_state.ObservedStatusAddress:X}, context {_state.ObservedStatusContext}; " +
            $"rebind attempts {_state.LifecycleRebindAttempts}."
        );
        string naturalBind = _state.NaturalBindResult switch
        {
            1 => "CONFIRMED",
            2 => "in progress",
            -1 => "rejected: not context 1",
            -2 => "legacy owner rejection (inactive in test7)",
            -3 => "legacy status-map rejection (inactive in test7)",
            -4 => "rejected: selected sigil became invalid",
            -5 => "rejected: slot sequence/status changed",
            -6 => "rejected: final status validation",
            -7 => "rejected: GemData copy failed",
            _ => "not observed",
        };
        ImGui.TextColored(
            _state.NaturalBindResult == 1
                ? _successColor
                : _state.NaturalBindResult < 0
                    ? _errorColor
                    : _captureColor,
            $"Direct Trait contribution: {naturalBind}; attempts/successes " +
            $"{_state.NaturalBindAttempts}/{_state.NaturalBindSuccesses}; " +
            $"0x{_state.NaturalBindCharacterHash:X8} @ 0x{_state.NaturalBindStatusAddress:X}, " +
            $"context {_state.NaturalBindContext}, " +
            $"{_state.NaturalBindInjectedCount}/{_state.NaturalBindExpectedCount}."
        );
        string battleTarget = _state.NaturalBindOwnerKey != 0
            ? $"legacy owner key 0x{_state.NaturalBindOwnerKey:X8} -> status " +
              $"0x{_state.NaturalBindOwnerStatusAddress:X}"
            : $"direct status 0x{_state.NaturalBindOwnerStatusAddress:X}";
        ImGui.TextDisabled(
            $"Battle target: {battleTarget}. No owner/session gate is used for injection."
        );
        ImGui.TextDisabled($"Owner snapshot characters: {_state.OwnerCharacterCount}.");
        ImGui.TextDisabled(
            $"Input gate requested/effective {_state.InputCaptureRequested}/{_state.InputCaptureEffective}; " +
            $"IAT {_state.InputIatHooksReady}, DirectInput {_state.DirectInputHookReady}."
        );
        if (!canEdit)
        {
            string reason = _state.EditSessionState == 2
                ? "Read-only: normal mission, ready, loading, or mission menu is locked."
                : _state.EditSessionState == 0
                    ? "Read-only: the current session has not been proven editable."
                    : "Read-only: no exact local character/status is available.";
            ImGui.TextColored(
                _captureColor,
                reason
            );
        }

        if (ImGui.SmallButton("Refresh inventory"))
            RefreshInventory();

        ImGui.Separator();
        bool requestPickerPopup = false;
        ImGui.BeginDisabled(!canEdit);
        for (int index = 0; index < NativeCore.VirtualSlotCount; ++index)
        {
            ImGui.PushID_Int(index);
            ImGui.Text($"{NativeSlotCount + index:00}");
            ImGui.SameLine(0.0f, -1.0f);
            uint slotId = _selection[index];
            string label = GetSelectedLabel(slotId);
            if (ImGui.Button(label, _buttonSize))
            {
                _pickerSlot = index;
                _pickerOpen = true;
                Array.Clear(_searchBuffer);
                RefreshInventory();
                requestPickerPopup = true;
            }
            ImGui.SameLine(0.0f, -1.0f);
            if (ImGui.SmallButton("X"))
            {
                if (NativeCore.SetSelection(characterHash, index, 0))
                {
                    LoadSelection(characterHash);
                    RefreshInventory();
                }
            }
            ImGui.PopID();
        }
        ImGui.EndDisabled();
        if (requestPickerPopup)
            ImGui.OpenPopupStr("Select an inventory sigil##Reloaded", 0);

        ImGui.Separator();
        bool autoApply = _state.AutoApply != 0;
        if (ImGui.Checkbox("Queue a native status rebuild immediately", ref autoApply))
        {
            NativeCore.SetAutoApply(autoApply);
            _state.AutoApply = autoApply ? 1 : 0;
        }
        if (!autoApply)
        {
            ImGui.SameLine(0.0f, -1.0f);
            ImGui.BeginDisabled(!canEdit);
            if (ImGui.Button("Apply now", _zeroSize))
                NativeCore.RequestApply(characterHash);
            ImGui.EndDisabled();
        }

        ImGui.Text($"Toggle key: {KeyName(_state.ToggleKey)}");
        ImGui.SameLine(0.0f, -1.0f);
        if (!_captureKey)
        {
            if (ImGui.Button("Change key", _zeroSize))
            {
                _captureKey = true;
                _captureWaitForRelease = true;
            }
        }
        else
        {
            ImGui.TextColored(
                _captureColor,
                "Release keys, then press a new key (Esc cancels)"
            );
        }

        ImGui.TextDisabled(
            "Selections are saved per character. SaveData and WORN_BY are never written."
        );

        DrawPicker(characterHash, canEdit);
        ImGui.End();
    }

    internal void Close()
    {
        SetWindowOpen(false);
        _captureKey = false;
        _pickerSlot = -1;
    }

    private void DrawPicker(uint characterHash, bool canEdit)
    {
        if (_pickerSlot < 0 || _pickerSlot >= NativeCore.VirtualSlotCount)
            return;

        if (!canEdit)
            _pickerOpen = false;

        ImGui.SetNextWindowSize(_pickerSize, 1 << 3);
        if (!ImGui.BeginPopupModal(
                "Select an inventory sigil##Reloaded",
                ref _pickerOpen,
                ImGuiWindowFlagsNoSavedSettings))
        {
            if (!_pickerOpen)
                _pickerSlot = -1;
            return;
        }

        ImGui.Text($"Extra slot {NativeSlotCount + _pickerSlot}");
        ImGui.SetNextItemWidth(-190.0f);
        fixed (byte* searchBuffer = _searchBuffer)
        {
            ImGui.InputTextWithHint(
                "##sigil_search",
                "Search sigil or either trait",
                (sbyte*)searchBuffer,
                (IntPtr)_searchBuffer.Length,
                0,
                null!,
                IntPtr.Zero
            );
        }
        ImGui.SameLine(0.0f, -1.0f);
        if (ImGui.Button("Refresh inventory", _zeroSize))
            RefreshInventory();

        BuildFilter(characterHash);
        ImGui.Separator();
        ImGui.Text($"{_filteredIndices.Count} matching inventory sigils");
        ImGui.BeginChildStr("SigilInventory##Reloaded", _childSize, true, 0);
        using (var clipper = new ImGuiListClipper())
        {
            ImGui.ImGuiListClipperBegin(clipper, _filteredIndices.Count, -1.0f);
            while (ImGui.ImGuiListClipperStep(clipper))
            {
                for (int row = clipper.DisplayStart; row < clipper.DisplayEnd; ++row)
                {
                    NativeCore.InventoryView item = _inventory[_filteredIndices[row]];
                    string label = $"{item.Label}##inventory_{item.Gem.SlotId}";
                    if (ImGui.SelectableBool(label, false, 0, _zeroSize))
                    {
                        if (NativeCore.SetSelection(
                                characterHash,
                                _pickerSlot,
                                item.Gem.SlotId))
                        {
                            LoadSelection(characterHash);
                            RefreshInventory();
                            _pickerSlot = -1;
                            ImGui.CloseCurrentPopup();
                        }
                    }
                }
            }
            ImGui.ImGuiListClipperEnd(clipper);
        }
        ImGui.EndChild();

        if (ImGui.Button("Clear this slot", _zeroSize))
        {
            if (NativeCore.SetSelection(characterHash, _pickerSlot, 0))
            {
                LoadSelection(characterHash);
                RefreshInventory();
            }
            _pickerSlot = -1;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine(0.0f, -1.0f);
        if (ImGui.Button("Cancel", _zeroSize))
        {
            _pickerSlot = -1;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
        if (!_pickerOpen)
            _pickerSlot = -1;
    }

    private void PollHotkey()
    {
        int toggleKey = _state.ToggleKey is >= 1 and <= 255 ? _state.ToggleKey : 0x68;
        IntPtr gameWindow = ImguiHook.WindowHandle;
        IntPtr foregroundWindow = GetForegroundWindow();
        GetWindowThreadProcessId(foregroundWindow, out uint foregroundProcessId);
        bool focused = gameWindow != IntPtr.Zero &&
            foregroundWindow != IntPtr.Zero &&
            foregroundProcessId == GetCurrentProcessId();
        bool toggleDown = focused && (GetAsyncKeyState(toggleKey) & 0x8000) != 0;
        if (!_captureKey && toggleDown && !_toggleWasDown)
        {
            SetWindowOpen(!_windowOpen);
            if (_windowOpen)
                RefreshInventory();
            else
                _pickerSlot = -1;
        }
        _toggleWasDown = toggleDown;

        if (!_captureKey || !focused)
            return;
        if (_captureWaitForRelease)
        {
            if (!AnyBindableKeyDown())
                _captureWaitForRelease = false;
            return;
        }

        for (int key = 0x08; key <= 0xFE; ++key)
        {
            if ((GetAsyncKeyState(key) & 0x8000) == 0)
                continue;
            if (key == 0x1B)
            {
                _captureKey = false;
                return;
            }
            if (NativeCore.SetToggleKey(key))
                _state.ToggleKey = key;
            _captureKey = false;
            _toggleWasDown = true;
            return;
        }
    }

    private void RefreshInventory()
    {
        if (!NativeCore.RefreshInventory())
        {
            _inventory.Clear();
            _inventoryBySlot.Clear();
            return;
        }

        uint count = Math.Min(NativeCore.GetInventoryCount(), MaxInventoryCount);
        _inventory.Clear();
        _inventoryBySlot.Clear();
        for (uint index = 0; index < count; ++index)
        {
            Array.Clear(_labelBuffer);
            if (!NativeCore.TryCopyInventoryItem(index, _labelBuffer, out var item) || item is null)
                continue;
            _inventory.Add(item);
            _inventoryBySlot[item.Gem.SlotId] = item;
        }
        if (_inventory.Count != _lastLoggedInventoryCount)
        {
            _lastLoggedInventoryCount = _inventory.Count;
            _log($"Inventory snapshot refreshed: {_inventory.Count} selectable records scanned.");
        }
    }

    private void LoadSelection(uint characterHash)
    {
        _selectionCharacterHash = characterHash;
        _selection = characterHash == 0
            ? new uint[NativeCore.VirtualSlotCount]
            : NativeCore.GetSelection(characterHash);
    }

    private string GetSelectedLabel(uint slotId)
    {
        if (slotId == 0)
            return "<empty>";
        return _inventoryBySlot.TryGetValue(slotId, out var item)
            ? item.Label
            : $"<missing inventory slot #{slotId}>";
    }

    private void BuildFilter(uint characterHash)
    {
        string search = GetSearchText().ToLowerInvariant();
        _filteredIndices.Clear();
        for (int index = 0; index < _inventory.Count; ++index)
        {
            NativeCore.InventoryView item = _inventory[index];
            if (item.Equipped)
                continue;
            if (item.RequiredCharacterHash != 0 &&
                item.RequiredCharacterHash != characterHash)
                continue;
            if (search.Length != 0 && !item.Searchable.Contains(search, StringComparison.Ordinal))
                continue;
            _filteredIndices.Add(index);
        }
    }

    private string GetSearchText()
    {
        int length = Array.IndexOf(_searchBuffer, (byte)0);
        if (length < 0)
            length = _searchBuffer.Length;
        return Encoding.UTF8.GetString(_searchBuffer, 0, length);
    }

    private void SetWindowOpen(bool open)
    {
        bool changed = _windowOpen != open;
        _windowOpen = open;
        _setInputCapture(open);
        if (changed && open)
            BeginReleasedMouse();
        else if (changed)
            RestoreMouseCapture();
        if (!open)
        {
            _pickerSlot = -1;
            _captureKey = false;
        }
    }

    private void BeginReleasedMouse()
    {
        _hasSavedClipRect = GetClipCursor(out _savedClipRect);
        _savedCaptureWindow = GetCapture();
        MaintainReleasedMouse();
    }

    private static void MaintainReleasedMouse()
    {
        ReleaseCapture();
        ClipCursor(IntPtr.Zero);
    }

    private void RestoreMouseCapture()
    {
        ReleaseCapture();
        if (_hasSavedClipRect)
            ClipCursorRect(ref _savedClipRect);
        if (_savedCaptureWindow != IntPtr.Zero && IsWindow(_savedCaptureWindow))
            SetCapture(_savedCaptureWindow);
        IntPtr gameWindow = ImguiHook.WindowHandle;
        if (gameWindow != IntPtr.Zero && IsWindow(gameWindow))
            SetForegroundWindow(gameWindow);
        _hasSavedClipRect = false;
        _savedCaptureWindow = IntPtr.Zero;
    }

    private static bool AnyBindableKeyDown()
    {
        for (int key = 0x08; key <= 0xFE; ++key)
        {
            if ((GetAsyncKeyState(key) & 0x8000) != 0)
                return true;
        }
        return false;
    }

    private static string KeyName(int virtualKey)
    {
        uint scanCode = MapVirtualKeyW((uint)virtualKey, 0);
        if (virtualKey is 0x25 or 0x26 or 0x27 or 0x28 or 0x21 or 0x22 or 0x23 or
            0x24 or 0x2D or 0x2E or 0x6F or 0x90)
        {
            scanCode |= 0x100;
        }
        StringBuilder name = new(128);
        return GetKeyNameTextW((int)(scanCode << 16), name, name.Capacity) > 0
            ? name.ToString()
            : $"VK 0x{virtualKey:X2}";
    }

    private static ImVec2 MakeVec2(float x, float y)
    {
        ImVec2 value = new();
        value.X = x;
        value.Y = y;
        return value;
    }

    private static ImVec4 MakeVec4(float x, float y, float z, float w)
    {
        ImVec4 value = new();
        value.X = x;
        value.Y = y;
        value.Z = z;
        value.W = w;
        return value;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Close();
        _zeroSize.Dispose();
        _windowPosition.Dispose();
        _windowSize.Dispose();
        _buttonSize.Dispose();
        _pickerSize.Dispose();
        _childSize.Dispose();
        _successColor.Dispose();
        _errorColor.Dispose();
        _captureColor.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetKeyNameTextW(int lParam, StringBuilder text, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClipCursor(out NativeRect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursor(IntPtr rectangle);

    [DllImport("user32.dll", EntryPoint = "ClipCursor")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursorRect(ref NativeRect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr GetCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}
