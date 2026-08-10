use std::collections::{HashMap, HashSet};
use std::ffi::OsStr;
use std::fs::{self, OpenOptions};
use std::io::Write;
use std::os::windows::ffi::OsStrExt;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

use serde::de::Error as _;
use serde::{Deserialize, Serialize};
use serde_json::Value;
use windows_sys::Win32::Storage::FileSystem::{
    MoveFileExW, MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH,
};

pub const PRESET_SCHEMA_VERSION: u32 = 3;
pub const SLOT_CAPACITY: usize = 24;
pub const MAX_PRESET_NAME_CHARS: usize = 48;

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct PresetDocument {
    pub schema_version: u32,
    pub characters: Vec<PresetCharacter>,
}

impl Default for PresetDocument {
    fn default() -> Self {
        Self::empty()
    }
}

impl PresetDocument {
    pub fn empty() -> Self {
        Self {
            schema_version: PRESET_SCHEMA_VERSION,
            characters: Vec::new(),
        }
    }
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct PresetCharacter {
    pub character_hash: u32,
    pub presets: Vec<Preset>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub active_name: Option<String>,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct Preset {
    pub name: String,
    pub slots: [u32; SLOT_CAPACITY],
}

#[derive(Debug, Clone)]
pub struct PresetStore {
    path: PathBuf,
    document: PresetDocument,
}

#[allow(dead_code)]
impl PresetStore {
    pub fn empty(path: impl AsRef<Path>) -> Self {
        Self {
            path: path.as_ref().to_path_buf(),
            document: PresetDocument::empty(),
        }
    }

    pub fn load(path: impl AsRef<Path>) -> Result<Self, String> {
        let path = path.as_ref().to_path_buf();
        let bytes = match fs::read(&path) {
            Ok(bytes) => bytes,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                return Ok(Self {
                    path,
                    document: PresetDocument::empty(),
                });
            }
            Err(error) => {
                return Err(format!(
                    "could not read preset file {}: {error}",
                    path.display()
                ));
            }
        };

        let document = match serde_json::from_slice::<PresetDocument>(&bytes) {
            Ok(document) => document,
            Err(_) => {
                backup_original(&path, &bytes, "invalid").map_err(|backup_error| {
                    format!(
                        "preset file {} is invalid and could not be backed up: {backup_error}",
                        path.display()
                    )
                })?;
                return Ok(Self {
                    path,
                    document: PresetDocument::empty(),
                });
            }
        };

        let legacy_root = has_legacy_root(&bytes);
        let canonical = serde_json::to_vec_pretty(&document)
            .map_err(|error| format!("could not serialize preset document: {error}"))?;
        if legacy_root || canonical != bytes {
            let reason = if legacy_root {
                "pre-v3"
            } else {
                "pre-normalize-v3"
            };
            backup_original(&path, &bytes, reason).map_err(|backup_error| {
                format!(
                    "preset file {} could not be backed up before migration: {backup_error}",
                    path.display()
                )
            })?;
            let store = Self { path, document };
            store.save_document()?;
            Ok(store)
        } else {
            Ok(Self { path, document })
        }
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    pub fn document(&self) -> &PresetDocument {
        &self.document
    }

    pub fn list(&self, character_hash: u32) -> &[Preset] {
        self.character(character_hash)
            .map(|character| character.presets.as_slice())
            .unwrap_or(&[])
    }

    pub fn get(&self, character_hash: u32, name: &str) -> Option<&Preset> {
        let name = trim_name(name)?;
        self.character(character_hash)
            .and_then(|character| find_preset(character, name))
    }

    pub fn active_name(&self, character_hash: u32) -> Option<&str> {
        self.character(character_hash)
            .and_then(|character| character.active_name.as_deref())
    }

    pub fn current_name(&self, character_hash: u32) -> Option<&str> {
        self.active_name(character_hash)
    }

    pub fn create(
        &mut self,
        character_hash: u32,
        name: &str,
        slots: [u32; SLOT_CAPACITY],
    ) -> Result<Preset, String> {
        validate_character(character_hash)?;
        let name = validate_name(name)?;
        let slots = normalize_slots(slots.to_vec());
        self.with_transaction(|document| {
            if let Some(character) = find_character_mut(document, character_hash) {
                if name_exists(character, &name, None) {
                    return Err(format!(
                        "a preset named '{name}' already exists for character {character_hash:08X}"
                    ));
                }
                let preset = Preset {
                    name: name.clone(),
                    slots,
                };
                character.presets.push(preset.clone());
                Ok(preset)
            } else {
                let preset = Preset {
                    name: name.clone(),
                    slots,
                };
                document.characters.push(PresetCharacter {
                    character_hash,
                    presets: vec![preset.clone()],
                    active_name: None,
                });
                Ok(preset)
            }
        })
    }

    pub fn overwrite(
        &mut self,
        character_hash: u32,
        name: &str,
        slots: [u32; SLOT_CAPACITY],
    ) -> Result<Preset, String> {
        validate_character(character_hash)?;
        let name = validate_name(name)?;
        let slots = normalize_slots(slots.to_vec());
        self.with_transaction(|document| {
            let character = find_character_mut(document, character_hash)
                .ok_or_else(|| format!("character {character_hash:08X} has no presets"))?;
            let preset = find_preset_mut(character, &name)
                .ok_or_else(|| format!("preset '{name}' was not found"))?;
            preset.slots = slots;
            Ok(preset.clone())
        })
    }

    pub fn rename(
        &mut self,
        character_hash: u32,
        current_name: &str,
        new_name: &str,
    ) -> Result<Preset, String> {
        validate_character(character_hash)?;
        let current_name = validate_name(current_name)?;
        let new_name = validate_name(new_name)?;
        self.with_transaction(|document| {
            let character = find_character_mut(document, character_hash)
                .ok_or_else(|| format!("character {character_hash:08X} has no presets"))?;
            let preset_index = character
                .presets
                .iter()
                .position(|preset| same_name(&preset.name, &current_name))
                .ok_or_else(|| format!("preset '{current_name}' was not found"))?;
            if name_exists(character, &new_name, Some(&current_name)) {
                return Err(format!(
                    "a preset named '{new_name}' already exists for character {character_hash:08X}"
                ));
            }
            let old_name = character.presets[preset_index].name.clone();
            character.presets[preset_index].name = new_name.clone();
            if character
                .active_name
                .as_deref()
                .is_some_and(|active| same_name(active, &old_name))
            {
                character.active_name = Some(new_name.clone());
            }
            Ok(character.presets[preset_index].clone())
        })
    }

    pub fn delete(&mut self, character_hash: u32, name: &str) -> Result<Preset, String> {
        validate_character(character_hash)?;
        let name = validate_name(name)?;
        self.with_transaction(|document| {
            let character = find_character_mut(document, character_hash)
                .ok_or_else(|| format!("character {character_hash:08X} has no presets"))?;
            let index = character
                .presets
                .iter()
                .position(|preset| same_name(&preset.name, &name))
                .ok_or_else(|| format!("preset '{name}' was not found"))?;
            let deleted = character.presets.remove(index);
            if character
                .active_name
                .as_deref()
                .is_some_and(|active| same_name(active, &deleted.name))
            {
                character.active_name = None;
            }
            Ok(deleted)
        })
    }

    pub fn transfer(
        &mut self,
        from_character: u32,
        name: &str,
        to_character: u32,
        new_name: &str,
        remove_source: bool,
    ) -> Result<Preset, String> {
        validate_character(from_character)?;
        validate_character(to_character)?;
        if from_character == to_character {
            return Err("source and target characters must differ".to_string());
        }
        let name = validate_name(name)?;
        let new_name = validate_name(new_name)?;
        self.with_transaction(|document| {
            let source_index = document
                .characters
                .iter()
                .position(|character| character.character_hash == from_character)
                .ok_or_else(|| format!("character {from_character:08X} has no presets"))?;
            let preset_index = document.characters[source_index]
                .presets
                .iter()
                .position(|preset| same_name(&preset.name, &name))
                .ok_or_else(|| format!("preset '{name}' was not found"))?;

            if let Some(target_index) = document
                .characters
                .iter()
                .position(|character| character.character_hash == to_character)
            {
                if name_exists(&document.characters[target_index], &new_name, None) {
                    return Err(format!(
                        "a preset named '{new_name}' already exists for character {to_character:08X}"
                    ));
                }
            }

            let mut transferred = document.characters[source_index].presets[preset_index].clone();
            transferred.name = new_name.clone();
            if remove_source {
                document.characters[source_index].presets.remove(preset_index);
                if document.characters[source_index]
                    .active_name
                    .as_deref()
                    .is_some_and(|active| same_name(active, &name))
                {
                    document.characters[source_index].active_name = None;
                }
            }

            if let Some(target_index) = document
                .characters
                .iter()
                .position(|character| character.character_hash == to_character)
            {
                document.characters[target_index].presets.push(transferred.clone());
            } else {
                document.characters.push(PresetCharacter {
                    character_hash: to_character,
                    presets: vec![transferred.clone()],
                    active_name: None,
                });
            }
            Ok(transferred)
        })
    }

    pub fn set_active(&mut self, character_hash: u32, name: &str) -> Result<(), String> {
        validate_character(character_hash)?;
        let name = validate_name(name)?;
        self.with_transaction(|document| {
            let character = find_character_mut(document, character_hash)
                .ok_or_else(|| format!("character {character_hash:08X} has no presets"))?;
            if !character
                .presets
                .iter()
                .any(|preset| same_name(&preset.name, &name))
            {
                return Err(format!("preset '{name}' was not found"));
            }
            character.active_name = Some(name);
            Ok(())
        })
    }

    pub fn snapshot(&self) -> PresetDocument {
        self.document.clone()
    }

    pub fn restore(&mut self, snapshot: PresetDocument) -> Result<(), String> {
        let mut snapshot = snapshot;
        normalize_document(&mut snapshot);
        self.with_transaction(|document| {
            *document = snapshot;
            Ok(())
        })
    }

    pub fn mutate<T>(
        &mut self,
        mutation: impl FnOnce(&mut PresetDocument) -> Result<T, String>,
    ) -> Result<T, String> {
        self.with_transaction(mutation)
    }

    fn character(&self, character_hash: u32) -> Option<&PresetCharacter> {
        self.document
            .characters
            .iter()
            .find(|character| character.character_hash == character_hash)
    }

    fn with_transaction<T>(
        &mut self,
        mutation: impl FnOnce(&mut PresetDocument) -> Result<T, String>,
    ) -> Result<T, String> {
        let backup = self.document.clone();
        let result = mutation(&mut self.document);
        let value = match result {
            Ok(value) => value,
            Err(error) => {
                self.document = backup;
                return Err(error);
            }
        };
        normalize_document(&mut self.document);
        if let Err(error) = self.save_document() {
            self.document = backup;
            return Err(error);
        }
        Ok(value)
    }

    fn save_document(&self) -> Result<(), String> {
        if let Some(parent) = self.path.parent() {
            if !parent.as_os_str().is_empty() {
                fs::create_dir_all(parent).map_err(|error| {
                    format!(
                        "could not create preset directory {}: {error}",
                        parent.display()
                    )
                })?;
            }
        }
        let json = serde_json::to_vec_pretty(&self.document)
            .map_err(|error| format!("could not serialize preset document: {error}"))?;

        for attempt in 0..8u32 {
            let temporary = temporary_path(&self.path, attempt);
            let write_result = (|| -> std::io::Result<()> {
                let mut file = OpenOptions::new()
                    .write(true)
                    .create_new(true)
                    .open(&temporary)?;
                file.write_all(&json)?;
                file.flush()?;
                file.sync_all()?;
                drop(file);
                atomic_replace(&temporary, &self.path)?;
                Ok(())
            })();
            match write_result {
                Ok(()) => return Ok(()),
                Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists && attempt < 7 => {
                    continue;
                }
                Err(error) => {
                    let _ = fs::remove_file(&temporary);
                    return Err(format!(
                        "could not save preset file {}: {error}",
                        self.path.display()
                    ));
                }
            }
        }
        Err("could not create a temporary preset file".to_string())
    }
}

fn atomic_replace(source: &Path, destination: &Path) -> std::io::Result<()> {
    let source = source
        .as_os_str()
        .encode_wide()
        .chain(Some(0))
        .collect::<Vec<_>>();
    let destination = destination
        .as_os_str()
        .encode_wide()
        .chain(Some(0))
        .collect::<Vec<_>>();
    if unsafe {
        MoveFileExW(
            source.as_ptr(),
            destination.as_ptr(),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
        )
    } == 0
    {
        Err(std::io::Error::last_os_error())
    } else {
        Ok(())
    }
}
impl<'de> Deserialize<'de> for PresetDocument {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: serde::Deserializer<'de>,
    {
        let value = Value::deserialize(deserializer)?;
        parse_document(&value).map_err(D::Error::custom)
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct CurrentDocument {
    #[serde(rename = "schemaVersion", alias = "schema_version")]
    schema_version: u32,
    #[serde(rename = "characters", alias = "Characters")]
    characters: Vec<CurrentCharacter>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct CurrentCharacter {
    #[serde(
        rename = "characterHash",
        alias = "character_hash",
        alias = "CharacterHash"
    )]
    character_hash: u32,
    #[serde(rename = "presets", alias = "Presets", default)]
    presets: Vec<CurrentPreset>,
    #[serde(
        rename = "activeName",
        alias = "currentName",
        alias = "active_name",
        alias = "current_name",
        default
    )]
    active_name: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct CurrentPreset {
    #[serde(rename = "name", alias = "Name", default)]
    name: String,
    #[serde(rename = "slots", alias = "Slots", default)]
    slots: Vec<u32>,
}

#[derive(Debug, Deserialize)]
struct LegacyDocument {
    #[serde(rename = "Version", alias = "version")]
    version: u32,
    #[serde(rename = "Presets", alias = "presets", default)]
    presets: Option<Vec<LegacyPreset>>,
}

#[derive(Debug, Deserialize)]
struct LegacyPreset {
    #[serde(rename = "Name", alias = "name", default)]
    name: Option<String>,
    #[serde(rename = "CharacterHash", alias = "character_hash", default)]
    character_hash: Option<u32>,
    #[serde(rename = "Slots", alias = "slots", default)]
    slots: Option<Vec<u32>>,
    #[serde(rename = "Characters", alias = "characters", default)]
    characters: Option<Vec<LegacyCharacter>>,
}

#[derive(Debug, Deserialize)]
struct LegacyCharacter {
    #[serde(rename = "CharacterHash", alias = "character_hash", default)]
    character_hash: u32,
    #[serde(rename = "Slots", alias = "slots", default)]
    slots: Option<Vec<u32>>,
}

fn parse_document(value: &Value) -> Result<PresetDocument, String> {
    let object = value
        .as_object()
        .ok_or_else(|| "preset document must be a JSON object".to_string())?;

    if object.contains_key("schemaVersion") || object.contains_key("schema_version") {
        let current: CurrentDocument = serde_json::from_value(value.clone())
            .map_err(|error| format!("invalid current preset schema: {error}"))?;
        if current.schema_version > PRESET_SCHEMA_VERSION {
            return Err(format!(
                "unsupported preset schema version {}",
                current.schema_version
            ));
        }
        let mut document = PresetDocument {
            schema_version: current.schema_version,
            characters: current
                .characters
                .into_iter()
                .map(|character| PresetCharacter {
                    character_hash: character.character_hash,
                    presets: character
                        .presets
                        .into_iter()
                        .map(|preset| Preset {
                            name: preset.name,
                            slots: normalize_slots(preset.slots),
                        })
                        .collect(),
                    active_name: character.active_name,
                })
                .collect(),
        };
        normalize_document(&mut document);
        Ok(document)
    } else if object.contains_key("Version") || object.contains_key("version") {
        let legacy: LegacyDocument = serde_json::from_value(value.clone())
            .map_err(|error| format!("invalid legacy preset schema: {error}"))?;
        if legacy.version > PRESET_SCHEMA_VERSION {
            return Err(format!(
                "unsupported preset schema version {}",
                legacy.version
            ));
        }
        let mut document = PresetDocument {
            schema_version: PRESET_SCHEMA_VERSION,
            characters: migrate_legacy_document(legacy.presets.unwrap_or_default()),
        };
        normalize_document(&mut document);
        Ok(document)
    } else {
        Err("preset document must contain schemaVersion/characters or Version/Presets".to_string())
    }
}

fn migrate_legacy_document(presets: Vec<LegacyPreset>) -> Vec<PresetCharacter> {
    let mut characters: Vec<PresetCharacter> = Vec::new();
    for legacy in presets {
        let name = legacy.name.unwrap_or_default();
        if let Some(legacy_characters) = legacy.characters {
            let mut by_hash: HashMap<u32, [u32; SLOT_CAPACITY]> = HashMap::new();
            for character in legacy_characters {
                if character.character_hash == 0 {
                    continue;
                }
                by_hash.insert(
                    character.character_hash,
                    normalize_slots(character.slots.unwrap_or_default()),
                );
            }

            let mut hashes: Vec<u32> = by_hash.keys().copied().collect();
            hashes.sort_unstable();
            let mut claimed_slot_ids = HashSet::new();
            let mut added_non_empty = false;
            for hash in hashes.iter().copied() {
                let mut slots = by_hash[&hash];
                for slot_id in slots.iter_mut() {
                    if *slot_id != 0 && !claimed_slot_ids.insert(*slot_id) {
                        *slot_id = 0;
                    }
                }
                if slots.iter().any(|slot_id| *slot_id != 0) {
                    push_preset(&mut characters, hash, name.clone(), slots);
                    added_non_empty = true;
                }
            }
            if !added_non_empty && !hashes.is_empty() {
                push_preset(&mut characters, hashes[0], name, by_hash[&hashes[0]]);
            }
        } else if let Some(character_hash) = legacy.character_hash {
            if character_hash != 0 {
                push_preset(
                    &mut characters,
                    character_hash,
                    name,
                    normalize_slots(legacy.slots.unwrap_or_default()),
                );
            }
        }
    }
    characters
}

fn push_preset(
    characters: &mut Vec<PresetCharacter>,
    character_hash: u32,
    name: String,
    slots: [u32; SLOT_CAPACITY],
) {
    let preset = Preset { name, slots };
    if let Some(character) = characters
        .iter_mut()
        .find(|character| character.character_hash == character_hash)
    {
        character.presets.push(preset);
    } else {
        characters.push(PresetCharacter {
            character_hash,
            presets: vec![preset],
            active_name: None,
        });
    }
}

fn normalize_document(document: &mut PresetDocument) {
    document.schema_version = PRESET_SCHEMA_VERSION;

    let mut merged: Vec<PresetCharacter> = Vec::new();
    for character in document.characters.drain(..) {
        if character.character_hash == 0 {
            continue;
        }
        if let Some(existing) = merged
            .iter_mut()
            .find(|existing| existing.character_hash == character.character_hash)
        {
            existing.presets.extend(character.presets);
            if existing.active_name.is_none() {
                existing.active_name = character.active_name;
            }
        } else {
            merged.push(character);
        }
    }

    let mut names_by_character: HashMap<u32, HashSet<String>> = HashMap::new();
    for character in merged.iter_mut() {
        let names = names_by_character
            .entry(character.character_hash)
            .or_default();
        for (index, preset) in character.presets.iter_mut().enumerate() {
            preset.name = unique_name(&preset.name, index, names);
            preset.slots = normalize_slots(preset.slots.to_vec());
        }

        if let Some(active_name) = character.active_name.take() {
            let trimmed = active_name.trim();
            if !trimmed.is_empty() {
                if let Some(preset) = character
                    .presets
                    .iter()
                    .find(|preset| same_name(&preset.name, trimmed))
                {
                    character.active_name = Some(preset.name.clone());
                }
            }
        }
    }

    merged.sort_by_key(|character| character.character_hash);
    document.characters = merged;
}

fn unique_name(name: &str, index: usize, names: &mut HashSet<String>) -> String {
    let mut base = name.trim().to_string();
    if base.is_empty() {
        base = format!("Preset {}", index + 1);
    }
    base = truncate_chars(&base, MAX_PRESET_NAME_CHARS);
    if names.insert(normalize_case(&base)) {
        return base;
    }

    for suffix in 2.. {
        let suffix_text = format!(" ({suffix})");
        let max_base = MAX_PRESET_NAME_CHARS.saturating_sub(suffix_text.chars().count());
        let base = truncate_chars(&base, max_base);
        let candidate = format!("{base}{suffix_text}");
        if names.insert(normalize_case(&candidate)) {
            return candidate;
        }
    }
    unreachable!("unique preset name suffix loop is infinite by construction")
}

fn normalize_slots(slots: Vec<u32>) -> [u32; SLOT_CAPACITY] {
    let mut normalized = [0u32; SLOT_CAPACITY];
    let mut seen = HashSet::new();
    for (index, slot_id) in slots.into_iter().take(SLOT_CAPACITY).enumerate() {
        if slot_id != 0 && !seen.insert(slot_id) {
            continue;
        }
        normalized[index] = slot_id;
    }
    normalized
}

fn validate_character(character_hash: u32) -> Result<(), String> {
    if character_hash == 0 {
        Err("preset character hash cannot be zero".to_string())
    } else {
        Ok(())
    }
}

fn validate_name(name: &str) -> Result<String, String> {
    let trimmed = name.trim();
    if trimmed.is_empty() {
        return Err("preset name cannot be empty".to_string());
    }
    let character_count = trimmed.chars().count();
    if character_count > MAX_PRESET_NAME_CHARS {
        return Err(format!(
            "preset name cannot exceed {MAX_PRESET_NAME_CHARS} characters"
        ));
    }
    Ok(trimmed.to_string())
}

fn trim_name(name: &str) -> Option<&str> {
    let trimmed = name.trim();
    if trimmed.is_empty() {
        None
    } else {
        Some(trimmed)
    }
}

fn find_character_mut(
    document: &mut PresetDocument,
    character_hash: u32,
) -> Option<&mut PresetCharacter> {
    document
        .characters
        .iter_mut()
        .find(|character| character.character_hash == character_hash)
}

#[allow(dead_code)]
fn find_preset<'a>(character: &'a PresetCharacter, name: &str) -> Option<&'a Preset> {
    character
        .presets
        .iter()
        .find(|preset| same_name(&preset.name, name))
}

