#pragma once

#include <cstdint>

#if defined(GBFR20_NATIVE_EXPORTS)
#define GBFR20_API extern "C" __declspec(dllexport)
#else
#define GBFR20_API extern "C" __declspec(dllimport)
#endif

#define GBFR20_CALL __cdecl

constexpr uint32_t GBFR20_ABI_VERSION = 6;
constexpr uint32_t GBFR20_VIRTUAL_SLOT_COUNT = 8;
constexpr uint32_t GBFR20_OWNER_CHARACTER_CAPACITY = 4;

#pragma pack(push, 1)
struct GBFR20_GemData
{
   uint32_t trait1;
   int32_t trait1_level;
   uint32_t trait2;
   int32_t trait2_level;
   uint32_t gem_id;
   uint32_t worn_by;
   int32_t sigil_level;
   uint32_t slot_id;
   uint32_t flags;
};

struct GBFR20_InventoryItem
{
   GBFR20_GemData gem;
   uint32_t equipped;
   uint32_t required_character_hash;
   uint32_t virtual_owner_character_hash;
   int32_t virtual_owner_slot;
};

struct GBFR20_RuntimeState
{
   uint32_t abi_version;
   uint32_t struct_size;
   int32_t initialized;
   int32_t hooks_ready;
   int32_t shutting_down;
   int32_t runtime_message_is_error;
   uint32_t ui_selected_character_hash;
   uint32_t effective_character_hash;
   uint32_t last_rebuilt_character_hash;
   int32_t last_context_mode;
   uint32_t owner_thread_id;
   uint32_t overlay_thread_id;
   uint64_t owner_tick_count;
   uint64_t overlay_frame_count;
   uint32_t owner_character_count;
   uint32_t owner_character_hashes[GBFR20_OWNER_CHARACTER_CAPACITY];
   uint64_t last_apply_generation;
   uint32_t last_apply_character_hash;
   uint32_t last_apply_expected_count;
   uint32_t last_apply_injected_count;
   int32_t last_apply_result;
   int32_t auto_apply;
   int32_t show_equipped;
   int32_t toggle_key;
   int32_t language;
   uint32_t authorized_status_count;
   uint32_t authorized_character_hash;
   uint64_t authorized_status_address;
   uint64_t inventory_revision;
   int32_t inventory_dirty;
   int32_t edit_allowed;
   int32_t ui_mode;
   int32_t source_mode;
   int32_t edit_session_state;
   uint32_t observed_character_hash;
   uint64_t observed_status_address;
   int32_t observed_status_context;
   uint32_t lifecycle_rebind_attempts;
   int32_t input_capture_requested;
   int32_t input_capture_effective;
   int32_t input_iat_hooks_ready;
   int32_t direct_input_hook_ready;
   uint64_t natural_bind_attempts;
   uint64_t natural_bind_successes;
   uint64_t natural_bind_status_address;
   uint32_t natural_bind_character_hash;
   int32_t natural_bind_context;
   uint32_t natural_bind_expected_count;
   uint32_t natural_bind_injected_count;
   int32_t natural_bind_result;
   uint64_t owner_manager_address;
   uint32_t natural_bind_owner_key;
   uint64_t natural_bind_owner_status_address;
};
#pragma pack(pop)

static_assert(sizeof(GBFR20_GemData) == 0x24);
static_assert(sizeof(GBFR20_RuntimeState) == 268);

GBFR20_API uint32_t GBFR20_CALL GBFR20_GetAbiVersion();
GBFR20_API int32_t GBFR20_CALL GBFR20_Initialize();
GBFR20_API void GBFR20_CALL GBFR20_Tick();
GBFR20_API void GBFR20_CALL GBFR20_Shutdown();
GBFR20_API int32_t GBFR20_CALL GBFR20_GetState(
   GBFR20_RuntimeState* state,
   uint32_t state_size);
GBFR20_API uint32_t GBFR20_CALL GBFR20_CopyRuntimeMessage(
   char* buffer,
   uint32_t buffer_size);
GBFR20_API int32_t GBFR20_CALL GBFR20_RefreshInventory();
GBFR20_API uint32_t GBFR20_CALL GBFR20_GetInventoryCount();
GBFR20_API int32_t GBFR20_CALL GBFR20_CopyInventoryItem(
   uint32_t index,
   GBFR20_InventoryItem* item,
   uint32_t item_size,
   char* label_buffer,
   uint32_t label_buffer_size);
GBFR20_API int32_t GBFR20_CALL GBFR20_GetSelection(
   uint32_t character_hash,
   uint32_t* slots,
   uint32_t slot_count);
GBFR20_API int32_t GBFR20_CALL GBFR20_SetSelection(
   uint32_t character_hash,
   int32_t virtual_slot,
   uint32_t inventory_slot_id);
GBFR20_API uint32_t GBFR20_CALL GBFR20_RequestApply(uint32_t character_hash);
GBFR20_API int32_t GBFR20_CALL GBFR20_SetAutoApply(int32_t enabled);
GBFR20_API int32_t GBFR20_CALL GBFR20_SetShowEquipped(int32_t enabled);
GBFR20_API int32_t GBFR20_CALL GBFR20_SetToggleKey(int32_t virtual_key);
GBFR20_API int32_t GBFR20_CALL GBFR20_SetLanguage(int32_t language);
GBFR20_API int32_t GBFR20_CALL GBFR20_SetInputCapture(int32_t requested);
GBFR20_API int32_t GBFR20_CALL GBFR20_GetInputCaptureActive();
GBFR20_API int32_t GBFR20_CALL GBFR20_IsInventoryDirty();
GBFR20_API int32_t GBFR20_CALL GBFR20_CanEditCharacter(uint32_t character_hash);
