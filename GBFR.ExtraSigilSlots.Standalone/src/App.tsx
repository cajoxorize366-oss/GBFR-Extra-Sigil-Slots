import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import {
  ArrowRightLeft,
  Check,
  ChevronLeft,
  ChevronRight,
  CircleAlert,
  CircleCheck,
  Cable,
  FolderCog,
  Info,
  LoaderCircle,
  Pencil,
  PlugZap,
  Plus,
  RefreshCw,
  Search,
  Settings2,
  Trash2,
  X,
} from "lucide-react";
import { api, isTauriRuntime, mockControls } from "./api";
import {
  CHARACTERS,
  characterName,
  isCharacterCompatible,
  type Dashboard,
  type GameProcess,
  type InventoryFilter,
  type InventoryItem,
  type Language,
  type PresetApplySummary,
  type PresetDialogMode,
  type SigilPreset,
} from "./types";
import "./App.css";

type Notice = { message: string; kind: "success" | "error" | "warning" } | null;

type Modal =
  | { kind: "body-conflict"; item: InventoryItem }
  | { kind: "transfer-conflict"; item: InventoryItem }
  | { kind: "slot-reduction"; target: number }
  | { kind: "preset-name"; mode: PresetDialogMode; preset?: SigilPreset }
  | { kind: "preset-transfer"; preset: SigilPreset }
  | null;

const defaultCharacterHash = CHARACTERS[0].hash;

function IconButton({
  label,
  onClick,
  disabled = false,
  children,
  className = "",
}: {
  label: string;
  onClick: () => void;
  disabled?: boolean;
  children: ReactNode;
  className?: string;
}) {
  return (
    <button
      type="button"
      className={`icon-button ${className}`}
      aria-label={label}
      title={label}
      onClick={onClick}
      disabled={disabled}
    >
      {children}
    </button>
  );
}

function ModalFrame({
  title,
  eyebrow,
  onClose,
  children,
  size = "regular",
}: {
  title: string;
  eyebrow?: string;
  onClose: () => void;
  children: ReactNode;
  size?: "regular" | "wide" | "manager";
}) {
  return (
    <div className="modal-layer" role="presentation">
      <div className={`modal-dialog modal-${size}`} role="dialog" aria-modal="true" aria-label={title}>
        <div className="modal-header">
          <div>
            {eyebrow && <div className="eyebrow">{eyebrow}</div>}
            <h2>{title}</h2>
          </div>
          <IconButton label="Close" onClick={onClose}>
            <X size={17} />
          </IconButton>
        </div>
        <div className="modal-content">{children}</div>
      </div>
    </div>
  );
}

function StatusBadge({ children, tone }: { children: ReactNode; tone: "green" | "amber" | "red" | "neutral" }) {
  return <span className={`status-badge status-${tone}`}>{children}</span>;
}

function formatPid(pid: number): string {
  return pid.toLocaleString("en-US");
}

function formatHash(hash: number): string {
  return `0x${hash.toString(16).padStart(8, "0").toUpperCase()}`;
}

function dashboardsMatch(left: Dashboard, right: Dashboard): boolean {
  return left.connection.pid === right.connection.pid
    && left.connection.process_name === right.connection.process_name
    && left.connection.injected === right.connection.injected
    && left.connection.protocol_version === right.connection.protocol_version
    && left.connection.native_abi_version === right.connection.native_abi_version
    && left.initialized === right.initialized
    && left.hooks_ready === right.hooks_ready
    && left.runtime_message === right.runtime_message
    && left.runtime_message_is_error === right.runtime_message_is_error
    && left.effective_character_hash === right.effective_character_hash
    && left.ui_selected_character_hash === right.ui_selected_character_hash
    && left.edit_allowed === right.edit_allowed
    && left.language === right.language
    && left.inventory_revision === right.inventory_revision
    && left.inventory_dirty === right.inventory_dirty
    && left.game_data_ready === right.game_data_ready
    && left.virtual_slot_count === right.virtual_slot_count
    && left.virtual_slot_capacity === right.virtual_slot_capacity
    && left.pending_virtual_slot_count === right.pending_virtual_slot_count;
}

