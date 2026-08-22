mod injector;
mod ipc;
mod presets;
mod process;
mod protocol;

use injector::ensure_agent;
use ipc::PipeClient;
use presets::{PresetDocument, PresetStore, PRESET_SCHEMA_VERSION};
use process::GameProcess;
use protocol::{
    GemData, NativeInventoryItem, NativePresetSlotResult, PresetCharacterSelection,
    NATIVE_ABI_VERSION, PROTOCOL_VERSION, VIRTUAL_SLOT_CAPACITY,
};
use serde::Serialize;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::Duration;
use tauri::{Manager, State};

const PRESET_FILE_NAME: &str = "GBFR-ExtraSigilSlots.presets.json";
const AGENT_FILE_NAME: &str = "GBFR.ExtraSigilSlots.Native.dll";
const GRAN_CHARACTER_HASH: u32 = 0x2A26_B1B2;
const DJEETA_CHARACTER_HASH: u32 = 0xA4AC_BA76;

fn is_captain_character_hash(character_hash: u32) -> bool {
    character_hash == GRAN_CHARACTER_HASH || character_hash == DJEETA_CHARACTER_HASH
}

fn is_character_compatible(required_character_hash: u32, character_hash: u32) -> bool {
    required_character_hash == 0
        || required_character_hash == character_hash
        || (is_captain_character_hash(required_character_hash)
            && is_captain_character_hash(character_hash))
}

struct ConnectedGame {
    info: ConnectionInfo,
    pipe: PipeClient,
}

struct BackendState {
    operation: Mutex<()>,
    connection: Mutex<Option<ConnectedGame>>,
    presets: Mutex<PresetStore>,
    data_directory: PathBuf,
    agent_path: PathBuf,
}

#[derive(Debug, Clone, Serialize)]
struct ConnectionInfo {
    pid: u32,
    process_name: String,
    injected: bool,
    protocol_version: u16,
    native_abi_version: u32,
}

#[derive(Debug, Clone, Serialize)]
struct Dashboard {
    connection: ConnectionInfo,
    initialized: bool,
    hooks_ready: bool,
    runtime_message: String,
    runtime_message_is_error: bool,
    effective_character_hash: u32,
    ui_selected_character_hash: u32,
    edit_allowed: bool,
    language: &'static str,
    inventory_revision: u64,
    inventory_dirty: bool,
    game_data_ready: bool,
    virtual_slot_count: u32,
    virtual_slot_capacity: u32,
    pending_virtual_slot_count: i32,
}

#[derive(Debug, Clone, Serialize)]
struct InventoryItem {
    gem: GemData,
    label: String,
    searchable: String,
    equipped: bool,
    required_character_hash: u32,
    virtual_owner_character_hash: u32,
    virtual_owner_slot: i32,
    preset_names: Vec<String>,
}

#[derive(Debug, Clone, Serialize)]
struct AssignResult {
    success: bool,
    message: String,
    affected_preset_names: Vec<String>,
}

#[derive(Debug, Clone, Serialize)]
struct FlatPresetDocument {
    version: u32,
    presets: Vec<SigilPreset>,
}

#[derive(Debug, Clone, Serialize)]
struct SigilPreset {
    id: String,
    name: String,
    character_hash: u32,
    slots: Vec<u32>,
}

#[derive(Debug, Clone, Serialize)]
struct PresetSlotResult {
    character_hash: u32,
    virtual_slot: i32,
    requested_slot_id: u32,
    owner_character_hash: u32,
    status: &'static str,
}

#[derive(Debug, Clone, Serialize)]
struct PresetApplySummary {
    applied_count: u32,
    requested_count: u32,
    conflicts: Vec<PresetSlotResult>,
}

#[derive(Debug, Clone, Serialize)]
struct SlotCountRequestResult {
    status: &'static str,
    pending_virtual_slot_count: i32,
    message: String,
}

#[tauri::command]
fn list_game_processes() -> Result<Vec<GameProcess>, String> {
    process::list_game_processes()
}

