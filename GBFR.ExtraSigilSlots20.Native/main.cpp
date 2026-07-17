#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#define DIRECTINPUT_VERSION 0x0800

#include <windows.h>
#include <bcrypt.h>
#include <dinput.h>
#include <intrin.h>
#include <xinput.h>

#include "native_api.h"
#include "third_party/safetyhook.hpp"

#include <algorithm>
#include <array>
#include <atomic>
#include <charconv>
#include <cctype>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <mutex>
#include <shared_mutex>
#include <sstream>
#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#pragma comment(lib, "bcrypt.lib")

namespace
{
constexpr char kExpectedExeSha256[] =
   "63340832BCF731FBC97796F686B05C988418E83D451D4A49B2244A85D00E297F";

constexpr uintptr_t kTraitApplyLoopLimitImmediateRva = 0x00A25484;
constexpr uintptr_t kTraitApplyGetterReturnRva = 0x00A254A9;
constexpr uintptr_t kTraitCategoryLoopLimitImmediateRva = 0x00A26096;
constexpr uintptr_t kTraitFetchPathRva = 0x00A260AE;
constexpr uintptr_t kTraitFetchCallPathRva = 0x00A260F0;
constexpr uintptr_t kTraitCategoryGetterReturnRva = 0x00A260FE;
constexpr uintptr_t kGetGemDataByIndexRva = 0x00A2C610;
constexpr uintptr_t kStatusRebuildRva = 0x00A23CC0;
constexpr uintptr_t kStatusNotifierRva = 0x002D93F0;
constexpr uintptr_t kStatusOwnerTickRva = 0x0024B2F0;
constexpr uintptr_t kStatusOwnerCharacterLoopRva = 0x0024CA4A;
constexpr uintptr_t kLocalContext1BindCallRva = 0x002EA29D;
constexpr uintptr_t kLocalContext1BindReturnRva = 0x002EA2A2;
constexpr uintptr_t kSystemDataGlobalRva = 0x07C20940;
constexpr uintptr_t kStatusManagerGlobalRva = 0x07C24980;
constexpr uintptr_t kUiManagerGlobalRva = 0x07C4A140;
constexpr uintptr_t kUiStateSourceGlobalRva = 0x07C4A1A8;
constexpr uintptr_t kInputContextGlobalRva = 0x07032D30;

constexpr int kNativeInternalSlotCount = 13;
constexpr int kVirtualSlotCount = 8;
constexpr int kExpandedInternalSlotCount = kNativeInternalSlotCount + kVirtualSlotCount;
constexpr int kMainGemCapacity = 5100;
constexpr uint32_t kExpectedCompatibilityMappingCount = 199;
constexpr uintptr_t kMainGemArrayOffset = 0x25D0;
constexpr uintptr_t kUiSelectedCharacterHashOffset = 0x5F0;
constexpr uintptr_t kUiModeOffset = 0xB14;
constexpr uintptr_t kUiStateSourceModeOffset = 0x34;
constexpr uintptr_t kStatusMapSentinelOffset = 0xA30;
constexpr uintptr_t kStatusMapBucketsOffset = 0xA40;
constexpr uintptr_t kStatusMapMaskOffset = 0xA58;
constexpr uintptr_t kCharacterRecordMapSentinelOffset = 0xED738;
constexpr uintptr_t kCharacterRecordMapBucketsOffset = 0xED748;
constexpr uintptr_t kCharacterRecordMapMaskOffset = 0xED760;
constexpr uintptr_t kCharacterRecordPrimaryHashOffset = 0x59F4;
constexpr uintptr_t kCharacterRecordFallbackHashOffset = 0x59F0;
constexpr uintptr_t kStatusCharacterHashOffset = 0x5EA8;
constexpr uintptr_t kStatusContextModeOffset = 0x5EAC;
constexpr uint32_t kUnwornCharacterHash = 0x887AE0B0;
// ids.txt names these four constants SLOT01-SLOT04. The local client-controlled
// character is normalized to SLOT01; SLOT02-SLOT04 may be AI or remote players.
constexpr uint32_t kLocalPlayerSlotKey = 0xDBD9A18D;

constexpr std::array<uint8_t, 16> kTraitApplyLoopPreflight = {
   0xFF, 0xC7, 0x83, 0xFF, 0x0D, 0x0F, 0x84, 0xB7,
   0x00, 0x00, 0x00, 0xC5, 0xF8, 0x11, 0x75, 0xF0};
constexpr std::array<uint8_t, 13> kTraitCategoryLoopPreflight = {
   0x49, 0xFF, 0xC5, 0x49, 0x83, 0xFD, 0x0D, 0x0F, 0x84, 0xE4, 0x00, 0x00, 0x00};
constexpr std::array<uint8_t, 11> kTraitFetchPreflight = {
   0x84, 0xDB, 0x74, 0x3E, 0x49, 0x8B, 0x87, 0x80, 0x5E, 0x00, 0x00};
constexpr std::array<uint8_t, 12> kGetterPreflight = {
   0x55, 0x41, 0x57, 0x41, 0x56, 0x56, 0x57, 0x53, 0x48, 0x83, 0xEC, 0x28};
constexpr std::array<uint8_t, 12> kStatusRebuildPreflight = {
   0x55, 0x56, 0x57, 0x48, 0x83, 0xEC, 0x50, 0x48, 0x8D, 0x6C, 0x24, 0x50};
constexpr std::array<uint8_t, 12> kStatusNotifierPreflight = {
   0x41, 0x56, 0x56, 0x57, 0x53, 0x48, 0x83, 0xEC, 0x38, 0x44, 0x89, 0xC6};
constexpr std::array<uint8_t, 24> kStatusOwnerTickPreflight = {
   0x55, 0x41, 0x57, 0x41, 0x56, 0x41, 0x55, 0x41,
   0x54, 0x56, 0x57, 0x53, 0x48, 0x81, 0xEC, 0x98,
   0x05, 0x00, 0x00, 0x48, 0x8D, 0xAC, 0x24, 0x80};
constexpr std::array<uint8_t, 24> kStatusOwnerCharacterLoopPreflight = {
   0x48, 0x8B, 0x73, 0x20, 0x48, 0x8B, 0x7B, 0x28,
   0x48, 0x39, 0xFE, 0x0F, 0x84, 0x76, 0x01, 0x00,
   0x00, 0x4C, 0x8D, 0xB3, 0x30, 0x32, 0x00, 0x00};
constexpr std::array<uint8_t, 10> kLocalContext1BindCallPreflight = {
   0xE8, 0xEE, 0x47, 0x74, 0x00, 0x89, 0xD8, 0x89, 0x5D, 0xFC};
constexpr std::array<uint8_t, 10> kLocalContext1BindReturnPreflight = {
   0x89, 0xD8, 0x89, 0x5D, 0xFC, 0x48, 0x8B, 0x45, 0xE8, 0x8B};

using GemData = GBFR20_GemData;
static_assert(sizeof(GemData) == 0x24);

struct StatusIdentity
{
   uint32_t character_hash = 0;
   int32_t context_mode = -1;
};

struct InventoryItem
{
   GemData gem{};
   uintptr_t address = 0;
   bool equipped = false;
   uint32_t required_character_hash = 0;
   uint32_t virtual_owner_character_hash = 0;
   int32_t virtual_owner_slot = -1;
   std::string label;
   std::string searchable;
};

struct AuthorizedStatus
{
   uintptr_t status = 0;
   uint32_t character_hash = 0;
   int32_t context_mode = -1;
   uint64_t generation = 0;
   std::array<uint32_t, kVirtualSlotCount> slots{};
};

struct VirtualOwner
{
   uint32_t character_hash = 0;
   int32_t virtual_slot = -1;
};

enum NaturalBindResult : int32_t
{
   NaturalBindNone = 0,
   NaturalBindSucceeded = 1,
   NaturalBindInProgress = 2,
   NaturalBindContextRejected = -1,
   NaturalBindOwnerRejected = -2,
   NaturalBindStatusRejected = -3,
   NaturalBindSelectionRejected = -4,
   NaturalBindSequenceRejected = -5,
   NaturalBindFinalValidationRejected = -6,
   NaturalBindCopyRejected = -7,
};

struct NaturalTraitBindFrame
{
   uintptr_t status = 0;
   uintptr_t loop_return_address = 0;
   StatusIdentity identity{};
   uint32_t owner_thread_id = 0;
   uint64_t owner_tick_count = 0;
   std::array<uint32_t, kVirtualSlotCount> slots{};
   std::array<GemData, kVirtualSlotCount> gems{};
   uint32_t expected = 0;
   uint32_t injected = 0;
   int next_slot = kNativeInternalSlotCount;
   bool active = false;
};

struct NaturalContributionFrame
{
   uintptr_t status = 0;
   StatusIdentity identity{};
   std::array<uint32_t, kVirtualSlotCount> slots{};
   uint32_t expected = 0;
   uint32_t injected = 0;
   int next_slot = kNativeInternalSlotCount;
   bool active = false;
};

struct LocalContext1Binding
{
   uintptr_t manager = 0;
   uintptr_t record = 0;
   uintptr_t status = 0;
   uint32_t owner_key = 0;
   uint32_t character_hash = 0;
   uint32_t thread_id = 0;
   uint64_t generation = 0;
   bool active = false;
};

struct IatPatch
{
   void** slot = nullptr;
   void* original = nullptr;
   void* replacement = nullptr;
};

struct UiSettings
{
   int toggle_key = VK_NUMPAD8;
   bool show_equipped = false;
   bool auto_apply = true;
   std::string language = "zh-CN";
};

HMODULE g_module = nullptr;
uintptr_t g_image_base = 0;
std::filesystem::path g_module_directory;
std::filesystem::path g_config_path;
std::filesystem::path g_compatibility_path;

std::once_flag g_initialize_once;
std::atomic_bool g_initialized{false};
std::atomic_bool g_hooks_ready{false};
std::atomic_bool g_shutting_down{false};
std::atomic_bool g_shutdown_complete{false};
std::mutex g_message_mutex;
std::string g_runtime_message = "Waiting for initialization.";
bool g_runtime_message_is_error = false;

SafetyHookInline g_get_gem_hook;
SafetyHookMid g_trait_fetch_hook;
SafetyHookMid g_status_owner_tick_hook;
SafetyHookMid g_local_context1_bind_call_hook;
SafetyHookMid g_local_context1_bind_return_hook;
SafetyHookInline g_direct_input_get_state_hook;
SafetyHookInline g_direct_input_get_data_hook;

std::shared_mutex g_selection_mutex;
std::unordered_map<uint32_t, std::array<uint32_t, kVirtualSlotCount>> g_character_selections;
std::unordered_map<uint32_t, uint32_t> g_required_character_by_gem;
std::unordered_map<uint32_t, VirtualOwner> g_virtual_owner_by_slot_id;

std::shared_mutex g_authorization_mutex;
std::unordered_map<uintptr_t, AuthorizedStatus> g_authorized_statuses;
std::atomic<uint32_t> g_last_authorized_character_hash{0};
std::atomic<uint64_t> g_last_authorized_status_address{0};

enum EditSessionState : int32_t
{
   EditSessionUnknownLocked = 0,
   EditSessionEquipment = 1,
   EditSessionMissionLocked = 2,
   EditSessionFreeTraining = 3,
};

std::atomic_int32_t g_edit_session_state{EditSessionUnknownLocked};
std::atomic_uint32_t g_observed_character_hash{0};
std::atomic_uint64_t g_observed_status_address{0};
std::atomic_int32_t g_observed_status_context{-1};
std::atomic_uint32_t g_lifecycle_rebind_attempts{0};
std::atomic_uint64_t g_lifecycle_rebind_signature{0};
std::atomic_uint32_t g_lifecycle_signature_attempts{0};
std::atomic_uint64_t g_lifecycle_rebind_not_before_ms{0};

std::mutex g_inventory_index_mutex;
uintptr_t g_indexed_system_data = 0;
std::unordered_map<uint32_t, uintptr_t> g_inventory_by_slot_id;

std::atomic<uint32_t> g_last_character_hash{0};
std::atomic<int32_t> g_last_context_mode{-1};
std::atomic_uint64_t g_status_owner_manager_address{0};
std::atomic_uint32_t g_status_owner_thread_id{0};
std::atomic_uint64_t g_status_owner_tick_count{0};
std::atomic_uint32_t g_status_owner_character_count{0};
std::array<std::atomic_uint32_t, 4> g_status_owner_character_hashes{};
std::shared_mutex g_local_context1_binding_mutex;
std::unordered_map<uintptr_t, LocalContext1Binding> g_local_context1_bindings;
std::atomic_uint64_t g_local_context1_binding_generation{0};
std::atomic_uint32_t g_overlay_thread_id{0};
std::atomic_uint64_t g_overlay_frame_count{0};
std::atomic_uint64_t g_inventory_revision{0};
std::atomic_bool g_inventory_dirty{true};

std::atomic_bool g_pending_refresh{false};
std::atomic<uint32_t> g_pending_character_hash{0};
std::atomic_uint32_t g_pending_injected_count{0};
std::atomic_uint32_t g_next_apply_generation{0};
std::atomic_uint64_t g_queued_apply_request{0};
std::atomic_uint64_t g_apply_retry_not_before_ms{0};
std::atomic_bool g_apply_in_flight{false};
std::mutex g_reconcile_apply_mutex;
std::unordered_set<uint32_t> g_reconcile_apply_hashes;
std::atomic_uint64_t g_active_apply_generation{0};
std::atomic_uint64_t g_claimed_apply_generation{0};
std::atomic_uint32_t g_active_apply_thread_id{0};
std::atomic_uint64_t g_active_apply_status{0};
std::atomic_bool g_native_apply_call_active{false};
std::array<std::atomic_uint32_t, kVirtualSlotCount> g_active_apply_slots{};
std::atomic_uint32_t g_active_apply_expected_count{0};
std::atomic_uint64_t g_last_apply_generation{0};
std::atomic_uint32_t g_last_apply_character_hash{0};
std::atomic_uint32_t g_last_apply_expected_count{0};
std::atomic_uint32_t g_last_apply_injected_count{0};
std::atomic_int g_apply_result{0};
std::atomic_int g_last_consumed_apply_result{0};
std::atomic_uint32_t g_active_getter_calls{0};
std::atomic_uint32_t g_active_mid_calls{0};
std::atomic_uint32_t g_active_input_calls{0};
thread_local uint64_t g_tls_apply_generation = 0;
thread_local NaturalTraitBindFrame g_tls_natural_bind{};
thread_local NaturalContributionFrame g_tls_natural_contribution{};
thread_local LocalContext1Binding g_tls_local_context1_binding{};
std::atomic_uint64_t g_natural_bind_attempts{0};
std::atomic_uint64_t g_natural_bind_successes{0};
std::atomic_uint64_t g_natural_bind_status_address{0};
std::atomic_uint32_t g_natural_bind_character_hash{0};
std::atomic_int32_t g_natural_bind_context{-1};
std::atomic_uint32_t g_natural_bind_expected_count{0};
std::atomic_uint32_t g_natural_bind_injected_count{0};
std::atomic_int32_t g_natural_bind_result{NaturalBindNone};
std::atomic_uint32_t g_natural_bind_owner_key{0};
std::atomic_uint64_t g_natural_bind_owner_status_address{0};

std::atomic_bool g_input_capture_requested{false};
std::atomic_bool g_input_capture_effective{false};
std::atomic_uint32_t g_input_neutral_frames{0};
std::atomic_bool g_input_iat_hooks_ready{false};
std::atomic_bool g_direct_input_hook_ready{false};
std::mutex g_input_hook_mutex;
std::vector<IatPatch> g_iat_patches;
POINT g_frozen_cursor_position{};

using GetAsyncKeyStateFn = SHORT(WINAPI*)(int);
using GetKeyStateFn = SHORT(WINAPI*)(int);
using GetCursorPosFn = BOOL(WINAPI*)(LPPOINT);
using SetCursorPosFn = BOOL(WINAPI*)(int, int);
using ClipCursorFn = BOOL(WINAPI*)(const RECT*);
using XInputGetStateFn = DWORD(WINAPI*)(DWORD, XINPUT_STATE*);

GetAsyncKeyStateFn g_original_get_async_key_state = nullptr;
GetKeyStateFn g_original_get_key_state = nullptr;
GetCursorPosFn g_original_get_cursor_pos = nullptr;
SetCursorPosFn g_original_set_cursor_pos = nullptr;
ClipCursorFn g_original_clip_cursor = nullptr;
XInputGetStateFn g_original_xinput_get_state = nullptr;

UiSettings g_settings;
std::mutex g_settings_mutex;
std::unordered_map<uint32_t, std::string> g_sigil_names;
std::unordered_map<uint32_t, std::string> g_trait_names;
std::mutex g_inventory_snapshot_mutex;
std::vector<InventoryItem> g_inventory_snapshot;

enum ApplyResult : int
{
   ApplyResultNone = 0,
   ApplyResultAppliedDuringNativeRebuild = 2,
   ApplyResultSavedNoStatus = -1,
   ApplyResultVirtualCopyFailed = -2,
   ApplyResultOwnerThreadMismatch = -3,
   ApplyResultStatusLookupFailed = -4,
   ApplyResultNativeRebuildFailed = -5,
   ApplyResultNativeTraitLoopMissing = -6,
   ApplyResultNotifierFailed = -7,
};

struct ActiveCallGuard
{
   explicit ActiveCallGuard(std::atomic_uint32_t& counter) : counter(counter)
   {
      counter.fetch_add(1, std::memory_order_acq_rel);
   }
   ~ActiveCallGuard()
   {
      counter.fetch_sub(1, std::memory_order_acq_rel);
   }
   std::atomic_uint32_t& counter;
};

struct ApplyInFlightGuard
{
   ApplyInFlightGuard()
      : acquired(!g_apply_in_flight.exchange(true, std::memory_order_acq_rel))
   {
   }
   ~ApplyInFlightGuard()
   {
      if (acquired)
         g_apply_in_flight.store(false, std::memory_order_release);
   }
   bool acquired = false;
};

void Log(const std::string& message)
{
   const std::string line = "[GBFR ExtraSigilSlots20 Native] " + message + "\n";
   OutputDebugStringA(line.c_str());
}

void SetRuntimeMessage(std::string message, bool is_error)
{
   {
      std::scoped_lock lock(g_message_mutex);
      g_runtime_message = std::move(message);
      g_runtime_message_is_error = is_error;
   }
   Log(g_runtime_message);
}

std::string GetRuntimeMessage(bool& is_error)
{
   std::scoped_lock lock(g_message_mutex);
   is_error = g_runtime_message_is_error;
   return g_runtime_message;
}

std::string WideToUtf8(std::wstring_view text)
{
   if (text.empty())
      return {};
   const int size = WideCharToMultiByte(
      CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
   if (size <= 0)
      return {};
   std::string result(static_cast<size_t>(size), '\0');
   WideCharToMultiByte(
      CP_UTF8, 0, text.data(), static_cast<int>(text.size()), result.data(), size, nullptr, nullptr);
   return result;
}

std::string ToUpperHex(uint32_t value)
{
   std::ostringstream stream;
   stream << std::uppercase << std::hex << std::setw(8) << std::setfill('0') << value;
   return stream.str();
}

std::string ToLowerAscii(std::string value)
{
   std::transform(value.begin(), value.end(), value.begin(), [](unsigned char character) {
      return static_cast<char>(std::tolower(character));
   });
   return value;
}

bool SafeReadPointer(uintptr_t address, uintptr_t& value) noexcept
{
   __try
   {
      value = *reinterpret_cast<const uintptr_t*>(address);
      return true;
   }
   __except (EXCEPTION_EXECUTE_HANDLER)
   {
      value = 0;
      return false;
   }
}

bool SafeReadUiSelectedCharacterHash(uint32_t& character_hash) noexcept
{
   character_hash = 0;
   uintptr_t ui_manager = 0;
   if (g_image_base == 0 ||
       !SafeReadPointer(g_image_base + kUiManagerGlobalRva, ui_manager) || ui_manager == 0)
      return false;
   __try
   {
      const uint32_t value =
         *reinterpret_cast<const uint32_t*>(ui_manager + kUiSelectedCharacterHashOffset);
      if (value == 0 || value == kUnwornCharacterHash)
         return false;
      character_hash = value;
      return true;
   }
   __except (EXCEPTION_EXECUTE_HANDLER)
   {
      character_hash = 0;
      return false;
   }
}

bool SafeReadInt32(uintptr_t address, int32_t& value) noexcept
{
   __try
   {
      value = *reinterpret_cast<const int32_t*>(address);
      return true;
   }
   __except (EXCEPTION_EXECUTE_HANDLER)
   {
      value = -1;
      return false;
   }
}

void SafeReadUiModes(int32_t& ui_mode, int32_t& source_mode) noexcept
{
   ui_mode = -1;
   source_mode = -1;
   uintptr_t ui_manager = 0;
   if (g_image_base != 0 &&
       SafeReadPointer(g_image_base + kUiManagerGlobalRva, ui_manager) &&
       ui_manager != 0)
      (void)SafeReadInt32(ui_manager + kUiModeOffset, ui_mode);

   uintptr_t source = 0;
   if (g_image_base != 0 &&
       SafeReadPointer(g_image_base + kUiStateSourceGlobalRva, source) &&
       source != 0)
      (void)SafeReadInt32(source + kUiStateSourceModeOffset, source_mode);
}

bool SafeResolveSelectedCharacterStatus(
   uint32_t character_hash,
   uintptr_t& manager,
   uintptr_t& status,
   StatusIdentity& identity) noexcept;

void UpdateEditSessionState() noexcept
{
   uint32_t character_hash = 0;
   const bool has_character = SafeReadUiSelectedCharacterHash(character_hash);
   int32_t ui_mode = -1;
   int32_t source_mode = -1;
   SafeReadUiModes(ui_mode, source_mode);

   if (source_mode != 1 || ui_mode < 0)
   {
      g_edit_session_state.store(EditSessionUnknownLocked, std::memory_order_release);
      return;
   }
   if (!has_character || ui_mode == 4)
   {
      g_edit_session_state.store(EditSessionMissionLocked, std::memory_order_release);
      return;
   }
   if (ui_mode == 0)
   {
      g_edit_session_state.store(EditSessionFreeTraining, std::memory_order_release);
      return;
   }
   if (ui_mode != 1)
   {
      g_edit_session_state.store(EditSessionUnknownLocked, std::memory_order_release);
      return;
   }

   // ui_mode 1 is shared by Equipment, normal missions, and free training. Equipment is
   // the only observed 1/1 state whose exact UI-selected local status has context 0, so it
   // may explicitly open a fresh Equipment edit session. A context-1 battle may preserve
   // only a FreeTraining latch established by the practice menu's 0/1 state. It must never
   // inherit Equipment edit permission. If status lookup is transiently unavailable,
   // preserve the current fail-closed latch; SafeCanEditCharacter still requires an exact
   // status lookup before accepting a mutation.
   uintptr_t manager = 0;
   uintptr_t status = 0;
   StatusIdentity identity{};
   if (SafeResolveSelectedCharacterStatus(
          character_hash, manager, status, identity))
   {
      if (identity.context_mode == 0)
      {
         g_edit_session_state.store(EditSessionEquipment, std::memory_order_release);
      }
      else if (g_edit_session_state.load(std::memory_order_acquire) ==
               EditSessionEquipment)
      {
         g_edit_session_state.store(EditSessionMissionLocked, std::memory_order_release);
      }
   }
}

bool SafeReadGem(uintptr_t address, GemData& value) noexcept
{
   __try
   {
      std::memcpy(&value, reinterpret_cast<const void*>(address), sizeof(value));
      return true;
   }
   __except (EXCEPTION_EXECUTE_HANDLER)
   {
      std::memset(&value, 0, sizeof(value));
      return false;
   }
}

bool SafeReadStatusIdentity(uintptr_t status, StatusIdentity& identity) noexcept
{
   if (status == 0)
      return false;
   __try
   {
      identity.character_hash = *reinterpret_cast<const uint32_t*>(status + kStatusCharacterHashOffset);
      identity.context_mode = *reinterpret_cast<const int32_t*>(status + kStatusContextModeOffset);
      return identity.character_hash != 0;
   }
   __except (EXCEPTION_EXECUTE_HANDLER)
   {
      identity = {};
      return false;
   }
}

uint32_t SafeReadOwnerCharacterHashes(
   uintptr_t manager,
   std::array<uint32_t, 4>& hashes) noexcept
{
   for (uint32_t& hash : hashes)
      hash = 0;
   if (manager == 0)
      return 0;

   __try
   {
      const uintptr_t begin = *reinterpret_cast<const uintptr_t*>(manager + 0x20);
      const uintptr_t end = *reinterpret_cast<const uintptr_t*>(manager + 0x28);
      if (begin == 0 || end < begin)
         return 0;
      const uintptr_t byte_count = end - begin;
      if ((byte_count % sizeof(uint32_t)) != 0 || byte_count > 0x1000)
         return 0;
      const uint32_t count = static_cast<uint32_t>(std::min<uintptr_t>(
         byte_count / sizeof(uint32_t), hashes.size()));
      for (uint32_t index = 0; index < count; ++index)
         hashes[index] = reinterpret_cast<const uint32_t*>(begin)[index];
      return count;
   }
   __except (EXCEPTION_EXECUTE_HANDLER)
   {
      for (uint32_t& hash : hashes)
         hash = 0;
      return 0;
   }
}

bool SafeResolveStatusByMapKey(
   uintptr_t manager,
   uint32_t map_key,
   uintptr_t& status) noexcept
{
   status = 0;
   if (manager == 0 || map_key == 0)
      return false;

   __try
   {
      const uintptr_t sentinel =
         *reinterpret_cast<const uintptr_t*>(manager + kStatusMapSentinelOffset);
      const uintptr_t buckets =
         *reinterpret_cast<const uintptr_t*>(manager + kStatusMapBucketsOffset);
      const uint32_t mask =
         *reinterpret_cast<const uint32_t*>(manager + kStatusMapMaskOffset);
      if (sentinel == 0 || buckets == 0 || (sentinel & 0x7) != 0 || (buckets & 0x7) != 0)
         return false;

       const uintptr_t bucket_index = static_cast<uintptr_t>(map_key & mask);
      if (bucket_index > (~uintptr_t{0} - buckets) / 0x10)
         return false;
      const uintptr_t bucket = buckets + bucket_index * 0x10;
      uintptr_t node = *reinterpret_cast<const uintptr_t*>(bucket + 0x08);
      if (node == 0 || node == sentinel)
         return false;

       if (*reinterpret_cast<const uint32_t*>(node + 0x10) != map_key)
      {
         const uintptr_t chain_stop = *reinterpret_cast<const uintptr_t*>(bucket);
         bool found = false;
         for (uint32_t traversed = 0; traversed < 0x10000; ++traversed)
         {
            if (node == chain_stop)
               return false;
            node = *reinterpret_cast<const uintptr_t*>(node + 0x08);
            if (node == 0 || node == sentinel)
               return false;
             if (*reinterpret_cast<const uint32_t*>(node + 0x10) == map_key)
            {
               found = true;
               break;
            }
         }
         if (!found)
            return false;
      }

      if (node == sentinel)
         return false;
      status = *reinterpret_cast<const uintptr_t*>(node + 0x30);
      return status != 0;
   }
   __except (EXCEPTION_EXECUTE_HANDLER)
   {
      status = 0;
      return false;
   }
}

bool SafeResolveCharacterRecordByOwnerKey(
   uintptr_t manager,
   uint32_t owner_key,
   uintptr_t& record) noexcept
{
   record = 0;
   if (manager == 0 || owner_key == 0)
      return false;

   __try
   {
      const uintptr_t sentinel = *reinterpret_cast<const uintptr_t*>(
         manager + kCharacterRecordMapSentinelOffset);
      const uintptr_t buckets = *reinterpret_cast<const uintptr_t*>(
         manager + kCharacterRecordMapBucketsOffset);
      const uint32_t mask = *reinterpret_cast<const uint32_t*>(
         manager + kCharacterRecordMapMaskOffset);
      if (sentinel == 0 || buckets == 0 || (sentinel & 0x7) != 0 ||
          (buckets & 0x7) != 0)
         return false;

      const uintptr_t bucket_index = static_cast<uintptr_t>(owner_key & mask);
      if (bucket_index > (~uintptr_t{0} - buckets) / 0x10)
         return false;
      const uintptr_t bucket = buckets + bucket_index * 0x10;
      uintptr_t node = *reinterpret_cast<const uintptr_t*>(bucket + 0x08);
      if (node == 0 || node == sentinel)
         return false;

      if (*reinterpret_cast<const uint32_t*>(node + 0x10) != owner_key)
      {
         const uintptr_t chain_stop = *reinterpret_cast<const uintptr_t*>(bucket);
         bool found = false;
         for (uint32_t traversed = 0; traversed < 0x10000; ++traversed)
         {
            if (node == chain_stop)
               return false;
            node = *reinterpret_cast<const uintptr_t*>(node + 0x08);
            if (node == 0 || node == sentinel)
               return false;
            if (*reinterpret_cast<const uint32_t*>(node + 0x10) == owner_key)
            {
               found = true;
               break;
            }
         }
         if (!found)
            return false;
      }

      if (node == sentinel)
         return false;
      record = *reinterpret_cast<const uintptr_t*>(node + 0x18);
      return record != 0;
   }
   __except (EXCEPTION_EXECUTE_HANDLER)
   {
      record = 0;
      return false;
   }
}

bool SafeReadCharacterRecordHash(uintptr_t record, uint32_t& character_hash) noexcept
{
   character_hash = 0;
   if (record == 0)
      return false;
   __try
   {
      uint32_t value = *reinterpret_cast<const uint32_t*>(
         record + kCharacterRecordPrimaryHashOffset);
      if (value == kUnwornCharacterHash)
         value = *reinterpret_cast<const uint32_t*>(
            record + kCharacterRecordFallbackHashOffset);
      if (value == 0 || value == kUnwornCharacterHash)
         return false;
      character_hash = value;
      return true;
   }
   __except (EXCEPTION_EXECUTE_HANDLER)
   {
      character_hash = 0;
      return false;
   }
}

bool ValidateLocalContext1Binding(
   const LocalContext1Binding& binding,
   uintptr_t expected_status,
   const StatusIdentity* expected_identity = nullptr) noexcept
{
   if (binding.manager == 0 || binding.record == 0 || binding.status == 0 ||
       binding.owner_key != kLocalPlayerSlotKey || binding.character_hash == 0 ||
       (expected_status != 0 && binding.status != expected_status))
      return false;

   uintptr_t current_record = 0;
   uintptr_t current_status = 0;
   uint32_t current_character_hash = 0;
   if (!SafeResolveCharacterRecordByOwnerKey(
          binding.manager, binding.owner_key, current_record) ||
       current_record != binding.record ||
       !SafeResolveStatusByMapKey(
          binding.manager, binding.owner_key, current_status) ||
       current_status != binding.status ||
       !SafeReadCharacterRecordHash(binding.record, current_character_hash) ||
       current_character_hash != binding.character_hash)
      return false;

   if (expected_identity != nullptr &&
       (expected_identity->character_hash != binding.character_hash ||
        expected_identity->context_mode != 1))
      return false;
   return true;
}

bool TryGetLocalContext1Binding(
   uintptr_t status,
   uint32_t character_hash,
   LocalContext1Binding& binding) noexcept
{
   binding = {};
   std::shared_lock lock(g_local_context1_binding_mutex);
   const auto iterator = g_local_context1_bindings.find(status);
   if (iterator == g_local_context1_bindings.end() ||
       iterator->second.character_hash != character_hash)
      return false;
   binding = iterator->second;
   lock.unlock();
   return ValidateLocalContext1Binding(binding, status);
}

bool TryGetLocalContext1BindingByCharacter(
   uint32_t character_hash,
   LocalContext1Binding& binding) noexcept
{
   binding = {};
   {
      std::shared_lock lock(g_local_context1_binding_mutex);
      for (const auto& [status, candidate] : g_local_context1_bindings)
      {
         if (candidate.owner_key == kLocalPlayerSlotKey &&
             candidate.character_hash == character_hash)
         {
            binding = candidate;
            break;
         }
      }
   }
   return binding.status != 0 &&
      ValidateLocalContext1Binding(binding, binding.status);
}

bool SafeResolveCharacterStatus(
   uint32_t character_hash,
   uintptr_t& manager,
   uintptr_t& status) noexcept
{
   manager = 0;
   status = 0;
   return g_image_base != 0 && character_hash != 0 &&
      SafeReadPointer(g_image_base + kStatusManagerGlobalRva, manager) &&
      manager != 0 &&
      SafeResolveStatusByMapKey(manager, character_hash, status);
}

bool SafeResolveSelectedCharacterStatus(
   uint32_t character_hash,
   uintptr_t& manager,
   uintptr_t& status,
   StatusIdentity& identity) noexcept
{
   manager = 0;
   status = 0;
   identity = {};
   return character_hash != 0 &&
      SafeResolveCharacterStatus(character_hash, manager, status) &&
      SafeReadStatusIdentity(status, identity) &&
      identity.character_hash == character_hash &&
      identity.context_mode >= 0 && identity.context_mode <= 2;
}

bool SafeCanEditCharacter(uint32_t character_hash) noexcept
{
   UpdateEditSessionState();
   const int32_t edit_session = g_edit_session_state.load(std::memory_order_acquire);
   if (edit_session != EditSessionEquipment &&
       edit_session != EditSessionFreeTraining)
      return false;
   uint32_t ui_character_hash = 0;
   if (character_hash == 0 ||
       !SafeReadUiSelectedCharacterHash(ui_character_hash) ||
       ui_character_hash != character_hash)
      return false;

   uintptr_t manager = 0;
   uintptr_t status = 0;
   StatusIdentity identity{};
   return SafeResolveSelectedCharacterStatus(
      character_hash, manager, status, identity);
}

void MarkInventoryDirty() noexcept
{
   g_inventory_dirty.store(true, std::memory_order_release);
}

void CommitAuthorizedStatus(
   uintptr_t status,
   const StatusIdentity& identity,
   uint64_t generation,
   const std::array<uint32_t, kVirtualSlotCount>& slots)
{
   if (status == 0 || identity.character_hash == 0)
      return;

   std::unique_lock lock(g_authorization_mutex);
   for (auto iterator = g_authorized_statuses.begin(); iterator != g_authorized_statuses.end();)
   {
      if (iterator->second.character_hash == identity.character_hash || iterator->first == status)
         iterator = g_authorized_statuses.erase(iterator);
      else
         ++iterator;
   }
   AuthorizedStatus authorization{};
   authorization.status = status;
   authorization.character_hash = identity.character_hash;
   authorization.context_mode = identity.context_mode;
   authorization.generation = generation;
   authorization.slots = slots;
   g_authorized_statuses.emplace(status, authorization);
   g_last_authorized_character_hash.store(identity.character_hash, std::memory_order_release);
   g_last_authorized_status_address.store(status, std::memory_order_release);
}

bool TryGetAuthorizedSelection(
   uintptr_t status,
   const StatusIdentity& identity,
   std::array<uint32_t, kVirtualSlotCount>& slots)
{
   std::shared_lock lock(g_authorization_mutex);
   const auto iterator = g_authorized_statuses.find(status);
   if (iterator == g_authorized_statuses.end() ||
       iterator->second.character_hash != identity.character_hash ||
       iterator->second.status != status ||
       iterator->second.context_mode != identity.context_mode ||
       identity.context_mode < 0 || identity.context_mode > 2)
      return false;
   slots = iterator->second.slots;
   return true;
}

bool HasMatchingAuthorizedSelection(
   uintptr_t status,
   const StatusIdentity& identity,
   const std::array<uint32_t, kVirtualSlotCount>& slots)
{
   std::shared_lock lock(g_authorization_mutex);
   const auto iterator = g_authorized_statuses.find(status);
   return iterator != g_authorized_statuses.end() &&
      iterator->second.status == status &&
      iterator->second.character_hash == identity.character_hash &&
      iterator->second.context_mode == identity.context_mode &&
      iterator->second.slots == slots;
}

bool TryGetAuthorizedContext1Status(
   uint32_t character_hash,
   AuthorizedStatus& authorization)
{
   authorization = {};
   std::shared_lock lock(g_authorization_mutex);
   for (const auto& [status, candidate] : g_authorized_statuses)
   {
      if (candidate.character_hash == character_hash &&
          candidate.context_mode == 1 && candidate.status == status)
      {
         authorization = candidate;
         return true;
      }
   }
   return false;
}

void EraseAuthorizedStatus(uintptr_t status)
{
   if (status == 0)
      return;
   std::unique_lock lock(g_authorization_mutex);
   g_authorized_statuses.erase(status);
   if (g_authorized_statuses.empty())
   {
      g_last_authorized_character_hash.store(0, std::memory_order_release);
      g_last_authorized_status_address.store(0, std::memory_order_release);
   }
}

void ValidateAuthorizedStatuses()
{
   std::unique_lock lock(g_authorization_mutex);
   for (auto iterator = g_authorized_statuses.begin(); iterator != g_authorized_statuses.end();)
   {
      uintptr_t manager = 0;
      uintptr_t current_status = 0;
      StatusIdentity identity{};
      bool resolved = false;
      if (iterator->second.context_mode == 1)
      {
         current_status = iterator->second.status;
         resolved = current_status != 0;
      }
      else
      {
         resolved = SafeResolveCharacterStatus(
            iterator->second.character_hash, manager, current_status);
      }
      if (!resolved || current_status != iterator->second.status ||
          !SafeReadStatusIdentity(current_status, identity) ||
          identity.character_hash != iterator->second.character_hash ||
          identity.context_mode != iterator->second.context_mode ||
          identity.context_mode < 0 || identity.context_mode > 2)
         iterator = g_authorized_statuses.erase(iterator);
      else
         ++iterator;
   }
   if (g_authorized_statuses.empty())
   {
      g_last_authorized_character_hash.store(0, std::memory_order_release);
      g_last_authorized_status_address.store(0, std::memory_order_release);
   }
}

bool SafeCopyToOutput(const GemData& source, void* destination) noexcept
{
   if (destination == nullptr)
      return false;
   __try
   {
      std::memcpy(destination, &source, sizeof(source));
      return true;
   }
   __except (EXCEPTION_EXECUTE_HANDLER)
   {
      return false;
   }
}

bool SafeInvokeStatusRebuild(
   uintptr_t status,
   uint32_t character_hash,
   StatusIdentity& restored_identity,
   bool preserve_context) noexcept
{
   restored_identity = {};
   if (g_image_base == 0 || status == 0 || character_hash == 0)
      return false;

   StatusIdentity original_identity{};
   if (!SafeReadStatusIdentity(status, original_identity) ||
       original_identity.character_hash != character_hash ||
       original_identity.context_mode < 0 || original_identity.context_mode > 2)
      return false;

   bool rebuild_succeeded = false;
   bool identity_was_overridden = false;
   __try
   {
      if (!preserve_context)
      {
         *reinterpret_cast<uint32_t*>(status + kStatusCharacterHashOffset) = character_hash;
         *reinterpret_cast<int32_t*>(status + kStatusContextModeOffset) = 0;
         identity_was_overridden = true;
      }
      reinterpret_cast<void(__fastcall*)(void*)>(g_image_base + kStatusRebuildRva)(
         reinterpret_cast<void*>(status));
      rebuild_succeeded = true;
   }
   __except (EXCEPTION_EXECUTE_HANDLER)
   {
      rebuild_succeeded = false;
   }

   if (identity_was_overridden)
   {
      __try
      {
         *reinterpret_cast<uint32_t*>(status + kStatusCharacterHashOffset) =
            original_identity.character_hash;
         *reinterpret_cast<int32_t*>(status + kStatusContextModeOffset) =
            original_identity.context_mode;
      }
      __except (EXCEPTION_EXECUTE_HANDLER)
      {
         return false;
      }
   }
   return rebuild_succeeded &&
      SafeReadStatusIdentity(status, restored_identity) &&
      restored_identity.character_hash == original_identity.character_hash &&
      restored_identity.context_mode == original_identity.context_mode;
}

bool SafeNotifyStatusDirty(
   uintptr_t manager,
   uint32_t character_hash,
   uint32_t dirty_mask) noexcept
{
   if (g_image_base == 0 || manager == 0 || character_hash == 0)
      return false;
   __try
   {
      reinterpret_cast<void(__fastcall*)(void*, uint32_t, uint32_t)>(
         g_image_base + kStatusNotifierRva)(
         reinterpret_cast<void*>(manager), character_hash, dirty_mask);
      return true;
   }
   __except (EXCEPTION_EXECUTE_HANDLER)
   {
      return false;
   }
}

template <size_t Size>
bool MatchesBytes(uintptr_t address, const std::array<uint8_t, Size>& expected) noexcept
{
   __try
   {
      return std::memcmp(reinterpret_cast<const void*>(address), expected.data(), Size) == 0;
   }
   __except (EXCEPTION_EXECUTE_HANDLER)
   {
      return false;
   }
}

bool ReadByte(uintptr_t address, uint8_t& value) noexcept
{
   __try
   {
      value = *reinterpret_cast<const uint8_t*>(address);
      return true;
   }
   __except (EXCEPTION_EXECUTE_HANDLER)
   {
      value = 0;
      return false;
   }
}

bool WriteByte(uintptr_t address, uint8_t value)
{
   DWORD old_protection = 0;
   if (!VirtualProtect(reinterpret_cast<void*>(address), 1, PAGE_EXECUTE_READWRITE, &old_protection))
      return false;
   *reinterpret_cast<volatile uint8_t*>(address) = value;
   FlushInstructionCache(GetCurrentProcess(), reinterpret_cast<const void*>(address), 1);
   DWORD ignored = 0;
   const bool restored = VirtualProtect(reinterpret_cast<void*>(address), 1, old_protection, &ignored) != FALSE;
   uint8_t actual = 0;
   return restored && ReadByte(address, actual) && actual == value;
}

bool PatchMainModuleImport(
   const char* module_name,
   const char* function_name,
   void* replacement,
   void*& original)
{
   original = nullptr;
   if (g_image_base == 0 || module_name == nullptr || function_name == nullptr ||
       replacement == nullptr)
      return false;

   __try
   {
      const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(g_image_base);
      if (dos->e_magic != IMAGE_DOS_SIGNATURE)
         return false;
      const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(
         g_image_base + static_cast<uintptr_t>(dos->e_lfanew));
      if (nt->Signature != IMAGE_NT_SIGNATURE)
         return false;
      const IMAGE_DATA_DIRECTORY& directory =
         nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
      if (directory.VirtualAddress == 0 || directory.Size < sizeof(IMAGE_IMPORT_DESCRIPTOR))
         return false;

      auto* descriptor = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(
         g_image_base + directory.VirtualAddress);
      for (; descriptor->Name != 0; ++descriptor)
      {
         const char* imported_module =
            reinterpret_cast<const char*>(g_image_base + descriptor->Name);
         if (_stricmp(imported_module, module_name) != 0)
            continue;

         auto* first_thunk = reinterpret_cast<IMAGE_THUNK_DATA64*>(
            g_image_base + descriptor->FirstThunk);
         auto* name_thunk = descriptor->OriginalFirstThunk != 0
            ? reinterpret_cast<IMAGE_THUNK_DATA64*>(
                 g_image_base + descriptor->OriginalFirstThunk)
            : first_thunk;
         for (; name_thunk->u1.AddressOfData != 0; ++name_thunk, ++first_thunk)
         {
            if (IMAGE_SNAP_BY_ORDINAL64(name_thunk->u1.Ordinal))
               continue;
            const auto* import = reinterpret_cast<const IMAGE_IMPORT_BY_NAME*>(
               g_image_base + name_thunk->u1.AddressOfData);
            if (std::strcmp(reinterpret_cast<const char*>(import->Name), function_name) != 0)
               continue;

            auto** slot = reinterpret_cast<void**>(&first_thunk->u1.Function);
            DWORD old_protection = 0;
            if (!VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &old_protection))
               return false;
            original = InterlockedExchangePointer(
               reinterpret_cast<void* volatile*>(slot), replacement);
            DWORD ignored = 0;
            const bool restored =
               VirtualProtect(slot, sizeof(void*), old_protection, &ignored) != FALSE;
            if (!restored || original == nullptr)
               return false;
            g_iat_patches.push_back({slot, original, replacement});
            return true;
         }
      }
   }
   __except (EXCEPTION_EXECUTE_HANDLER)
   {
      original = nullptr;
      return false;
   }
   return false;
}

void RestoreInputIatHooks()
{
   for (auto iterator = g_iat_patches.rbegin(); iterator != g_iat_patches.rend(); ++iterator)
   {
      if (iterator->slot == nullptr || iterator->original == nullptr)
         continue;
      DWORD old_protection = 0;
      if (!VirtualProtect(iterator->slot, sizeof(void*), PAGE_READWRITE, &old_protection))
         continue;
      if (*iterator->slot == iterator->replacement)
         InterlockedExchangePointer(
            reinterpret_cast<void* volatile*>(iterator->slot), iterator->original);
      DWORD ignored = 0;
      (void)VirtualProtect(iterator->slot, sizeof(void*), old_protection, &ignored);
   }
   g_iat_patches.clear();
   g_input_iat_hooks_ready.store(false, std::memory_order_release);
}

SHORT WINAPI GetAsyncKeyStateDetour(int virtual_key)
{
   ActiveCallGuard guard(g_active_input_calls);
   if (g_input_capture_effective.load(std::memory_order_acquire))
      return 0;
   return g_original_get_async_key_state != nullptr
      ? g_original_get_async_key_state(virtual_key)
      : 0;
}

SHORT WINAPI GetKeyStateDetour(int virtual_key)
{
   ActiveCallGuard guard(g_active_input_calls);
   if (g_input_capture_effective.load(std::memory_order_acquire))
      return 0;
   return g_original_get_key_state != nullptr ? g_original_get_key_state(virtual_key) : 0;
}

BOOL WINAPI GetCursorPosDetour(LPPOINT point)
{
   ActiveCallGuard guard(g_active_input_calls);
   if (g_input_capture_effective.load(std::memory_order_acquire))
   {
      if (point != nullptr)
         *point = g_frozen_cursor_position;
      return TRUE;
   }
   return g_original_get_cursor_pos != nullptr ? g_original_get_cursor_pos(point) : FALSE;
}

BOOL WINAPI SetCursorPosDetour(int x, int y)
{
   ActiveCallGuard guard(g_active_input_calls);
   if (g_input_capture_effective.load(std::memory_order_acquire))
      return TRUE;
   return g_original_set_cursor_pos != nullptr ? g_original_set_cursor_pos(x, y) : FALSE;
}

BOOL WINAPI ClipCursorDetour(const RECT* rectangle)
{
   ActiveCallGuard guard(g_active_input_calls);
   if (g_input_capture_effective.load(std::memory_order_acquire))
   {
      if (g_original_clip_cursor != nullptr)
         (void)g_original_clip_cursor(nullptr);
      return TRUE;
   }
   return g_original_clip_cursor != nullptr ? g_original_clip_cursor(rectangle) : FALSE;
}

DWORD WINAPI XInputGetStateDetour(DWORD user_index, XINPUT_STATE* state)
{
   ActiveCallGuard guard(g_active_input_calls);
   if (g_original_xinput_get_state == nullptr)
      return ERROR_DEVICE_NOT_CONNECTED;
   const DWORD result = g_original_xinput_get_state(user_index, state);
   if (result == ERROR_SUCCESS && state != nullptr &&
       g_input_capture_effective.load(std::memory_order_acquire))
      std::memset(&state->Gamepad, 0, sizeof(state->Gamepad));
   return result;
}

HRESULT __fastcall DirectInputGetDeviceStateDetour(
   void* device,
   DWORD data_size,
   void* data)
{
   ActiveCallGuard guard(g_active_input_calls);
   const HRESULT result = g_direct_input_get_state_hook.call<HRESULT>(
      device, data_size, data);
   if (SUCCEEDED(result) && data != nullptr && data_size != 0 &&
       g_input_capture_effective.load(std::memory_order_acquire))
      std::memset(data, 0, data_size);
   return result;
}

HRESULT __fastcall DirectInputGetDeviceDataDetour(
   void* device,
   DWORD object_data_size,
   void* object_data,
   DWORD* object_count,
   DWORD flags)
{
   ActiveCallGuard guard(g_active_input_calls);
   const DWORD requested = object_count != nullptr ? *object_count : 0;
   const HRESULT result = g_direct_input_get_data_hook.call<HRESULT>(
      device, object_data_size, object_data, object_count, flags);
   if (SUCCEEDED(result) && object_count != nullptr &&
       g_input_capture_effective.load(std::memory_order_acquire))
   {
      if (object_data != nullptr && object_data_size != 0 && requested != 0)
         std::memset(object_data, 0, static_cast<size_t>(object_data_size) * requested);
      *object_count = 0;
   }
   return result;
}

bool InstallInputIatHooks()
{
   std::scoped_lock lock(g_input_hook_mutex);
   if (g_input_iat_hooks_ready.load(std::memory_order_acquire))
      return true;

   void* original = nullptr;
   bool succeeded = PatchMainModuleImport(
      "USER32.dll", "GetAsyncKeyState", reinterpret_cast<void*>(&GetAsyncKeyStateDetour), original);
   g_original_get_async_key_state = reinterpret_cast<GetAsyncKeyStateFn>(original);

   original = nullptr;
   succeeded = PatchMainModuleImport(
      "USER32.dll", "GetKeyState", reinterpret_cast<void*>(&GetKeyStateDetour), original) && succeeded;
   g_original_get_key_state = reinterpret_cast<GetKeyStateFn>(original);

   original = nullptr;
   succeeded = PatchMainModuleImport(
      "USER32.dll", "GetCursorPos", reinterpret_cast<void*>(&GetCursorPosDetour), original) && succeeded;
   g_original_get_cursor_pos = reinterpret_cast<GetCursorPosFn>(original);

   original = nullptr;
   succeeded = PatchMainModuleImport(
      "USER32.dll", "SetCursorPos", reinterpret_cast<void*>(&SetCursorPosDetour), original) && succeeded;
   g_original_set_cursor_pos = reinterpret_cast<SetCursorPosFn>(original);

   original = nullptr;
   succeeded = PatchMainModuleImport(
      "USER32.dll", "ClipCursor", reinterpret_cast<void*>(&ClipCursorDetour), original) && succeeded;
   g_original_clip_cursor = reinterpret_cast<ClipCursorFn>(original);

   original = nullptr;
   succeeded = PatchMainModuleImport(
      "XINPUT9_1_0.dll", "XInputGetState", reinterpret_cast<void*>(&XInputGetStateDetour), original) && succeeded;
   g_original_xinput_get_state = reinterpret_cast<XInputGetStateFn>(original);

   g_input_iat_hooks_ready.store(succeeded, std::memory_order_release);
   Log(succeeded
      ? "Game-local USER32 and XInput input gates installed."
      : "One or more game-local USER32/XInput input gates failed to install.");
   return succeeded;
}

bool IsExecutableAddress(uintptr_t address) noexcept
{
   MEMORY_BASIC_INFORMATION information{};
   if (address == 0 || VirtualQuery(
          reinterpret_cast<const void*>(address), &information, sizeof(information)) == 0)
      return false;
   const DWORD protection = information.Protect & 0xFF;
   return information.State == MEM_COMMIT &&
      (protection == PAGE_EXECUTE || protection == PAGE_EXECUTE_READ ||
       protection == PAGE_EXECUTE_READWRITE || protection == PAGE_EXECUTE_WRITECOPY);
}

void TryInstallDirectInputHooks()
{
   if (g_direct_input_hook_ready.load(std::memory_order_acquire) || g_image_base == 0)
      return;
   std::scoped_lock lock(g_input_hook_mutex);
   if (g_direct_input_hook_ready.load(std::memory_order_acquire))
      return;

   uintptr_t context = 0;
   uintptr_t device = 0;
   uintptr_t vtable = 0;
   uintptr_t get_state = 0;
   uintptr_t get_data = 0;
   if (!SafeReadPointer(g_image_base + kInputContextGlobalRva, context) || context == 0 ||
       !SafeReadPointer(context + sizeof(uintptr_t), device) || device == 0 ||
       !SafeReadPointer(device, vtable) || vtable == 0 ||
       !SafeReadPointer(vtable + sizeof(uintptr_t) * 9, get_state) ||
       !SafeReadPointer(vtable + sizeof(uintptr_t) * 10, get_data) ||
       !IsExecutableAddress(get_state) || !IsExecutableAddress(get_data))
      return;

   g_direct_input_get_state_hook = safetyhook::create_inline(
      reinterpret_cast<void*>(get_state), reinterpret_cast<void*>(&DirectInputGetDeviceStateDetour));
   if (!g_direct_input_get_state_hook)
      return;
   g_direct_input_get_data_hook = safetyhook::create_inline(
      reinterpret_cast<void*>(get_data), reinterpret_cast<void*>(&DirectInputGetDeviceDataDetour));
   if (!g_direct_input_get_data_hook)
   {
      g_direct_input_get_state_hook.reset();
      return;
   }
   g_direct_input_hook_ready.store(true, std::memory_order_release);
   Log("Existing DirectInput mouse device state/data gates installed.");
}

bool IsPhysicalInputNeutral()
{
   if (g_original_get_async_key_state != nullptr)
   {
      for (int key = VK_BACK; key <= 0xFE; ++key)
         if ((g_original_get_async_key_state(key) & 0x8000) != 0)
            return false;
   }
   if (g_original_xinput_get_state != nullptr)
   {
      for (DWORD user = 0; user < XUSER_MAX_COUNT; ++user)
      {
         XINPUT_STATE state{};
         if (g_original_xinput_get_state(user, &state) != ERROR_SUCCESS)
            continue;
         const XINPUT_GAMEPAD& pad = state.Gamepad;
         if (pad.wButtons != 0 || pad.bLeftTrigger > XINPUT_GAMEPAD_TRIGGER_THRESHOLD ||
             pad.bRightTrigger > XINPUT_GAMEPAD_TRIGGER_THRESHOLD ||
             std::abs(static_cast<int>(pad.sThumbLX)) > XINPUT_GAMEPAD_LEFT_THUMB_DEADZONE ||
             std::abs(static_cast<int>(pad.sThumbLY)) > XINPUT_GAMEPAD_LEFT_THUMB_DEADZONE ||
             std::abs(static_cast<int>(pad.sThumbRX)) > XINPUT_GAMEPAD_RIGHT_THUMB_DEADZONE ||
             std::abs(static_cast<int>(pad.sThumbRY)) > XINPUT_GAMEPAD_RIGHT_THUMB_DEADZONE)
            return false;
      }
   }
   return true;
}

void UpdateInputCaptureBarrier()
{
   if (g_input_capture_requested.load(std::memory_order_acquire))
   {
      g_input_neutral_frames.store(0, std::memory_order_release);
      g_input_capture_effective.store(true, std::memory_order_release);
      return;
   }
   if (!g_input_capture_effective.load(std::memory_order_acquire))
      return;
   if (!IsPhysicalInputNeutral())
   {
      g_input_neutral_frames.store(0, std::memory_order_release);
      return;
   }
   const uint32_t neutral_frames =
      g_input_neutral_frames.fetch_add(1, std::memory_order_acq_rel) + 1;
   if (neutral_frames >= 2)
      g_input_capture_effective.store(false, std::memory_order_release);
}

std::string ComputeFileSha256(const std::filesystem::path& path)
{
   BCRYPT_ALG_HANDLE algorithm = nullptr;
   BCRYPT_HASH_HANDLE hash = nullptr;
   HANDLE file = INVALID_HANDLE_VALUE;
   std::vector<uint8_t> hash_object;
   std::vector<uint8_t> digest;
   std::string result;
   DWORD object_size = 0;
   DWORD digest_size = 0;
   DWORD returned = 0;

   if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0)
      goto cleanup;

