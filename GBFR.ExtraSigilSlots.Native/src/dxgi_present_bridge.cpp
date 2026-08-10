#include "../native_internal.h"

#include <d3d11.h>
#include <dxgi.h>
#include <sstream>

namespace
{
using DxgiPresentFn = int32_t(__stdcall*)(void*, uint32_t, uint32_t);

constexpr int32_t kEPointer = static_cast<int32_t>(0x80004003u);
constexpr int32_t kEFail = static_cast<int32_t>(0x80004005u);
constexpr uint32_t kMaxSupportedJumpCount = 32;

enum class ResolveStatus : uint32_t
{
   Ok = 0,
   InvalidArgument = 1,
   Unreadable = 2,
   NonExecutable = 3,
   Cycle = 4,
   DepthExceeded = 5,
   UnsupportedJump = 6,
};

enum class JumpDecodeResult
{
   NotJump,
   Resolved,
   Invalid,
   Unsupported,
};

bool IsReadableProtection(DWORD protection) noexcept
{
   if ((protection & (PAGE_GUARD | PAGE_NOACCESS)) != 0)
      return false;
   switch (protection & 0xFFu)
   {
   case PAGE_READONLY:
   case PAGE_READWRITE:
   case PAGE_WRITECOPY:
   case PAGE_EXECUTE:
   case PAGE_EXECUTE_READ:
   case PAGE_EXECUTE_READWRITE:
   case PAGE_EXECUTE_WRITECOPY:
      return true;
   default:
      return false;
   }
}

bool IsExecutableProtection(DWORD protection) noexcept
{
   if ((protection & (PAGE_GUARD | PAGE_NOACCESS)) != 0)
      return false;
   switch (protection & 0xFFu)
   {
   case PAGE_EXECUTE:
   case PAGE_EXECUTE_READ:
   case PAGE_EXECUTE_READWRITE:
   case PAGE_EXECUTE_WRITECOPY:
      return true;
   default:
      return false;
   }
}

bool IsReadableRange(uintptr_t address, size_t size) noexcept
{
   if (address == 0 || size == 0 || address > UINTPTR_MAX - (size - 1))
      return false;

   const uintptr_t last = address + size - 1;
   uintptr_t cursor = address;
   while (cursor <= last)
   {
      MEMORY_BASIC_INFORMATION information{};
      if (VirtualQuery(
             reinterpret_cast<const void*>(cursor),
             &information,
             sizeof(information)) == 0 ||
          information.State != MEM_COMMIT ||
          !IsReadableProtection(information.Protect))
      {
         return false;
      }

      const uintptr_t region_begin = reinterpret_cast<uintptr_t>(
         information.BaseAddress);
      if (information.RegionSize == 0 ||
          region_begin > UINTPTR_MAX - information.RegionSize)
      {
         return false;
      }
      const uintptr_t region_end = region_begin + information.RegionSize;
      if (cursor < region_begin || cursor >= region_end)
         return false;
      if (last < region_end)
         return true;
      cursor = region_end;
   }
   return true;
}

bool IsExecutableAddress(uintptr_t address) noexcept
{
   MEMORY_BASIC_INFORMATION information{};
   return address != 0 &&
      VirtualQuery(
         reinterpret_cast<const void*>(address),
         &information,
         sizeof(information)) != 0 &&
      information.State == MEM_COMMIT &&
      IsExecutableProtection(information.Protect);
}

bool TryReadMemory(uintptr_t address, void* destination, size_t size) noexcept
{
   if (destination == nullptr || !IsReadableRange(address, size))
      return false;
   __try
   {
      std::memcpy(destination, reinterpret_cast<const void*>(address), size);
      return true;
   }
   __except (EXCEPTION_EXECUTE_HANDLER)
   {
      return false;
   }
}

template <typename T>
bool TryReadValue(uintptr_t address, T* destination) noexcept
{
   return TryReadMemory(address, destination, sizeof(T));
}

bool TryAddressAtOffset(
   uintptr_t address,
   size_t offset,
   uintptr_t* result_out) noexcept
{
   if (result_out == nullptr || address > UINTPTR_MAX - offset)
      return false;
   *result_out = address + offset;
   return true;
}

template <typename T>
bool TryReadValueAtOffset(
   uintptr_t address,
   size_t offset,
   T* destination) noexcept
{
   uintptr_t source = 0;
   return TryAddressAtOffset(address, offset, &source) &&
      TryReadValue(source, destination);
}

bool TryReadMemoryAtOffset(
   uintptr_t address,
   size_t offset,
   void* destination,
   size_t size) noexcept
{
   uintptr_t source = 0;
   return TryAddressAtOffset(address, offset, &source) &&
      TryReadMemory(source, destination, size);
}

bool TryAddRelative(
   uintptr_t instruction_end,
   int64_t displacement,
   uintptr_t* target_out) noexcept
{
   if (target_out == nullptr)
      return false;
   if (displacement >= 0)
   {
      const auto offset = static_cast<uintptr_t>(displacement);
      if (instruction_end > UINTPTR_MAX - offset)
         return false;
      *target_out = instruction_end + offset;
      return true;
   }

   const auto magnitude = static_cast<uintptr_t>(
      static_cast<uint64_t>(-(displacement + 1)) + 1);
   if (instruction_end < magnitude)
      return false;
   *target_out = instruction_end - magnitude;
   return true;
}

JumpDecodeResult DecodeEntryJump(
   uintptr_t address,
   uintptr_t* target_out) noexcept
{
   std::array<uint8_t, 2> prefix{};
   if (target_out == nullptr ||
       !TryReadMemory(address, prefix.data(), prefix.size()))
   {
      return JumpDecodeResult::Invalid;
   }

   if (prefix[0] == 0xE9)
   {
      int32_t displacement = 0;
      uintptr_t instruction_end = 0;
      if (!TryReadValueAtOffset(address, 1, &displacement) ||
          !TryAddressAtOffset(address, 5, &instruction_end) ||
          !TryAddRelative(instruction_end, displacement, target_out))
      {
         return JumpDecodeResult::Invalid;
      }
      return JumpDecodeResult::Resolved;
   }
   if (prefix[0] == 0xEB)
   {
      int8_t displacement = 0;
      uintptr_t instruction_end = 0;
      if (!TryReadValueAtOffset(address, 1, &displacement) ||
          !TryAddressAtOffset(address, 2, &instruction_end) ||
          !TryAddRelative(instruction_end, displacement, target_out))
      {
         return JumpDecodeResult::Invalid;
      }
      return JumpDecodeResult::Resolved;
   }

   uintptr_t pointer_slot = 0;
   if (prefix[0] == 0xFF && prefix[1] == 0x25)
   {
      int32_t displacement = 0;
      uintptr_t instruction_end = 0;
      if (!TryReadValueAtOffset(address, 2, &displacement) ||
          !TryAddressAtOffset(address, 6, &instruction_end) ||
          !TryAddRelative(instruction_end, displacement, &pointer_slot) ||
          !TryReadValue(pointer_slot, target_out))
      {
         return JumpDecodeResult::Invalid;
      }
      return JumpDecodeResult::Resolved;
   }
   if (prefix[0] == 0xFF && (prefix[1] & 0x38u) == 0x20u)
      return JumpDecodeResult::Unsupported;

   if ((prefix[0] & 0xF0u) == 0x40u && prefix[1] == 0xFF)
   {
      uint8_t mod_rm = 0;
      if (!TryReadValueAtOffset(address, 2, &mod_rm))
         return JumpDecodeResult::Invalid;
      const bool is_jump = (prefix[0] & 0x04u) == 0 &&
         (mod_rm & 0x38u) == 0x20u;
      if (is_jump && mod_rm == 0x25)
      {
         int32_t displacement = 0;
         uintptr_t instruction_end = 0;
         if (!TryReadValueAtOffset(address, 3, &displacement) ||
             !TryAddressAtOffset(address, 7, &instruction_end) ||
             !TryAddRelative(instruction_end, displacement, &pointer_slot) ||
             !TryReadValue(pointer_slot, target_out))
         {
            return JumpDecodeResult::Invalid;
         }
         return JumpDecodeResult::Resolved;
      }
      if (is_jump)
         return JumpDecodeResult::Unsupported;
   }

   if ((prefix[0] == 0x48 || prefix[0] == 0x49) &&
       prefix[1] >= 0xB8 && prefix[1] <= 0xBF)
   {
      uintptr_t immediate_target = 0;
      if (!TryReadValueAtOffset(address, 2, &immediate_target))
         return JumpDecodeResult::Invalid;

      const uint8_t register_index = static_cast<uint8_t>(prefix[1] - 0xB8);
      if (prefix[0] == 0x48)
      {
         std::array<uint8_t, 2> suffix{};
         if (!TryReadMemoryAtOffset(
                address, 10, suffix.data(), suffix.size()))
            return JumpDecodeResult::Invalid;
         if (suffix[0] == 0xFF &&
             suffix[1] == static_cast<uint8_t>(0xE0 + register_index))
         {
            *target_out = immediate_target;
            return JumpDecodeResult::Resolved;
         }
      }
      else
      {
         std::array<uint8_t, 3> suffix{};
         if (!TryReadMemoryAtOffset(
                address, 10, suffix.data(), suffix.size()))
            return JumpDecodeResult::Invalid;
         if (suffix[0] == 0x41 && suffix[1] == 0xFF &&
             suffix[2] == static_cast<uint8_t>(0xE0 + register_index))
         {
            *target_out = immediate_target;
            return JumpDecodeResult::Resolved;
         }
      }
   }

   return JumpDecodeResult::NotJump;
}

void SetResolveOutputs(
   uint32_t jump_count,
   ResolveStatus status,
   uint32_t* jump_count_out,
   uint32_t* status_out) noexcept
{
   if (jump_count_out != nullptr)
      *jump_count_out = jump_count;
   if (status_out != nullptr)
      *status_out = static_cast<uint32_t>(status);
}

int CaptureExceptionCode(uint32_t code, uint32_t* destination) noexcept
{
   if (code != EXCEPTION_ACCESS_VIOLATION)
      return EXCEPTION_CONTINUE_SEARCH;
   if (destination != nullptr)
      *destination = code;
   return EXCEPTION_EXECUTE_HANDLER;
}
}

