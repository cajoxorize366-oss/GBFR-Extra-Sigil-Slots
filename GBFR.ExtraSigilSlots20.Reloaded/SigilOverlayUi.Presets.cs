using DearImguiSharp;
using System.Text;

namespace GBFR.ExtraSigilSlots20.Reloaded;

internal sealed unsafe partial class SigilOverlayUi
{
    private enum InventoryUsageFilter
    {
        All,
        Used,
        BodyUsed,
        ExtensionUsed,
        Unused,
    }

    private enum PresetNameMode
    {
        None,
        Create,
        Rename,
    }

    private readonly SigilPresetStore _presetStore;
    private readonly byte[] _presetNameBuffer = new byte[256];
    private readonly ImVec2 _presetManagerSize = MakeVec2(620.0f, 480.0f);
    private readonly ImVec2 _presetManagerChildSize = MakeVec2(0.0f, -82.0f);
    private readonly ImVec2 _dialogSize = MakeVec2(650.0f, 0.0f);
    private readonly ImVec4 _conflictColor = MakeVec4(1.0f, 0.35f, 0.3f, 1.0f);

    private readonly Dictionary<(uint CharacterHash, int Slot),
        NativeCore.PresetSlotResult> _presetConflicts = [];
    private InventoryUsageFilter _usageFilter = InventoryUsageFilter.Unused;
    private string? _selectedPresetId;
    private string _presetStatus = string.Empty;
    private bool _presetStatusIsError;
    private bool _presetManagerOpen = true;
    private bool _openPresetNameNextFrame;
    private bool _presetNameDialogOpen = true;
    private PresetNameMode _presetNameMode;
    private string? _renamePresetId;
    private string _presetNameError = string.Empty;
    private NativeCore.InventoryView? _pendingBodyItem;
    private NativeCore.InventoryView? _pendingTransferItem;
    private bool _requestBodyDialogOpen;
    private bool _requestTransferDialogOpen;
    private bool _bodyDialogOpen = true;
    private bool _transferDialogOpen = true;
    private bool _suppressTransferPrompt;

    private bool DrawVirtualSlots(uint characterHash, bool canEdit, bool english)
    {
        bool requestPickerPopup = false;
        ImGui.BeginDisabled(!canEdit);
        for (int index = 0; index < NativeCore.VirtualSlotCount; ++index)
        {
            ImGui.PushID_Int(index);
            ImGui.Text($"{NativeSlotCount + index:00}");
            ImGui.SameLine(0.0f, -1.0f);

            if (_presetConflicts.TryGetValue((characterHash, index), out var conflict))
            {
                ImGui.TextColored(_conflictColor, PresetConflictText(conflict, english));
                ImGui.SameLine(0.0f, -1.0f);
                if (ImGui.SmallButton(english ? "Replace" : "更换"))
                    requestPickerPopup = PreparePicker(index);
            }
            else
            {
                uint slotId = _selection[index];
                string label = GetSelectedLabel(slotId, english);
                if (ImGui.Button(label, _buttonSize))
                    requestPickerPopup = PreparePicker(index);
            }

            ImGui.SameLine(0.0f, -1.0f);
            if (ImGui.SmallButton("X"))
                ClearVirtualSlot(characterHash, index);
            ImGui.PopID();
        }
        ImGui.EndDisabled();
        return requestPickerPopup;
    }

    private bool PreparePicker(int slot)
    {
        _pickerSlot = slot;
        _pickerOpen = true;
        _usageFilter = InventoryUsageFilter.Unused;
        _pendingBodyItem = null;
        _pendingTransferItem = null;
        _requestBodyDialogOpen = false;
        _requestTransferDialogOpen = false;
        Array.Clear(_searchBuffer);
        RefreshInventory();
        return true;
    }