   if (BCryptGetProperty(
          algorithm,
          BCRYPT_OBJECT_LENGTH,
          reinterpret_cast<PUCHAR>(&object_size),
          sizeof(object_size),
          &returned,
          0) < 0 ||
       BCryptGetProperty(
          algorithm,
          BCRYPT_HASH_LENGTH,
          reinterpret_cast<PUCHAR>(&digest_size),
          sizeof(digest_size),
          &returned,
          0) < 0)
      goto cleanup;

   hash_object.resize(object_size);
   digest.resize(digest_size);
   if (BCryptCreateHash(
          algorithm,
          &hash,
          hash_object.data(),
          static_cast<ULONG>(hash_object.size()),
          nullptr,
          0,
          0) < 0)
      goto cleanup;

   file = CreateFileW(
      path.c_str(),
      GENERIC_READ,
      FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
      nullptr,
      OPEN_EXISTING,
      FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN,
      nullptr);
   if (file == INVALID_HANDLE_VALUE)
      goto cleanup;

   {
      // Keep the file buffer on the heap so executable hash verification does not depend on
      // the loader thread's stack reserve.
      std::vector<uint8_t> buffer(64 * 1024);
      for (;;)
      {
         DWORD bytes_read = 0;
         if (!ReadFile(file, buffer.data(), static_cast<DWORD>(buffer.size()), &bytes_read, nullptr))
            goto cleanup;
         if (bytes_read == 0)
            break;
         if (BCryptHashData(hash, buffer.data(), bytes_read, 0) < 0)
            goto cleanup;
      }
   }

