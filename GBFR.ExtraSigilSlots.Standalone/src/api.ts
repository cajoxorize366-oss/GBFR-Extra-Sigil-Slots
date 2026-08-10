import { invoke } from "@tauri-apps/api/core";
import { CHARACTERS, type CharacterOption, type ConnectionInfo, type Dashboard, type GameProcess, type InventoryItem, type Language, type PresetApplySummary, type PresetDocument, type SigilPreset, type SlotCountRequestResult } from "./types";

export interface StandaloneApi {
  listGameProcesses(): Promise<GameProcess[]>;
  connectGame(pid: number): Promise<ConnectionInfo>;
  disconnectGame(): Promise<void>;
  getDashboard(): Promise<Dashboard>;
  refreshInventory(): Promise<InventoryItem[]>;
  getSelection(characterHash: number): Promise<number[]>;
  assignInventorySigil(characterHash: number, virtualSlot: number, inventorySlotId: number): Promise<AssignResponse>;
  clearVirtualSlot(characterHash: number, virtualSlot: number): Promise<AssignResponse>;
  setLanguage(language: Language): Promise<Dashboard>;
  requestVirtualSlotCount(slotCount: number): Promise<SlotCountRequestResult>;
  listPresets(): Promise<PresetDocument>;
  createPreset(characterHash: number, name: string): Promise<SigilPreset>;
  overwritePreset(presetId: string): Promise<SigilPreset>;
  renamePreset(presetId: string, name: string): Promise<SigilPreset>;
  deletePreset(presetId: string): Promise<PresetDocument>;
  transferPreset(presetId: string, targetCharacterHash: number): Promise<SigilPreset>;
  applyPreset(presetId: string, characterHash: number): Promise<PresetApplySummary>;
}

export interface AssignResponse {
  success: boolean;
  message: string;
  affected_preset_names: string[];
}

export function isTauriRuntime(): boolean {
  return typeof window !== "undefined" && "__TAURI_INTERNALS__" in window;
}

async function invokeCommand<T>(command: string, args?: Record<string, unknown>): Promise<T> {
  return invoke<T>(command, args);
}

const captainHash = CHARACTERS[0].hash;
const katalinaHash = CHARACTERS[1].hash;
const rackamHash = CHARACTERS[2].hash;
const ioHash = CHARACTERS[3].hash;

interface MockState {
  connectedProcess?: GameProcess;
  language: Language;
  editAllowed: boolean;
  connectionFailuresRemaining: number;
  inventoryRevision: number;
  inventoryRefreshFailuresRemaining: number;
  virtualSlotCount: number;
  pendingVirtualSlotCount: number;
  selection: Map<number, number[]>;
  inventory: InventoryItem[];
  presets: SigilPreset[];
}

function clone<T>(value: T): T {
  return structuredClone(value);
}

function makeGem(slotId: number, gemId: number, level: number): InventoryItem["gem"] {
  return {
    trait1: 1000 + gemId,
    trait1_level: level,
    trait2: 2000 + gemId,
    trait2_level: Math.max(1, level - 1),
    gem_id: gemId,
    worn_by: 0,
    sigil_level: level,
    slot_id: slotId,
    flags: 0,
  };
}

function createMockInventory(): InventoryItem[] {
  const definitions: Array<[
    string,
    number,
    boolean,
    number,
    number,
    number,
    number,
  ]> = [
    ["War Elemental +", 1001, true, captainHash, 0, 0, 0],
    ["Supplemental Damage III", 1002, false, 0, 0, katalinaHash, 2],
    ["Critical Hit V", 1003, false, 0, 0, 0, 0],
    ["Damage Cap V", 1004, false, 0, 0, captainHash, 0],
    ["Stamina V", 1005, false, 0, 0, 0, 0],
    ["Combo Booster IV", 1006, false, 0, 0, 0, 0],
    ["Linked Together III", 1007, false, 0, 0, 0, 0],
    ["Quick Charge IV", 1008, false, 0, 0, 0, 0],
    ["Nimble Onslaught III", 1009, false, 0, 0, rackamHash, 4],
    ["Life on the Line V", 1010, false, 0, 0, 0, 0],
    ["Potion Hoarder III", 1011, false, 0, 0, 0, 0],
    ["Crabby Resonance V", 1012, false, 0, 0, 0, 0],
    ["Autumn's Transformation IV", 1013, false, 0, 0, 0, 0],
    ["Damage Cap V", 1014, false, 0, 0, 0, 0],
    ["War Elemental +", 1015, false, 0, 0, 0, 0],
    ["Supplemental Damage III", 1016, false, 0, 0, 0, 0],
    ["Health V", 1017, true, katalinaHash, 0, 0, 0],
    ["Stun V", 1018, false, 0, 0, ioHash, 1],
    ["Aegis IV", 1019, false, 0, 0, 0, 0],
    ["Provoke III", 1020, false, 0, 0, 0, 0],
  ];

  return definitions.map(([label, gemId, equipped, wornBy, requiredCharacterHash, virtualOwner, virtualSlot], index) => ({
    gem: { ...makeGem(5000 + index, gemId, 3 + (index % 3)), worn_by: equipped ? wornBy : 0 },
    label,
    searchable: label.toLowerCase(),
    equipped,
    required_character_hash: requiredCharacterHash,
    virtual_owner_character_hash: virtualOwner,
    virtual_owner_slot: virtualSlot,
    preset_names: [],
  }));
}

