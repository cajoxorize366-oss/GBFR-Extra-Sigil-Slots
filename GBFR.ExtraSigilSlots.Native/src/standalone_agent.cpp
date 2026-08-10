#include "../native_internal.h"
#include "../standalone_protocol.h"

#include <array>
#include <type_traits>

using namespace gbfr::native;

namespace
{
std::mutex g_standalone_start_mutex;
std::atomic_bool g_standalone_started{false};
std::atomic_uint32_t g_standalone_controller_process_id{0};

bool CopyBootstrapConfiguration(
   const void* source,
   GBFR20_StandaloneBootstrapConfig& destination) noexcept
{
   if (source == nullptr)
      return false;
   __try
   {
      std::memcpy(&destination, source, sizeof(destination));
      return true;
   }
   __except (EXCEPTION_EXECUTE_HANDLER)
   {
      std::memset(&destination, 0, sizeof(destination));
      return false;
   }
}

bool IsExpectedPipeClient(HANDLE pipe) noexcept
{
   ULONG client_process_id = 0;
   return GetNamedPipeClientProcessId(pipe, &client_process_id) != FALSE &&
      client_process_id != 0 &&
      client_process_id ==
         g_standalone_controller_process_id.load(std::memory_order_acquire);
}

std::wstring PipeName()
{
   return L"\\\\.\\pipe\\GBFR.ExtraSigilSlots.Standalone." +
      std::to_wstring(GetCurrentProcessId());
}

bool ReadExact(HANDLE pipe, void* destination, uint32_t size)
{
   auto* bytes = static_cast<uint8_t*>(destination);
   uint32_t completed = 0;
   while (completed < size)
   {
      DWORD current = 0;
      if (!ReadFile(pipe, bytes + completed, size - completed, &current, nullptr) ||
          current == 0)
         return false;
      completed += current;
   }
   return true;
}

bool WriteExact(HANDLE pipe, const void* source, uint32_t size)
{
   const auto* bytes = static_cast<const uint8_t*>(source);
   uint32_t completed = 0;
   while (completed < size)
   {
      DWORD current = 0;
      if (!WriteFile(pipe, bytes + completed, size - completed, &current, nullptr) ||
          current == 0)
         return false;
      completed += current;
   }
   return true;
}

template <typename T>
void AppendValue(std::vector<uint8_t>& output, const T& value)
{
   static_assert(std::is_trivially_copyable_v<T>);
   const auto* begin = reinterpret_cast<const uint8_t*>(&value);
   output.insert(output.end(), begin, begin + sizeof(T));
}

template <typename T>
bool ReadValue(const std::vector<uint8_t>& input, size_t& offset, T& value)
{
   static_assert(std::is_trivially_copyable_v<T>);
   if (offset > input.size() || input.size() - offset < sizeof(T))
      return false;
   std::memcpy(&value, input.data() + offset, sizeof(T));
   offset += sizeof(T);
   return true;
}

bool RequireEmpty(const std::vector<uint8_t>& payload, int32_t& status)
{
   if (payload.empty())
      return true;
   status = GBFR20_STANDALONE_STATUS_BAD_PAYLOAD;
   return false;
}

void Dispatch(
   uint16_t command,
   const std::vector<uint8_t>& payload,
   std::vector<uint8_t>& response,
   int32_t& status)
{
   status = GBFR20_STANDALONE_STATUS_OK;
   size_t offset = 0;
   switch (command)
   {
   case GBFR20_STANDALONE_HELLO:
   {
      if (!RequireEmpty(payload, status))
         return;
      GBFR20_RuntimeState state{};
      (void)GBFR20_GetState(&state, sizeof(state));
      const GBFR20_StandaloneHelloResponse hello{
         GBFR20_ABI_VERSION,
         GetCurrentProcessId(),
         state.initialized,
         state.hooks_ready};
      AppendValue(response, hello);
      return;
   }
   case GBFR20_STANDALONE_GET_STATE:
   {
      if (!RequireEmpty(payload, status))
         return;
      GBFR20_StandaloneStateResponse snapshot{};
      if (GBFR20_GetState(&snapshot.state, sizeof(snapshot.state)) == 0)
      {
         status = GBFR20_STANDALONE_STATUS_INTERNAL_ERROR;
         return;
      }
      snapshot.pending_virtual_slot_count = GBFR20_GetPendingVirtualSlotCount();
      bool message_is_error = false;
      const std::string message = GetRuntimeMessage(message_is_error);
      snapshot.runtime_message_size = static_cast<uint32_t>(message.size());
      AppendValue(response, snapshot);
      response.insert(response.end(), message.begin(), message.end());
      return;
   }
   case GBFR20_STANDALONE_REFRESH_INVENTORY:
   {
      if (!RequireEmpty(payload, status))
         return;
      if (GBFR20_RefreshInventory() == 0)
      {
         status = GBFR20_STANDALONE_STATUS_GAME_REJECTED;
         return;
      }
      const uint32_t count = std::min<uint32_t>(GBFR20_GetInventoryCount(), kMainGemCapacity);
      AppendValue(response, count);
      std::array<char, 4096> label{};
      for (uint32_t index = 0; index < count; ++index)
      {
         GBFR20_InventoryItem item{};
         label.fill('\0');
         if (GBFR20_CopyInventoryItem(
                index,
                &item,
                sizeof(item),
                label.data(),
                static_cast<uint32_t>(label.size())) == 0)
         {
            status = GBFR20_STANDALONE_STATUS_INTERNAL_ERROR;
            response.clear();
            return;
         }
         const uint32_t label_size = static_cast<uint32_t>(std::strlen(label.data()));
         if (response.size() + sizeof(item) + sizeof(label_size) + label_size >
             GBFR20_STANDALONE_MAX_PAYLOAD)
         {
            status = GBFR20_STANDALONE_STATUS_INTERNAL_ERROR;
            response.clear();
            return;
         }
         AppendValue(response, item);
         AppendValue(response, label_size);
         response.insert(response.end(), label.data(), label.data() + label_size);
      }
      return;
   }
   case GBFR20_STANDALONE_GET_SELECTION:
   {
      uint32_t character_hash = 0;
      if (!ReadValue(payload, offset, character_hash) || offset != payload.size())
      {
         status = GBFR20_STANDALONE_STATUS_BAD_PAYLOAD;
         return;
      }
      std::array<uint32_t, GBFR20_VIRTUAL_SLOT_CAPACITY> slots{};
      if (GBFR20_GetSelection(
             character_hash,
             slots.data(),
             static_cast<uint32_t>(slots.size())) == 0)
      {
         status = GBFR20_STANDALONE_STATUS_GAME_REJECTED;
         return;
      }
      const auto* begin = reinterpret_cast<const uint8_t*>(slots.data());
      response.insert(response.end(), begin, begin + sizeof(slots));
      return;
   }
   case GBFR20_STANDALONE_SET_SELECTION:
   {
      uint32_t character_hash = 0;
      int32_t virtual_slot = 0;
      uint32_t inventory_slot_id = 0;
      if (!ReadValue(payload, offset, character_hash) ||
          !ReadValue(payload, offset, virtual_slot) ||
          !ReadValue(payload, offset, inventory_slot_id) || offset != payload.size())
      {
         status = GBFR20_STANDALONE_STATUS_BAD_PAYLOAD;
         return;
      }
      if (GBFR20_SetSelection(character_hash, virtual_slot, inventory_slot_id) == 0)
         status = GBFR20_STANDALONE_STATUS_GAME_REJECTED;
      return;
   }
   case GBFR20_STANDALONE_APPLY_PRESET:
   {
      uint32_t selection_count = 0;
      if (!ReadValue(payload, offset, selection_count) || selection_count == 0 ||
          selection_count > GBFR20_PRESET_CHARACTER_CAPACITY)
      {
         status = GBFR20_STANDALONE_STATUS_BAD_PAYLOAD;
         return;
      }
      const size_t selection_bytes =
         static_cast<size_t>(selection_count) * sizeof(GBFR20_PresetCharacterSelection);
      if (offset > payload.size() || payload.size() - offset != selection_bytes)
      {
         status = GBFR20_STANDALONE_STATUS_BAD_PAYLOAD;
         return;
      }
      std::vector<GBFR20_PresetCharacterSelection> selections(selection_count);
      std::memcpy(selections.data(), payload.data() + offset, selection_bytes);
      std::vector<GBFR20_PresetSlotResult> results(
         static_cast<size_t>(selection_count) * GBFR20_VIRTUAL_SLOT_CAPACITY);
      uint32_t result_count = 0;
      if (GBFR20_ApplyPreset(
             selections.data(),
             selection_count,
             results.data(),
             static_cast<uint32_t>(results.size()),
             &result_count) == 0 || result_count > results.size())
      {
         status = GBFR20_STANDALONE_STATUS_GAME_REJECTED;
         return;
      }
      AppendValue(response, result_count);
      const auto* begin = reinterpret_cast<const uint8_t*>(results.data());
      response.insert(
         response.end(), begin, begin + static_cast<size_t>(result_count) * sizeof(results[0]));
      return;
   }
   case GBFR20_STANDALONE_REQUEST_APPLY:
   {
      uint32_t character_hash = 0;
      if (!ReadValue(payload, offset, character_hash) || offset != payload.size())
      {
         status = GBFR20_STANDALONE_STATUS_BAD_PAYLOAD;
         return;
      }
      const uint32_t generation = GBFR20_RequestApply(character_hash);
      AppendValue(response, generation);
      return;
   }
   case GBFR20_STANDALONE_SET_LANGUAGE:
   {
      int32_t language = 0;
      if (!ReadValue(payload, offset, language) || offset != payload.size())
      {
         status = GBFR20_STANDALONE_STATUS_BAD_PAYLOAD;
         return;
      }
      if (GBFR20_SetLanguage(language) == 0)
         status = GBFR20_STANDALONE_STATUS_GAME_REJECTED;
      return;
   }
   case GBFR20_STANDALONE_REQUEST_VIRTUAL_SLOT_COUNT:
   {
      int32_t slot_count = 0;
      if (!ReadValue(payload, offset, slot_count) || offset != payload.size())
      {
         status = GBFR20_STANDALONE_STATUS_BAD_PAYLOAD;
         return;
      }
      const int32_t result = GBFR20_RequestVirtualSlotCount(slot_count);
      AppendValue(response, result);
      if (result == GBFR20_SLOT_COUNT_REQUEST_FAILED)
         status = GBFR20_STANDALONE_STATUS_GAME_REJECTED;
      return;
   }
   case GBFR20_STANDALONE_GET_PENDING_VIRTUAL_SLOT_COUNT:
   {
      if (!RequireEmpty(payload, status))
         return;
      const int32_t pending = GBFR20_GetPendingVirtualSlotCount();
      AppendValue(response, pending);
      return;
   }
   default:
      status = GBFR20_STANDALONE_STATUS_UNKNOWN_COMMAND;
      return;
   }
}

void ServeClient(HANDLE pipe)
{
   while (!g_shutting_down.load(std::memory_order_acquire))
   {
      if (!IsExpectedPipeClient(pipe))
         return;
      GBFR20_StandaloneFrameHeader request{};
      if (!ReadExact(pipe, &request, sizeof(request)))
         return;

      int32_t status = GBFR20_STANDALONE_STATUS_OK;
      if (request.magic != GBFR20_STANDALONE_FRAME_MAGIC)
         status = GBFR20_STANDALONE_STATUS_BAD_FRAME;
      else if (request.protocol_version != GBFR20_STANDALONE_PROTOCOL_VERSION)
         status = GBFR20_STANDALONE_STATUS_UNSUPPORTED_VERSION;
      else if (request.payload_size > GBFR20_STANDALONE_MAX_PAYLOAD)
         status = GBFR20_STANDALONE_STATUS_BAD_FRAME;
      bool close_after_response = status != GBFR20_STANDALONE_STATUS_OK;

      std::vector<uint8_t> payload;
      if (status == GBFR20_STANDALONE_STATUS_OK && request.payload_size != 0)
      {
         payload.resize(request.payload_size);
         if (!ReadExact(pipe, payload.data(), request.payload_size))
            return;
      }

      std::vector<uint8_t> response_payload;
      if (status == GBFR20_STANDALONE_STATUS_OK)
      {
         try
         {
            Dispatch(request.command, payload, response_payload, status);
         }
         catch (...)
         {
            response_payload.clear();
            status = GBFR20_STANDALONE_STATUS_INTERNAL_ERROR;
         }
         if (status != GBFR20_STANDALONE_STATUS_OK)
            close_after_response = true;
      }

      GBFR20_StandaloneFrameHeader response{
         GBFR20_STANDALONE_FRAME_MAGIC,
         GBFR20_STANDALONE_PROTOCOL_VERSION,
         request.command,
         request.request_id,
         status,
         static_cast<uint32_t>(response_payload.size())};
      if (!WriteExact(pipe, &response, sizeof(response)) ||
          (!response_payload.empty() &&
           !WriteExact(pipe, response_payload.data(), response.payload_size)))
         return;
      if (close_after_response)
         return;
   }
}

void PipeLoop()
{
   const std::wstring pipe_name = PipeName();
   while (!g_shutting_down.load(std::memory_order_acquire))
   {
      HANDLE pipe = CreateNamedPipeW(
         pipe_name.c_str(),
         PIPE_ACCESS_DUPLEX,
         PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
         1,
         64 * 1024,
         64 * 1024,
         0,
         nullptr);
      if (pipe == INVALID_HANDLE_VALUE)
      {
         Sleep(250);
         continue;
      }

      const bool connected = ConnectNamedPipe(pipe, nullptr) != FALSE ||
         GetLastError() == ERROR_PIPE_CONNECTED;
      if (connected && IsExpectedPipeClient(pipe))
      {
         ServeClient(pipe);
         FlushFileBuffers(pipe);
         DisconnectNamedPipe(pipe);
      }
      CloseHandle(pipe);
   }
}

DWORD WINAPI InitializeStandalone(void*)
{
   (void)GBFR20_Initialize();
   return 0;
}

DWORD WINAPI StandaloneMain(void*)
{
   (void)GBFR20_SetInputHooksEnabled(0);
   g_standalone_owner_tick_enabled.store(true, std::memory_order_release);
   if (HANDLE initialize_thread =
          CreateThread(nullptr, 0, &InitializeStandalone, nullptr, 0, nullptr);
       initialize_thread != nullptr)
      CloseHandle(initialize_thread);
   PipeLoop();
   return 0;
}
}