   if (BCryptFinishHash(hash, digest.data(), static_cast<ULONG>(digest.size()), 0) < 0)
      goto cleanup;

   {
      std::ostringstream stream;
      stream << std::uppercase << std::hex << std::setfill('0');
      for (const uint8_t byte : digest)
         stream << std::setw(2) << static_cast<unsigned int>(byte);
      result = stream.str();
   }

cleanup:
   if (file != INVALID_HANDLE_VALUE)
      CloseHandle(file);
   if (hash != nullptr)
      BCryptDestroyHash(hash);
   if (algorithm != nullptr)
      BCryptCloseAlgorithmProvider(algorithm, 0);
   return result;
}

std::wstring ReadIniString(const wchar_t* section, const wchar_t* key, const wchar_t* fallback)
{
   std::array<wchar_t, 1024> buffer{};
   GetPrivateProfileStringW(
      section,
      key,
      fallback,
      buffer.data(),
      static_cast<DWORD>(buffer.size()),
      g_config_path.c_str());
   return buffer.data();
}

void WriteIniInt(const wchar_t* section, const wchar_t* key, int value)
{
   const std::wstring text = std::to_wstring(value);
   WritePrivateProfileStringW(section, key, text.c_str(), g_config_path.c_str());
}

std::wstring CharacterSectionName(uint32_t character_hash)
{
   wchar_t buffer[32]{};
   swprintf_s(buffer, L"Character_%08X", character_hash);
   return buffer;
}

