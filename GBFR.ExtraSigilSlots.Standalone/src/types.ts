export type Language = "zh-CN" | "en";

export type InventoryFilter = "all" | "used" | "body" | "extension" | "unused";

export type PresetDialogMode = "create" | "rename";

export interface GameProcess {
  pid: number;
  executable_name: string;
  executable_path?: string;
  agent_loaded: boolean;
}

export interface ConnectionInfo {
  pid: number;
  process_name: string;
  injected: boolean;
  protocol_version: number;
  native_abi_version: number;
}

export interface Dashboard {
  connection: ConnectionInfo;
  initialized: boolean;
  hooks_ready: boolean;
  runtime_message: string;
  runtime_message_is_error: boolean;
  effective_character_hash: number;
  ui_selected_character_hash: number;
  edit_allowed: boolean;
  language: Language;
  inventory_revision: number;
  inventory_dirty: boolean;
  game_data_ready: boolean;
  virtual_slot_count: number;
  virtual_slot_capacity: number;
  pending_virtual_slot_count: number;
}

export interface GemData {
  trait1: number;
  trait1_level: number;
  trait2: number;
  trait2_level: number;
  gem_id: number;
  worn_by: number;
  sigil_level: number;
  slot_id: number;
  flags: number;
}

export interface InventoryItem {
  gem: GemData;
  label: string;
  searchable: string;
  equipped: boolean;
  required_character_hash: number;
  virtual_owner_character_hash: number;
  virtual_owner_slot: number;
  preset_names: string[];
}

export interface AssignResult {
  success: boolean;
  message: string;
  affected_preset_names: string[];
}

export interface PresetDocument {
  version: number;
  presets: SigilPreset[];
}

export interface SigilPreset {
  id: string;
  name: string;
  character_hash: number;
  slots: number[];
}

export type PresetSlotStatus =
  | "empty"
  | "applied"
  | "missing"
  | "equipped"
  | "disabled"
  | "character_restricted"
  | "duplicate";

export interface PresetSlotResult {
  character_hash: number;
  virtual_slot: number;
  requested_slot_id: number;
  owner_character_hash: number;
  status: PresetSlotStatus;
}

export interface PresetApplySummary {
  applied_count: number;
  requested_count: number;
  conflicts: PresetSlotResult[];
}

export interface SlotCountRequestResult {
  status: "pending" | "cleared" | "failed";
  pending_virtual_slot_count: number;
  message: string;
}

export interface CharacterOption {
  hash: number;
  zh: string;
  en: string;
}

export const CHARACTERS: CharacterOption[] = [
  { hash: 0x2a26b1b2, zh: "主角（格兰/姬塔）", en: "Captain (Gran/Djeeta)" },
  { hash: 0x18e2f9f9, zh: "卡塔莉娜", en: "Katalina" },
  { hash: 0x079df0cc, zh: "拉卡姆", en: "Rackam" },
  { hash: 0x4d0a60c3, zh: "伊欧", en: "Io" },
  { hash: 0xdd7a151e, zh: "欧根", en: "Eugen" },
  { hash: 0xc8616284, zh: "萝赛塔", en: "Rosetta" },
  { hash: 0xc3ffd418, zh: "菲莉", en: "Ferry" },
  { hash: 0x22e437e5, zh: "兰斯洛特", en: "Lancelot" },
  { hash: 0x2ebe91d5, zh: "巴恩", en: "Vane" },
  { hash: 0xbdef7181, zh: "珀西瓦尔", en: "Percival" },
  { hash: 0x627bcb0d, zh: "齐格飞", en: "Siegfried" },
  { hash: 0xfd3be362, zh: "夏洛特", en: "Charlotta" },
  { hash: 0xfc6cdf7b, zh: "尤达拉哈", en: "Yodarha" },
  { hash: 0xe7053919, zh: "娜露梅", en: "Narmaya" },
  { hash: 0x978e4b18, zh: "冈达葛萨", en: "Ghandagoza" },
  { hash: 0x0d21b430, zh: "塞达", en: "Zeta" },
  { hash: 0xf0eb77ef, zh: "巴萨拉卡", en: "Vaseraga" },
  { hash: 0xaa66178a, zh: "卡莉奥丝特罗", en: "Cagliostro" },
  { hash: 0xa3a3cb2f, zh: "伊德", en: "Id" },
  { hash: 0x718e1a14, zh: "圣德芬", en: "Sandalphon" },
  { hash: 0x296471be, zh: "希耶提", en: "Seofon" },
  { hash: 0xbad16e3b, zh: "索恩", en: "Tweyen" },
  { hash: 0x1bb37ef0, zh: "伽兰查", en: "Gallanza" },
  { hash: 0x25d46f4b, zh: "玛琪拉菲菈", en: "Maglielle" },
  { hash: 0x9a8af295, zh: "贝阿朵丽丝", en: "Beatrix" },
  { hash: 0x9b15cfb1, zh: "尤斯提斯", en: "Eustace" },
  { hash: 0x646c3168, zh: "芙劳", en: "Fraux" },
  { hash: 0x74dd4c79, zh: "菲迪埃尔", en: "Fediel" },
];

export function getCharacter(hash: number): CharacterOption | undefined {
  return CHARACTERS.find((character) => character.hash === hash);
}

export function characterName(hash: number, language: Language): string {
  if (hash === 0) {
    return language === "en" ? "No current character detected" : "未检测到当前角色";
  }
  const character = getCharacter(hash);
  if (!character) {
    return language === "en" ? "Unknown character" : "未知角色";
  }
  return language === "en" ? character.en : character.zh;
}