function createMockPresets(): SigilPreset[] {
  return [
    {
      id: "preset-captain-raid",
      name: "Raid / Damage",
      character_hash: captainHash,
      slots: [5003, 5004, 5005, 5006, 5007, 5014, 0, 0, ...Array(16).fill(0)],
    },
    {
      id: "preset-captain-safe",
      name: "Safe clear",
      character_hash: captainHash,
      slots: [5001, 5008, 5009, 5010, 5011, 5012, 0, 0, ...Array(16).fill(0)],
    },
    {
      id: "preset-katalina-frost",
      name: "Frost guard",
      character_hash: katalinaHash,
      slots: [5002, 5016, 5017, 5018, 5019, 0, 0, 0, ...Array(16).fill(0)],
    },
    {
      id: "preset-rackam-burst",
      name: "Burst cycle",
      character_hash: rackamHash,
      slots: [5009, 5013, 5015, 5006, 5007, 5010, 0, 0, ...Array(16).fill(0)],
    },
  ];
}

function createMockState(): MockState {
  const inventory = createMockInventory();
  const presets = createMockPresets();
  const selection = new Map<number, number[]>([
    [captainHash, [5003, 5004, 5005, 5006, 5007, 5014, 0, 0, ...Array(16).fill(0)]],
    [katalinaHash, [5002, 5016, 0, 0, 0, 0, 0, 0, ...Array(16).fill(0)]],
    [rackamHash, [5009, 5013, 5015, 5006, 0, 0, 0, 0, ...Array(16).fill(0)]],
  ]);
  return {
    language: "zh-CN",
    editAllowed: true,
    connectionFailuresRemaining: 0,
    inventoryRevision: 42,
    inventoryRefreshFailuresRemaining: 0,
    virtualSlotCount: 8,
    pendingVirtualSlotCount: 0,
    selection,
    inventory,
    presets,
  };
}

let mockState = createMockState();

function createMockProcessList(): GameProcess[] {
  return [
    {
      pid: 18244,
      executable_name: "granblue_fantasy_relink.exe",
      executable_path: "C:\\Games\\Granblue Fantasy Relink\\granblue_fantasy_relink.exe",
      agent_loaded: false,
    },
    {
      pid: 22660,
      executable_name: "granblue_fantasy_relink.exe",
      executable_path: "D:\\SteamLibrary\\steamapps\\common\\Granblue Fantasy Relink\\granblue_fantasy_relink.exe",
      agent_loaded: true,
    },
  ];
}

let mockProcesses = createMockProcessList();

function mockProcessList(): GameProcess[] {
  return mockProcesses;
}

function mockConnection(): ConnectionInfo {
  const process = mockState.connectedProcess ?? mockProcessList()[0];
  return {
    pid: process.pid,
    process_name: process.executable_name,
    injected: true,
    protocol_version: 1,
    native_abi_version: 13,
  };
}

function mockDashboard(): Dashboard {
  return {
    connection: mockConnection(),
    initialized: true,
    hooks_ready: true,
    runtime_message: mockState.editAllowed
      ? "Ready. Inventory changes apply after the next safe rebuild."
      : "The game is currently in a read-only state.",
    runtime_message_is_error: false,
    effective_character_hash: captainHash,
    ui_selected_character_hash: captainHash,
    edit_allowed: mockState.editAllowed,
    language: mockState.language,
    inventory_revision: mockState.inventoryRevision,
    inventory_dirty: false,
    virtual_slot_count: mockState.virtualSlotCount,
    virtual_slot_capacity: 24,
    pending_virtual_slot_count: mockState.pendingVirtualSlotCount,
  };
}

