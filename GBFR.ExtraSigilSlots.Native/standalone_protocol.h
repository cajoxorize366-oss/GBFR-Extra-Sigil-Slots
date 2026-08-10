#pragma once

#include "native_api.h"

#include <cstdint>

constexpr uint32_t GBFR20_STANDALONE_BOOTSTRAP_MAGIC = 0x42524647;
constexpr uint32_t GBFR20_STANDALONE_FRAME_MAGIC = 0x53524647;
constexpr uint16_t GBFR20_STANDALONE_PROTOCOL_VERSION = 1;
constexpr uint32_t GBFR20_STANDALONE_MAX_PAYLOAD = 8u * 1024u * 1024u;
constexpr uint32_t GBFR20_STANDALONE_DATA_DIRECTORY_CAPACITY = 1024;

enum GBFR20_StandaloneCommand : uint16_t
{
   GBFR20_STANDALONE_HELLO = 1,
   GBFR20_STANDALONE_GET_STATE = 2,
   GBFR20_STANDALONE_REFRESH_INVENTORY = 3,
   GBFR20_STANDALONE_GET_SELECTION = 4,
   GBFR20_STANDALONE_SET_SELECTION = 5,
   GBFR20_STANDALONE_APPLY_PRESET = 6,
   GBFR20_STANDALONE_REQUEST_APPLY = 7,
   GBFR20_STANDALONE_SET_LANGUAGE = 8,
   GBFR20_STANDALONE_REQUEST_VIRTUAL_SLOT_COUNT = 9,
   GBFR20_STANDALONE_GET_PENDING_VIRTUAL_SLOT_COUNT = 10,
};

enum GBFR20_StandaloneStatus : int32_t
{
   GBFR20_STANDALONE_STATUS_OK = 0,
   GBFR20_STANDALONE_STATUS_BAD_FRAME = -1,
   GBFR20_STANDALONE_STATUS_UNSUPPORTED_VERSION = -2,
   GBFR20_STANDALONE_STATUS_UNKNOWN_COMMAND = -3,
   GBFR20_STANDALONE_STATUS_BAD_PAYLOAD = -4,
   GBFR20_STANDALONE_STATUS_GAME_REJECTED = -5,
   GBFR20_STANDALONE_STATUS_INTERNAL_ERROR = -6,
};

#pragma pack(push, 1)
struct GBFR20_StandaloneBootstrapConfig
{
   uint32_t magic;
   uint16_t protocol_version;
   uint16_t reserved;
   uint32_t struct_size;
   uint32_t controller_process_id;
   uint16_t data_directory[GBFR20_STANDALONE_DATA_DIRECTORY_CAPACITY];
};

struct GBFR20_StandaloneFrameHeader
{
   uint32_t magic;
   uint16_t protocol_version;
   uint16_t command;
   uint32_t request_id;
   int32_t status;
   uint32_t payload_size;
};

struct GBFR20_StandaloneHelloResponse
{
   uint32_t native_abi_version;
   uint32_t process_id;
   int32_t initialized;
   int32_t hooks_ready;
};

struct GBFR20_StandaloneStateResponse
{
   GBFR20_RuntimeState state;
   int32_t pending_virtual_slot_count;
   uint32_t runtime_message_size;
};
#pragma pack(pop)

static_assert(sizeof(GBFR20_StandaloneFrameHeader) == 20);
static_assert(sizeof(GBFR20_StandaloneHelloResponse) == 16);
static_assert(sizeof(GBFR20_StandaloneStateResponse) == 288);
static_assert(sizeof(GBFR20_StandaloneBootstrapConfig) == 2064);