    private void DrawPresetBar(uint characterHash, bool canEdit, bool english)
    {
        SigilPreset? selected = ResolveSelectedPreset();
        string selectedName = selected?.Name ?? (english ? "<none>" : "<无>");
        ImGui.Text(english ? $"Current preset: {selectedName}" : $"当前预设：{selectedName}");

        ImGui.BeginDisabled(_presetStore.Presets.Count < 2);
        if (ImGui.SmallButton("<##preset_previous"))
            CyclePreset(-1);
        ImGui.SameLine(0.0f, -1.0f);
        if (ImGui.SmallButton(">##preset_next"))
            CyclePreset(1);
        ImGui.EndDisabled();
        ImGui.SameLine(0.0f, -1.0f);

        ImGui.BeginDisabled(!canEdit || selected is null);
        if (ImGui.SmallButton(english ? "Apply preset##preset_apply" : "套用预设##preset_apply"))
            ApplySelectedPreset(characterHash, english);
        ImGui.SameLine(0.0f, -1.0f);
        if (ImGui.SmallButton(english ? "Overwrite##preset_overwrite" : "覆盖保存##preset_overwrite"))
            OverwriteSelectedPreset(english);
        ImGui.EndDisabled();
        ImGui.SameLine(0.0f, -1.0f);

        ImGui.BeginDisabled(!canEdit);
        if (ImGui.SmallButton(english ? "Save as##preset_save_as" : "另存为##preset_save_as"))
            QueuePresetNameDialog(PresetNameMode.Create, null, string.Empty);
        ImGui.EndDisabled();
        ImGui.SameLine(0.0f, -1.0f);
        if (ImGui.SmallButton(english ? "Manage##preset_manage" : "管理预设##preset_manage"))
        {
            _presetManagerOpen = true;
            ImGui.OpenPopupStr(PresetManagerTitle(english), 0);
        }

        if (_presetStatus.Length != 0)
        {
            ImGui.TextColored(
                _presetStatusIsError ? _conflictColor : _successColor,
                _presetStatus);
        }
    }

    private void DrawPresetManager(uint characterHash, bool canEdit, bool english)
    {
        string title = PresetManagerTitle(english);
        ImGui.SetNextWindowSize(_presetManagerSize, 1 << 3);
        if (!ImGui.BeginPopupModal(
                title,
                ref _presetManagerOpen,
                ImGuiWindowFlagsNoSavedSettings))
            return;

        SigilPreset? selected = ResolveSelectedPreset();
        ImGui.BeginChildStr("PresetList##GBFR20", _presetManagerChildSize, true, 0);
        for (int index = 0; index < _presetStore.Presets.Count; ++index)
        {
            SigilPreset preset = _presetStore.Presets[index];
            string visibleName = preset.Name.Replace("##", "# #", StringComparison.Ordinal);
            string label = $"{visibleName}##preset_{preset.Id}";
            if (ImGui.SelectableBool(
                    label,
                    string.Equals(preset.Id, selected?.Id, StringComparison.Ordinal),
                    0,
                    _zeroSize))
            {
                _selectedPresetId = preset.Id;
                selected = preset;
            }
        }
        ImGui.EndChild();

        ImGui.BeginDisabled(!canEdit || selected is null);
        if (ImGui.Button(english ? "Apply" : "套用", _zeroSize))
        {
            ApplySelectedPreset(characterHash, english);
            ImGui.CloseCurrentPopup();
            _presetManagerOpen = false;
        }
        ImGui.EndDisabled();
        ImGui.SameLine(0.0f, -1.0f);

        ImGui.BeginDisabled(!canEdit);
        if (ImGui.Button(english ? "New" : "新建", _zeroSize))
        {
            QueuePresetNameDialog(PresetNameMode.Create, null, string.Empty);
            ImGui.CloseCurrentPopup();
            _presetManagerOpen = false;
        }
        ImGui.EndDisabled();
        ImGui.SameLine(0.0f, -1.0f);

        ImGui.BeginDisabled(selected is null);
        if (ImGui.Button(english ? "Rename" : "重命名", _zeroSize) && selected is not null)
        {
            QueuePresetNameDialog(PresetNameMode.Rename, selected.Id, selected.Name);
            ImGui.CloseCurrentPopup();
            _presetManagerOpen = false;
        }
        ImGui.SameLine(0.0f, -1.0f);
        if (ImGui.Button(english ? "Delete" : "删除", _zeroSize) && selected is not null)
            DeleteSelectedPreset(selected, english);
        ImGui.EndDisabled();
        ImGui.SameLine(0.0f, -1.0f);

        if (ImGui.Button(english ? "Close" : "关闭", _zeroSize))
        {
            ImGui.CloseCurrentPopup();
            _presetManagerOpen = false;
        }
        ImGui.EndPopup();
    }