function updatePresetReferences(): void {
  for (const item of mockState.inventory) {
    item.preset_names = mockState.presets
      .filter((preset) => preset.slots.includes(item.gem.slot_id))
      .map((preset) => preset.name);
  }
}

function findInventory(slotId: number): InventoryItem | undefined {
  return mockState.inventory.find((item) => item.gem.slot_id === slotId);
}

function getMockPresets(): PresetDocument {
  updatePresetReferences();
  return { version: 3, presets: clone(mockState.presets) };
}

function mockAssign(characterHash: number, virtualSlot: number, inventorySlotId: number): AssignResponse {
  if (!mockState.editAllowed) {
    return { success: false, message: "The current state is read-only.", affected_preset_names: [] };
  }
  const item = findInventory(inventorySlotId);
  if (!item) {
    return { success: false, message: "The inventory sigil no longer exists.", affected_preset_names: [] };
  }
  if (item.equipped) {
    return { success: false, message: "This sigil is already used in body slots.", affected_preset_names: [] };
  }
  const oldOwner = item.virtual_owner_character_hash;
  const affectedPresetNames = mockState.presets
    .filter((preset) => preset.slots.includes(inventorySlotId))
    .map((preset) => preset.name);
  for (const preset of mockState.presets) {
    preset.slots = preset.slots.map((slotId) => (slotId === inventorySlotId ? 0 : slotId));
  }
  for (const slots of mockState.selection.values()) {
    for (let index = 0; index < slots.length; index += 1) {
      if (slots[index] === inventorySlotId) slots[index] = 0;
    }
  }
  const selection = mockState.selection.get(characterHash) ?? Array(24).fill(0);
  selection[virtualSlot] = inventorySlotId;
  mockState.selection.set(characterHash, selection);
  item.virtual_owner_character_hash = characterHash;
  item.virtual_owner_slot = virtualSlot;
  for (const candidate of mockState.inventory) {
    if (candidate.gem.slot_id !== inventorySlotId && candidate.virtual_owner_character_hash === characterHash && candidate.virtual_owner_slot === virtualSlot) {
      candidate.virtual_owner_character_hash = 0;
      candidate.virtual_owner_slot = 0;
    }
  }
  mockState.inventoryRevision += 1;
  updatePresetReferences();
  return {
    success: true,
    message: oldOwner !== 0 ? "Sigil moved and related preset references were cleared." : "Sigil assigned.",
    affected_preset_names: affectedPresetNames,
  };
}