#[tauri::command]
fn connect_game(state: State<'_, BackendState>, pid: u32) -> Result<ConnectionInfo, String> {
    let _operation = state
        .operation
        .lock()
        .map_err(|_| "The controller operation lock is poisoned.".to_string())?;
    let process = process::verify_game_process(pid)?;
    let injected = ensure_agent(pid, &state.agent_path, &state.data_directory)?;
    let (pipe, hello) = PipeClient::connect_ready(pid, Duration::from_secs(15))?;
    if hello.process_id != pid {
        return Err(format!(
            "Agent pipe identity mismatch: selected PID {pid}, Agent PID {}.",
            hello.process_id
        ));
    }
    if hello.native_abi_version != NATIVE_ABI_VERSION {
        return Err(format!(
            "Agent ABI {} is incompatible with controller ABI {NATIVE_ABI_VERSION}.",
            hello.native_abi_version
        ));
    }
    let info = ConnectionInfo {
        pid,
        process_name: process.executable_name,
        injected,
        protocol_version: PROTOCOL_VERSION,
        native_abi_version: hello.native_abi_version,
    };
    *state
        .connection
        .lock()
        .map_err(|_| "The game connection lock is poisoned.".to_string())? = Some(ConnectedGame {
        info: info.clone(),
        pipe,
    });
    Ok(info)
}

#[tauri::command]
fn disconnect_game(state: State<'_, BackendState>) -> Result<(), String> {
    let _operation = lock_operation(&state)?;
    state
        .connection
        .lock()
        .map_err(|_| "The game connection lock is poisoned.".to_string())?
        .take();
    Ok(())
}

#[tauri::command]
fn get_dashboard(state: State<'_, BackendState>) -> Result<Dashboard, String> {
    let _operation = lock_operation(&state)?;
    dashboard_locked(&state)
}

#[tauri::command]
fn refresh_inventory(state: State<'_, BackendState>) -> Result<Vec<InventoryItem>, String> {
    let _operation = lock_operation(&state)?;
    let native_items = with_connection(&state, |connection| connection.pipe.refresh_inventory())?;
    let presets = state
        .presets
        .lock()
        .map_err(|_| "The preset store lock is poisoned.".to_string())?;
    Ok(native_items
        .into_iter()
        .map(|item| inventory_item(item, presets.document()))
        .collect())
}

#[tauri::command]
fn get_selection(state: State<'_, BackendState>, character_hash: u32) -> Result<Vec<u32>, String> {
    let _operation = lock_operation(&state)?;
    Ok(with_connection(&state, |connection| {
        connection.pipe.get_selection(character_hash)
    })?
    .to_vec())
}

#[tauri::command]
fn assign_inventory_sigil(
    state: State<'_, BackendState>,
    character_hash: u32,
    virtual_slot: i32,
    inventory_slot_id: u32,
) -> Result<AssignResult, String> {
    let _operation = lock_operation(&state)?;
    if virtual_slot < 0 || virtual_slot >= VIRTUAL_SLOT_CAPACITY as i32 {
        return Ok(assign_failure(
            "Virtual slot is outside the 24-slot capacity.",
        ));
    }

    let inventory = with_connection(&state, |connection| connection.pipe.refresh_inventory())?;
    let item = match inventory
        .iter()
        .find(|item| item.gem.slot_id == inventory_slot_id)
    {
        Some(item) => item,
        None => {
            return Ok(assign_failure(
                "The selected inventory sigil no longer exists.",
            ))
        }
    };
    if item.equipped {
        return Ok(assign_failure(
            "The selected sigil is equipped in a body slot and cannot be moved externally.",
        ));
    }
    if !is_character_compatible(item.required_character_hash, character_hash) {
        return Ok(assign_failure(
            "The selected sigil is restricted to another character.",
        ));
    }

    let mut presets = state
        .presets
        .lock()
        .map_err(|_| "The preset store lock is poisoned.".to_string())?;
    let affected_preset_names = preset_names_for_slot(presets.document(), inventory_slot_id);
    let snapshot = presets.snapshot();
    if !affected_preset_names.is_empty() {
        clear_preset_slot_references(&mut presets, inventory_slot_id)?;
    }

    let native_result = with_connection(&state, |connection| {
        connection
            .pipe
            .set_selection(character_hash, virtual_slot, inventory_slot_id)
    });
    if let Err(error) = native_result {
        let rollback = presets.restore(snapshot);
        let message = match rollback {
            Ok(()) => error,
            Err(rollback_error) => {
                format!("{error} Preset rollback also failed: {rollback_error}")
            }
        };
        return Ok(AssignResult {
            success: false,
            message,
            affected_preset_names,
        });
    }

    Ok(AssignResult {
        success: true,
        message: if affected_preset_names.is_empty() {
            "Sigil assigned.".to_string()
        } else {
            "Sigil moved and its saved preset references were cleared.".to_string()
        },
        affected_preset_names,
    })
}