void SaveCharacterSelection(uint32_t character_hash)
{
   std::array<uint32_t, kVirtualSlotCount> slots{};
   {
      std::shared_lock lock(g_selection_mutex);
      const auto iterator = g_character_selections.find(character_hash);
      if (iterator != g_character_selections.end())
         slots = iterator->second;
   }

   std::wostringstream stream;
   stream << std::uppercase << std::hex << std::setfill(L'0');
   for (size_t index = 0; index < slots.size(); ++index)
   {
      if (index != 0)
         stream << L',';
      stream << std::setw(8) << slots[index];
   }
   const std::wstring section = CharacterSectionName(character_hash);
   const std::wstring value = stream.str();
   WritePrivateProfileStringW(section.c_str(), L"Slots", value.c_str(), g_config_path.c_str());
}

std::array<uint32_t, kVirtualSlotCount> ParseSlots(std::wstring_view text)
{
   std::array<uint32_t, kVirtualSlotCount> slots{};
   size_t slot_index = 0;
   size_t offset = 0;
   while (slot_index < slots.size() && offset <= text.size())
   {
      const size_t comma = text.find(L',', offset);
      const std::wstring token(text.substr(offset, comma == std::wstring_view::npos ? text.size() - offset : comma - offset));
      wchar_t* end = nullptr;
      const unsigned long value = std::wcstoul(token.c_str(), &end, 16);
      if (end != token.c_str())
         slots[slot_index] = static_cast<uint32_t>(value);
      ++slot_index;
      if (comma == std::wstring_view::npos)
         break;
      offset = comma + 1;
   }
   return slots;
}

void LoadSettingsAndSelections()
{
   UiSettings settings;
   settings.toggle_key = std::clamp(
      static_cast<int>(GetPrivateProfileIntW(L"Settings", L"ToggleKey", VK_NUMPAD8, g_config_path.c_str())),
      1,
      255);
   settings.show_equipped =
      GetPrivateProfileIntW(L"Settings", L"ShowEquipped", 0, g_config_path.c_str()) != 0;
   settings.auto_apply =
      GetPrivateProfileIntW(L"Settings", L"AutoApply", 1, g_config_path.c_str()) != 0;
   settings.language = WideToUtf8(ReadIniString(L"Settings", L"Language", L"zh-CN"));
   {
      std::scoped_lock lock(g_settings_mutex);
      g_settings = std::move(settings);
   }

   std::vector<wchar_t> section_names(65536, L'\0');
   const DWORD copied = GetPrivateProfileSectionNamesW(
      section_names.data(), static_cast<DWORD>(section_names.size()), g_config_path.c_str());
   if (copied == 0)
      return;

   std::unique_lock lock(g_selection_mutex);
   for (const wchar_t* section = section_names.data(); *section != L'\0'; section += std::wcslen(section) + 1)
   {
      constexpr std::wstring_view prefix = L"Character_";
      const std::wstring_view name(section);
      if (!name.starts_with(prefix) || name.size() != prefix.size() + 8)
         continue;
      wchar_t* end = nullptr;
      const uint32_t hash = static_cast<uint32_t>(std::wcstoul(section + prefix.size(), &end, 16));
      if (hash == 0 || end == section + prefix.size())
         continue;
      g_character_selections[hash] = ParseSlots(ReadIniString(section, L"Slots", L""));
   }

   std::vector<uint32_t> character_hashes;
   character_hashes.reserve(g_character_selections.size());
   for (const auto& [hash, slots] : g_character_selections)
      character_hashes.push_back(hash);
   std::sort(character_hashes.begin(), character_hashes.end());

   std::unordered_set<uint32_t> claimed_slot_ids;
   std::unordered_set<uint32_t> changed_characters;
   g_virtual_owner_by_slot_id.clear();
   for (const uint32_t hash : character_hashes)
   {
      auto& slots = g_character_selections[hash];
      for (int index = 0; index < kVirtualSlotCount; ++index)
      {
         uint32_t& slot_id = slots[static_cast<size_t>(index)];
         if (slot_id == 0)
            continue;
         if (!claimed_slot_ids.emplace(slot_id).second)
         {
            slot_id = 0;
            changed_characters.emplace(hash);
            continue;
         }
         g_virtual_owner_by_slot_id[slot_id] = {hash, index};
      }
   }
   lock.unlock();
   for (const uint32_t hash : changed_characters)
      SaveCharacterSelection(hash);
}

void SaveUiSettings()
{
   UiSettings settings;
   {
      std::scoped_lock lock(g_settings_mutex);
      settings = g_settings;
   }
   WriteIniInt(L"Settings", L"ToggleKey", settings.toggle_key);
   WriteIniInt(L"Settings", L"ShowEquipped", settings.show_equipped ? 1 : 0);
   WriteIniInt(L"Settings", L"AutoApply", settings.auto_apply ? 1 : 0);
   const std::wstring language(settings.language.begin(), settings.language.end());
   WritePrivateProfileStringW(L"Settings", L"Language", language.c_str(), g_config_path.c_str());
}

void LoadNameTable(const std::filesystem::path& path)
{
   std::ifstream stream(path, std::ios::binary);
   if (!stream)
   {
      Log("Name table is missing: " + path.filename().string());
      return;
   }

   std::string line;
   while (std::getline(stream, line))
   {
      if (!line.empty() && line.back() == '\r')
         line.pop_back();
      if (line.size() < 12 || line[1] != '\t')
         continue;
      const size_t second_tab = line.find('\t', 2);
      if (second_tab == std::string::npos)
         continue;
      uint32_t hash = 0;
      const std::string_view hash_text(line.data() + 2, second_tab - 2);
      const auto conversion = std::from_chars(hash_text.data(), hash_text.data() + hash_text.size(), hash, 16);
      if (conversion.ec != std::errc{} || hash == 0)
         continue;
      std::string text = line.substr(second_tab + 1);
      if (line[0] == 'S')
         g_sigil_names[hash] = std::move(text);
      else if (line[0] == 'T')
         g_trait_names[hash] = std::move(text);
   }
}

bool LoadCompatibilityTable(const std::filesystem::path& path)
{
   g_required_character_by_gem.clear();
   std::ifstream stream(path, std::ios::binary);
   if (!stream)
   {
      Log("Compatibility table is missing: " + path.filename().string());
      return false;
   }

   std::string line;
   uint32_t loaded = 0;
   while (std::getline(stream, line))
   {
      if (!line.empty() && line.back() == '\r')
         line.pop_back();
      if (line.empty() || line.front() == '#')
         continue;
      const size_t first_tab = line.find('\t');
      const size_t second_tab =
         first_tab == std::string::npos ? std::string::npos : line.find('\t', first_tab + 1);
      if (first_tab == std::string::npos)
         continue;
      const std::string_view gem_text(line.data(), first_tab);
      const size_t character_end = second_tab == std::string::npos ? line.size() : second_tab;
      const std::string_view character_text(
         line.data() + first_tab + 1, character_end - first_tab - 1);
      uint32_t gem_hash = 0;
      uint32_t character_hash = 0;
      const auto gem_conversion = std::from_chars(
         gem_text.data(), gem_text.data() + gem_text.size(), gem_hash, 16);
      const auto character_conversion = std::from_chars(
         character_text.data(),
         character_text.data() + character_text.size(),
         character_hash,
         16);
      if (gem_conversion.ec != std::errc{} ||
          character_conversion.ec != std::errc{} ||
          gem_hash == 0 || character_hash == 0)
         continue;
      g_required_character_by_gem[gem_hash] = character_hash;
      ++loaded;
   }
   Log("Loaded " + std::to_string(loaded) + " character-restricted sigil mappings.");
   return loaded == kExpectedCompatibilityMappingCount &&
      g_required_character_by_gem.size() == kExpectedCompatibilityMappingCount;
}