const mockApi: StandaloneApi = {
  async listGameProcesses() {
    return clone(mockProcessList());
  },
  async connectGame(pid) {
    const process = mockProcessList().find((candidate) => candidate.pid === pid);
    if (!process) throw new Error("The selected game process is no longer available.");
    if (mockState.connectionFailuresRemaining > 0) {
      mockState.connectionFailuresRemaining -= 1;
      process.agent_loaded = true;
      throw new Error("Agent pipe is not ready yet.");
    }
    mockState.connectedProcess = process;
    return clone(mockConnection());
  },
  async disconnectGame() {
    mockState.connectedProcess = undefined;
  },
  async getDashboard() {
    if (!mockState.connectedProcess) throw new Error("No game process is connected.");
    return clone(mockDashboard());
  },
  async refreshInventory() {
    if (mockState.inventoryRefreshFailuresRemaining > 0) {
      mockState.inventoryRefreshFailuresRemaining -= 1;
      throw new Error("Inventory is not ready yet.");
    }
    updatePresetReferences();
    return clone(mockState.inventory);
  },
  async getSelection(characterHash) {
    return clone(mockState.selection.get(characterHash) ?? Array(24).fill(0));
  },
  async assignInventorySigil(characterHash, virtualSlot, inventorySlotId) {
    return clone(mockAssign(characterHash, virtualSlot, inventorySlotId));
  },
  async clearVirtualSlot(characterHash, virtualSlot) {
    if (!mockState.editAllowed) {
      return { success: false, message: "The current state is read-only.", affected_preset_names: [] };
    }
    const slots = mockState.selection.get(characterHash) ?? Array(24).fill(0);
    const inventorySlotId = slots[virtualSlot] ?? 0;
    slots[virtualSlot] = 0;
    mockState.selection.set(characterHash, slots);
    const item = findInventory(inventorySlotId);
    if (item) {
      item.virtual_owner_character_hash = 0;
      item.virtual_owner_slot = 0;
    }
    mockState.inventoryRevision += 1;
    return { success: true, message: "Slot cleared.", affected_preset_names: [] };
  },
  async setLanguage(language) {
    mockState.language = language;
    return clone(mockDashboard());
  },
  async requestVirtualSlotCount(slotCount) {
    if (slotCount < 1 || slotCount > 24) {
      return { status: "failed", pending_virtual_slot_count: mockState.pendingVirtualSlotCount, message: "Slot count must be between 1 and 24." };
    }
    if (slotCount === mockState.virtualSlotCount) {
      mockState.pendingVirtualSlotCount = 0;
      return { status: "cleared", pending_virtual_slot_count: 0, message: "Pending slot-count change cleared." };
    }
    mockState.pendingVirtualSlotCount = slotCount;
    return { status: "pending", pending_virtual_slot_count: slotCount, message: `Saved. ${slotCount} slots will take effect after restart.` };
  },
  async listPresets() {
    return getMockPresets();
  },
  async createPreset(characterHash, name) {
    if (mockState.presets.some((preset) => preset.character_hash === characterHash && preset.name.toLowerCase() === name.toLowerCase())) {
      throw new Error("This character already has a preset with that name.");
    }
    const preset: SigilPreset = {
      id: `preset-${Date.now()}`,
      name,
      character_hash: characterHash,
      slots: clone(mockState.selection.get(characterHash) ?? Array(24).fill(0)),
    };
    mockState.presets.push(preset);
    updatePresetReferences();
    return clone(preset);
  },
  async overwritePreset(presetId) {
    const preset = mockState.presets.find((candidate) => candidate.id === presetId);
    if (!preset) throw new Error("The preset no longer exists.");
    preset.slots = clone(mockState.selection.get(preset.character_hash) ?? Array(24).fill(0));
    updatePresetReferences();
    return clone(preset);
  },
  async renamePreset(presetId, name) {
    const preset = mockState.presets.find((candidate) => candidate.id === presetId);
    if (!preset) throw new Error("The preset no longer exists.");
    if (mockState.presets.some((candidate) => candidate.id !== presetId && candidate.character_hash === preset.character_hash && candidate.name.toLowerCase() === name.toLowerCase())) {
      throw new Error("This character already has a preset with that name.");
    }
    preset.name = name;
    updatePresetReferences();
    return clone(preset);
  },
  async deletePreset(presetId) {
    const index = mockState.presets.findIndex((candidate) => candidate.id === presetId);
    if (index < 0) throw new Error("The preset no longer exists.");
    mockState.presets.splice(index, 1);
    return getMockPresets();
  },
  async transferPreset(presetId, targetCharacterHash) {
    const preset = mockState.presets.find((candidate) => candidate.id === presetId);
    if (!preset) throw new Error("The preset no longer exists.");
    if (mockState.presets.some((candidate) => candidate.character_hash === targetCharacterHash && candidate.name.toLowerCase() === preset.name.toLowerCase())) {
      throw new Error("The target character already has a preset with that name.");
    }
    preset.character_hash = targetCharacterHash;
    return clone(preset);
  },
  async applyPreset(presetId, characterHash) {
    const preset = mockState.presets.find((candidate) => candidate.id === presetId);
    if (!preset) throw new Error("The preset no longer exists.");
    if (preset.character_hash !== characterHash) throw new Error("The preset belongs to another character.");
    if (!mockState.editAllowed) throw new Error("The current state is read-only.");
    const conflicts: PresetApplySummary["conflicts"] = [];
    let appliedCount = 0;
    const slots = mockState.selection.get(characterHash) ?? Array(24).fill(0);
    for (let index = 0; index < mockState.virtualSlotCount; index += 1) {
      const requestedSlotId = preset.slots[index] ?? 0;
      if (requestedSlotId === 0) {
        slots[index] = 0;
        continue;
      }
      const item = findInventory(requestedSlotId);
      if (!item) {
        conflicts.push({ character_hash: characterHash, virtual_slot: index, requested_slot_id: requestedSlotId, owner_character_hash: 0, status: "missing" });
      } else if (item.equipped) {
        conflicts.push({ character_hash: characterHash, virtual_slot: index, requested_slot_id: requestedSlotId, owner_character_hash: item.gem.worn_by, status: "equipped" });
      } else {
        slots[index] = requestedSlotId;
        appliedCount += 1;
      }
    }
    mockState.selection.set(characterHash, slots);
    for (const item of mockState.inventory) {
      if (item.virtual_owner_character_hash === characterHash) {
        item.virtual_owner_character_hash = 0;
        item.virtual_owner_slot = 0;
      }
    }
    for (let index = 0; index < mockState.virtualSlotCount; index += 1) {
      const item = findInventory(slots[index] ?? 0);
      if (item) {
        item.virtual_owner_character_hash = characterHash;
        item.virtual_owner_slot = index;
      }
    }
    mockState.inventoryRevision += 1;
    return { applied_count: appliedCount, requested_count: preset.slots.slice(0, mockState.virtualSlotCount).filter((slotId) => slotId !== 0).length, conflicts };
  },
};