#[tauri::command]
fn clear_virtual_slot(
    state: State<'_, BackendState>,
    character_hash: u32,
    virtual_slot: i32,
) -> Result<AssignResult, String> {
    let _operation = lock_operation(&state)?;
    if virtual_slot < 0 || virtual_slot >= VIRTUAL_SLOT_CAPACITY as i32 {
        return Ok(assign_failure(
            "Virtual slot is outside the 24-slot capacity.",
        ));
    }
    match with_connection(&state, |connection| {
        connection
            .pipe
            .set_selection(character_hash, virtual_slot, 0)
    }) {
        Ok(()) => Ok(AssignResult {
            success: true,
            message: "Slot cleared.".to_string(),
            affected_preset_names: Vec::new(),
        }),
        Err(error) => Ok(assign_failure(&error)),
    }
}

#[tauri::command]
fn set_language(state: State<'_, BackendState>, language: String) -> Result<Dashboard, String> {
    let _operation = lock_operation(&state)?;
    let language_value = match language.as_str() {
        "zh-CN" => 0,
        "en" => 1,
        _ => return Err("Language must be 'zh-CN' or 'en'.".to_string()),
    };
    with_connection(&state, |connection| {
        connection.pipe.set_language(language_value)
    })?;
    dashboard_locked(&state)
}

#[tauri::command]
fn request_virtual_slot_count(
    state: State<'_, BackendState>,
    slot_count: i32,
) -> Result<SlotCountRequestResult, String> {
    let _operation = lock_operation(&state)?;
    if !(1..=VIRTUAL_SLOT_CAPACITY as i32).contains(&slot_count) {
        return Ok(SlotCountRequestResult {
            status: "failed",
            pending_virtual_slot_count: 0,
            message: "Slot count must be between 1 and 24.".to_string(),
        });
    }
    let result = with_connection(&state, |connection| {
        connection.pipe.request_virtual_slot_count(slot_count)
    });
    let result = match result {
        Ok(result) => result,
        Err(error) => {
            return Ok(SlotCountRequestResult {
                status: "failed",
                pending_virtual_slot_count: 0,
                message: error,
            });
        }
    };
    let pending = with_connection(&state, |connection| {
        connection.pipe.get_pending_virtual_slot_count()
    })?;
    Ok(match result {
        1 => SlotCountRequestResult {
            status: "pending",
            pending_virtual_slot_count: pending.max(0),
            message: format!("Saved. {slot_count} slots will take effect after game restart."),
        },
        2 => SlotCountRequestResult {
            status: "cleared",
            pending_virtual_slot_count: 0,
            message: "Pending slot-count change cleared.".to_string(),
        },
        _ => SlotCountRequestResult {
            status: "failed",
            pending_virtual_slot_count: pending.max(0),
            message: "The Native Agent rejected the slot-count request.".to_string(),
        },
    })
}

#[tauri::command]
fn list_presets(state: State<'_, BackendState>) -> Result<FlatPresetDocument, String> {
    let _operation = lock_operation(&state)?;
    let presets = state
        .presets
        .lock()
        .map_err(|_| "The preset store lock is poisoned.".to_string())?;
    Ok(flatten_presets(presets.document()))
}

#[tauri::command]
fn create_preset(
    state: State<'_, BackendState>,
    character_hash: u32,
    name: String,
) -> Result<SigilPreset, String> {
    let _operation = lock_operation(&state)?;
    let slots = with_connection(&state, |connection| {
        connection.pipe.get_selection(character_hash)
    })?;
    let mut presets = state
        .presets
        .lock()
        .map_err(|_| "The preset store lock is poisoned.".to_string())?;
    let preset = presets.create(character_hash, &name, slots)?;
    Ok(sigil_preset(character_hash, &preset.name, &preset.slots))
}