namespace gbfr::native
{
namespace
{
SafetyHookInline g_standalone_present_hook;
std::atomic_uint32_t g_active_standalone_present_calls{0};
std::atomic_bool g_standalone_present_seen{false};
thread_local bool g_tls_standalone_present_tick_active = false;

void ReleaseDummySwapChain(
   IDXGISwapChain*& swap_chain,
   ID3D11DeviceContext*& context,
   ID3D11Device*& device,
   HWND& window) noexcept
{
   if (swap_chain != nullptr)
   {
      swap_chain->Release();
      swap_chain = nullptr;
   }
   if (context != nullptr)
   {
      context->Release();
      context = nullptr;
   }
   if (device != nullptr)
   {
      device->Release();
      device = nullptr;
   }
   if (window != nullptr)
   {
      DestroyWindow(window);
      window = nullptr;
   }
}

bool ResolveStandalonePresentTarget(uintptr_t& target) noexcept
{
   target = 0;
   HWND window = CreateWindowExW(
      0,
      L"STATIC",
      L"GBFR Extra Sigil Slots Standalone Present Probe",
      WS_POPUP,
      0,
      0,
      2,
      2,
      nullptr,
      nullptr,
      GetModuleHandleW(nullptr),
      nullptr);
   if (window == nullptr)
      return false;

   DXGI_SWAP_CHAIN_DESC description{};
   description.BufferDesc.Width = 2;
   description.BufferDesc.Height = 2;
   description.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
   description.SampleDesc.Count = 1;
   description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
   description.BufferCount = 1;
   description.OutputWindow = window;
   description.Windowed = TRUE;
   description.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

   IDXGISwapChain* swap_chain = nullptr;
   ID3D11Device* device = nullptr;
   ID3D11DeviceContext* context = nullptr;
   D3D_FEATURE_LEVEL feature_level{};
   const HRESULT result = D3D11CreateDeviceAndSwapChain(
      nullptr,
      D3D_DRIVER_TYPE_HARDWARE,
      nullptr,
      0,
      nullptr,
      0,
      D3D11_SDK_VERSION,
      &description,
      &swap_chain,
      &device,
      &feature_level,
      &context);
   if (FAILED(result) || swap_chain == nullptr)
   {
      ReleaseDummySwapChain(swap_chain, context, device, window);
      return false;
   }

   void** vtable = *reinterpret_cast<void***>(swap_chain);
   const uintptr_t present_entry = vtable == nullptr
      ? 0
      : reinterpret_cast<uintptr_t>(vtable[8]);
   uint32_t jump_count = 0;
   uint32_t resolve_status = 0;
   const uint64_t resolved = GBFR20_ResolveHookChainTarget(
      present_entry,
      16,
      &jump_count,
      &resolve_status);
   ReleaseDummySwapChain(swap_chain, context, device, window);
   if (resolved == 0)
      return false;

   target = static_cast<uintptr_t>(resolved);
   return true;
}

HRESULT __stdcall StandalonePresentDetour(
   IDXGISwapChain* swap_chain,
   uint32_t sync_interval,
   uint32_t present_flags)
{
   ActiveCallGuard active_call(g_active_standalone_present_calls);
   if (!g_shutting_down.load(std::memory_order_acquire) &&
       g_standalone_owner_tick_enabled.load(std::memory_order_acquire) &&
       g_hooks_ready.load(std::memory_order_acquire) &&
       !g_tls_standalone_present_tick_active)
   {
      g_tls_standalone_present_tick_active = true;
      try
      {
         if (!g_standalone_present_seen.exchange(true, std::memory_order_acq_rel))
         {
            SetRuntimeMessage(
               "Standalone DX11 Present tick source is active; queued virtual-sigil rebuilds now run on the game render thread.",
               false);
         }
         GBFR20_Tick();
      }
      catch (...)
      {
         try
         {
            Log("Standalone DX11 Present tick contained an unexpected exception.");
         }
         catch (...)
         {
         }
      }
      g_tls_standalone_present_tick_active = false;
   }

   return g_standalone_present_hook.call<HRESULT>(
      swap_chain, sync_interval, present_flags);
}
}

bool InstallStandalonePresentTickHook()
{
   if (!g_standalone_owner_tick_enabled.load(std::memory_order_acquire))
      return true;
   if (g_standalone_present_hook)
      return true;

   uintptr_t target = 0;
   if (!ResolveStandalonePresentTarget(target))
   {
      Log("Standalone DX11 Present target resolution failed.");
      return false;
   }

   g_standalone_present_hook = safetyhook::create_inline(
      reinterpret_cast<void*>(target),
      reinterpret_cast<void*>(&StandalonePresentDetour));
   if (!g_standalone_present_hook)
   {
      Log("Standalone DX11 Present tick hook installation failed.");
      return false;
   }

   std::ostringstream message;
   message << "Standalone DX11 Present tick hook installed at 0x"
           << std::uppercase << std::hex << target << ".";
   Log(message.str());
   return true;
}

void ShutdownStandalonePresentTickHook() noexcept
{
   if (g_standalone_present_hook)
      (void)g_standalone_present_hook.disable();
   while (g_active_standalone_present_calls.load(std::memory_order_acquire) != 0)
      SwitchToThread();
   g_standalone_present_hook.reset();
   g_standalone_present_seen.store(false, std::memory_order_release);
}
}