export const api: StandaloneApi = {
  listGameProcesses: () => (isTauriRuntime() ? invokeCommand<GameProcess[]>("list_game_processes") : mockApi.listGameProcesses()),
  connectGame: (pid) => (isTauriRuntime() ? invokeCommand<ConnectionInfo>("connect_game", { pid }) : mockApi.connectGame(pid)),
  disconnectGame: () => (isTauriRuntime() ? invokeCommand<void>("disconnect_game") : mockApi.disconnectGame()),
  getDashboard: () => (isTauriRuntime() ? invokeCommand<Dashboard>("get_dashboard") : mockApi.getDashboard()),
  refreshInventory: () => (isTauriRuntime() ? invokeCommand<InventoryItem[]>("refresh_inventory") : mockApi.refreshInventory()),
  getSelection: (characterHash) => (isTauriRuntime() ? invokeCommand<number[]>("get_selection", { characterHash }) : mockApi.getSelection(characterHash)),
  assignInventorySigil: (characterHash, virtualSlot, inventorySlotId) => (isTauriRuntime() ? invokeCommand<AssignResponse>("assign_inventory_sigil", { characterHash, virtualSlot, inventorySlotId }) : mockApi.assignInventorySigil(characterHash, virtualSlot, inventorySlotId)),
  clearVirtualSlot: (characterHash, virtualSlot) => (isTauriRuntime() ? invokeCommand<AssignResponse>("clear_virtual_slot", { characterHash, virtualSlot }) : mockApi.clearVirtualSlot(characterHash, virtualSlot)),
  setLanguage: (language) => (isTauriRuntime() ? invokeCommand<Dashboard>("set_language", { language }) : mockApi.setLanguage(language)),
  requestVirtualSlotCount: (slotCount) => (isTauriRuntime() ? invokeCommand<SlotCountRequestResult>("request_virtual_slot_count", { slotCount }) : mockApi.requestVirtualSlotCount(slotCount)),
  listPresets: () => (isTauriRuntime() ? invokeCommand<PresetDocument>("list_presets") : mockApi.listPresets()),
  createPreset: (characterHash, name) => (isTauriRuntime() ? invokeCommand<SigilPreset>("create_preset", { characterHash, name }) : mockApi.createPreset(characterHash, name)),
  overwritePreset: (presetId) => (isTauriRuntime() ? invokeCommand<SigilPreset>("overwrite_preset", { presetId }) : mockApi.overwritePreset(presetId)),
  renamePreset: (presetId, name) => (isTauriRuntime() ? invokeCommand<SigilPreset>("rename_preset", { presetId, name }) : mockApi.renamePreset(presetId, name)),
  deletePreset: (presetId) => (isTauriRuntime() ? invokeCommand<PresetDocument>("delete_preset", { presetId }) : mockApi.deletePreset(presetId)),
  transferPreset: (presetId, targetCharacterHash) => (isTauriRuntime() ? invokeCommand<SigilPreset>("transfer_preset", { presetId, targetCharacterHash }) : mockApi.transferPreset(presetId, targetCharacterHash)),
  applyPreset: (presetId, characterHash) => (isTauriRuntime() ? invokeCommand<PresetApplySummary>("apply_preset", { presetId, characterHash }) : mockApi.applyPreset(presetId, characterHash)),
};

export const mockControls = {
  reset(): void {
    mockState = createMockState();
    mockProcesses = createMockProcessList();
  },
  setEditAllowed(editAllowed: boolean): void {
    mockState.editAllowed = editAllowed;
  },
  setDetectedProcessCount(count: number): void {
    mockProcesses = createMockProcessList().slice(0, count);
  },
  failNextConnections(count: number): void {
    mockState.connectionFailuresRemaining = Math.max(0, count);
  },
  failNextInventoryRefreshes(count: number): void {
    mockState.inventoryRefreshFailuresRemaining = Math.max(0, count);
  },
};

export function characterOption(hash: number): CharacterOption {
  return CHARACTERS.find((character) => character.hash === hash) ?? CHARACTERS[0];
}
