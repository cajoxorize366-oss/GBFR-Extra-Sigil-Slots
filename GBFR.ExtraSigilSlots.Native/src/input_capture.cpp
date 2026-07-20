#define DIRECTINPUT_VERSION 0x0800

#include "../native_internal.h"

#include <dinput.h>

namespace gbfr::native
{
SafetyHookInline g_direct_input_get_state_hook;
SafetyHookInline g_direct_input_get_data_hook;
std::atomic_bool g_input_capture_requested{false};
std::atomic_bool g_input_capture_effective{false};
std::atomic_uint32_t g_input_neutral_frames{0};
std::atomic_bool g_input_iat_hooks_ready{false};
std::atomic_bool g_direct_input_hook_ready{false};
std::atomic_uintptr_t g_direct_input_mouse_device{0};
std::mutex g_input_hook_mutex;
std::vector<IatPatch> g_iat_patches;
POINT g_frozen_cursor_position{};
GetAsyncKeyStateFn g_original_get_async_key_state = nullptr;
GetKeyStateFn g_original_get_key_state = nullptr;
GetCursorPosFn g_original_get_cursor_pos = nullptr;
SetCursorPosFn g_original_set_cursor_pos = nullptr;
ClipCursorFn g_original_clip_cursor = nullptr;
std::atomic_uint32_t g_active_input_calls{0};

namespace
{
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

HRESULT __fastcall DirectInputGetDeviceStateDetour(
   void* device,
   DWORD data_size,
   void* data)
{
   ActiveCallGuard guard(g_active_input_calls);
   const HRESULT result = g_direct_input_get_state_hook.call<HRESULT>(
      device, data_size, data);
   if (SUCCEEDED(result) && data != nullptr && data_size != 0 &&
       reinterpret_cast<uintptr_t>(device) ==
          g_direct_input_mouse_device.load(std::memory_order_acquire) &&
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
       reinterpret_cast<uintptr_t>(device) ==
          g_direct_input_mouse_device.load(std::memory_order_acquire) &&
       g_input_capture_effective.load(std::memory_order_acquire))
   {
      if (object_data != nullptr && object_data_size != 0 && requested != 0)
         std::memset(object_data, 0, static_cast<size_t>(object_data_size) * requested);
      *object_count = 0;
   }
   return result;
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

bool IsPhysicalInputNeutral()
{
   if (g_original_get_async_key_state != nullptr)
   {
      for (int key = VK_BACK; key <= 0xFE; ++key)
         if ((g_original_get_async_key_state(key) & 0x8000) != 0)
            return false;
   }
   return true;
}
}

void RestoreInputIatHooks()
{
   std::scoped_lock lock(g_input_hook_mutex);
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

   g_input_iat_hooks_ready.store(succeeded, std::memory_order_release);
   Log(succeeded
      ? "Game-local keyboard and mouse USER32 input gates installed; controller input is passed through."
      : "One or more game-local keyboard/mouse USER32 input gates failed to install.");
   return succeeded;
}

void TryInstallDirectInputHooks()
{
   if (!g_hooks_ready.load(std::memory_order_acquire) ||
       g_direct_input_hook_ready.load(std::memory_order_acquire) || g_image_base == 0)
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
   g_direct_input_mouse_device.store(device, std::memory_order_release);
   g_direct_input_hook_ready.store(true, std::memory_order_release);
   Log("Existing DirectInput mouse device state/data gates installed; other devices are passed through.");
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
}