fn find_preset_mut<'a>(character: &'a mut PresetCharacter, name: &str) -> Option<&'a mut Preset> {
    character
        .presets
        .iter_mut()
        .find(|preset| same_name(&preset.name, name))
}

fn name_exists(character: &PresetCharacter, name: &str, except: Option<&str>) -> bool {
    let except = except.and_then(trim_name);
    character.presets.iter().any(|preset| {
        except.is_none_or(|except| !same_name(&preset.name, except))
            && same_name(&preset.name, name)
    })
}

fn same_name(left: &str, right: &str) -> bool {
    normalize_case(left) == normalize_case(right)
}

fn normalize_case(name: &str) -> String {
    name.trim().to_lowercase()
}

fn truncate_chars(name: &str, max_chars: usize) -> String {
    name.chars().take(max_chars).collect()
}

fn has_legacy_root(bytes: &[u8]) -> bool {
    serde_json::from_slice::<Value>(bytes)
        .ok()
        .and_then(|value| value.as_object().cloned())
        .is_some_and(|object| object.contains_key("Version") || object.contains_key("version"))
}

fn content_digest(bytes: &[u8]) -> String {
    let mut hash = 0xcbf2_9ce4_8422_2325u64;
    for byte in bytes {
        hash ^= u64::from(*byte);
        hash = hash.wrapping_mul(0x0000_0100_0000_01b3);
    }
    format!("{hash:016x}")
}