    private void QueuePresetNameDialog(
        PresetNameMode mode,
        string? renamePresetId,
        string initialName)
    {
        _presetNameMode = mode;
        _renamePresetId = renamePresetId;
        _presetNameError = string.Empty;
        SetUtf8BufferText(_presetNameBuffer, initialName);
        _presetNameDialogOpen = true;
        _openPresetNameNextFrame = true;
    }

    private void DrawPresetNameDialog(bool english)
    {
        if (_presetNameMode == PresetNameMode.None)
            return;

        string title = PresetNameTitle(english);
        if (_openPresetNameNextFrame)
        {
            _openPresetNameNextFrame = false;
            ImGui.OpenPopupStr(title, 0);
        }
        ImGui.SetNextWindowSize(_dialogSize, 1 << 3);
        if (!ImGui.BeginPopupModal(
                title,
                ref _presetNameDialogOpen,
                ImGuiWindowFlagsNoSavedSettings))
        {
            if (!_presetNameDialogOpen)
                _presetNameMode = PresetNameMode.None;
            return;
        }

        ImGui.Text(english ? "Preset name" : "预设名称");
        ImGui.SetNextItemWidth(-1.0f);
        fixed (byte* nameBuffer = _presetNameBuffer)
        {
            ImGui.InputTextWithHint(
                "##preset_name",
                english ? "Enter a custom preset name" : "输入自定义预设名称",
                (sbyte*)nameBuffer,
                (IntPtr)_presetNameBuffer.Length,
                0,
                null!,
                IntPtr.Zero);
        }
        if (!english && RepairChineseBuffer(_presetNameBuffer))
        {
            ImGui.ClearActiveID();
            ImGui.SetKeyboardFocusHere(-1);
        }
        if (_presetNameError.Length != 0)
            ImGui.TextColored(_conflictColor, _presetNameError);

        if (ImGui.Button(english ? "Save" : "保存", _zeroSize))
            SavePresetNameDialog(english);
        ImGui.SameLine(0.0f, -1.0f);
        if (ImGui.Button(english ? "Cancel" : "取消", _zeroSize))
        {
            _presetNameMode = PresetNameMode.None;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void SavePresetNameDialog(bool english)
    {
        string name = GetUtf8BufferText(_presetNameBuffer).Trim();
        if (name.Length == 0)
        {
            _presetNameError = english ? "Preset name cannot be empty." : "预设名称不能为空。";
            return;
        }
        if (name.Length > SigilPresetStore.MaximumNameLength)
        {
            _presetNameError = english
                ? $"Preset name cannot exceed {SigilPresetStore.MaximumNameLength} characters."
                : $"预设名称不能超过 {SigilPresetStore.MaximumNameLength} 个字符。";
            return;
        }
        if (_presetStore.NameExists(
                name,
                _presetNameMode == PresetNameMode.Rename ? _renamePresetId : null))
        {
            _presetNameError = english
                ? "A preset with that name already exists."
                : "已经存在同名预设。";
            return;
        }

        try
        {
            if (_presetNameMode == PresetNameMode.Create)
            {
                SigilPreset created = _presetStore.Create(name);
                _selectedPresetId = created.Id;
                SetPresetStatus(
                    english ? $"Saved preset: {created.Name}" : $"已保存预设：{created.Name}",
                    false);
            }
            else
            {
                SigilPreset? preset = _presetStore.FindById(_renamePresetId);
                if (preset is null)
                    throw new InvalidOperationException("The preset no longer exists.");
                _presetStore.Rename(preset, name);
                _selectedPresetId = preset.Id;
                SetPresetStatus(
                    english ? $"Renamed preset: {preset.Name}" : $"已重命名预设：{preset.Name}",
                    false);
            }
            _presetNameMode = PresetNameMode.None;
            ImGui.CloseCurrentPopup();
        }
        catch (Exception exception)
        {
            _log($"Preset name operation failed: {exception}");
            _presetNameError = english ? "Could not save the preset." : "保存预设失败。";
        }
    }

    private void ApplySelectedPreset(uint characterHash, bool english)
    {
        SigilPreset? preset = ResolveSelectedPreset();
        if (preset is null)
            return;

        try
        {
            NativeCore.PresetApplySummary? summary =
                NativeCore.ApplyPreset(_presetStore.GetSelections(preset));
            if (summary is null)
            {
                SetPresetStatus(
                    english ? "Could not apply the preset in the current state."
                            : "当前状态无法套用预设。",
                    true);
                return;
            }

            _presetConflicts.Clear();
            int requested = 0;
            int applied = 0;
            int conflicts = 0;
            foreach (NativeCore.PresetSlotResult result in summary.SlotResults)
            {
                if (result.RequestedSlotId == 0)
                    continue;
                ++requested;
                if (result.Status == NativeCore.PresetSlotStatus.Applied)
                {
                    ++applied;
                    continue;
                }
                ++conflicts;
                _presetConflicts[(result.CharacterHash, result.VirtualSlot)] = result;
            }

            SetPresetStatus(
                english
                    ? $"Preset applied: {applied}/{requested} sigils, {conflicts} conflicts."
                    : $"预设已套用：{applied}/{requested} 个因子，{conflicts} 个冲突。",
                conflicts != 0);
            LoadSelection(characterHash);
            RefreshInventory();
        }
        catch (Exception exception)
        {
            _log($"Preset apply failed: {exception}");
            SetPresetStatus(english ? "Preset apply failed." : "套用预设失败。", true);
        }
    }

    private void OverwriteSelectedPreset(bool english)
    {
        SigilPreset? preset = ResolveSelectedPreset();
        if (preset is null)
            return;
        try
        {
            _presetStore.Overwrite(preset);
            SetPresetStatus(
                english ? $"Updated preset: {preset.Name}" : $"已更新预设：{preset.Name}",
                false);
        }
        catch (Exception exception)
        {
            _log($"Preset overwrite failed: {exception}");
            SetPresetStatus(english ? "Could not update the preset." : "更新预设失败。", true);
        }
    }

    private void DeleteSelectedPreset(SigilPreset preset, bool english)
    {
        try
        {
            string deletedName = preset.Name;
            _presetStore.Delete(preset);
            _selectedPresetId = _presetStore.Presets.FirstOrDefault()?.Id;
            SetPresetStatus(
                english ? $"Deleted preset: {deletedName}" : $"已删除预设：{deletedName}",
                false);
        }
        catch (Exception exception)
        {
            _log($"Preset delete failed: {exception}");
            SetPresetStatus(english ? "Could not delete the preset." : "删除预设失败。", true);
        }
    }

    private SigilPreset? ResolveSelectedPreset()
    {
        SigilPreset? selected = _presetStore.FindById(_selectedPresetId);
        if (selected is not null)
            return selected;
        selected = _presetStore.Presets.FirstOrDefault();
        _selectedPresetId = selected?.Id;
        return selected;
    }

    private void CyclePreset(int direction)
    {
        if (_presetStore.Presets.Count == 0)
            return;
        SigilPreset? selected = ResolveSelectedPreset();
        int index = selected is null
            ? 0
            : _presetStore.Presets
                .Select((preset, presetIndex) => (preset, presetIndex))
                .First(pair => pair.preset.Id == selected.Id)
                .presetIndex;
        index = (index + direction + _presetStore.Presets.Count) % _presetStore.Presets.Count;
        _selectedPresetId = _presetStore.Presets[index].Id;
    }

    private void DrawUsageFilterButtons(bool english)
    {
        ImGui.Text(english ? "Filter" : "搜索条件");
        ImGui.SameLine(0.0f, -1.0f);
        DrawUsageFilterButton(InventoryUsageFilter.All, english ? "All" : "所有因子");
        ImGui.SameLine(0.0f, -1.0f);
        DrawUsageFilterButton(InventoryUsageFilter.Used, english ? "Used" : "已使用的因子");
        ImGui.SameLine(0.0f, -1.0f);
        DrawUsageFilterButton(
            InventoryUsageFilter.BodyUsed,
            english ? "Body used" : "本体已使用的因子");
        ImGui.SameLine(0.0f, -1.0f);
        DrawUsageFilterButton(
            InventoryUsageFilter.ExtensionUsed,
            english ? "Extension used" : "扩展已使用的因子");
        ImGui.SameLine(0.0f, -1.0f);
        DrawUsageFilterButton(InventoryUsageFilter.Unused, english ? "Unused" : "未被使用的因子");
    }

    private void DrawUsageFilterButton(InventoryUsageFilter filter, string label)
    {
        ImGui.BeginDisabled(_usageFilter == filter);
        if (ImGui.SmallButton($"{label}##usage_{filter}"))
            _usageFilter = filter;
        ImGui.EndDisabled();
    }

    private string BuildInventoryDisplayLabel(
        NativeCore.InventoryView item,
        uint currentCharacterHash,
        bool english)
    {
        List<string> details = [];
        if (item.Equipped)
        {
            string owner = UiLocalization.CharacterName(item.Gem.WornBy, english);
            details.Add(item.Gem.WornBy == currentCharacterHash
                ? english ? $"Current character: {owner}" : $"当前角色：{owner}"
                : owner);
            details.Add(english ? "body used" : "本体已使用");
        }
        else if (item.VirtualOwnerCharacterHash != 0)
        {
            string owner = UiLocalization.CharacterName(item.VirtualOwnerCharacterHash, english);
            details.Add(item.VirtualOwnerCharacterHash == currentCharacterHash
                ? english ? $"Current character: {owner}" : $"当前角色：{owner}"
                : owner);
            details.Add(english
                ? $"extension slot {NativeSlotCount + item.VirtualOwnerSlot}"
                : $"扩展已使用（槽 {NativeSlotCount + item.VirtualOwnerSlot}）");
        }

        IReadOnlyList<string> presetNames =
            _presetStore.GetPresetNamesForSlot(item.Gem.SlotId);
        if (presetNames.Count != 0)
        {
            details.Add((english ? "Presets: " : "预设：") + string.Join("、", presetNames));
        }
        if (details.Count == 0)
            return item.Label;
        string separator = english ? " | " : "｜";
        return $"[{string.Join(separator, details)}] {item.Label}";
    }

    private void HandleInventoryItemClick(
        NativeCore.InventoryView item,
        uint characterHash,
        bool english)
    {
        if (item.Equipped)
        {
            _pendingBodyItem = item;
            _bodyDialogOpen = true;
            _requestBodyDialogOpen = true;
            return;
        }

        bool exactCurrentSlot = item.VirtualOwnerCharacterHash == characterHash &&
            item.VirtualOwnerSlot == _pickerSlot;
        if (exactCurrentSlot)
        {
            ClosePickerPopup();
            return;
        }
        if (item.VirtualOwnerCharacterHash != 0 && !_suppressTransferPrompt)
        {
            _pendingTransferItem = item;
            _transferDialogOpen = true;
            _requestTransferDialogOpen = true;
            return;
        }

        bool clearPresetReferences = item.VirtualOwnerCharacterHash != 0;
        if (ApplyInventorySelection(item, characterHash, clearPresetReferences, english))
            ClosePickerPopup();
    }

    private bool DrawInventoryConflictDialogs(uint characterHash, bool english)
    {
        DrawBodyBlockedDialog(english);
        return DrawTransferDialog(characterHash, english);
    }

    private void OpenRequestedInventoryConflictDialogs(bool english)
    {
        if (_requestBodyDialogOpen)
        {
            _requestBodyDialogOpen = false;
            ImGui.OpenPopupStr(BodyBlockedTitle(english), 0);
        }
        if (_requestTransferDialogOpen)
        {
            _requestTransferDialogOpen = false;
            ImGui.OpenPopupStr(TransferTitle(english), 0);
        }
    }

    private void DrawBodyBlockedDialog(bool english)
    {
        if (_pendingBodyItem is null)
            return;
        ImGui.SetNextWindowSize(_dialogSize, 1 << 3);
        if (!ImGui.BeginPopupModal(
                BodyBlockedTitle(english),
                ref _bodyDialogOpen,
                ImGuiWindowFlagsNoSavedSettings))
        {
            if (!_bodyDialogOpen)
                _pendingBodyItem = null;
            return;
        }

        string owner = UiLocalization.CharacterName(_pendingBodyItem.Gem.WornBy, english);
        ImGui.Text(english
            ? $"This sigil is already used in {owner}'s body slots."
            : $"当前因子已被{owner}的本体因子栏使用。");
        ImGui.Text(english
            ? "To put it in a virtual extension slot, remove it from that character first,"
            : "若你想将其放入虚拟扩展栏，请先到对应角色位置脱除因子，");
        ImGui.Text(english ? "then add it again." : "然后再重新添加。");
        if (ImGui.Button(english ? "OK" : "知道了", _zeroSize))
        {
            _pendingBodyItem = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private bool DrawTransferDialog(uint characterHash, bool english)
    {
        if (_pendingTransferItem is null)
            return false;
        bool closePicker = false;
        ImGui.SetNextWindowSize(_dialogSize, 1 << 3);
        if (!ImGui.BeginPopupModal(
                TransferTitle(english),
                ref _transferDialogOpen,
                ImGuiWindowFlagsNoSavedSettings))
        {
            if (!_transferDialogOpen)
                _pendingTransferItem = null;
            return false;
        }

        NativeCore.InventoryView item = _pendingTransferItem;
        string owner = UiLocalization.CharacterName(item.VirtualOwnerCharacterHash, english);
        int sourceSlot = NativeSlotCount + item.VirtualOwnerSlot;
        ImGui.Text(english
            ? $"The selected sigil is used by {owner} in extension slot {sourceSlot}."
            : $"当前因子已被{owner}用于虚拟扩展槽 {sourceSlot}。");

        IReadOnlyList<string> presetNames =
            _presetStore.GetPresetNamesForSlot(item.Gem.SlotId);
        if (presetNames.Count != 0)
        {
            ImGui.Text((english ? "Referenced by presets: " : "同时被以下预设引用：") +
                string.Join("、", presetNames));
        }
        ImGui.Text(english
            ? $"Confirming moves it to {UiLocalization.CharacterName(characterHash, true)}."
            : $"确认后会将其转移给{UiLocalization.CharacterName(characterHash, false)}。");
        ImGui.Text(english
            ? $"{owner}'s current extension slot will be cleared."
            : $"{owner}的当前虚拟因子槽将直接清空。");
        ImGui.Text(english
            ? "Every related preset position will also be cleared."
            : "所有相关预设中的对应位置也会直接清空。");

        ImGui.Checkbox(
            english ? "Do not ask again while this menu is open"
                    : "当前菜单期间不再提示",
            ref _suppressTransferPrompt);
        if (ImGui.Button(english ? "No" : "否", _zeroSize))
        {
            _pendingTransferItem = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine(0.0f, -1.0f);
        if (ImGui.Button(english ? "Yes" : "是", _zeroSize))
        {
            if (ApplyInventorySelection(item, characterHash, true, english))
            {
                _pendingTransferItem = null;
                ImGui.CloseCurrentPopup();
                closePicker = true;
            }
        }
        ImGui.EndPopup();
        return closePicker;
    }

    private bool ApplyInventorySelection(
        NativeCore.InventoryView item,
        uint characterHash,
        bool clearPresetReferences,
        bool english)
    {
        if (_pickerSlot < 0 || _pickerSlot >= NativeCore.VirtualSlotCount)
            return false;
        int targetSlot = _pickerSlot;
        try
        {
            IReadOnlyList<string> affectedPresets = Array.Empty<string>();
            bool success = clearPresetReferences
                ? _presetStore.ClearSlotReferencesAndRun(
                    item.Gem.SlotId,
                    () => NativeCore.SetSelection(
                        characterHash,
                        targetSlot,
                        item.Gem.SlotId),
                    out affectedPresets)
                : NativeCore.SetSelection(characterHash, targetSlot, item.Gem.SlotId);
            if (!success)
            {
                SetPresetStatus(
                    english ? "The sigil could not be assigned." : "无法分配当前因子。",
                    true);
                return false;
            }

            _presetConflicts.Remove((characterHash, targetSlot));
            LoadSelection(characterHash);
            RefreshInventory();
            if (affectedPresets.Count != 0)
            {
                SetPresetStatus(
                    english
                        ? $"Moved the sigil and cleared presets: {string.Join(", ", affectedPresets)}"
                        : $"因子已转移，并清空相关预设：{string.Join("、", affectedPresets)}",
                    false);
            }
            return true;
        }
        catch (Exception exception)
        {
            _log($"Sigil transfer failed: {exception}");
            SetPresetStatus(
                english ? "The sigil transfer failed." : "转移因子失败。",
                true);
            return false;
        }
    }

    private void ClearVirtualSlot(uint characterHash, int slot)
    {
        if (slot < 0 || slot >= NativeCore.VirtualSlotCount)
            return;
        if (NativeCore.SetSelection(characterHash, slot, 0))
        {
            _presetConflicts.Remove((characterHash, slot));
            LoadSelection(characterHash);
            RefreshInventory();
        }
    }

    private void ClosePickerPopup()
    {
        _pickerOpen = false;
        _pickerSlot = -1;
        _pendingBodyItem = null;
        _pendingTransferItem = null;
        _requestBodyDialogOpen = false;
        _requestTransferDialogOpen = false;
        ImGui.CloseCurrentPopup();
    }

    private string PresetConflictText(NativeCore.PresetSlotResult conflict, bool english)
    {
        if (conflict.Status == NativeCore.PresetSlotStatus.Equipped)
        {
            string owner = UiLocalization.CharacterName(conflict.OwnerCharacterHash, english);
            return english
                ? $"This sigil is used by {owner}; please choose another."
                : $"当前因子被用在{owner}角色上，请您替换另一个因子";
        }
        return english
            ? "This preset sigil is unavailable; please choose another."
            : "当前预设因子不可用，请您替换另一个因子";
    }

    private void SetPresetStatus(string text, bool isError)
    {
        _presetStatus = text;
        _presetStatusIsError = isError;
    }

    private static string GetUtf8BufferText(byte[] buffer)
    {
        int length = Array.IndexOf(buffer, (byte)0);
        if (length < 0)
            length = buffer.Length;
        return Encoding.UTF8.GetString(buffer, 0, length);
    }

    private static void SetUtf8BufferText(byte[] buffer, string text)
    {
        Array.Clear(buffer);
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        int length = Math.Min(bytes.Length, buffer.Length - 1);
        Array.Copy(bytes, buffer, length);
    }

    private static string PresetManagerTitle(bool english) => english
        ? "Manage presets##GBFR20PresetManager"
        : "管理预设##GBFR20PresetManager";

    private static string PresetNameTitle(bool english) => english
        ? "Preset name##GBFR20PresetName"
        : "预设名称##GBFR20PresetName";

    private static string BodyBlockedTitle(bool english) => english
        ? "Body-used sigil##GBFR20BodyBlocked"
        : "本体已使用的因子##GBFR20BodyBlocked";

    private static string TransferTitle(bool english) => english
        ? "Transfer extension sigil##GBFR20Transfer"
        : "转移扩展因子##GBFR20Transfer";

    private void DisposePresetUiResources()
    {
        _presetManagerSize.Dispose();
        _presetManagerChildSize.Dispose();
        _dialogSize.Dispose();
        _conflictColor.Dispose();
    }
}