uint32_t GBFR20_CALL GBFR20_StandaloneBootstrap(const void* configuration)
{
   GBFR20_StandaloneBootstrapConfig bootstrap{};
   if (!CopyBootstrapConfiguration(configuration, bootstrap))
      return 0;
   if (bootstrap.magic != GBFR20_STANDALONE_BOOTSTRAP_MAGIC ||
       bootstrap.protocol_version != GBFR20_STANDALONE_PROTOCOL_VERSION ||
       bootstrap.struct_size != sizeof(bootstrap) ||
       bootstrap.controller_process_id == 0 ||
       bootstrap.controller_process_id == GetCurrentProcessId())
      return 0;

   size_t directory_length = 0;
   while (directory_length < GBFR20_STANDALONE_DATA_DIRECTORY_CAPACITY &&
          bootstrap.data_directory[directory_length] != 0)
      ++directory_length;
   if (directory_length == 0 ||
       directory_length == GBFR20_STANDALONE_DATA_DIRECTORY_CAPACITY)
      return 0;

   HANDLE thread = nullptr;
   try
   {
      static_assert(sizeof(wchar_t) == sizeof(uint16_t));
      const std::filesystem::path data_directory(std::wstring(
         reinterpret_cast<const wchar_t*>(bootstrap.data_directory), directory_length));
      if (!data_directory.is_absolute())
         return 0;

      std::scoped_lock lock(g_standalone_start_mutex);
      if (g_standalone_started.load(std::memory_order_acquire))
      {
         std::scoped_lock directory_lock(g_directory_mutex);
         if (g_data_directory.empty() || g_data_directory != data_directory)
            return 0;
         g_standalone_controller_process_id.store(
            bootstrap.controller_process_id, std::memory_order_release);
         return 1;
      }
      if (g_initialized.load(std::memory_order_acquire) ||
          !ConfigureStandaloneDataDirectory(data_directory))
         return 0;

      g_standalone_controller_process_id.store(
         bootstrap.controller_process_id, std::memory_order_release);
      thread = CreateThread(nullptr, 0, &StandaloneMain, nullptr, 0, nullptr);
      if (thread == nullptr)
      {
         g_standalone_controller_process_id.store(0, std::memory_order_release);
         return 0;
      }
      g_standalone_started.store(true, std::memory_order_release);
   }
   catch (...)
   {
      return 0;
   }

   CloseHandle(thread);
   return 1;
}