fn backup_path(path: &Path, reason: &str, digest: &str) -> PathBuf {
    let mut name = path
        .file_name()
        .unwrap_or_else(|| OsStr::new("presets.json"))
        .to_os_string();
    name.push(format!(".{reason}-{digest}.bak"));
    path.with_file_name(name)
}

fn temporary_path(path: &Path, attempt: u32) -> PathBuf {
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_nanos())
        .unwrap_or(0);
    let mut name = path
        .file_name()
        .unwrap_or_else(|| OsStr::new("presets.json"))
        .to_os_string();
    name.push(format!(".tmp-{}-{nanos}-{attempt}", std::process::id()));
    path.with_file_name(name)
}

fn backup_original(path: &Path, bytes: &[u8], reason: &str) -> Result<PathBuf, String> {
    let digest = content_digest(bytes);
    let backup = backup_path(path, reason, &digest);
    match fs::read(&backup) {
        Ok(existing) if existing == bytes => return Ok(backup),
        Ok(_) => {
            return Err(format!(
                "existing preset backup {} does not match the original file",
                backup.display()
            ));
        }
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
        Err(error) => {
            return Err(format!(
                "could not read preset backup {}: {error}",
                backup.display()
            ));
        }
    }

    for attempt in 0..8u32 {
        let temporary = temporary_path(&backup, attempt);
        let write_result = (|| -> std::io::Result<()> {
            let mut file = OpenOptions::new()
                .write(true)
                .create_new(true)
                .open(&temporary)?;
            file.write_all(bytes)?;
            file.flush()?;
            file.sync_all()?;
            drop(file);
            fs::rename(&temporary, &backup)?;
            Ok(())
        })();
        match write_result {
            Ok(()) => return Ok(backup),
            Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists && attempt < 7 => {
                continue;
            }
            Err(error) => {
                let _ = fs::remove_file(&temporary);
                return Err(format!(
                    "could not write preset backup {}: {error}",
                    backup.display()
                ));
            }
        }
    }
    Err(format!(
        "could not create temporary preset backup for {}",
        backup.display()
    ))
}
#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicU64, Ordering};

    static NEXT_TEMP_ID: AtomicU64 = AtomicU64::new(0);

    struct TempDir(PathBuf);

    impl TempDir {
        fn new(label: &str) -> Self {
            let nanos = SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos();
            let id = NEXT_TEMP_ID.fetch_add(1, Ordering::Relaxed);
            let path = std::env::temp_dir().join(format!(
                "gbfres-preset-{label}-{}-{nanos}-{id}",
                std::process::id()
            ));
            fs::create_dir_all(&path).unwrap();
            Self(path)
        }

        fn path(&self) -> &Path {
            &self.0
        }
    }

    impl Drop for TempDir {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    fn preset_path(temp: &TempDir) -> PathBuf {
        temp.path().join("GBFR-ExtraSigilSlots.presets.json")
    }

    fn slots(values: &[u32]) -> [u32; SLOT_CAPACITY] {
        let mut result = [0u32; SLOT_CAPACITY];
        for (index, value) in values.iter().copied().take(SLOT_CAPACITY).enumerate() {
            result[index] = value;
        }
        result
    }

    #[test]
    fn missing_file_loads_empty_v3_document() {
        let temp = TempDir::new("missing");
        let path = preset_path(&temp);
        let store = PresetStore::load(&path).unwrap();

        assert_eq!(store.document().schema_version, PRESET_SCHEMA_VERSION);
        assert!(store.list(0x2A26B1B2).is_empty());
        assert!(!path.exists());
    }

    #[test]
    fn create_rename_overwrite_delete_and_case_collisions_are_persisted() {
        let temp = TempDir::new("crud");
        let path = preset_path(&temp);
        let mut store = PresetStore::load(&path).unwrap();
        let character = 0x2A26B1B2u32;

        let created = store
            .create(character, "  Raid  ", slots(&[111, 222]))
            .unwrap();
        assert_eq!(created.name, "Raid");
        assert_eq!(created.slots[0], 111);
        assert!(store.create(character, "raid", slots(&[333])).is_err());

        let renamed = store.rename(character, "Raid", "Safe").unwrap();
        assert_eq!(renamed.name, "Safe");
        assert!(store.create(character, "safe", slots(&[444])).is_err());
        assert!(store.rename(character, "Safe", "safe").is_ok());

        store.set_active(character, "Safe").unwrap();
        assert_eq!(store.active_name(character), Some("safe"));

        let overwritten = store
            .overwrite(character, "safe", slots(&[555, 666]))
            .unwrap();
        assert_eq!(overwritten.slots[0], 555);
        assert_eq!(overwritten.slots[1], 666);

        store.delete(character, "SAFE").unwrap();
        assert!(store.list(character).is_empty());
        assert_eq!(store.active_name(character), None);

        let reloaded = PresetStore::load(&path).unwrap();
        assert!(reloaded.list(character).is_empty());
        let value: Value = serde_json::from_str(&fs::read_to_string(&path).unwrap()).unwrap();
        assert!(value.get("schemaVersion").is_some());
        assert!(value.get("characters").is_some());
    }

    #[test]
    fn preset_name_length_uses_unicode_characters() {
        let temp = TempDir::new("name-length");
        let path = preset_path(&temp);
        let mut store = PresetStore::load(&path).unwrap();
        let character = 0x18E2F9F9u32;

        assert!(store
            .create(character, &"a".repeat(MAX_PRESET_NAME_CHARS), slots(&[]))
            .is_ok());
        assert!(store
            .create(
                character,
                &"a".repeat(MAX_PRESET_NAME_CHARS + 1),
                slots(&[])
            )
            .is_err());
        assert!(store
            .create(
                character,
                &"\u{3042}".repeat(MAX_PRESET_NAME_CHARS),
                slots(&[])
            )
            .is_ok());
        assert!(store
            .create(
                character,
                &"\u{3042}".repeat(MAX_PRESET_NAME_CHARS + 1),
                slots(&[])
            )
            .is_err());
    }

    #[test]
    fn current_schema_is_normalized_and_backed_up() {
        let temp = TempDir::new("current");
        let path = preset_path(&temp);
        let character = 0x4D0A60C3u32;
        let json = serde_json::json!({
            "schemaVersion": 3,
            "characters": [
                {
                    "characterHash": character,
                    "presets": [
                        { "name": "A", "slots": [42, 42, 43] }
                    ],
                    "activeName": "a"
                }
            ]
        });
        fs::write(&path, serde_json::to_vec_pretty(&json).unwrap()).unwrap();

        let store = PresetStore::load(&path).unwrap();
        let preset = &store.list(character)[0];
        assert_eq!(preset.name, "A");
        assert_eq!(preset.slots[0], 42);
        assert_eq!(preset.slots[1], 0);
        assert_eq!(preset.slots[2], 43);
        assert_eq!(store.active_name(character), Some("A"));

        let backups = backup_files(&temp, "pre-normalize-v3");
        assert_eq!(backups.len(), 1);
        let reloaded = PresetStore::load(&path).unwrap();
        assert_eq!(reloaded.list(character).len(), 1);
        assert_eq!(backup_files(&temp, "pre-normalize-v3").len(), 1);
    }

    #[test]
    fn invalid_json_is_backed_up_once_and_original_is_untouched() {
        let temp = TempDir::new("invalid");
        let path = preset_path(&temp);
        let original = b"{ invalid".to_vec();
        fs::write(&path, &original).unwrap();

        for _ in 0..2 {
            let store = PresetStore::load(&path).unwrap();
            assert_eq!(store.document().schema_version, PRESET_SCHEMA_VERSION);
            assert!(store.document().characters.is_empty());
        }

        assert_eq!(fs::read(&path).unwrap(), original);
        let backups = backup_files(&temp, "invalid");
        assert_eq!(backups.len(), 1);
        assert_eq!(fs::read(&backups[0]).unwrap(), original);
    }

    #[test]
    fn legacy_v1_presets_are_migrated_per_character_and_backed_up() {
        let temp = TempDir::new("legacy");
        let path = preset_path(&temp);
        let source = 0x2A26B1B2u32;
        let target = 0x18E2F9F9u32;
        let json = serde_json::json!({
            "Version": 1,
            "Presets": [
                {
                    "Name": "Shared",
                    "Characters": [
                        { "CharacterHash": source, "Slots": [123] },
                        { "CharacterHash": target, "Slots": [123] }
                    ]
                },
                {
                    "Name": "Empty",
                    "Characters": [
                        { "CharacterHash": source, "Slots": [] }
                    ]
                }
            ]
        });
        fs::write(&path, serde_json::to_vec_pretty(&json).unwrap()).unwrap();

        let store = PresetStore::load(&path).unwrap();
        assert_eq!(store.list(source).len(), 1);
        assert_eq!(store.list(target).len(), 1);
        assert_eq!(store.list(target)[0].slots[0], 123);
        assert_eq!(store.list(source)[0].slots[0], 0);

        let backups = backup_files(&temp, "pre-v3");
        assert_eq!(backups.len(), 1);
        let value: Value = serde_json::from_str(&fs::read_to_string(&path).unwrap()).unwrap();
        assert_eq!(value["schemaVersion"].as_u64(), Some(3));
        assert!(value.get("Version").is_none());
    }

    #[test]
    fn transfer_supports_copy_move_and_rejects_same_character_and_collision() {
        let temp = TempDir::new("transfer");
        let path = preset_path(&temp);
        let mut store = PresetStore::load(&path).unwrap();
        let source = 0x2A26B1B2u32;
        let target = 0x18E2F9F9u32;

        store.create(source, "A", slots(&[1])).unwrap();
        store.create(source, "B", slots(&[2])).unwrap();
        store.create(target, "C", slots(&[3])).unwrap();

        let copied = store.transfer(source, "A", target, "A", false).unwrap();
        assert_eq!(copied.name, "A");
        assert_eq!(store.list(source).len(), 2);
        assert_eq!(store.list(target).len(), 2);

        let moved = store.transfer(source, "B", target, "B", true).unwrap();
        assert_eq!(moved.name, "B");
        assert_eq!(store.list(source).len(), 1);
        assert_eq!(store.list(target).len(), 3);

        assert!(store.transfer(source, "A", target, "C", true).is_err());
        assert_eq!(store.list(source).len(), 1);
        assert_eq!(store.list(target).len(), 3);

        assert!(store.transfer(source, "A", source, "A", true).is_err());

        let reloaded = PresetStore::load(&path).unwrap();
        assert_eq!(reloaded.list(source).len(), 1);
        assert_eq!(reloaded.list(target).len(), 3);
    }

    #[test]
    fn failed_save_rolls_back_memory() {
        let temp = TempDir::new("io-failure");
        let blocker = temp.path().join("blocker");
        fs::write(&blocker, b"not a directory").unwrap();
        let path = blocker.join("presets.json");
        let mut store = PresetStore::empty(path);

        assert!(store.create(0x2A26B1B2, "A", slots(&[1])).is_err());
        assert!(store.list(0x2A26B1B2).is_empty());
    }

    #[test]
    fn snapshot_and_restore_are_persisted() {
        let temp = TempDir::new("snapshot");
        let path = preset_path(&temp);
        let mut store = PresetStore::load(&path).unwrap();
        let character = 0x2A26B1B2u32;

        store.create(character, "A", slots(&[1])).unwrap();
        let snapshot = store.snapshot();
        store.create(character, "B", slots(&[2])).unwrap();
        assert_eq!(store.list(character).len(), 2);

        store.restore(snapshot).unwrap();
        assert_eq!(store.list(character).len(), 1);
        assert_eq!(PresetStore::load(&path).unwrap().list(character).len(), 1);
    }

    fn backup_files(temp: &TempDir, reason: &str) -> Vec<PathBuf> {
        fs::read_dir(temp.path())
            .unwrap()
            .filter_map(|entry| entry.ok())
            .map(|entry| entry.path())
            .filter(|path| {
                path.file_name()
                    .and_then(|name| name.to_str())
                    .is_some_and(|name| {
                        name.starts_with("GBFR-ExtraSigilSlots.presets.json.")
                            && name.contains(&format!(".{reason}-"))
                            && name.ends_with(".bak")
                    })
            })
            .collect()
    }
}