#[tauri::command]
fn overwrite_preset(
    state: State<'_, BackendState>,
    preset_id: String,
) -> Result<SigilPreset, String> {
    let _operation = lock_operation(&state)?;
    let (character_hash, name, _) = find_preset_by_id(&state, &preset_id)?;
    let slots = with_connection(&state, |connection| {
        connection.pipe.get_selection(character_hash)
    })?;
    let mut presets = state
        .presets
        .lock()
        .map_err(|_| "The preset store lock is poisoned.".to_string())?;
    let preset = presets.overwrite(character_hash, &name, slots)?;
    Ok(sigil_preset(character_hash, &preset.name, &preset.slots))
}

#[tauri::command]
fn rename_preset(
    state: State<'_, BackendState>,
    preset_id: String,
    name: String,
) -> Result<SigilPreset, String> {
    let _operation = lock_operation(&state)?;
    let (character_hash, old_name, _) = find_preset_by_id(&state, &preset_id)?;
    let mut presets = state
        .presets
        .lock()
        .map_err(|_| "The preset store lock is poisoned.".to_string())?;
    let preset = presets.rename(character_hash, &old_name, &name)?;
    Ok(sigil_preset(character_hash, &preset.name, &preset.slots))
}

#[tauri::command]
fn delete_preset(
    state: State<'_, BackendState>,
    preset_id: String,
) -> Result<FlatPresetDocument, String> {
    let _operation = lock_operation(&state)?;
    let (character_hash, name, _) = find_preset_by_id(&state, &preset_id)?;
    let mut presets = state
        .presets
        .lock()
        .map_err(|_| "The preset store lock is poisoned.".to_string())?;
    presets.delete(character_hash, &name)?;
    Ok(flatten_presets(presets.document()))
}

#[tauri::command]
fn transfer_preset(
    state: State<'_, BackendState>,
    preset_id: String,
    target_character_hash: u32,
) -> Result<SigilPreset, String> {
    let _operation = lock_operation(&state)?;
    let (source_character_hash, name, _) = find_preset_by_id(&state, &preset_id)?;
    let mut presets = state
        .presets
        .lock()
        .map_err(|_| "The preset store lock is poisoned.".to_string())?;
    let preset = presets.transfer(
        source_character_hash,
        &name,
        target_character_hash,
        &name,
        true,
    )?;
    Ok(sigil_preset(
        target_character_hash,
        &preset.name,
        &preset.slots,
    ))
}

#[tauri::command]
fn apply_preset(
    state: State<'_, BackendState>,
    preset_id: String,
    character_hash: u32,
) -> Result<PresetApplySummary, String> {
    let _operation = lock_operation(&state)?;
    let (preset_character_hash, name, slots) = find_preset_by_id(&state, &preset_id)?;
    if preset_character_hash != character_hash {
        return Err("The selected preset belongs to another character.".to_string());
    }
    let active_slot_count = with_connection(&state, |connection| {
        Ok(connection.pipe.get_state()?.state.virtual_slot_count)
    })? as usize;

    let snapshot = {
        let mut presets = state
            .presets
            .lock()
            .map_err(|_| "The preset store lock is poisoned.".to_string())?;
        let snapshot = presets.snapshot();
        presets.set_active(character_hash, &name)?;
        snapshot
    };
    let results = with_connection(&state, |connection| {
        connection.pipe.apply_preset(&[PresetCharacterSelection {
            character_hash,
            slots,
        }])
    });
    let results = match results {
        Ok(results) => results,
        Err(error) => {
            let mut presets = state
                .presets
                .lock()
                .map_err(|_| "The preset store lock is poisoned.".to_string())?;
            return match presets.restore(snapshot) {
                Ok(()) => Err(error),
                Err(rollback_error) => Err(format!(
                    "{error} Preset active-state rollback also failed: {rollback_error}"
                )),
            };
        }
    };
    let requested_count = slots
        .iter()
        .take(active_slot_count.min(VIRTUAL_SLOT_CAPACITY))
        .filter(|slot| **slot != 0)
        .count() as u32;
    let applied_count = results.iter().filter(|result| result.status == 1).count() as u32;
    let conflicts = results
        .into_iter()
        .filter(|result| result.status < 0)
        .map(preset_slot_result)
        .collect();
    Ok(PresetApplySummary {
        applied_count,
        requested_count,
        conflicts,
    })
}