uint32_t GetRequiredCharacterHash(uint32_t gem_hash)
{
   const auto iterator = g_required_character_by_gem.find(gem_hash);
   return iterator == g_required_character_by_gem.end() ? 0 : iterator->second;
}

std::string LookupName(
   const std::unordered_map<uint32_t, std::string>& table,
   uint32_t hash,
   const char* prefix)
{
   if (hash == 0)
      return "-";
   const auto iterator = table.find(hash);
   if (iterator != table.end())
      return iterator->second;
   return std::string(prefix) + ToUpperHex(hash);
}

std::array<uint32_t, kVirtualSlotCount> GetSelection(uint32_t character_hash)
{
   std::shared_lock lock(g_selection_mutex);
   const auto iterator = g_character_selections.find(character_hash);
   return iterator == g_character_selections.end()
      ? std::array<uint32_t, kVirtualSlotCount>{}
      : iterator->second;
}

uintptr_t ResolveGemAddress(uint32_t slot_id);
uint32_t RequestHotApply(uint32_t character_hash);

void ScheduleReconcileApply(uint32_t character_hash)
{
   if (character_hash == 0)
      return;
   std::scoped_lock lock(g_reconcile_apply_mutex);
   g_reconcile_apply_hashes.emplace(character_hash);
}

void PumpReconcileApplyQueue()
{
   if (g_queued_apply_request.load(std::memory_order_acquire) != 0)
      return;
   uint32_t character_hash = 0;
   {
      std::scoped_lock lock(g_reconcile_apply_mutex);
      if (g_reconcile_apply_hashes.empty())
         return;
      const auto iterator = g_reconcile_apply_hashes.begin();
      character_hash = *iterator;
      g_reconcile_apply_hashes.erase(iterator);
   }
   RequestHotApply(character_hash);
}

uint32_t RequestHotApply(uint32_t character_hash)
{
   if (character_hash == 0)
   {
      g_apply_result.store(ApplyResultSavedNoStatus, std::memory_order_release);
      return 0;
   }
   uint32_t generation =
      g_next_apply_generation.fetch_add(1, std::memory_order_acq_rel) + 1;
   if (generation == 0)
      generation = g_next_apply_generation.fetch_add(1, std::memory_order_acq_rel) + 1;
   const uint64_t request =
      (static_cast<uint64_t>(generation) << 32) | static_cast<uint64_t>(character_hash);
   g_apply_retry_not_before_ms.store(0, std::memory_order_release);
   g_queued_apply_request.store(request, std::memory_order_release);
   return generation;
}

void RequeueHotApply(uint64_t request, uint64_t delay_ms)
{
   if (request == 0)
      return;
   uint64_t expected = 0;
   if (g_queued_apply_request.compare_exchange_strong(
          expected, request, std::memory_order_acq_rel, std::memory_order_acquire))
      g_apply_retry_not_before_ms.store(GetTickCount64() + delay_ms, std::memory_order_release);
}

void ProcessPendingHotApply()
{
   if (GetTickCount64() <
       g_apply_retry_not_before_ms.load(std::memory_order_acquire))
      return;
   const uint64_t request = g_queued_apply_request.exchange(0, std::memory_order_acq_rel);
   if (request == 0)
      return;

   ApplyInFlightGuard in_flight;
   if (!in_flight.acquired)
   {
      RequeueHotApply(request, 16);
      return;
   }

   const uint64_t generation = request >> 32;
   const uint32_t character_hash = static_cast<uint32_t>(request);
   g_pending_character_hash.store(character_hash, std::memory_order_release);
   g_last_apply_generation.store(generation, std::memory_order_release);
   g_last_apply_character_hash.store(character_hash, std::memory_order_release);
   g_last_apply_expected_count.store(0, std::memory_order_release);
   g_last_apply_injected_count.store(0, std::memory_order_release);
   if (!g_hooks_ready.load(std::memory_order_acquire) || character_hash == 0)
   {
      g_apply_result.store(ApplyResultSavedNoStatus, std::memory_order_release);
      return;
   }

   const uint32_t current_thread_id = GetCurrentThreadId();
   const int32_t edit_session =
      g_edit_session_state.load(std::memory_order_acquire);
   AuthorizedStatus context1_authorization{};
   const bool use_context1_status =
      (edit_session == EditSessionFreeTraining ||
       edit_session == EditSessionMissionLocked) &&
      TryGetAuthorizedContext1Status(character_hash, context1_authorization);

   uintptr_t manager = 0;
   uintptr_t status = 0;
   if (use_context1_status)
   {
      status = context1_authorization.status;
   }
   else if (!SafeResolveCharacterStatus(character_hash, manager, status))
   {
      g_apply_result.store(ApplyResultStatusLookupFailed, std::memory_order_release);
      RequeueHotApply(request, 100);
      return;
   }

   StatusIdentity original_identity{};
   if (!SafeReadStatusIdentity(status, original_identity) ||
       original_identity.character_hash != character_hash ||
       original_identity.context_mode < 0 || original_identity.context_mode > 2)
   {
      g_apply_result.store(ApplyResultStatusLookupFailed, std::memory_order_release);
      RequeueHotApply(request, 100);
      return;
   }

   const std::array<uint32_t, kVirtualSlotCount> selection = GetSelection(character_hash);
   uint32_t expected = 0;
   for (size_t index = 0; index < selection.size(); ++index)
   {
      g_active_apply_slots[index].store(selection[index], std::memory_order_release);
      if (selection[index] != 0)
         ++expected;
   }
   g_active_apply_expected_count.store(expected, std::memory_order_release);
   g_last_apply_expected_count.store(expected, std::memory_order_release);
   g_pending_injected_count.store(0, std::memory_order_release);
   g_claimed_apply_generation.store(0, std::memory_order_release);
   g_active_apply_thread_id.store(current_thread_id, std::memory_order_release);
   g_active_apply_status.store(status, std::memory_order_release);
   g_active_apply_generation.store(generation, std::memory_order_release);
   g_pending_refresh.store(true, std::memory_order_release);
   g_native_apply_call_active.store(true, std::memory_order_release);

   g_tls_apply_generation = generation;
   StatusIdentity restored_identity{};
   const bool rebuild_succeeded =
      SafeInvokeStatusRebuild(
         status, character_hash, restored_identity, use_context1_status);
   g_tls_apply_generation = 0;
   g_native_apply_call_active.store(false, std::memory_order_release);
   g_active_apply_status.store(0, std::memory_order_release);
   const uint64_t active_after_rebuild =
      g_active_apply_generation.load(std::memory_order_acquire);
   const bool trait_loop_claimed =
      g_claimed_apply_generation.load(std::memory_order_acquire) == generation;
   const uint32_t injected = g_pending_injected_count.load(std::memory_order_acquire);
   g_last_apply_injected_count.store(injected, std::memory_order_release);
   g_pending_refresh.store(false, std::memory_order_release);
   if (active_after_rebuild == generation)
      g_active_apply_generation.store(0, std::memory_order_release);

   if (!rebuild_succeeded)
   {
      EraseAuthorizedStatus(status);
      g_apply_result.store(ApplyResultNativeRebuildFailed, std::memory_order_release);
      return;
   }
   if (!trait_loop_claimed || active_after_rebuild == generation)
   {
      EraseAuthorizedStatus(status);
      g_apply_result.store(ApplyResultNativeTraitLoopMissing, std::memory_order_release);
      return;
   }

   if (injected == expected)
   {
      if (expected == 0)
      {
         std::unique_lock lock(g_authorization_mutex);
         g_authorized_statuses.erase(status);
         if (g_authorized_statuses.empty())
         {
            g_last_authorized_character_hash.store(0, std::memory_order_release);
            g_last_authorized_status_address.store(0, std::memory_order_release);
         }
      }
      else
      {
         if (restored_identity.character_hash == character_hash &&
             restored_identity.context_mode == original_identity.context_mode)
            CommitAuthorizedStatus(status, restored_identity, generation, selection);
      }
   }
   else
   {
      EraseAuthorizedStatus(status);
   }
   g_apply_retry_not_before_ms.store(0, std::memory_order_release);
   MarkInventoryDirty();

   if (!use_context1_status &&
       !SafeNotifyStatusDirty(manager, character_hash, 0xFFFFFFFFu))
      g_apply_result.store(ApplyResultNotifierFailed, std::memory_order_release);
}

bool SetSelection(uint32_t character_hash, int virtual_slot, uint32_t inventory_slot_id)
{
   if (character_hash == 0 || virtual_slot < 0 || virtual_slot >= kVirtualSlotCount ||
       !SafeCanEditCharacter(character_hash))
      return false;

   if (inventory_slot_id != 0)
   {
      const uintptr_t source_address = ResolveGemAddress(inventory_slot_id);
      GemData source{};
      const uint32_t required_character = source_address == 0
         ? 0
         : (SafeReadGem(source_address, source)
               ? GetRequiredCharacterHash(source.gem_id)
               : 0);
      if (source_address == 0 || source.slot_id != inventory_slot_id ||
          source.gem_id == 0 || source.worn_by != kUnwornCharacterHash ||
          (source.flags & 0x10) != 0 ||
          (required_character != 0 && required_character != character_hash))
         return false;
   }

   std::unordered_set<uint32_t> affected_characters;
   {
      std::unique_lock lock(g_selection_mutex);
      auto& target_slots = g_character_selections[character_hash];
      const uint32_t previous_slot_id = target_slots[static_cast<size_t>(virtual_slot)];
      if (previous_slot_id != 0)
         g_virtual_owner_by_slot_id.erase(previous_slot_id);
      if (inventory_slot_id != 0)
      {
         for (auto& [owner_hash, owner_slots] : g_character_selections)
         {
            for (uint32_t& existing : owner_slots)
            {
               if (existing != inventory_slot_id)
                  continue;
               existing = 0;
               affected_characters.emplace(owner_hash);
            }
         }
      }
      target_slots[static_cast<size_t>(virtual_slot)] = inventory_slot_id;
      affected_characters.emplace(character_hash);
      if (inventory_slot_id != 0)
         g_virtual_owner_by_slot_id[inventory_slot_id] = {character_hash, virtual_slot};
   }
   for (const uint32_t affected_hash : affected_characters)
      SaveCharacterSelection(affected_hash);
   MarkInventoryDirty();

   bool auto_apply = false;
   {
      std::scoped_lock lock(g_settings_mutex);
      auto_apply = g_settings.auto_apply;
   }
   if (auto_apply)
      RequestHotApply(character_hash);
   for (const uint32_t affected_hash : affected_characters)
      if (affected_hash != character_hash)
         ScheduleReconcileApply(affected_hash);
   return true;
}

bool RebuildInventoryIndexLocked(uintptr_t system_data)
{
   g_inventory_by_slot_id.clear();
   if (system_data == 0)
   {
      g_indexed_system_data = 0;
      return false;
   }
   uintptr_t address = system_data + kMainGemArrayOffset;
   for (int index = 0; index < kMainGemCapacity; ++index, address += sizeof(GemData))
   {
      GemData gem{};
      if (!SafeReadGem(address, gem))
      {
         g_inventory_by_slot_id.clear();
         g_indexed_system_data = 0;
         return false;
      }
      if (gem.slot_id != 0)
         g_inventory_by_slot_id[gem.slot_id] = address;
   }
   g_indexed_system_data = system_data;
   return true;
}

uintptr_t ResolveGemAddress(uint32_t slot_id)
{
   if (slot_id == 0 || g_image_base == 0)
      return 0;
   uintptr_t system_data = 0;
   if (!SafeReadPointer(g_image_base + kSystemDataGlobalRva, system_data) || system_data == 0)
      return 0;

   std::scoped_lock lock(g_inventory_index_mutex);
   if (g_indexed_system_data != system_data || g_inventory_by_slot_id.empty())
      RebuildInventoryIndexLocked(system_data);

   auto iterator = g_inventory_by_slot_id.find(slot_id);
   if (iterator == g_inventory_by_slot_id.end())
   {
      RebuildInventoryIndexLocked(system_data);
      iterator = g_inventory_by_slot_id.find(slot_id);
      return iterator == g_inventory_by_slot_id.end() ? 0 : iterator->second;
   }

   GemData current{};
   if (SafeReadGem(iterator->second, current) && current.slot_id == slot_id)
      return iterator->second;

   RebuildInventoryIndexLocked(system_data);
   iterator = g_inventory_by_slot_id.find(slot_id);
   return iterator == g_inventory_by_slot_id.end() ? 0 : iterator->second;
}

bool RefreshInventorySnapshot()
{
   uintptr_t system_data = 0;
   if (!g_hooks_ready.load(std::memory_order_acquire) ||
       !SafeReadPointer(g_image_base + kSystemDataGlobalRva, system_data) ||
       system_data == 0)
   {
      std::scoped_lock lock(g_inventory_snapshot_mutex);
      g_inventory_snapshot.clear();
      return false;
   }

   struct RawInventoryRecord
   {
      GemData gem{};
      uintptr_t address = 0;
   };

   std::vector<RawInventoryRecord> records;
   records.reserve(kMainGemCapacity);
   std::unordered_map<uint32_t, uintptr_t> new_index;
   std::unordered_map<uint32_t, GemData> gems_by_slot_id;
   uintptr_t address = system_data + kMainGemArrayOffset;
   for (int index = 0; index < kMainGemCapacity; ++index, address += sizeof(GemData))
   {
      GemData gem{};
      if (!SafeReadGem(address, gem))
      {
         std::scoped_lock lock(g_inventory_snapshot_mutex);
         g_inventory_snapshot.clear();
         return false;
      }
      if (gem.slot_id == 0)
         continue;

      records.push_back({gem, address});
      new_index[gem.slot_id] = address;
      gems_by_slot_id[gem.slot_id] = gem;
   }

   std::unordered_set<uint32_t> affected_characters;
   std::unordered_map<uint32_t, VirtualOwner> virtual_owners;
   {
      std::unique_lock lock(g_selection_mutex);
      for (auto& [character_hash, slots] : g_character_selections)
      {
         for (uint32_t& selected_slot_id : slots)
         {
            if (selected_slot_id == 0)
               continue;
            const auto gem_iterator = gems_by_slot_id.find(selected_slot_id);
            const bool valid = gem_iterator != gems_by_slot_id.end() &&
               gem_iterator->second.gem_id != 0 &&
               gem_iterator->second.worn_by == kUnwornCharacterHash &&
               (gem_iterator->second.flags & 0x10) == 0 &&
               (GetRequiredCharacterHash(gem_iterator->second.gem_id) == 0 ||
                GetRequiredCharacterHash(gem_iterator->second.gem_id) == character_hash);
            if (valid)
               continue;
            g_virtual_owner_by_slot_id.erase(selected_slot_id);
            selected_slot_id = 0;
            affected_characters.emplace(character_hash);
         }
      }
      virtual_owners = g_virtual_owner_by_slot_id;
   }
   for (const uint32_t character_hash : affected_characters)
   {
      SaveCharacterSelection(character_hash);
      ScheduleReconcileApply(character_hash);
   }

   std::vector<InventoryItem> snapshot;
   snapshot.reserve(records.size());
   for (const RawInventoryRecord& record : records)
   {
      const GemData& gem = record.gem;
      if (gem.gem_id == 0 || (gem.flags & 0x10) != 0)
         continue;

      InventoryItem item{};
      item.gem = gem;
      item.address = record.address;
      item.equipped = gem.worn_by != kUnwornCharacterHash;
      item.required_character_hash = GetRequiredCharacterHash(gem.gem_id);
      if (const auto owner = virtual_owners.find(gem.slot_id); owner != virtual_owners.end())
      {
         item.virtual_owner_character_hash = owner->second.character_hash;
         item.virtual_owner_slot = owner->second.virtual_slot;
      }

      const std::string sigil = LookupName(g_sigil_names, gem.gem_id, "Sigil#");
      const std::string trait1 = LookupName(g_trait_names, gem.trait1, "Trait#");
      const std::string trait2 = LookupName(g_trait_names, gem.trait2, "Trait#");
      std::ostringstream label;
      label << sigil << "  |  " << trait1 << " Lv" << gem.trait1_level;
      if (gem.trait2 != 0)
         label << " + " << trait2 << " Lv" << gem.trait2_level;
      label << "  [#" << gem.slot_id << ']';
      if (item.equipped)
         label << "  (equipped)";
      else if (item.virtual_owner_character_hash != 0)
         label << "  (virtual 0x" << ToUpperHex(item.virtual_owner_character_hash)
               << ":" << (kNativeInternalSlotCount + item.virtual_owner_slot) << ')';
      if (item.required_character_hash != 0)
         label << "  (requires 0x" << ToUpperHex(item.required_character_hash) << ')';
      item.label = label.str();
      item.searchable = ToLowerAscii(item.label);
      snapshot.push_back(std::move(item));
   }

   std::sort(snapshot.begin(), snapshot.end(), [](const InventoryItem& left, const InventoryItem& right) {
      if (left.equipped != right.equipped)
         return !left.equipped;
      if (left.gem.sigil_level != right.gem.sigil_level)
         return left.gem.sigil_level > right.gem.sigil_level;
      return left.label < right.label;
   });

   {
      std::scoped_lock lock(g_inventory_index_mutex);
      g_inventory_by_slot_id = std::move(new_index);
      g_indexed_system_data = system_data;
   }
   {
      std::scoped_lock lock(g_inventory_snapshot_mutex);
      g_inventory_snapshot = std::move(snapshot);
   }
   g_inventory_dirty.store(false, std::memory_order_release);
   g_inventory_revision.fetch_add(1, std::memory_order_acq_rel);
   return true;
}

