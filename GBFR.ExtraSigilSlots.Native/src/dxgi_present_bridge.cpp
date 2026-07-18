#include "../native_internal.h"

namespace
{
using DxgiPresentFn = int32_t(__stdcall*)(void*, uint32_t, uint32_t);

constexpr int32_t kEPointer = static_cast<int32_t>(0x80004003u);
constexpr int32_t kEFail = static_cast<int32_t>(0x80004005u);

int CaptureExceptionCode(uint32_t code, uint32_t* destination) noexcept
{
   if (code != EXCEPTION_ACCESS_VIOLATION)
      return EXCEPTION_CONTINUE_SEARCH;
   if (destination != nullptr)
      *destination = code;
   return EXCEPTION_EXECUTE_HANDLER;
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