fn lock_operation(state: &BackendState) -> Result<std::sync::MutexGuard<'_, ()>, String> {
    state
        .operation
        .lock()
        .map_err(|_| "The controller operation lock is poisoned.".to_string())
}

fn with_connection<T>(
    state: &State<'_, BackendState>,
    operation: impl FnOnce(&mut ConnectedGame) -> Result<T, String>,
) -> Result<T, String> {
    let mut connection = state
        .connection
        .lock()
        .map_err(|_| "The game connection lock is poisoned.".to_string())?;
    let connection = connection
        .as_mut()
        .ok_or_else(|| "No game process is connected.".to_string())?;
    operation(connection)
}

fn dashboard_locked(state: &State<'_, BackendState>) -> Result<Dashboard, String> {
    with_connection(state, |connection| {
        let response = connection.pipe.get_state()?;
        if response.state.shutting_down {
            return Err("The connected game process is shutting down.".to_string());
        }
        Ok(Dashboard {
            connection: connection.info.clone(),
            initialized: response.state.initialized,
            hooks_ready: response.state.hooks_ready,
            runtime_message: response.runtime_message,
            runtime_message_is_error: response.state.runtime_message_is_error,
            effective_character_hash: response.state.effective_character_hash,
            ui_selected_character_hash: response.state.ui_selected_character_hash,
            edit_allowed: response.state.edit_allowed,
            language: if response.state.language == 1 {
                "en"
            } else {
                "zh-CN"
            },
            inventory_revision: response.state.inventory_revision,
            inventory_dirty: response.state.inventory_dirty,
            game_data_ready: response.state.game_data_ready,
            virtual_slot_count: response.state.virtual_slot_count,
            virtual_slot_capacity: response.state.virtual_slot_capacity,
            pending_virtual_slot_count: response.pending_virtual_slot_count.max(0),
        })
    })
}

fn inventory_item(item: NativeInventoryItem, presets: &PresetDocument) -> InventoryItem {
    let preset_names = preset_names_for_slot(presets, item.gem.slot_id);
    InventoryItem {
        searchable: item.label.to_lowercase(),
        gem: item.gem,
        label: item.label,
        equipped: item.equipped,
        required_character_hash: item.required_character_hash,
        virtual_owner_character_hash: item.virtual_owner_character_hash,
        virtual_owner_slot: item.virtual_owner_slot,
        preset_names,
    }
}

fn preset_names_for_slot(document: &PresetDocument, slot_id: u32) -> Vec<String> {
    if slot_id == 0 {
        return Vec::new();
    }
    let mut names = document
        .characters
        .iter()
        .flat_map(|character| character.presets.iter())
        .filter(|preset| preset.slots.contains(&slot_id))
        .map(|preset| preset.name.clone())
        .collect::<Vec<_>>();
    names.sort_by_key(|name| name.to_lowercase());
    names.dedup_by(|left, right| left.eq_ignore_ascii_case(right));
    names
}

fn clear_preset_slot_references(store: &mut PresetStore, slot_id: u32) -> Result<(), String> {
    store.mutate(|document| {
        for character in &mut document.characters {
            for preset in &mut character.presets {
                for slot in &mut preset.slots {
                    if *slot == slot_id {
                        *slot = 0;
                    }
                }
            }
        }
        Ok(())
    })
}

fn flatten_presets(document: &PresetDocument) -> FlatPresetDocument {
    FlatPresetDocument {
        version: PRESET_SCHEMA_VERSION,
        presets: document
            .characters
            .iter()
            .flat_map(|character| {
                character.presets.iter().map(|preset| {
                    sigil_preset(character.character_hash, &preset.name, &preset.slots)
                })
            })
            .collect(),
    }
}

fn sigil_preset(character_hash: u32, name: &str, slots: &[u32; 24]) -> SigilPreset {
    SigilPreset {
        id: preset_id(character_hash, name),
        name: name.to_string(),
        character_hash,
        slots: slots.to_vec(),
    }
}