void PublishNaturalBindDiagnostic(
   uintptr_t status,
   const StatusIdentity& identity,
   uint32_t expected,
   uint32_t injected,
   NaturalBindResult result) noexcept
{
   g_natural_bind_status_address.store(status, std::memory_order_release);
   g_natural_bind_character_hash.store(identity.character_hash, std::memory_order_release);
   g_natural_bind_context.store(identity.context_mode, std::memory_order_release);
   g_natural_bind_expected_count.store(expected, std::memory_order_release);
   g_natural_bind_injected_count.store(injected, std::memory_order_release);
   g_natural_bind_result.store(result, std::memory_order_release);
}

uint32_t CountSelectedSlots(
   const std::array<uint32_t, kVirtualSlotCount>& selection) noexcept
{
   return static_cast<uint32_t>(std::count_if(
      selection.begin(), selection.end(), [](uint32_t slot_id) { return slot_id != 0; }));
}

bool IsExactOwnerStatus(
   uintptr_t status,
   const StatusIdentity& identity,
   uint32_t owner_thread_id,
   uint64_t owner_tick_count) noexcept
{
   if (status == 0 || identity.character_hash == 0 || owner_thread_id == 0 ||
       GetCurrentThreadId() != owner_thread_id)
      return false;

   if (identity.context_mode == 1)
   {
      const LocalContext1Binding& binding = g_tls_local_context1_binding;
      const bool matches = binding.active && binding.status == status &&
         binding.character_hash == identity.character_hash &&
         binding.owner_key == kLocalPlayerSlotKey &&
         binding.thread_id == owner_thread_id &&
         binding.generation == owner_tick_count &&
         GetCurrentThreadId() == binding.thread_id &&
         ValidateLocalContext1Binding(binding, status, &identity);
      g_natural_bind_owner_key.store(
         matches ? binding.owner_key : 0, std::memory_order_release);
      g_natural_bind_owner_status_address.store(
         binding.status, std::memory_order_release);
      if (binding.manager != 0)
         g_status_owner_manager_address.store(
            binding.manager, std::memory_order_release);
      return matches;
   }

   if (g_status_owner_thread_id.load(std::memory_order_acquire) != owner_thread_id ||
       g_status_owner_tick_count.load(std::memory_order_acquire) != owner_tick_count)
      return false;

   uintptr_t manager = 0;
   uintptr_t current_status = 0;
   StatusIdentity current_identity{};
   const bool matches =
      SafeResolveCharacterStatus(identity.character_hash, manager, current_status) &&
      current_status == status &&
      SafeReadStatusIdentity(current_status, current_identity) &&
      current_identity.character_hash == identity.character_hash &&
      current_identity.context_mode == identity.context_mode;
   g_natural_bind_owner_key.store(0, std::memory_order_release);
   g_natural_bind_owner_status_address.store(current_status, std::memory_order_release);
   return matches;
}

bool ValidateNaturalSelection(
   uint32_t character_hash,
   const std::array<uint32_t, kVirtualSlotCount>& selection,
   std::array<GemData, kVirtualSlotCount>& gems) noexcept
{
   gems = {};
   for (size_t index = 0; index < selection.size(); ++index)
   {
      const uint32_t slot_id = selection[index];
      if (slot_id == 0)
         continue;

      const uintptr_t source_address = ResolveGemAddress(slot_id);
      GemData source{};
      if (source_address == 0 || !SafeReadGem(source_address, source) ||
          source.slot_id != slot_id || source.gem_id == 0 ||
          source.worn_by != kUnwornCharacterHash || (source.flags & 0x10) != 0 ||
          (GetRequiredCharacterHash(source.gem_id) != 0 &&
           GetRequiredCharacterHash(source.gem_id) != character_hash))
         return false;
      gems[index] = source;
   }
   return true;
}

bool BeginNaturalTraitBind(
   uintptr_t status,
   uintptr_t loop_return_address,
   const StatusIdentity& identity,
   bool report_rejection) noexcept
{
   g_tls_natural_bind = {};
   const auto selection = GetSelection(identity.character_hash);
   const uint32_t expected = CountSelectedSlots(selection);
   if (expected == 0)
      return false;

   if (identity.context_mode != 1)
   {
      if (report_rejection)
         PublishNaturalBindDiagnostic(
            status, identity, expected, 0, NaturalBindContextRejected);
      return false;
   }

   const uint32_t owner_thread_id = identity.context_mode == 1
      ? g_tls_local_context1_binding.thread_id
      : g_status_owner_thread_id.load(std::memory_order_acquire);
   const uint64_t owner_tick_count = identity.context_mode == 1
      ? g_tls_local_context1_binding.generation
      : g_status_owner_tick_count.load(std::memory_order_acquire);
   if (owner_thread_id == 0 || GetCurrentThreadId() != owner_thread_id)
   {
      if (report_rejection)
         PublishNaturalBindDiagnostic(
            status, identity, expected, 0, NaturalBindOwnerRejected);
      return false;
   }
   if (!IsExactOwnerStatus(
          status, identity, owner_thread_id, owner_tick_count))
   {
      if (report_rejection)
         PublishNaturalBindDiagnostic(
            status, identity, expected, 0, NaturalBindStatusRejected);
      return false;
   }

   std::array<GemData, kVirtualSlotCount> gems{};
   if (!ValidateNaturalSelection(identity.character_hash, selection, gems))
   {
      if (report_rejection)
         PublishNaturalBindDiagnostic(
            status, identity, expected, 0, NaturalBindSelectionRejected);
      return false;
   }

   g_tls_natural_bind.status = status;
   g_tls_natural_bind.loop_return_address = loop_return_address;
   g_tls_natural_bind.identity = identity;
   g_tls_natural_bind.owner_thread_id = owner_thread_id;
   g_tls_natural_bind.owner_tick_count = owner_tick_count;
   g_tls_natural_bind.slots = selection;
   g_tls_natural_bind.gems = gems;
   g_tls_natural_bind.expected = expected;
   g_tls_natural_bind.next_slot = kNativeInternalSlotCount;
   g_tls_natural_bind.active = true;
   return true;
}

bool IsNaturalTraitBindCurrent(
   uintptr_t status,
   uintptr_t loop_return_address,
   const StatusIdentity& identity,
   int slot_index) noexcept
{
   return g_tls_natural_bind.active &&
      g_tls_natural_bind.status == status &&
      g_tls_natural_bind.loop_return_address == loop_return_address &&
      g_tls_natural_bind.identity.character_hash == identity.character_hash &&
      g_tls_natural_bind.identity.context_mode == identity.context_mode &&
      g_tls_natural_bind.next_slot == slot_index &&
      IsExactOwnerStatus(
         status,
         identity,
         g_tls_natural_bind.owner_thread_id,
         g_tls_natural_bind.owner_tick_count);
}

void BeginNaturalContributionTracking(
   uintptr_t status,
   const StatusIdentity& identity,
   const std::array<uint32_t, kVirtualSlotCount>& selection) noexcept
{
   g_tls_natural_contribution = {};
   const uint32_t expected = CountSelectedSlots(selection);
   if (expected == 0)
      return;

   PublishNaturalBindDiagnostic(
      status, identity, expected, 0, NaturalBindInProgress);
   if (identity.context_mode != 1)
   {
      PublishNaturalBindDiagnostic(
         status, identity, expected, 0, NaturalBindContextRejected);
      return;
   }

   g_natural_bind_attempts.fetch_add(1, std::memory_order_acq_rel);
   g_tls_natural_contribution.status = status;
   g_tls_natural_contribution.identity = identity;
   g_tls_natural_contribution.slots = selection;
   g_tls_natural_contribution.expected = expected;
   g_tls_natural_contribution.next_slot = kNativeInternalSlotCount;
   g_tls_natural_contribution.active = true;
   g_natural_bind_owner_key.store(0, std::memory_order_release);
   g_natural_bind_owner_status_address.store(status, std::memory_order_release);
}

void TrackNaturalContributionResult(
   uintptr_t status,
   const StatusIdentity& identity,
   int slot_index,
   uint32_t selected_slot_id,
   bool copied) noexcept
{
   if (!g_tls_natural_contribution.active)
      return;
   if (g_tls_natural_contribution.status != status ||
       g_tls_natural_contribution.identity.character_hash != identity.character_hash ||
       g_tls_natural_contribution.identity.context_mode != identity.context_mode ||
       g_tls_natural_contribution.next_slot != slot_index)
   {
      PublishNaturalBindDiagnostic(
         status,
         identity,
         g_tls_natural_contribution.expected,
         g_tls_natural_contribution.injected,
         NaturalBindSequenceRejected);
      g_tls_natural_contribution = {};
      return;
   }

   if (selected_slot_id != 0)
   {
      if (!copied)
      {
         PublishNaturalBindDiagnostic(
            status,
            identity,
            g_tls_natural_contribution.expected,
            g_tls_natural_contribution.injected,
            NaturalBindCopyRejected);
         g_tls_natural_contribution = {};
         return;
      }
      ++g_tls_natural_contribution.injected;
   }
   ++g_tls_natural_contribution.next_slot;

   if (slot_index != kExpandedInternalSlotCount - 1)
      return;

   const uint32_t expected = g_tls_natural_contribution.expected;
   const uint32_t injected = g_tls_natural_contribution.injected;
   StatusIdentity final_identity{};
   const bool final_valid = injected == expected && expected != 0 &&
      SafeReadStatusIdentity(status, final_identity) &&
      final_identity.character_hash == identity.character_hash &&
      final_identity.context_mode == identity.context_mode;
   PublishNaturalBindDiagnostic(
      status,
      identity,
      expected,
      injected,
      final_valid ? NaturalBindSucceeded : NaturalBindFinalValidationRejected);
   if (final_valid)
   {
      uint64_t generation =
         g_next_apply_generation.fetch_add(1, std::memory_order_acq_rel) + 1;
      if (generation == 0)
         generation =
            g_next_apply_generation.fetch_add(1, std::memory_order_acq_rel) + 1;
      CommitAuthorizedStatus(
         status,
         identity,
         generation,
         g_tls_natural_contribution.slots);
      g_natural_bind_successes.fetch_add(1, std::memory_order_acq_rel);
      SetRuntimeMessage(
         "Live battle Trait contribution confirmed for 0x" +
            ToUpperHex(identity.character_hash) + ": " +
            std::to_string(injected) + "/" + std::to_string(expected) +
            " virtual sigils reached the context-1 status.",
         false);
   }
   g_tls_natural_contribution = {};
}

uint8_t GetGemDataByIndexDetour(void* status, int slot_index, void* output)
{
   ActiveCallGuard active_call(g_active_getter_calls);
   const uintptr_t return_address = reinterpret_cast<uintptr_t>(_ReturnAddress());
   const bool from_trait_apply_loop =
      return_address == g_image_base + kTraitApplyGetterReturnRva;
   const bool from_trait_category_loop =
      return_address == g_image_base + kTraitCategoryGetterReturnRva;
   const bool from_trait_data_loop =
      from_trait_apply_loop || from_trait_category_loop;
   StatusIdentity identity{};
   const bool valid_identity =
      SafeReadStatusIdentity(reinterpret_cast<uintptr_t>(status), identity) &&
      identity.context_mode >= 0 && identity.context_mode <= 5;
   if (valid_identity)
   {
      // This is the most recently rebuilt native status. Equipment-screen Q/E preview changes do
      // not necessarily call this getter, so the UI must not present it as the preview selection.
      g_last_character_hash.store(identity.character_hash, std::memory_order_release);
      g_last_context_mode.store(identity.context_mode, std::memory_order_release);
   }

   if (slot_index < kNativeInternalSlotCount || slot_index >= kExpandedInternalSlotCount)
   {
      const uint8_t result = g_get_gem_hook.call<uint8_t>(status, slot_index, output);
      if (from_trait_apply_loop && slot_index == 0)
         MarkInventoryDirty();
      return result;
   }
   if (g_shutting_down.load(std::memory_order_acquire) || !valid_identity ||
       identity.context_mode > 2 || output == nullptr)
      return 0;

   const uint64_t active_generation =
      g_active_apply_generation.load(std::memory_order_acquire);
   const bool tracks_pending_apply =
      from_trait_data_loop && active_generation != 0 &&
      g_tls_apply_generation == active_generation &&
      g_pending_refresh.load(std::memory_order_acquire) &&
       g_native_apply_call_active.load(std::memory_order_acquire) &&
       g_active_apply_thread_id.load(std::memory_order_acquire) == GetCurrentThreadId() &&
       g_pending_character_hash.load(std::memory_order_acquire) == identity.character_hash &&
       g_active_apply_status.load(std::memory_order_acquire) ==
           reinterpret_cast<uintptr_t>(status);
   std::array<uint32_t, kVirtualSlotCount> selection{};
   if (tracks_pending_apply)
   {
      for (size_t index = 0; index < selection.size(); ++index)
         selection[index] = g_active_apply_slots[index].load(std::memory_order_acquire);
   }
   else if (!TryGetAuthorizedSelection(
              reinterpret_cast<uintptr_t>(status), identity, selection))
   {
      if (!from_trait_data_loop)
         return 0;
      selection = GetSelection(identity.character_hash);
      if (CountSelectedSlots(selection) == 0)
         return 0;
   }
   const int virtual_index = slot_index - kNativeInternalSlotCount;
   const uint32_t selected_slot_id = selection[static_cast<size_t>(virtual_index)];
   if (tracks_pending_apply && from_trait_apply_loop &&
       slot_index == kNativeInternalSlotCount)
   {
      g_pending_injected_count.store(0, std::memory_order_release);
      g_claimed_apply_generation.store(active_generation, std::memory_order_release);
   }
   const bool generation_claimed =
      tracks_pending_apply && from_trait_apply_loop &&
      g_claimed_apply_generation.load(std::memory_order_acquire) == active_generation;

   if (from_trait_apply_loop && slot_index == kNativeInternalSlotCount &&
       identity.context_mode == 1)
      BeginNaturalContributionTracking(
         reinterpret_cast<uintptr_t>(status), identity, selection);

   bool copied = false;
   if (selected_slot_id != 0)
   {
      const uintptr_t source_address = ResolveGemAddress(selected_slot_id);
      GemData source{};
      copied = source_address != 0 && SafeReadGem(source_address, source) &&
          source.slot_id == selected_slot_id &&
          source.worn_by == kUnwornCharacterHash &&
          (source.flags & 0x10) == 0 &&
          (GetRequiredCharacterHash(source.gem_id) == 0 ||
           GetRequiredCharacterHash(source.gem_id) == identity.character_hash) &&
          SafeCopyToOutput(source, output);
   }

   if (generation_claimed)
   {
      if (copied)
         g_pending_injected_count.fetch_add(1, std::memory_order_acq_rel);
      if (slot_index == kExpandedInternalSlotCount - 1)
      {
         const uint32_t expected =
            g_active_apply_expected_count.load(std::memory_order_acquire);
         const uint32_t injected = g_pending_injected_count.load(std::memory_order_acquire);
         uint64_t expected_generation = active_generation;
         if (g_active_apply_generation.compare_exchange_strong(
                expected_generation,
                0,
                std::memory_order_acq_rel,
                std::memory_order_acquire))
         {
            g_pending_refresh.store(false, std::memory_order_release);
            g_apply_result.store(
               injected == expected ? ApplyResultAppliedDuringNativeRebuild
                                    : ApplyResultVirtualCopyFailed,
               std::memory_order_release);
         }
      }
   }

   if (from_trait_apply_loop)
      TrackNaturalContributionResult(
         reinterpret_cast<uintptr_t>(status),
         identity,
         slot_index,
         selected_slot_id,
         copied);

   return copied ? 1 : 0;
}