uint64_t GBFR20_CALL GBFR20_ResolveHookChainTarget(
   uint64_t function_address,
   uint32_t max_jump_count,
   uint32_t* jump_count_out,
   uint32_t* status_out)
{
   SetResolveOutputs(0, ResolveStatus::Ok, jump_count_out, status_out);
   if (function_address == 0 || max_jump_count == 0 ||
       max_jump_count > kMaxSupportedJumpCount)
   {
      SetResolveOutputs(
         0, ResolveStatus::InvalidArgument, jump_count_out, status_out);
      return 0;
   }

   uintptr_t current = static_cast<uintptr_t>(function_address);
   std::array<uintptr_t, kMaxSupportedJumpCount + 1> visited{};
   uint32_t visited_count = 0;
   uint32_t jump_count = 0;
   for (;;)
   {
      if (!IsExecutableAddress(current))
      {
         SetResolveOutputs(
            jump_count, ResolveStatus::NonExecutable, jump_count_out, status_out);
         return 0;
      }
      if (std::find(
             visited.begin(),
             visited.begin() + visited_count,
             current) != visited.begin() + visited_count)
      {
         SetResolveOutputs(
            jump_count, ResolveStatus::Cycle, jump_count_out, status_out);
         return 0;
      }
      visited[visited_count++] = current;

      uintptr_t next = 0;
      const JumpDecodeResult decode_result = DecodeEntryJump(current, &next);
      if (decode_result == JumpDecodeResult::Invalid)
      {
         SetResolveOutputs(
            jump_count, ResolveStatus::Unreadable, jump_count_out, status_out);
         return 0;
      }
      if (decode_result == JumpDecodeResult::Unsupported)
      {
         SetResolveOutputs(
            jump_count,
            ResolveStatus::UnsupportedJump,
            jump_count_out,
            status_out);
         return 0;
      }
      if (decode_result == JumpDecodeResult::NotJump)
      {
         SetResolveOutputs(
            jump_count, ResolveStatus::Ok, jump_count_out, status_out);
         return static_cast<uint64_t>(current);
      }
      if (jump_count >= max_jump_count)
      {
         SetResolveOutputs(
            jump_count,
            ResolveStatus::DepthExceeded,
            jump_count_out,
            status_out);
         return 0;
      }

      current = next;
      ++jump_count;
   }
}

int32_t GBFR20_CALL GBFR20_InvokeOriginalPresent(
   uint64_t original_function_address,
   void* swap_chain,
   uint32_t sync_interval,
   uint32_t present_flags,
   uint32_t* exception_code_out)
{
   if (exception_code_out != nullptr)
      *exception_code_out = 0;
   if (original_function_address == 0 || swap_chain == nullptr)
      return kEPointer;

   const auto present = reinterpret_cast<DxgiPresentFn>(
      static_cast<uintptr_t>(original_function_address));
   __try
   {
      return present(swap_chain, sync_interval, present_flags);
   }
   __except (CaptureExceptionCode(
      static_cast<uint32_t>(GetExceptionCode()),
      exception_code_out))
   {
      return kEFail;
   }
}