fn preset_id(character_hash: u32, name: &str) -> String {
    let mut hash = 0xcbf2_9ce4_8422_2325_u64;
    for byte in name.to_lowercase().as_bytes() {
        hash ^= u64::from(*byte);
        hash = hash.wrapping_mul(0x0000_0100_0000_01B3);
    }
    format!("{character_hash:08X}-{hash:016X}")
}

fn find_preset_by_id(
    state: &State<'_, BackendState>,
    preset_id_value: &str,
) -> Result<(u32, String, [u32; 24]), String> {
    let presets = state
        .presets
        .lock()
        .map_err(|_| "The preset store lock is poisoned.".to_string())?;
    for character in &presets.document().characters {
        for preset in &character.presets {
            if preset_id(character.character_hash, &preset.name) == preset_id_value {
                return Ok((character.character_hash, preset.name.clone(), preset.slots));
            }
        }
    }
    Err("The selected preset no longer exists.".to_string())
}

fn preset_slot_result(result: NativePresetSlotResult) -> PresetSlotResult {
    PresetSlotResult {
        character_hash: result.character_hash,
        virtual_slot: result.virtual_slot,
        requested_slot_id: result.requested_slot_id,
        owner_character_hash: result.owner_character_hash,
        status: match result.status {
            0 => "empty",
            1 => "applied",
            -1 => "missing",
            -2 => "equipped",
            -3 => "disabled",
            -4 => "character_restricted",
            -5 => "duplicate",
            _ => "missing",
        },
    }
}

fn assign_failure(message: &str) -> AssignResult {
    AssignResult {
        success: false,
        message: message.to_string(),
        affected_preset_names: Vec::new(),
    }
}

fn resolve_agent_path(resource_directory: &Path) -> Result<PathBuf, String> {
    let candidates = [
        resource_directory
            .join("resources")
            .join("agent")
            .join(AGENT_FILE_NAME),
        resource_directory.join("agent").join(AGENT_FILE_NAME),
        PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("resources")
            .join("agent")
            .join(AGENT_FILE_NAME),
    ];
    candidates
        .into_iter()
        .find(|candidate| candidate.is_file())
        .ok_or_else(|| {
            format!(
                "Standalone Native Agent resource is missing below {}.",
                resource_directory.display()
            )
        })
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .setup(|app| {
            let data_directory = app.path().app_data_dir()?;
            std::fs::create_dir_all(&data_directory)?;
            let resource_directory = app.path().resource_dir()?;
            let agent_path =
                resolve_agent_path(&resource_directory).map_err(std::io::Error::other)?;
            let presets = PresetStore::load(data_directory.join(PRESET_FILE_NAME))
                .map_err(std::io::Error::other)?;
            app.manage(BackendState {
                operation: Mutex::new(()),
                connection: Mutex::new(None),
                presets: Mutex::new(presets),
                data_directory,
                agent_path,
            });
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            list_game_processes,
            connect_game,
            disconnect_game,
            get_dashboard,
            refresh_inventory,
            get_selection,
            assign_inventory_sigil,
            clear_virtual_slot,
            set_language,
            request_virtual_slot_count,
            list_presets,
            create_preset,
            overwrite_preset,
            rename_preset,
            delete_preset,
            transfer_preset,
            apply_preset,
        ])
        .run(tauri::generate_context!())
        .expect("error while running GBFR Extra Sigil Slots Standalone");
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn shares_captain_sigil_compatibility() {
        assert!(is_character_compatible(0, 0xA4AC_BA76));
        assert!(is_character_compatible(0x2A26_B1B2, 0x2A26_B1B2));
        assert!(is_character_compatible(0x2A26_B1B2, 0xA4AC_BA76));
        assert!(is_character_compatible(0xA4AC_BA76, 0x2A26_B1B2));
        assert!(is_character_compatible(0xA4AC_BA76, 0xA4AC_BA76));
        assert!(!is_character_compatible(0x2A26_B1B2, 0x18E2_F9F9));
        assert!(!is_character_compatible(0x18E2_F9F9, 0xA4AC_BA76));
    }
}