void OnTraitFetch(safetyhook::Context& context)
{
   ActiveCallGuard active_call(g_active_mid_calls);
   if (context.r13 >= static_cast<uintptr_t>(kNativeInternalSlotCount) &&
       context.r13 < static_cast<uintptr_t>(kExpandedInternalSlotCount))
      context.rip = g_image_base + kTraitFetchCallPathRva;
}

void OnLocalContext1BindCall(safetyhook::Context& context)
{
   ActiveCallGuard active_call(g_active_mid_calls);
   g_tls_local_context1_binding = {};
   if (g_shutting_down.load(std::memory_order_acquire))
      return;

   const uint32_t owner_key = static_cast<uint32_t>(context.rbx);
   if (owner_key != kLocalPlayerSlotKey)
      return;

   LocalContext1Binding binding{};
   binding.manager = context.rdi;
   binding.record = context.rdx;
   binding.status = context.rcx;
   binding.owner_key = owner_key;
   binding.thread_id = GetCurrentThreadId();
   binding.generation =
      g_local_context1_binding_generation.fetch_add(1, std::memory_order_acq_rel) + 1;
   if (binding.generation == 0)
      binding.generation =
         g_local_context1_binding_generation.fetch_add(1, std::memory_order_acq_rel) + 1;
   binding.active = true;

   uintptr_t resolved_record = 0;
   uintptr_t resolved_status = 0;
   if (!SafeReadCharacterRecordHash(binding.record, binding.character_hash) ||
       !SafeResolveCharacterRecordByOwnerKey(
          binding.manager, binding.owner_key, resolved_record) ||
       resolved_record != binding.record ||
       !SafeResolveStatusByMapKey(
          binding.manager, binding.owner_key, resolved_status) ||
       resolved_status != binding.status)
   {
      g_status_owner_manager_address.store(binding.manager, std::memory_order_release);
      g_natural_bind_owner_key.store(0, std::memory_order_release);
      g_natural_bind_owner_status_address.store(
         resolved_status, std::memory_order_release);
      StatusIdentity rejected_identity{binding.character_hash, 1};
      PublishNaturalBindDiagnostic(
         binding.status,
         rejected_identity,
         CountSelectedSlots(GetSelection(binding.character_hash)),
         0,
         NaturalBindStatusRejected);
      return;
   }

   {
      LocalContext1Binding persisted = binding;
      persisted.active = false;
      std::unique_lock lock(g_local_context1_binding_mutex);
      g_local_context1_bindings.clear();
      g_local_context1_bindings.emplace(binding.status, persisted);
   }
   {
      std::unique_lock lock(g_authorization_mutex);
      for (auto iterator = g_authorized_statuses.begin();
           iterator != g_authorized_statuses.end();)
      {
         if (iterator->second.context_mode == 1 && iterator->first != binding.status)
            iterator = g_authorized_statuses.erase(iterator);
         else
            ++iterator;
      }
   }

   g_tls_local_context1_binding = binding;
   g_status_owner_manager_address.store(binding.manager, std::memory_order_release);
   g_status_owner_thread_id.store(binding.thread_id, std::memory_order_release);
   g_natural_bind_owner_key.store(binding.owner_key, std::memory_order_release);
   g_natural_bind_owner_status_address.store(binding.status, std::memory_order_release);
}

void OnLocalContext1BindReturn(safetyhook::Context&)
{
   ActiveCallGuard active_call(g_active_mid_calls);
   g_tls_local_context1_binding.active = false;
}

void OnStatusOwnerCharacterLoop(safetyhook::Context& context)
{
   ActiveCallGuard active_call(g_active_mid_calls);
   if (g_shutting_down.load(std::memory_order_acquire))
      return;

   g_status_owner_manager_address.store(context.rbx, std::memory_order_release);
   g_status_owner_thread_id.store(GetCurrentThreadId(), std::memory_order_release);
   g_status_owner_tick_count.fetch_add(1, std::memory_order_acq_rel);

   std::array<uint32_t, 4> hashes{};
   const uint32_t count = SafeReadOwnerCharacterHashes(context.rbx, hashes);
   for (uint32_t index = 0; index < count; ++index)
      g_status_owner_character_hashes[index].store(hashes[index], std::memory_order_release);

   for (uint32_t index = count; index < g_status_owner_character_hashes.size(); ++index)
      g_status_owner_character_hashes[index].store(0, std::memory_order_release);
   g_status_owner_character_count.store(count, std::memory_order_release);
}

uint64_t BuildLifecycleSignature(
   uint32_t character_hash,
   uintptr_t status,
   int32_t context_mode,
   const std::array<uint32_t, kVirtualSlotCount>& slots)
{
   uint64_t signature = static_cast<uint64_t>(status) ^
      (static_cast<uint64_t>(character_hash) << 32) ^
      static_cast<uint32_t>(context_mode);
   for (const uint32_t slot_id : slots)
      signature = (signature ^ slot_id) * 0x9E3779B185EBCA87ull;
   return signature == 0 ? 1 : signature;
}

void ScheduleSelectedStatusRebind()
{
   uint32_t character_hash = 0;
   if (!SafeReadUiSelectedCharacterHash(character_hash))
   {
      g_observed_character_hash.store(0, std::memory_order_release);
      g_observed_status_address.store(0, std::memory_order_release);
      g_observed_status_context.store(-1, std::memory_order_release);
      return;
   }

   uintptr_t manager = 0;
   uintptr_t status = 0;
   StatusIdentity identity{};
   // The owner-loop vector contains a different set of party keys than the Equipment UI
   // character hash. It remains useful as a thread/diagnostic snapshot, but comparing the
   // two namespaces here rejects valid local Equipment and free-training statuses. The
   // status map lookup plus exact status identity is the authoritative local selection.
   if (!SafeResolveSelectedCharacterStatus(
          character_hash, manager, status, identity))
   {
      g_observed_character_hash.store(character_hash, std::memory_order_release);
      g_observed_status_address.store(0, std::memory_order_release);
      g_observed_status_context.store(-1, std::memory_order_release);
      return;
   }

   const uint32_t previous_character =
      g_observed_character_hash.exchange(character_hash, std::memory_order_acq_rel);
   const uint64_t previous_status =
      g_observed_status_address.exchange(status, std::memory_order_acq_rel);
   const int32_t previous_context =
      g_observed_status_context.exchange(identity.context_mode, std::memory_order_acq_rel);
   const bool identity_changed = previous_character != character_hash ||
      previous_status != status || previous_context != identity.context_mode;

   const auto selection = GetSelection(character_hash);
   if (std::none_of(selection.begin(), selection.end(), [](uint32_t slot_id) {
          return slot_id != 0;
       }))
      return;
   if (HasMatchingAuthorizedSelection(status, identity, selection))
   {
      g_lifecycle_signature_attempts.store(0, std::memory_order_release);
      return;
   }

   bool auto_apply = false;
   {
      std::scoped_lock lock(g_settings_mutex);
      auto_apply = g_settings.auto_apply;
   }
   if (!auto_apply && !identity_changed)
      return;

   const uint64_t signature =
      BuildLifecycleSignature(character_hash, status, identity.context_mode, selection);
   const uint64_t previous_signature =
      g_lifecycle_rebind_signature.exchange(signature, std::memory_order_acq_rel);
   if (previous_signature != signature)
   {
      g_lifecycle_signature_attempts.store(0, std::memory_order_release);
      g_lifecycle_rebind_not_before_ms.store(0, std::memory_order_release);
   }
   if (g_lifecycle_signature_attempts.load(std::memory_order_acquire) >= 3 ||
       g_queued_apply_request.load(std::memory_order_acquire) != 0 ||
       g_apply_in_flight.load(std::memory_order_acquire) ||
       GetTickCount64() <
          g_lifecycle_rebind_not_before_ms.load(std::memory_order_acquire))
      return;

   RequestHotApply(character_hash);
   g_lifecycle_signature_attempts.fetch_add(1, std::memory_order_acq_rel);
   g_lifecycle_rebind_attempts.fetch_add(1, std::memory_order_acq_rel);
   g_lifecycle_rebind_not_before_ms.store(
      GetTickCount64() + 1000, std::memory_order_release);
}

void ShutdownHooks()
{
   g_shutting_down.store(true, std::memory_order_release);
   g_hooks_ready.store(false, std::memory_order_release);
   g_input_capture_requested.store(false, std::memory_order_release);
   g_input_capture_effective.store(false, std::memory_order_release);
   g_native_apply_call_active.store(false, std::memory_order_release);
   g_active_apply_status.store(0, std::memory_order_release);
   g_pending_refresh.store(false, std::memory_order_release);
   g_queued_apply_request.store(0, std::memory_order_release);
   g_apply_retry_not_before_ms.store(0, std::memory_order_release);
   g_active_apply_generation.store(0, std::memory_order_release);
   g_observed_character_hash.store(0, std::memory_order_release);
   g_observed_status_address.store(0, std::memory_order_release);
   g_observed_status_context.store(-1, std::memory_order_release);
   g_status_owner_manager_address.store(0, std::memory_order_release);
   g_natural_bind_owner_key.store(0, std::memory_order_release);
   g_natural_bind_owner_status_address.store(0, std::memory_order_release);
   g_tls_local_context1_binding = {};

   RestoreInputIatHooks();
   if (g_direct_input_get_data_hook)
      (void)g_direct_input_get_data_hook.disable();
   if (g_direct_input_get_state_hook)
      (void)g_direct_input_get_state_hook.disable();

   if (g_local_context1_bind_return_hook)
      (void)g_local_context1_bind_return_hook.disable();
   if (g_local_context1_bind_call_hook)
      (void)g_local_context1_bind_call_hook.disable();
   if (g_status_owner_tick_hook)
      (void)g_status_owner_tick_hook.disable();
   if (g_trait_fetch_hook)
      (void)g_trait_fetch_hook.disable();
   if (g_get_gem_hook)
      (void)g_get_gem_hook.disable();

   while (g_active_getter_calls.load(std::memory_order_acquire) != 0 ||
          g_active_mid_calls.load(std::memory_order_acquire) != 0 ||
          g_active_input_calls.load(std::memory_order_acquire) != 0)
      SwitchToThread();

   if (g_image_base != 0)
   {
      uint8_t apply_loop_limit = 0;
      if (ReadByte(g_image_base + kTraitApplyLoopLimitImmediateRva, apply_loop_limit) &&
          apply_loop_limit == kExpandedInternalSlotCount)
         WriteByte(g_image_base + kTraitApplyLoopLimitImmediateRva, kNativeInternalSlotCount);
      uint8_t loop_limit = 0;
      if (ReadByte(g_image_base + kTraitCategoryLoopLimitImmediateRva, loop_limit) &&
          loop_limit == kExpandedInternalSlotCount)
         WriteByte(g_image_base + kTraitCategoryLoopLimitImmediateRva, kNativeInternalSlotCount);
   }
   g_status_owner_tick_hook.reset();
   g_local_context1_bind_return_hook.reset();
   g_local_context1_bind_call_hook.reset();
   g_trait_fetch_hook.reset();
   g_get_gem_hook.reset();
   g_direct_input_get_data_hook.reset();
   g_direct_input_get_state_hook.reset();
   g_direct_input_hook_ready.store(false, std::memory_order_release);
   {
      std::unique_lock lock(g_authorization_mutex);
      g_authorized_statuses.clear();
   }
   {
      std::unique_lock lock(g_local_context1_binding_mutex);
      g_local_context1_bindings.clear();
   }
}

bool InstallHooks()
{
   if (!MatchesBytes(
          g_image_base + kTraitApplyLoopLimitImmediateRva - 4, kTraitApplyLoopPreflight) ||
       !MatchesBytes(
          g_image_base + kTraitCategoryLoopLimitImmediateRva - 6,
          kTraitCategoryLoopPreflight))
   {
      SetRuntimeMessage(
         "Trait-loop preflight failed. Disable the old Reloaded-II 20-slot prototype and verify ER 2.0.2.",
         true);
      return false;
   }
   if (!MatchesBytes(g_image_base + kTraitFetchPathRva, kTraitFetchPreflight) ||
       !MatchesBytes(g_image_base + kGetGemDataByIndexRva, kGetterPreflight) ||
       !MatchesBytes(g_image_base + kStatusRebuildRva, kStatusRebuildPreflight) ||
       !MatchesBytes(g_image_base + kStatusNotifierRva, kStatusNotifierPreflight) ||
       !MatchesBytes(g_image_base + kStatusOwnerTickRva, kStatusOwnerTickPreflight) ||
       !MatchesBytes(
           g_image_base + kStatusOwnerCharacterLoopRva,
           kStatusOwnerCharacterLoopPreflight))
   {
      SetRuntimeMessage("Native code preflight failed; no hook or byte patch was installed.", true);
      return false;
   }

   g_get_gem_hook = safetyhook::create_inline(
      reinterpret_cast<void*>(g_image_base + kGetGemDataByIndexRva),
      reinterpret_cast<void*>(&GetGemDataByIndexDetour));
   if (!g_get_gem_hook)
   {
      SetRuntimeMessage("Failed to install the GemData getter hook.", true);
      return false;
   }

   g_trait_fetch_hook = safetyhook::create_mid(
      reinterpret_cast<void*>(g_image_base + kTraitFetchPathRva), &OnTraitFetch);
   if (!g_trait_fetch_hook)
   {
      g_get_gem_hook.reset();
      SetRuntimeMessage("Failed to install the trait fetch-path hook.", true);
      return false;
   }

   g_status_owner_tick_hook = safetyhook::create_mid(
      reinterpret_cast<void*>(g_image_base + kStatusOwnerCharacterLoopRva),
      &OnStatusOwnerCharacterLoop);
   if (!g_status_owner_tick_hook)
   {
      ShutdownHooks();
      SetRuntimeMessage("Failed to install the status owner-thread trace hook.", true);
      return false;
   }

   if (!WriteByte(
          g_image_base + kTraitApplyLoopLimitImmediateRva, kExpandedInternalSlotCount) ||
       !WriteByte(
          g_image_base + kTraitCategoryLoopLimitImmediateRva, kExpandedInternalSlotCount))
   {
      ShutdownHooks();
      SetRuntimeMessage("Failed to patch both native trait loop limits; changes were rolled back.", true);
      return false;
   }

   (void)InstallInputIatHooks();

   g_hooks_ready.store(true, std::memory_order_release);
   SetRuntimeMessage(
         "Test7 ready: direct character-hash battle injection is enabled for all context-1 statuses.",
      false);
   return true;
}

void Initialize()
{
   g_image_base = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
   std::vector<wchar_t> module_path(32768, L'\0');
   const DWORD module_length = GetModuleFileNameW(
      g_module, module_path.data(), static_cast<DWORD>(module_path.size()));
   if (module_length == 0 || module_length >= module_path.size())
   {
      SetRuntimeMessage("Could not resolve the native core directory.", true);
      g_initialized.store(true, std::memory_order_release);
      return;
   }

   g_module_directory = std::filesystem::path(module_path.data()).parent_path();
   g_config_path = g_module_directory / L"GBFR-ExtraSigilSlots20.ini";
   g_compatibility_path =
      g_module_directory / L"GBFR-ExtraSigilSlots20.compatibility.tsv";
   LoadSettingsAndSelections();

   std::string language;
   {
      std::scoped_lock lock(g_settings_mutex);
      language = g_settings.language == "en" ? "en" : "zh-CN";
   }
   LoadNameTable(g_module_directory / ("GBFR-ExtraSigilSlots20.names." + language + ".tsv"));
   if (!LoadCompatibilityTable(g_compatibility_path))
   {
      SetRuntimeMessage(
         "The ER 2.0.2 character-compatibility table is missing or incomplete; hooks were not installed.",
         true);
      g_initialized.store(true, std::memory_order_release);
      return;
   }

   std::vector<wchar_t> executable_path(32768, L'\0');
   const DWORD executable_length = GetModuleFileNameW(
      nullptr, executable_path.data(), static_cast<DWORD>(executable_path.size()));
   if (executable_length == 0 || executable_length >= executable_path.size())
   {
      SetRuntimeMessage("Could not resolve the game executable path.", true);
      g_initialized.store(true, std::memory_order_release);
      return;
   }

   const std::filesystem::path executable(executable_path.data());
   if (_wcsicmp(executable.filename().c_str(), L"granblue_fantasy_relink.exe") != 0)
   {
      SetRuntimeMessage("This native core only supports granblue_fantasy_relink.exe.", true);
      g_initialized.store(true, std::memory_order_release);
      return;
   }

   const std::string executable_hash = ComputeFileSha256(executable);
   if (executable_hash != kExpectedExeSha256)
   {
      SetRuntimeMessage(
         "Unsupported game executable SHA-256: " +
            (executable_hash.empty() ? std::string("<read failed>") : executable_hash),
         true);
      g_initialized.store(true, std::memory_order_release);
      return;
   }

   InstallHooks();
   g_initialized.store(true, std::memory_order_release);
}