function App() {
  const [processes, setProcesses] = useState<GameProcess[]>([]);
  const [selectedPid, setSelectedPid] = useState<number | null>(null);
  const [dashboard, setDashboard] = useState<Dashboard | null>(null);
  const [inventory, setInventory] = useState<InventoryItem[]>([]);
  const [selection, setSelection] = useState<number[]>(Array(24).fill(0));
  const [presets, setPresets] = useState<SigilPreset[]>([]);
  const [selectedPresetIds, setSelectedPresetIds] = useState<Record<number, string>>({});
  const [language, setLanguage] = useState<Language>("zh-CN");
  const [inventoryFilter, setInventoryFilter] = useState<InventoryFilter>("unused");
  const [inventorySearch, setInventorySearch] = useState("");
  const [pickerSlot, setPickerSlot] = useState<number | null>(null);
  const [modal, setModal] = useState<Modal>(null);
  const [managerOpen, setManagerOpen] = useState(false);
  const [managerCharacterHash, setManagerCharacterHash] = useState(defaultCharacterHash);
  const [managerPresetId, setManagerPresetId] = useState<string | null>(null);
  const [presetName, setPresetName] = useState("");
  const [transferTargetHash, setTransferTargetHash] = useState(CHARACTERS[1].hash);
  const [slotCountInput, setSlotCountInput] = useState("8");
  const [notice, setNotice] = useState<Notice>(null);
  const [connectionError, setConnectionError] = useState("");
  const [sessionHydrated, setSessionHydrated] = useState(false);
  const [hydrationWarning, setHydrationWarning] = useState("");
  const [loading, setLoading] = useState<string | null>(null);
  const [suppressTransferPrompt, setSuppressTransferPrompt] = useState(false);
  const connectionInFlightRef = useRef(false);
  const hydrationInFlightRef = useRef(false);
  const autoConnectSuppressedPidRef = useRef<number | null>(null);
  const hydrationAttemptRef = useRef("");
  const inventoryRefreshAttemptRef = useRef("");

  const isConnected = dashboard !== null;
  const currentCharacterHash = dashboard?.ui_selected_character_hash || dashboard?.effective_character_hash || defaultCharacterHash;
  const editAllowed = sessionHydrated && (dashboard?.edit_allowed ?? false);
  const activeSlotCount = dashboard?.virtual_slot_count ?? 8;
  const selectedPresetId = selectedPresetIds[currentCharacterHash];
  const currentPresets = presets.filter((preset) => preset.character_hash === currentCharacterHash);
  const selectedPreset = currentPresets.find((preset) => preset.id === selectedPresetId) ?? currentPresets[0];
  const managerPresets = presets.filter((preset) => preset.character_hash === managerCharacterHash);
  const managerSelectedPreset = managerPresets.find((preset) => preset.id === managerPresetId) ?? managerPresets[0];

  useEffect(() => {
    if (dashboard) return;
    let active = true;
    let scanInFlight = false;
    const scanProcesses = async (): Promise<void> => {
      if (!active || scanInFlight || connectionInFlightRef.current) return;
      scanInFlight = true;
      try {
        const nextProcesses = await api.listGameProcesses();
        if (!active) return;
        setProcesses(nextProcesses);
        const soleProcess = nextProcesses.length === 1 ? nextProcesses[0] : undefined;
        setSelectedPid((current) => {
          if (soleProcess) return soleProcess.pid;
          return current !== null && nextProcesses.some((process) => process.pid === current) ? current : null;
        });

        let suppressedPid = autoConnectSuppressedPidRef.current;
        if (suppressedPid !== null && !nextProcesses.some((process) => process.pid === suppressedPid)) {
          autoConnectSuppressedPidRef.current = null;
          suppressedPid = null;
        }
        if (soleProcess && soleProcess.pid !== suppressedPid) {
          void connectToProcess(soleProcess.pid, true);
        }
      } catch (error: unknown) {
        if (active) setConnectionError(error instanceof Error ? error.message : String(error));
      } finally {
        scanInFlight = false;
      }
    };
    void scanProcesses();
    const timer = window.setInterval(() => void scanProcesses(), 1_000);
    return () => {
      active = false;
      window.clearInterval(timer);
    };
  }, [dashboard?.connection.pid]);

  useEffect(() => {
    if (pickerSlot === null && !modal && !managerOpen) return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        setPickerSlot(null);
        setModal(null);
        setManagerOpen(false);
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [pickerSlot, modal, managerOpen]);

  useEffect(() => {
    if (!dashboard || sessionHydrated) return;
    if (!dashboard.game_data_ready) {
      hydrationAttemptRef.current = "";
      setHydrationWarning("");
      return;
    }

    const attemptKey = `${dashboard.connection.pid}:${dashboard.inventory_revision}`;
    if (hydrationAttemptRef.current === attemptKey || hydrationInFlightRef.current) return;
    hydrationAttemptRef.current = attemptKey;
    let active = true;

    const hydrate = async (): Promise<void> => {
      hydrationInFlightRef.current = true;
      try {
        const nextCharacterHash = dashboard.ui_selected_character_hash || dashboard.effective_character_hash || defaultCharacterHash;
        const [nextInventory, nextPresets, nextSelection] = await Promise.all([
          api.refreshInventory(),
          api.listPresets(),
          api.getSelection(nextCharacterHash),
        ]);
        if (!active) return;
        setInventory(nextInventory);
        setPresets(nextPresets.presets);
        setSelection(nextSelection);
        setHydrationWarning("");
        setSessionHydrated(true);
        const firstPreset = nextPresets.presets.find((preset) => preset.character_hash === nextCharacterHash);
        if (firstPreset) {
          setSelectedPresetIds((current) => ({ ...current, [firstPreset.character_hash]: current[firstPreset.character_hash] ?? firstPreset.id }));
        }
      } catch (error: unknown) {
        if (!active) return;
        const detail = error instanceof Error ? error.message : String(error);
        setHydrationWarning(language === "en"
          ? `The ready signal was received, but the initial game-data read failed. Use Refresh to try once more. ${detail}`
          : `已收到游戏数据就绪标志，但首次读取失败。请点击“刷新”再尝试一次。${detail}`);
      } finally {
        hydrationInFlightRef.current = false;
      }
    };

    void hydrate();
    return () => {
      active = false;
    };
  }, [dashboard?.connection.pid, dashboard?.effective_character_hash, dashboard?.game_data_ready, dashboard?.inventory_revision, dashboard?.ui_selected_character_hash, language, sessionHydrated]);

  useEffect(() => {
    if (!dashboard) return;
    let active = true;
    let polling = false;
    const timer = window.setInterval(() => {
      if (polling) return;
      polling = true;
      void api.getDashboard().then(async (nextDashboard) => {
        if (!active) return;
        const nextCharacterHash = nextDashboard.ui_selected_character_hash || nextDashboard.effective_character_hash || defaultCharacterHash;
        const selectionPromise = nextCharacterHash !== currentCharacterHash
          ? api.getSelection(nextCharacterHash)
          : Promise.resolve<number[] | null>(null);
        let inventoryPromise: Promise<InventoryItem[] | null> = Promise.resolve(null);
        if (!nextDashboard.game_data_ready || !nextDashboard.inventory_dirty) {
          inventoryRefreshAttemptRef.current = "";
        } else if (sessionHydrated) {
          const refreshKey = `${nextDashboard.connection.pid}:${nextDashboard.inventory_revision}`;
          if (inventoryRefreshAttemptRef.current !== refreshKey) {
            inventoryRefreshAttemptRef.current = refreshKey;
            inventoryPromise = api.refreshInventory();
          }
        }
        const [nextSelectionResult, nextInventoryResult] = await Promise.allSettled([selectionPromise, inventoryPromise]);
        if (!active) return;
        setDashboard((current) => current && dashboardsMatch(current, nextDashboard) ? current : nextDashboard);
        setLanguage(nextDashboard.language);
        if (nextSelectionResult.status === "fulfilled" && nextSelectionResult.value) setSelection(nextSelectionResult.value);
        if (nextInventoryResult.status === "fulfilled" && nextInventoryResult.value) setInventory(nextInventoryResult.value);
      }).catch(async (error: unknown) => {
        await api.disconnectGame().catch(() => undefined);
        if (!active) return;
        setDashboard(null);
        setInventory([]);
        setSelection(Array(24).fill(0));
        setSessionHydrated(false);
        setHydrationWarning("");
        setConnectionError(error instanceof Error ? error.message : String(error));
      }).finally(() => {
        polling = false;
      });
    }, 750);
    return () => {
      active = false;
      window.clearInterval(timer);
    };
  }, [dashboard?.connection.pid, currentCharacterHash, sessionHydrated]);

  async function connectToProcess(pid: number, automatic: boolean): Promise<void> {
    if (connectionInFlightRef.current || dashboard) return;
    connectionInFlightRef.current = true;
    setSelectedPid(pid);
    setLoading("connect");
    setConnectionError("");
    try {
      const nextConnection = await api.connectGame(pid);
      const nextDashboard = await api.getDashboard();
      const connectedDashboard = { ...nextDashboard, connection: nextConnection };
      setDashboard(connectedDashboard);
      setLanguage(connectedDashboard.language);
      setSlotCountInput(String(connectedDashboard.pending_virtual_slot_count || connectedDashboard.virtual_slot_count));
      setSessionHydrated(false);
      setHydrationWarning("");
      hydrationAttemptRef.current = "";
      inventoryRefreshAttemptRef.current = "";
      autoConnectSuppressedPidRef.current = null;
      setNotice({
        message: automatic
          ? language === "en" ? "Game detected, injected, and connected automatically." : "已自动检测、注入并连接游戏。"
          : language === "en" ? "Connected to the selected game process." : "已连接到选定的游戏进程。",
        kind: "success",
      });
    } catch (error: unknown) {
      await api.disconnectGame().catch(() => undefined);
      autoConnectSuppressedPidRef.current = automatic ? null : pid;
      setConnectionError(error instanceof Error ? error.message : String(error));
    } finally {
      connectionInFlightRef.current = false;
      setLoading(null);
    }
  }

  async function handleConnect(): Promise<void> {
    if (selectedPid === null) return;
    autoConnectSuppressedPidRef.current = null;
    await connectToProcess(selectedPid, false);
  }

  async function handleDisconnect(): Promise<void> {
    setLoading("disconnect");
    try {
      await api.disconnectGame();
      autoConnectSuppressedPidRef.current = dashboard?.connection.pid ?? null;
      setDashboard(null);
      setInventory([]);
      setSelection(Array(24).fill(0));
      setSessionHydrated(false);
      setHydrationWarning("");
      hydrationAttemptRef.current = "";
      inventoryRefreshAttemptRef.current = "";
      setNotice(null);
      setConnectionError("");
    } catch (error: unknown) {
      setNotice({ message: error instanceof Error ? error.message : String(error), kind: "error" });
    } finally {
      setLoading(null);
    }
  }

  async function handleRefreshProcesses(): Promise<void> {
    setLoading("processes");
    try {
      setProcesses(await api.listGameProcesses());
      setConnectionError("");
    } catch (error: unknown) {
      setConnectionError(error instanceof Error ? error.message : String(error));
    } finally {
      setLoading(null);
    }
  }

  async function handleRefreshInventory(): Promise<void> {
    if (!dashboard) return;
    setLoading("inventory");
    try {
      if (!sessionHydrated) {
        const nextCharacterHash = dashboard.ui_selected_character_hash || dashboard.effective_character_hash || defaultCharacterHash;
        const [nextInventory, nextPresets, nextSelection] = await Promise.all([
          api.refreshInventory(),
          api.listPresets(),
          api.getSelection(nextCharacterHash),
        ]);
        setInventory(nextInventory);
        setPresets(nextPresets.presets);
        setSelection(nextSelection);
        setSessionHydrated(true);
        setHydrationWarning("");
      } else {
        setInventory(await api.refreshInventory());
      }
      setNotice({ message: language === "en" ? "Inventory refreshed." : "库存已刷新。", kind: "success" });
    } catch (error: unknown) {
      setNotice({ message: error instanceof Error ? error.message : String(error), kind: "error" });
    } finally {
      setLoading(null);
    }
  }

  async function handleLanguageChange(nextLanguage: Language): Promise<void> {
    if (nextLanguage === language || !dashboard) return;
    setLoading("language");
    try {
      const nextDashboard = await api.setLanguage(nextLanguage);
      setLanguage(nextLanguage);
      setDashboard(nextDashboard);
    } catch (error: unknown) {
      setNotice({ message: error instanceof Error ? error.message : String(error), kind: "error" });
    } finally {
      setLoading(null);
    }
  }

  async function handleSetEditAllowed(nextValue: boolean): Promise<void> {
    if (isTauriRuntime() || !dashboard) return;
    mockControls.setEditAllowed(nextValue);
    const nextDashboard = await api.getDashboard();
    setDashboard(nextDashboard);
    setNotice({ message: nextValue ? "Editing enabled for this session." : "Read-only state enabled for this session.", kind: nextValue ? "success" : "warning" });
  }

  async function reloadSelection(): Promise<void> {
    if (!dashboard) return;
    setSelection(await api.getSelection(currentCharacterHash));
    setInventory(await api.refreshInventory());
  }

  async function assignInventory(item: InventoryItem, virtualSlot: number): Promise<void> {
    if (!dashboard || !editAllowed) return;
    setLoading(`assign-${item.gem.slot_id}`);
    try {
      const result = await api.assignInventorySigil(currentCharacterHash, virtualSlot, item.gem.slot_id);
      if (!result.success) {
        setNotice({ message: result.message, kind: "error" });
        return;
      }
      await reloadSelection();
      setSelectedPresetIds((current) => {
        const next = { ...current };
        delete next[currentCharacterHash];
        return next;
      });
      setNotice({ message: result.affected_preset_names.length > 0 ? `${result.message} ${result.affected_preset_names.join(", ")}` : result.message, kind: "success" });
      setPickerSlot(null);
      setModal(null);
    } catch (error: unknown) {
      setNotice({ message: error instanceof Error ? error.message : String(error), kind: "error" });
    } finally {
      setLoading(null);
    }
  }

  async function clearSlot(virtualSlot: number): Promise<void> {
    if (!dashboard || !editAllowed) return;
    setLoading(`clear-${virtualSlot}`);
    try {
      const result = await api.clearVirtualSlot(currentCharacterHash, virtualSlot);
      if (!result.success) {
        setNotice({ message: result.message, kind: "error" });
        return;
      }
      await reloadSelection();
      setNotice({ message: language === "en" ? "Slot cleared." : "槽位已清空。", kind: "success" });
      setPickerSlot(null);
    } catch (error: unknown) {
      setNotice({ message: error instanceof Error ? error.message : String(error), kind: "error" });
    } finally {
      setLoading(null);
    }
  }

  function openPicker(slot: number): void {
    if (!editAllowed) return;
    setPickerSlot(slot);
    setInventoryFilter("unused");
    setInventorySearch("");
    setModal(null);
  }

  function handleInventoryPick(item: InventoryItem): void {
    if (pickerSlot === null) return;
    if (item.equipped) {
      setModal({ kind: "body-conflict", item });
      return;
    }
    const isCurrentSlot = item.virtual_owner_character_hash === currentCharacterHash && item.virtual_owner_slot === pickerSlot;
    if (isCurrentSlot) {
      setPickerSlot(null);
      return;
    }
    if (item.virtual_owner_character_hash !== 0 && !suppressTransferPrompt) {
      setModal({ kind: "transfer-conflict", item });
      return;
    }
    void assignInventory(item, pickerSlot);
  }

  async function commitSlotCount(target: number): Promise<void> {
    setLoading("slot-count");
    try {
      const result = await api.requestVirtualSlotCount(target);
      if (result.status === "failed") {
        setNotice({ message: result.message, kind: "error" });
      } else {
        setDashboard((current) => current ? { ...current, pending_virtual_slot_count: result.pending_virtual_slot_count } : current);
        setNotice({ message: result.message, kind: "success" });
        setModal(null);
      }
    } catch (error: unknown) {
      setNotice({ message: error instanceof Error ? error.message : String(error), kind: "error" });
    } finally {
      setLoading(null);
    }
  }

  function submitSlotCount(): void {
    const target = Math.min(24, Math.max(1, Number.parseInt(slotCountInput, 10) || activeSlotCount));
    setSlotCountInput(String(target));
    if (target < activeSlotCount) {
      setModal({ kind: "slot-reduction", target });
      return;
    }
    void commitSlotCount(target);
  }

  function selectPresetForCharacter(characterHash: number, presetId: string | null): void {
    if (!presetId) return;
    setSelectedPresetIds((current) => ({ ...current, [characterHash]: presetId }));
  }

  function cyclePreset(direction: -1 | 1): void {
    if (currentPresets.length === 0) return;
    const currentIndex = selectedPreset ? currentPresets.findIndex((preset) => preset.id === selectedPreset.id) : 0;
    const nextIndex = (currentIndex + direction + currentPresets.length) % currentPresets.length;
    selectPresetForCharacter(currentCharacterHash, currentPresets[nextIndex].id);
  }

  async function applyPreset(preset: SigilPreset): Promise<void> {
    if (!editAllowed || preset.character_hash !== currentCharacterHash) return;
    setLoading("apply-preset");
    try {
      const summary: PresetApplySummary = await api.applyPreset(preset.id, currentCharacterHash);
      await reloadSelection();
      selectPresetForCharacter(currentCharacterHash, preset.id);
      setNotice({ message: language === "en" ? `Preset applied: ${summary.applied_count}/${summary.requested_count} sigils.` : `预设已套用：${summary.applied_count}/${summary.requested_count} 个因子。`, kind: summary.conflicts.length > 0 ? "warning" : "success" });
    } catch (error: unknown) {
      setNotice({ message: error instanceof Error ? error.message : String(error), kind: "error" });
    } finally {
      setLoading(null);
    }
  }

  async function overwritePreset(): Promise<void> {
    if (!selectedPreset || !editAllowed) return;
    setLoading("preset");
    try {
      const updated = await api.overwritePreset(selectedPreset.id);
      setPresets((current) => current.map((preset) => preset.id === updated.id ? updated : preset));
      setNotice({ message: language === "en" ? `Updated preset: ${updated.name}.` : `已更新预设：${updated.name}。`, kind: "success" });
    } catch (error: unknown) {
      setNotice({ message: error instanceof Error ? error.message : String(error), kind: "error" });
    } finally {
      setLoading(null);
    }
  }

  function openManager(): void {
    setManagerCharacterHash(currentCharacterHash);
    setManagerPresetId(selectedPreset?.id ?? currentPresets[0]?.id ?? null);
    setManagerOpen(true);
  }

  function openCreatePreset(): void {
    setPresetName("");
    setManagerOpen(false);
    setModal({ kind: "preset-name", mode: "create" });
  }

  function openRenamePreset(preset: SigilPreset): void {
    setPresetName(preset.name);
    setManagerOpen(false);
    setModal({ kind: "preset-name", mode: "rename", preset });
  }

  async function submitPresetName(): Promise<void> {
    const normalizedName = presetName.trim();
    if (!normalizedName) {
      setNotice({ message: language === "en" ? "Preset name cannot be empty." : "预设名称不能为空。", kind: "error" });
      return;
    }
    if (Array.from(normalizedName).length > 48) {
      setNotice({ message: language === "en" ? "Preset name cannot exceed 48 characters." : "预设名称不能超过 48 个字符。", kind: "error" });
      return;
    }
    setLoading("preset-name");
    try {
      if (modal?.kind === "preset-name" && modal.mode === "rename" && modal.preset) {
        const renamed = await api.renamePreset(modal.preset.id, normalizedName);
        setPresets((current) => current.map((preset) => preset.id === modal.preset!.id ? renamed : preset));
        setSelectedPresetIds((current) => ({ ...current, [renamed.character_hash]: renamed.id }));
      } else {
        const created = await api.createPreset(currentCharacterHash, normalizedName);
        setPresets((current) => [...current, created]);
        selectPresetForCharacter(currentCharacterHash, created.id);
      }
      setModal(null);
      setNotice({ message: language === "en" ? "Preset saved." : "预设已保存。", kind: "success" });
    } catch (error: unknown) {
      setNotice({ message: error instanceof Error ? error.message : String(error), kind: "error" });
    } finally {
      setLoading(null);
    }
  }

  async function deletePreset(): Promise<void> {
    if (!managerSelectedPreset) return;
    setLoading("preset-delete");
    try {
      const nextDocument = await api.deletePreset(managerSelectedPreset.id);
      setPresets(nextDocument.presets);
      setManagerPresetId(nextDocument.presets.find((preset) => preset.character_hash === managerCharacterHash)?.id ?? null);
      setNotice({ message: language === "en" ? "Preset deleted." : "预设已删除。", kind: "success" });
    } catch (error: unknown) {
      setNotice({ message: error instanceof Error ? error.message : String(error), kind: "error" });
    } finally {
      setLoading(null);
    }
  }

  function openTransferPreset(preset: SigilPreset): void {
    setTransferTargetHash(CHARACTERS.find((character) => character.hash !== preset.character_hash)?.hash ?? defaultCharacterHash);
    setManagerOpen(false);
    setModal({ kind: "preset-transfer", preset });
  }

  async function submitTransferPreset(preset: SigilPreset): Promise<void> {
    setLoading("preset-transfer");
    try {
      const transferred = await api.transferPreset(preset.id, transferTargetHash);
      setPresets((current) => current.map((candidate) => candidate.id === preset.id ? transferred : candidate));
      setNotice({ message: language === "en" ? "Preset transferred." : "预设已转让。", kind: "success" });
      setModal(null);
    } catch (error: unknown) {
      setNotice({ message: error instanceof Error ? error.message : String(error), kind: "error" });
    } finally {
      setLoading(null);
    }
  }

  const filteredInventory = useMemo(() => {
    const query = inventorySearch.trim().toLowerCase();
    return inventory.filter((item) => {
      if (!isCharacterCompatible(item.required_character_hash, currentCharacterHash)) return false;
      const usedByBody = item.equipped;
      const usedByExtension = item.virtual_owner_character_hash !== 0;
      const matchesFilter = inventoryFilter === "all"
        || (inventoryFilter === "used" && (usedByBody || usedByExtension))
        || (inventoryFilter === "body" && usedByBody)
        || (inventoryFilter === "extension" && usedByExtension)
        || (inventoryFilter === "unused" && !usedByBody && !usedByExtension);
      if (!matchesFilter) return false;
      return query.length === 0 || item.searchable.includes(query);
    });
  }, [currentCharacterHash, inventory, inventoryFilter, inventorySearch]);

  const characterForHash = (hash: number): string => characterName(hash, language);
  const currentSlotValue = (slot: number): InventoryItem | undefined => inventory.find((item) => item.gem.slot_id === selection[slot]);
  const presetReferenceNames = (item: InventoryItem): string => item.preset_names.length > 0 ? item.preset_names.join(language === "en" ? ", " : "、") : "";

  function renderConnectionPage(): ReactNode {
    const selectedProcess = processes.find((process) => process.pid === selectedPid);
    const autoConnectPaused = selectedProcess?.pid === autoConnectSuppressedPidRef.current;
    return (
      <main className="connection-page">
        <section className="connection-intro">
          <div>
            <div className="eyebrow">{language === "en" ? "CONTROL SESSION" : "控制会话"}</div>
            <h1>{language === "en" ? "Connect to a running game" : "连接正在运行的游戏"}</h1>
            <p>{language === "en" ? "A single compatible game process is detected, injected, and connected automatically. Choose manually only when several are running." : "检测到唯一兼容游戏进程后会自动注入并连接；只有同时运行多个进程时才需要手动选择。"}</p>
          </div>
          <IconButton label={language === "en" ? "Refresh process list" : "刷新进程列表"} onClick={() => void handleRefreshProcesses()} disabled={loading === "processes"}>
            <RefreshCw size={17} className={loading === "processes" ? "spin" : ""} />
          </IconButton>
        </section>

        <section className="process-panel" aria-label={language === "en" ? "Game processes" : "游戏进程"}>
          <div className="process-panel-header">
            <div>
              <h2>{language === "en" ? "Detected processes" : "检测到的进程"}</h2>
              <span>{language === "en" ? `${processes.length} matching processes` : `${processes.length} 个匹配进程`}</span>
            </div>
            <StatusBadge tone="neutral">granblue_fantasy_relink.exe</StatusBadge>
          </div>
          <div className="process-table">
            <div className="process-table-head">
              <span>PID</span>
              <span>{language === "en" ? "Executable" : "可执行文件"}</span>
              <span>{language === "en" ? "Agent" : "Agent 状态"}</span>
            </div>
            {processes.map((process) => (
              <button
                type="button"
                className={`process-row ${selectedPid === process.pid ? "process-row-selected" : ""}`}
                key={process.pid}
                data-testid={`process-row-${process.pid}`}
                aria-pressed={selectedPid === process.pid}
                onClick={() => setSelectedPid(process.pid)}
              >
                <span className="process-pid">{formatPid(process.pid)}</span>
                <span className="process-name">
                  <strong>{process.executable_name}</strong>
                  <small>{process.executable_path}</small>
                </span>
                <span>{process.agent_loaded ? <StatusBadge tone="green">{language === "en" ? "Loaded" : "已加载"}</StatusBadge> : <StatusBadge tone="amber">{language === "en" ? "Auto inject" : "自动注入"}</StatusBadge>}</span>
              </button>
            ))}
            {processes.length === 0 && <div className="empty-state">{language === "en" ? "No compatible game process found." : "未找到兼容的游戏进程。"}</div>}
          </div>
          <div className="connection-footer">
            <div className="error-copy" role="status" aria-live="polite">
              {connectionError && <><CircleAlert size={16} /> <span>{connectionError}</span></>}
              {!connectionError && loading === "connect" && <span>{language === "en" ? "Preparing the Agent and waiting for IPC to become ready..." : "正在准备 Agent，并等待 IPC 完全就绪……"}</span>}
              {!connectionError && loading !== "connect" && selectedProcess && processes.length === 1 && <span>{autoConnectPaused
                ? language === "en" ? "Automatic connection is paused for this process. Click Connect to try again." : "已暂停对此进程的自动连接；点击“连接”可再次尝试。"
                : selectedProcess.agent_loaded ? (language === "en" ? "Existing Agent detected; connection will resume automatically." : "已检测到现有 Agent，将自动恢复连接。") : (language === "en" ? "The game was detected and will be injected automatically." : "已检测到游戏，将自动完成注入。")}</span>}
              {!connectionError && loading !== "connect" && selectedProcess && processes.length > 1 && <span>{selectedProcess.agent_loaded ? (language === "en" ? "Existing Agent will be reused." : "将复用现有 Agent。") : (language === "en" ? "The selected process will be validated again before injection." : "注入前会再次核验选定进程。")}</span>}
              {!connectionError && loading !== "connect" && !selectedProcess && <span>{language === "en" ? "Scanning automatically; no manual refresh is required." : "正在自动检测游戏进程，无需手动刷新。"}</span>}
            </div>
            <button type="button" className="button button-primary" onClick={() => void handleConnect()} disabled={selectedPid === null || loading === "connect"}>
              {loading === "connect" ? <LoaderCircle size={16} className="spin" /> : <PlugZap size={16} />}
              {language === "en" ? "Connect" : "连接"}
            </button>
          </div>
        </section>

      </main>
    );
  }

  function renderInventoryModal(): ReactNode {
    if (pickerSlot === null) return null;
    const filterLabels: Array<[InventoryFilter, string, string]> = [
      ["all", "All", "所有"],
      ["used", "Used", "已使用"],
      ["body", "Body used", "本体占用"],
      ["extension", "Extension used", "扩展占用"],
      ["unused", "Unused", "未使用"],
    ];
    return (
      <ModalFrame
        title={language === "en" ? `Select inventory sigil · Slot ${pickerSlot + 13}` : `选择库存因子 · 槽位 ${pickerSlot + 13}`}
        eyebrow={language === "en" ? "INVENTORY" : "库存选择"}
        onClose={() => setPickerSlot(null)}
        size="wide"
      >
        <div className="picker-toolbar">
          <label className="search-field">
            <Search size={16} />
            <span className="sr-only">{language === "en" ? "Search sigils" : "搜索因子"}</span>
            <input value={inventorySearch} onChange={(event) => setInventorySearch(event.currentTarget.value)} placeholder={language === "en" ? "Search name or trait" : "搜索因子名称或特性"} />
          </label>
          <IconButton label={language === "en" ? "Refresh inventory" : "刷新库存"} onClick={() => void handleRefreshInventory()} disabled={loading === "inventory"}>
            <RefreshCw size={16} className={loading === "inventory" ? "spin" : ""} />
          </IconButton>
        </div>
        <div className="segmented filter-segmented" role="group" aria-label={language === "en" ? "Inventory filters" : "库存筛选"}>
          {filterLabels.map(([value, englishLabel, chineseLabel]) => (
            <button type="button" key={value} className={inventoryFilter === value ? "segment-active" : ""} onClick={() => setInventoryFilter(value)}>
              {language === "en" ? englishLabel : chineseLabel}
            </button>
          ))}
        </div>
        <div className="picker-summary">
          <span>{language === "en" ? "Matching sigils" : "匹配的因子"}</span>
          <strong>{filteredInventory.length}</strong>
        </div>
        <div className="inventory-list" role="listbox" aria-label={language === "en" ? "Inventory sigils" : "库存因子列表"}>
          {filteredInventory.map((item) => {
            const owner = item.equipped ? characterForHash(item.gem.worn_by) : item.virtual_owner_character_hash !== 0 ? characterForHash(item.virtual_owner_character_hash) : "";
            const usage = item.equipped
              ? language === "en" ? `Body used · ${owner}` : `本体占用 · ${owner}`
              : item.virtual_owner_character_hash !== 0
                ? language === "en" ? `Extension slot ${item.virtual_owner_slot + 13} · ${owner}` : `扩展槽 ${item.virtual_owner_slot + 13} · ${owner}`
                : language === "en" ? "Unused" : "未使用";
            const presetReferences = presetReferenceNames(item);
            return (
              <button type="button" className="inventory-row" key={item.gem.slot_id} role="option" onClick={() => handleInventoryPick(item)}>
                <span className="inventory-gem-mark">{item.gem.sigil_level}</span>
                <span className="inventory-main">
                  <strong>{item.label}</strong>
                  <span>{language === "en" ? `Inventory slot ${item.gem.slot_id}` : `库存槽 ${item.gem.slot_id}`}</span>
                </span>
                <span className="inventory-meta">
                  <span className={item.equipped || item.virtual_owner_character_hash !== 0 ? "meta-occupied" : "meta-free"}>{usage}</span>
                  {presetReferences && <span>{language === "en" ? `Presets · ${presetReferences}` : `预设 · ${presetReferences}`}</span>}
                </span>
                <ChevronRight size={16} className="inventory-chevron" />
              </button>
            );
          })}
          {filteredInventory.length === 0 && <div className="empty-state">{language === "en" ? "No matching sigils." : "没有匹配的因子。"}</div>}
        </div>
        <div className="modal-actions">
          <button type="button" className="button button-secondary" onClick={() => clearSlot(pickerSlot)} disabled={!editAllowed || loading === `clear-${pickerSlot}`}>
            <Trash2 size={15} />
            {language === "en" ? "Clear this slot" : "清空此槽"}
          </button>
        </div>
      </ModalFrame>
    );
  }

  function renderConflictModal(): ReactNode {
    if (!modal || (modal.kind !== "body-conflict" && modal.kind !== "transfer-conflict")) return null;
    if (modal.kind === "body-conflict") {
      const owner = characterForHash(modal.item.gem.worn_by);
      return (
        <ModalFrame title={language === "en" ? "Body-used sigil" : "本体已使用的因子"} eyebrow={language === "en" ? "CONFLICT" : "冲突"} onClose={() => setModal(null)}>
          <div className="dialog-copy">
            <div className="dialog-icon dialog-icon-amber"><CircleAlert size={20} /></div>
            <div>
              <p>{language === "en" ? `This sigil is already used in ${owner}'s body slots.` : `当前因子已被${owner}的本体因子栏使用。`}</p>
              <p className="muted">{language === "en" ? "Remove it from that character first, then add it to a virtual extension slot." : "请先到对应角色位置脱除因子，然后再重新添加到虚拟扩展栏。"}</p>
            </div>
          </div>
          <div className="modal-actions"><button type="button" className="button button-primary" onClick={() => setModal(null)}>{language === "en" ? "OK" : "知道了"}</button></div>
        </ModalFrame>
      );
    }
    const owner = characterForHash(modal.item.virtual_owner_character_hash);
    const references = presetReferenceNames(modal.item);
    return (
      <ModalFrame title={language === "en" ? "Transfer extension sigil" : "转移扩展因子"} eyebrow={language === "en" ? "CONFLICT" : "冲突"} onClose={() => setModal(null)}>
        <div className="dialog-copy">
          <div className="dialog-icon dialog-icon-red"><ArrowRightLeft size={20} /></div>
          <div>
            <p>{language === "en" ? `This sigil is used by ${owner} in extension slot ${modal.item.virtual_owner_slot + 13}.` : `当前因子已被${owner}用于虚拟扩展槽 ${modal.item.virtual_owner_slot + 13}。`}</p>
            {references && <p className="muted">{language === "en" ? `Referenced by presets: ${references}` : `同时被以下预设引用：${references}`}</p>}
            <p className="muted">{language === "en" ? `Confirming moves it to ${characterForHash(currentCharacterHash)} and clears the source slot.` : `确认后会将其转移给${characterForHash(currentCharacterHash)}，并清空来源角色的当前槽位。`}</p>
          </div>
        </div>
        <label className="checkbox-row"><input type="checkbox" checked={suppressTransferPrompt} onChange={(event) => setSuppressTransferPrompt(event.currentTarget.checked)} /><span>{language === "en" ? "Do not ask again while this menu is open" : "当前菜单期间不再提示"}</span></label>
        <div className="modal-actions"><button type="button" className="button button-secondary" onClick={() => setModal(null)}>{language === "en" ? "Cancel" : "取消"}</button><button type="button" className="button button-danger" onClick={() => pickerSlot !== null && void assignInventory(modal.item, pickerSlot)} disabled={loading?.startsWith("assign-")}>{language === "en" ? "Move sigil" : "转移因子"}</button></div>
      </ModalFrame>
    );
  }

  function renderSlotReductionModal(): ReactNode {
    if (!modal || modal.kind !== "slot-reduction") return null;
    return (
      <ModalFrame title={language === "en" ? "Confirm extra-slot reduction" : "确认缩减扩展因子槽"} eyebrow={language === "en" ? "RESTART REQUIRED" : "需要重启"} onClose={() => setModal(null)}>
        <div className="dialog-copy">
          <div className="dialog-icon dialog-icon-amber"><Settings2 size={20} /></div>
          <div>
            <p>{language === "en" ? `Reduce extra slots from ${activeSlotCount} to ${modal.target}?` : `将扩展因子槽从 ${activeSlotCount} 个缩减到 ${modal.target} 个？`}</p>
            <p className="muted">{language === "en" ? "After the next restart, removed slots will be cleared for every character and their sigils become available again. Inventory sigils are not deleted; saved presets retain all 24 slot definitions." : "下次重启后，所有角色超出新上限的当前扩展槽都会被清空，相关因子会重新变为可用。库存因子不会被删除，已保存预设仍会保留全部 24 个槽位定义。"}</p>
          </div>
        </div>
        <div className="modal-actions"><button type="button" className="button button-secondary" onClick={() => setModal(null)}>{language === "en" ? "Cancel" : "取消"}</button><button type="button" className="button button-primary" onClick={() => void commitSlotCount(modal.target)} disabled={loading === "slot-count"}>{language === "en" ? "Confirm" : "确认"}</button></div>
      </ModalFrame>
    );
  }

  function renderPresetNameModal(): ReactNode {
    if (!modal || modal.kind !== "preset-name") return null;
    return (
      <ModalFrame title={language === "en" ? "Preset name" : "预设名称"} eyebrow={modal.mode === "create" ? (language === "en" ? "NEW PRESET" : "新建预设") : (language === "en" ? "RENAME" : "重命名")} onClose={() => setModal(null)}>
        <label className="field-label" htmlFor="preset-name-input">{language === "en" ? "Name" : "名称"}</label>
        <input id="preset-name-input" className="text-input" value={presetName} onChange={(event) => setPresetName(event.currentTarget.value)} maxLength={48} autoFocus />
        <div className="field-footer"><span>{language === "en" ? "Up to 48 characters" : "最多 48 个字符"}</span><span>{presetName.length}/48</span></div>
        <div className="modal-actions"><button type="button" className="button button-secondary" onClick={() => setModal(null)}>{language === "en" ? "Cancel" : "取消"}</button><button type="button" className="button button-primary" onClick={() => void submitPresetName()} disabled={loading === "preset-name"}>{language === "en" ? "Save" : "保存"}</button></div>
      </ModalFrame>
    );
  }

  function renderPresetTransferModal(): ReactNode {
    if (!modal || modal.kind !== "preset-transfer") return null;
    const sourceCharacter = characterForHash(modal.preset.character_hash);
    return (
      <ModalFrame title={language === "en" ? "Transfer preset" : "转让预设"} eyebrow={language === "en" ? "PRESET" : "预设"} onClose={() => setModal(null)}>
        <div className="transfer-summary"><span>{language === "en" ? "Preset" : "预设"}</span><strong>{modal.preset.name}</strong><span>{language === "en" ? `From ${sourceCharacter}` : `来源角色：${sourceCharacter}`}</span></div>
        <label className="field-label" htmlFor="transfer-target">{language === "en" ? "Transfer to" : "转让给"}</label>
        <select id="transfer-target" className="text-input" value={transferTargetHash} onChange={(event) => setTransferTargetHash(Number(event.currentTarget.value))}>
          {CHARACTERS.filter((character) => character.hash !== modal.preset.character_hash).map((character) => <option value={character.hash} key={character.hash}>{language === "en" ? character.en : character.zh}</option>)}
        </select>
        <p className="muted transfer-note">{language === "en" ? "The target character must not already have a preset with the same name." : "目标角色不能已有同名预设。"}</p>
        <div className="modal-actions"><button type="button" className="button button-secondary" onClick={() => setModal(null)}>{language === "en" ? "Cancel" : "取消"}</button><button type="button" className="button button-primary" onClick={() => void submitTransferPreset(modal.preset)} disabled={loading === "preset-transfer"}>{language === "en" ? "Confirm transfer" : "确认转让"}</button></div>
      </ModalFrame>
    );
  }

  function renderPresetManager(): ReactNode {
    if (!managerOpen) return null;
    const managerIsCurrentCharacter = managerCharacterHash === currentCharacterHash;
    const canManageCurrent = editAllowed && managerIsCurrentCharacter;
    return (
      <ModalFrame title={language === "en" ? "Manage presets" : "管理预设"} eyebrow={language === "en" ? "PRESETS" : "预设管理"} onClose={() => setManagerOpen(false)} size="manager">
        <div className="manager-grid">
          <section className="manager-column manager-characters">
            <div className="section-title"><span>{language === "en" ? "Characters" : "角色"}</span><span>{CHARACTERS.length}</span></div>
            <div className="manager-list">
              {CHARACTERS.map((character) => {
                const count = presets.filter((preset) => preset.character_hash === character.hash).length;
                return <button type="button" className={`manager-list-row ${managerCharacterHash === character.hash ? "manager-row-selected" : ""}`} key={character.hash} onClick={() => { setManagerCharacterHash(character.hash); setManagerPresetId(presets.find((preset) => preset.character_hash === character.hash)?.id ?? null); }}><span>{language === "en" ? character.en : character.zh}</span><span className="count-badge">{count}</span></button>;
              })}
            </div>
          </section>
          <section className="manager-column manager-presets">
            <div className="section-title"><span>{language === "en" ? "Presets" : "预设"}</span><span>{managerPresets.length}</span></div>
            <div className="manager-list">
              {managerPresets.map((preset) => <button type="button" className={`manager-list-row ${managerSelectedPreset?.id === preset.id ? "manager-row-selected" : ""}`} key={preset.id} onClick={() => setManagerPresetId(preset.id)}><span>{preset.name}</span><span className="preset-slot-count">{preset.slots.filter((slotId) => slotId !== 0).length}/24</span></button>)}
              {managerPresets.length === 0 && <div className="empty-state">{language === "en" ? "No presets" : "没有预设"}</div>}
            </div>
          </section>
        </div>
        <div className="modal-actions manager-actions">
          <button type="button" className="button button-primary" disabled={!canManageCurrent || !managerSelectedPreset} onClick={() => managerSelectedPreset && void applyPreset(managerSelectedPreset)}><Check size={15} />{language === "en" ? "Apply" : "套用"}</button>
          <button type="button" className="button button-secondary" disabled={!canManageCurrent} onClick={openCreatePreset}><Plus size={15} />{language === "en" ? "New" : "新建"}</button>
          <button type="button" className="button button-secondary" disabled={!managerSelectedPreset} onClick={() => managerSelectedPreset && openRenamePreset(managerSelectedPreset)}><Pencil size={15} />{language === "en" ? "Rename" : "重命名"}</button>
          <button type="button" className="button button-danger-quiet" disabled={!managerSelectedPreset || loading === "preset-delete"} onClick={() => void deletePreset()}><Trash2 size={15} />{language === "en" ? "Delete" : "删除"}</button>
          <button type="button" className="button button-secondary" disabled={!managerSelectedPreset} onClick={() => managerSelectedPreset && openTransferPreset(managerSelectedPreset)}><ArrowRightLeft size={15} />{language === "en" ? "Transfer" : "转让"}</button>
          <button type="button" className="button button-ghost" onClick={() => setManagerOpen(false)}>{language === "en" ? "Close" : "关闭"}</button>
        </div>
      </ModalFrame>
    );
  }

  function renderConnected(): ReactNode {
    if (!dashboard) return null;
    const pendingCount = dashboard.pending_virtual_slot_count;
    const statusTone = !dashboard.game_data_ready || !sessionHydrated ? "neutral" : editAllowed ? "green" : "amber";
    const statusLabel = !dashboard.game_data_ready
      ? language === "en" ? "Waiting for game" : "等待游戏"
      : !sessionHydrated
        ? language === "en" ? "Reading data" : "读取数据"
        : editAllowed
          ? language === "en" ? "Editable" : "可修改"
          : language === "en" ? "Read-only" : "只读";
    const scanLabel = sessionHydrated
      ? language === "en" ? `${inventory.length} sigils scanned` : `已扫描 ${inventory.length} 个因子`
      : dashboard.game_data_ready
        ? language === "en" ? "Reading game data" : "正在读取游戏数据"
        : language === "en" ? "Waiting for game data" : "等待游戏数据";
    return (
      <main className="workbench">
        <section className="workspace-toolbar">
          <div className="toolbar-character"><span className="eyebrow">{language === "en" ? "CURRENT CHARACTER" : "当前角色"}</span><strong>{characterForHash(currentCharacterHash)}</strong><span className="hash-label">{formatHash(currentCharacterHash)}</span></div>
          <div className="toolbar-actions">
            <span className="scan-count">{scanLabel}</span>
            <button type="button" className="button button-secondary button-compact" onClick={() => void handleRefreshInventory()} disabled={loading === "inventory" || !dashboard.game_data_ready}><RefreshCw size={15} className={loading === "inventory" ? "spin" : ""} />{language === "en" ? "Refresh" : "刷新"}</button>
            <button type="button" className="button button-ghost button-compact" onClick={() => void handleDisconnect()} disabled={loading === "disconnect"}>{language === "en" ? "Disconnect" : "断开"}</button>
          </div>
        </section>

        <div className="workspace-grid">
          <aside className="control-pane">
            <section className="work-section language-section">
              <div className="section-title"><span>{language === "en" ? "Language" : "语言"}</span><span className="section-meta">{language === "en" ? "UI" : "界面"}</span></div>
              <div className="segmented" role="group" aria-label={language === "en" ? "Language" : "语言"}>
                <button type="button" className={language === "zh-CN" ? "segment-active" : ""} onClick={() => void handleLanguageChange("zh-CN")} disabled={loading === "language"}>中文</button>
                <button type="button" className={language === "en" ? "segment-active" : ""} onClick={() => void handleLanguageChange("en")} disabled={loading === "language"}>English</button>
              </div>
            </section>

            <section className="work-section state-section">
              <div className="section-title"><span>{language === "en" ? "Session state" : "当前状态"}</span><StatusBadge tone={statusTone}>{statusLabel}</StatusBadge></div>
              <p className="runtime-message">{dashboard.runtime_message}</p>
              {!isTauriRuntime() && <div className="state-switch" role="group" aria-label={language === "en" ? "Session edit state" : "会话编辑状态"}><button type="button" className={editAllowed ? "state-active" : ""} onClick={() => void handleSetEditAllowed(true)}>Editable</button><button type="button" className={!editAllowed ? "state-active" : ""} onClick={() => void handleSetEditAllowed(false)}>Read-only</button></div>}
              <div className="warning-line"><CircleAlert size={15} /><span>{language === "en" ? "The game does not support hot-updating sigils during battle." : "游戏不支持战斗状态热更新因子。"}</span></div>
            </section>

            <section className="work-section slot-count-section">
              <div className="section-title"><span>{language === "en" ? "Extra slots" : "扩展槽数量"}</span><span className="section-meta">1–24</span></div>
              <div className="count-line"><strong>{activeSlotCount}</strong><span>{language === "en" ? "active now" : "当前生效"}</span>{pendingCount > 0 && <StatusBadge tone="amber">{language === "en" ? `${pendingCount} pending` : `待生效 ${pendingCount}`}</StatusBadge>}</div>
              <div className="count-controls"><input aria-label={language === "en" ? "Extra slot count" : "扩展槽数量"} className="number-input" type="number" min={1} max={24} value={slotCountInput} onChange={(event) => setSlotCountInput(event.currentTarget.value)} onKeyDown={(event) => { if (event.key === "Enter") submitSlotCount(); }} /><button type="button" className="button button-secondary button-compact" onClick={submitSlotCount} disabled={loading === "slot-count"}><Settings2 size={15} />{language === "en" ? "Save for restart" : "保存并重启生效"}</button></div>
              <p className="field-note">{language === "en" ? "Saved presets always retain all 24 slot definitions." : "已保存预设始终保留全部 24 个槽位定义。"}</p>
            </section>

            <section className="work-section preset-section">
              <div className="section-title"><span>{language === "en" ? "Current preset" : "当前预设"}</span><FolderCog size={16} /></div>
              <div className="preset-current"><strong>{selectedPreset?.name ?? (language === "en" ? "Temporary preset" : "临时预设")}</strong><span>{currentPresets.length} {language === "en" ? "saved" : "个已保存"}</span></div>
              <div className="preset-controls"><IconButton label={language === "en" ? "Previous preset" : "上一个预设"} onClick={() => cyclePreset(-1)} disabled={currentPresets.length === 0}><ChevronLeft size={16} /></IconButton><IconButton label={language === "en" ? "Next preset" : "下一个预设"} onClick={() => cyclePreset(1)} disabled={currentPresets.length === 0}><ChevronRight size={16} /></IconButton><button type="button" className="button button-secondary button-compact" onClick={() => selectedPreset && void applyPreset(selectedPreset)} disabled={!selectedPreset || !editAllowed || loading === "apply-preset"}>{language === "en" ? "Apply" : "套用"}</button><button type="button" className="button button-secondary button-compact" onClick={() => void overwritePreset()} disabled={!selectedPreset || !editAllowed}>{language === "en" ? "Overwrite" : "覆盖"}</button></div>
              <div className="preset-actions"><button type="button" className="button button-ghost button-compact" onClick={openCreatePreset} disabled={!editAllowed}><Plus size={15} />{language === "en" ? "Save as" : "另存为"}</button><button type="button" className="button button-ghost button-compact" onClick={openManager}><FolderCog size={15} />{language === "en" ? "Manage" : "管理"}</button></div>
            </section>
          </aside>

          <section className="slots-pane" aria-label={language === "en" ? "Virtual extension slots" : "虚拟扩展槽列表"}>
            <div className="slots-header"><div><div className="eyebrow">{language === "en" ? "VIRTUAL EXTENSION SLOTS" : "虚拟扩展槽"}</div><h2>{language === "en" ? "Slot selection" : "槽位选择"}</h2></div><div className="slots-capacity">{language === "en" ? `${activeSlotCount} of 24 active` : `${activeSlotCount} / 24 生效`}</div></div>
            <div className="slot-list">
              {Array.from({ length: activeSlotCount }, (_, slot) => {
                const item = currentSlotValue(slot);
                return <div className="slot-row" key={slot}><span className="slot-number">{String(slot + 13).padStart(2, "0")}</span><button type="button" className={`slot-value ${item ? "slot-filled" : "slot-empty"}`} onClick={() => openPicker(slot)} disabled={!editAllowed}>{item ? <><span className="slot-level">{item.gem.sigil_level}</span><span><strong>{item.label}</strong><small>{language === "en" ? `Inventory slot ${item.gem.slot_id}` : `库存槽 ${item.gem.slot_id}`}</small></span></> : <span>{language === "en" ? "Select a sigil" : "选择因子"}</span>}<ChevronRight size={16} /></button><IconButton label={language === "en" ? `Clear slot ${slot + 13}` : `清空槽位 ${slot + 13}`} onClick={() => void clearSlot(slot)} disabled={!item || !editAllowed || loading === `clear-${slot}`} className="slot-clear"><X size={15} /></IconButton></div>;
              })}
            </div>
            <div className="slots-footer"><Info size={15} /><span>{language === "en" ? "Extension slots begin at 13 to match the game’s internal slot numbering." : "扩展槽从 13 开始编号，与游戏内部槽位编号保持一致。"}</span></div>
          </section>
        </div>
        {hydrationWarning && <div className="notice notice-warning" role="status" aria-live="polite"><CircleAlert size={16} /><span>{hydrationWarning}</span><IconButton label={language === "en" ? "Dismiss notice" : "关闭提示"} onClick={() => setHydrationWarning("")}><X size={15} /></IconButton></div>}
        {notice && <div className={`notice notice-${notice.kind}`} role="status" aria-live="polite">{notice.kind === "success" ? <CircleCheck size={16} /> : <CircleAlert size={16} />}<span>{notice.message}</span><IconButton label={language === "en" ? "Dismiss notice" : "关闭提示"} onClick={() => setNotice(null)}><X size={15} /></IconButton></div>}
      </main>
    );
  }

  return (
    <div className="app-shell">
      <header className="topbar">
        <div className="brand-lockup"><div className="brand-mark"><Cable size={18} /></div><div><strong>GBFR Extra Sigil Slots Standalone</strong><span>{language === "en" ? "External control desk" : "独立控制台"}</span></div></div>
        <div className="topbar-status"><StatusBadge tone={isConnected ? "green" : "neutral"}>{isConnected ? (language === "en" ? "Connected" : "已连接") : (language === "en" ? "Not connected" : "未连接")}</StatusBadge>{isConnected && <><span className="topbar-pid">PID {formatPid(dashboard.connection.pid)}</span><span className="topbar-process">{dashboard.connection.process_name}</span></>}</div>
      </header>
      {isConnected ? renderConnected() : renderConnectionPage()}
      {renderInventoryModal()}
      {renderConflictModal()}
      {renderSlotReductionModal()}
      {renderPresetNameModal()}
      {renderPresetTransferModal()}
      {renderPresetManager()}
    </div>
  );
}

export default App;