void EnsureInitialized()
{
   std::call_once(g_initialize_once, &Initialize);
}


void ConsumeApplyResult()
{
   const int result = g_apply_result.exchange(ApplyResultNone, std::memory_order_acq_rel);
   if (result == ApplyResultNone)
      return;
   g_last_consumed_apply_result.store(result, std::memory_order_release);
   const uint64_t generation = g_last_apply_generation.load(std::memory_order_acquire);
   const uint32_t character_hash =
      g_last_apply_character_hash.load(std::memory_order_acquire);
   const uint32_t expected = g_last_apply_expected_count.load(std::memory_order_acquire);
   const uint32_t injected = g_last_apply_injected_count.load(std::memory_order_acquire);
   std::ostringstream prefix;
   prefix << "Generation " << generation << " for 0x" << ToUpperHex(character_hash) << ": ";
   switch (result)
   {
   case ApplyResultAppliedDuringNativeRebuild:
      SetRuntimeMessage(
         prefix.str() + "equipment/test rebuild copied " + std::to_string(injected) + "/" +
            std::to_string(expected) +
            " selected virtual sigils. Combat reads the same saved selection directly from the native Trait loop.",
         false);
      break;
   case ApplyResultSavedNoStatus:
      SetRuntimeMessage(prefix.str() + "no valid equipment-selected character was available.", true);
      break;
   case ApplyResultVirtualCopyFailed:
      SetRuntimeMessage(
         prefix.str() + "native trait build ran, but only " + std::to_string(injected) + "/" +
            std::to_string(expected) + " sigils were valid, unequipped, and copied.",
         true);
      break;
   case ApplyResultOwnerThreadMismatch:
      SetRuntimeMessage(
         prefix.str() + "overlay callback was not on the verified native status owner thread.", true);
      break;
   case ApplyResultStatusLookupFailed:
      SetRuntimeMessage(prefix.str() + "the native character status map had no matching status.", true);
      break;
   case ApplyResultNativeRebuildFailed:
      SetRuntimeMessage(prefix.str() + "the synchronous native status rebuild failed.", true);
      break;
   case ApplyResultNativeTraitLoopMissing:
      SetRuntimeMessage(
         prefix.str() + "A23CC0 returned without completing virtual trait slots 13 through 20.", true);
      break;
   case ApplyResultNotifierFailed:
      SetRuntimeMessage(
         prefix.str() + "traits rebuilt, but the post-rebuild native UI notifier failed.", true);
      break;
   default:
      break;
   }
}

} // namespace

uint32_t GBFR20_CALL GBFR20_GetAbiVersion()
{
   return GBFR20_ABI_VERSION;
}

int32_t GBFR20_CALL GBFR20_Initialize()
{
   if (g_shutting_down.load(std::memory_order_acquire))
      return 0;
   EnsureInitialized();
   return g_hooks_ready.load(std::memory_order_acquire) ? 1 : 0;
}

void GBFR20_CALL GBFR20_Tick()
{
   if (g_shutting_down.load(std::memory_order_acquire))
      return;
   g_overlay_thread_id.store(GetCurrentThreadId(), std::memory_order_release);
   g_overlay_frame_count.fetch_add(1, std::memory_order_acq_rel);
   EnsureInitialized();
   TryInstallDirectInputHooks();
   UpdateInputCaptureBarrier();
   UpdateEditSessionState();
   ValidateAuthorizedStatuses();
   ScheduleSelectedStatusRebind();
   PumpReconcileApplyQueue();
   ProcessPendingHotApply();
   ConsumeApplyResult();
}

void GBFR20_CALL GBFR20_Shutdown()
{
   if (g_shutdown_complete.exchange(true, std::memory_order_acq_rel))
      return;
   ShutdownHooks();
}

int32_t GBFR20_CALL GBFR20_GetState(GBFR20_RuntimeState* state, uint32_t state_size)
{
   if (state == nullptr || state_size < sizeof(GBFR20_RuntimeState))
      return 0;

   GBFR20_RuntimeState snapshot{};
   snapshot.abi_version = GBFR20_ABI_VERSION;
   snapshot.struct_size = sizeof(snapshot);
   snapshot.initialized = g_initialized.load(std::memory_order_acquire) ? 1 : 0;
   snapshot.hooks_ready = g_hooks_ready.load(std::memory_order_acquire) ? 1 : 0;
   snapshot.shutting_down = g_shutting_down.load(std::memory_order_acquire) ? 1 : 0;
   {
      std::scoped_lock lock(g_message_mutex);
      snapshot.runtime_message_is_error = g_runtime_message_is_error ? 1 : 0;
   }
   uint32_t ui_character_hash = 0;
   SafeReadUiSelectedCharacterHash(ui_character_hash);
   snapshot.ui_selected_character_hash = ui_character_hash;
   snapshot.last_rebuilt_character_hash =
      g_last_character_hash.load(std::memory_order_acquire);
   snapshot.effective_character_hash = ui_character_hash != 0
      ? ui_character_hash
      : 0;
   snapshot.last_context_mode = g_last_context_mode.load(std::memory_order_acquire);
   snapshot.owner_thread_id = g_status_owner_thread_id.load(std::memory_order_acquire);
   snapshot.overlay_thread_id = g_overlay_thread_id.load(std::memory_order_acquire);
   snapshot.owner_tick_count = g_status_owner_tick_count.load(std::memory_order_acquire);
   snapshot.overlay_frame_count = g_overlay_frame_count.load(std::memory_order_acquire);
   snapshot.owner_character_count =
      std::min<uint32_t>(
         g_status_owner_character_count.load(std::memory_order_acquire),
         GBFR20_OWNER_CHARACTER_CAPACITY);
   for (uint32_t index = 0; index < GBFR20_OWNER_CHARACTER_CAPACITY; ++index)
      snapshot.owner_character_hashes[index] =
         g_status_owner_character_hashes[index].load(std::memory_order_acquire);
   snapshot.last_apply_generation = g_last_apply_generation.load(std::memory_order_acquire);
   snapshot.last_apply_character_hash =
      g_last_apply_character_hash.load(std::memory_order_acquire);
   snapshot.last_apply_expected_count =
      g_last_apply_expected_count.load(std::memory_order_acquire);
   snapshot.last_apply_injected_count =
      g_last_apply_injected_count.load(std::memory_order_acquire);
   snapshot.last_apply_result =
      g_last_consumed_apply_result.load(std::memory_order_acquire);
   {
      std::scoped_lock lock(g_settings_mutex);
      snapshot.auto_apply = g_settings.auto_apply ? 1 : 0;
      snapshot.show_equipped = g_settings.show_equipped ? 1 : 0;
      snapshot.toggle_key = g_settings.toggle_key;
   }
   {
      std::shared_lock lock(g_authorization_mutex);
      snapshot.authorized_status_count =
         g_authorized_statuses.size() > UINT32_MAX
            ? UINT32_MAX
            : static_cast<uint32_t>(g_authorized_statuses.size());
   }
   snapshot.authorized_character_hash =
      g_last_authorized_character_hash.load(std::memory_order_acquire);
   snapshot.authorized_status_address =
      g_last_authorized_status_address.load(std::memory_order_acquire);
   snapshot.inventory_revision = g_inventory_revision.load(std::memory_order_acquire);
   snapshot.inventory_dirty = g_inventory_dirty.load(std::memory_order_acquire) ? 1 : 0;
   snapshot.edit_allowed =
      SafeCanEditCharacter(snapshot.effective_character_hash) ? 1 : 0;
   SafeReadUiModes(snapshot.ui_mode, snapshot.source_mode);
   snapshot.edit_session_state =
      g_edit_session_state.load(std::memory_order_acquire);
   snapshot.observed_character_hash =
      g_observed_character_hash.load(std::memory_order_acquire);
   snapshot.observed_status_address =
      g_observed_status_address.load(std::memory_order_acquire);
   snapshot.observed_status_context =
      g_observed_status_context.load(std::memory_order_acquire);
   snapshot.lifecycle_rebind_attempts =
      g_lifecycle_rebind_attempts.load(std::memory_order_acquire);
   snapshot.input_capture_requested =
      g_input_capture_requested.load(std::memory_order_acquire) ? 1 : 0;
   snapshot.input_capture_effective =
      g_input_capture_effective.load(std::memory_order_acquire) ? 1 : 0;
   snapshot.input_iat_hooks_ready =
      g_input_iat_hooks_ready.load(std::memory_order_acquire) ? 1 : 0;
   snapshot.direct_input_hook_ready =
      g_direct_input_hook_ready.load(std::memory_order_acquire) ? 1 : 0;
   snapshot.natural_bind_attempts =
      g_natural_bind_attempts.load(std::memory_order_acquire);
   snapshot.natural_bind_successes =
      g_natural_bind_successes.load(std::memory_order_acquire);
   snapshot.natural_bind_status_address =
      g_natural_bind_status_address.load(std::memory_order_acquire);
   snapshot.natural_bind_character_hash =
      g_natural_bind_character_hash.load(std::memory_order_acquire);
   snapshot.natural_bind_context =
      g_natural_bind_context.load(std::memory_order_acquire);
   snapshot.natural_bind_expected_count =
      g_natural_bind_expected_count.load(std::memory_order_acquire);
   snapshot.natural_bind_injected_count =
      g_natural_bind_injected_count.load(std::memory_order_acquire);
   snapshot.natural_bind_result =
      g_natural_bind_result.load(std::memory_order_acquire);
   snapshot.owner_manager_address =
      g_status_owner_manager_address.load(std::memory_order_acquire);
   snapshot.natural_bind_owner_key =
      g_natural_bind_owner_key.load(std::memory_order_acquire);
   snapshot.natural_bind_owner_status_address =
      g_natural_bind_owner_status_address.load(std::memory_order_acquire);
   std::memcpy(state, &snapshot, sizeof(snapshot));
   return 1;
}

uint32_t GBFR20_CALL GBFR20_CopyRuntimeMessage(char* buffer, uint32_t buffer_size)
{
   std::string message;
   {
      std::scoped_lock lock(g_message_mutex);
      message = g_runtime_message;
   }
   const size_t required_size = message.size() + 1;
   if (buffer != nullptr && buffer_size != 0)
   {
      const size_t copy_size = std::min<size_t>(message.size(), buffer_size - 1);
      std::memcpy(buffer, message.data(), copy_size);
      buffer[copy_size] = '\0';
   }
   return required_size > UINT32_MAX ? UINT32_MAX : static_cast<uint32_t>(required_size);
}

int32_t GBFR20_CALL GBFR20_RefreshInventory()
{
   EnsureInitialized();
   return RefreshInventorySnapshot() ? 1 : 0;
}

uint32_t GBFR20_CALL GBFR20_GetInventoryCount()
{
   std::scoped_lock lock(g_inventory_snapshot_mutex);
   return g_inventory_snapshot.size() > UINT32_MAX
      ? UINT32_MAX
      : static_cast<uint32_t>(g_inventory_snapshot.size());
}

int32_t GBFR20_CALL GBFR20_CopyInventoryItem(
   uint32_t index,
   GBFR20_InventoryItem* item,
   uint32_t item_size,
   char* label_buffer,
   uint32_t label_buffer_size)
{
   if (item == nullptr || item_size < sizeof(GBFR20_InventoryItem))
      return 0;
   std::scoped_lock lock(g_inventory_snapshot_mutex);
   if (index >= g_inventory_snapshot.size())
      return 0;

   const InventoryItem& source = g_inventory_snapshot[index];
   GBFR20_InventoryItem result{};
   result.gem = source.gem;
   result.equipped = source.equipped ? 1u : 0u;
   result.required_character_hash = source.required_character_hash;
   result.virtual_owner_character_hash = source.virtual_owner_character_hash;
   result.virtual_owner_slot = source.virtual_owner_slot;
   std::memcpy(item, &result, sizeof(result));
   if (label_buffer != nullptr && label_buffer_size != 0)
   {
      const size_t copy_size =
         std::min<size_t>(source.label.size(), label_buffer_size - 1);
      std::memcpy(label_buffer, source.label.data(), copy_size);
      label_buffer[copy_size] = '\0';
   }
   return 1;
}

int32_t GBFR20_CALL GBFR20_GetSelection(
   uint32_t character_hash,
   uint32_t* slots,
   uint32_t slot_count)
{
   if (character_hash == 0 || slots == nullptr || slot_count < kVirtualSlotCount)
      return 0;
   const auto selection = GetSelection(character_hash);
   std::memcpy(slots, selection.data(), sizeof(selection));
   return 1;
}

int32_t GBFR20_CALL GBFR20_SetSelection(
   uint32_t character_hash,
   int32_t virtual_slot,
   uint32_t inventory_slot_id)
{
   return SetSelection(character_hash, virtual_slot, inventory_slot_id) ? 1 : 0;
}

uint32_t GBFR20_CALL GBFR20_RequestApply(uint32_t character_hash)
{
   return SafeCanEditCharacter(character_hash) ? RequestHotApply(character_hash) : 0;
}

int32_t GBFR20_CALL GBFR20_SetAutoApply(int32_t enabled)
{
   {
      std::scoped_lock lock(g_settings_mutex);
      g_settings.auto_apply = enabled != 0;
   }
   SaveUiSettings();
   return 1;
}

int32_t GBFR20_CALL GBFR20_SetShowEquipped(int32_t enabled)
{
   {
      std::scoped_lock lock(g_settings_mutex);
      g_settings.show_equipped = enabled != 0;
   }
   SaveUiSettings();
   return 1;
}

int32_t GBFR20_CALL GBFR20_SetToggleKey(int32_t virtual_key)
{
   if (virtual_key < 1 || virtual_key > 255)
      return 0;
   {
      std::scoped_lock lock(g_settings_mutex);
      g_settings.toggle_key = virtual_key;
   }
   SaveUiSettings();
   return 1;
}

int32_t GBFR20_CALL GBFR20_SetInputCapture(int32_t requested)
{
   if (requested < 0)
   {
      g_input_capture_requested.store(false, std::memory_order_release);
      g_input_capture_effective.store(false, std::memory_order_release);
      g_input_neutral_frames.store(0, std::memory_order_release);
      return 1;
   }
   const bool enable = requested != 0;
   g_input_capture_requested.store(enable, std::memory_order_release);
   if (enable)
   {
      if (g_original_get_cursor_pos != nullptr)
         (void)g_original_get_cursor_pos(&g_frozen_cursor_position);
      else
         (void)GetCursorPos(&g_frozen_cursor_position);
      g_input_neutral_frames.store(0, std::memory_order_release);
      g_input_capture_effective.store(true, std::memory_order_release);
   }
   return 1;
}

int32_t GBFR20_CALL GBFR20_GetInputCaptureActive()
{
   return g_input_capture_effective.load(std::memory_order_acquire) ? 1 : 0;
}

int32_t GBFR20_CALL GBFR20_IsInventoryDirty()
{
   return g_inventory_dirty.load(std::memory_order_acquire) ? 1 : 0;
}

int32_t GBFR20_CALL GBFR20_CanEditCharacter(uint32_t character_hash)
{
   return SafeCanEditCharacter(character_hash) ? 1 : 0;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
   if (reason == DLL_PROCESS_ATTACH)
   {
      g_module = module;
      DisableThreadLibraryCalls(module);
   }
   else if (reason == DLL_PROCESS_DETACH)
   {
      g_shutting_down.store(true, std::memory_order_release);
   }
   return TRUE;
}
