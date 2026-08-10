use serde::Serialize;
use std::io::{Read, Write};

pub const PROTOCOL_VERSION: u16 = 1;
pub const NATIVE_ABI_VERSION: u32 = 13;
pub const VIRTUAL_SLOT_CAPACITY: usize = 24;
pub const PRESET_CHARACTER_CAPACITY: usize = 32;
pub const MAX_PAYLOAD_SIZE: usize = 8 * 1024 * 1024;

const FRAME_MAGIC: u32 = 0x5352_4647;
const FRAME_HEADER_SIZE: usize = 20;

const COMMAND_HELLO: u16 = 1;
const COMMAND_GET_STATE: u16 = 2;
const COMMAND_REFRESH_INVENTORY: u16 = 3;
const COMMAND_GET_SELECTION: u16 = 4;
const COMMAND_SET_SELECTION: u16 = 5;
const COMMAND_APPLY_PRESET: u16 = 6;
#[allow(dead_code)]
const COMMAND_REQUEST_APPLY: u16 = 7;
const COMMAND_SET_LANGUAGE: u16 = 8;
const COMMAND_REQUEST_VIRTUAL_SLOT_COUNT: u16 = 9;
const COMMAND_GET_PENDING_VIRTUAL_SLOT_COUNT: u16 = 10;

#[derive(Debug, Clone, Serialize)]
pub struct HelloResponse {
    pub native_abi_version: u32,
    pub process_id: u32,
    pub initialized: bool,
    pub hooks_ready: bool,
}

#[derive(Debug, Clone)]
pub struct NativeState {
    pub initialized: bool,
    pub hooks_ready: bool,
    pub shutting_down: bool,
    pub runtime_message_is_error: bool,
    pub ui_selected_character_hash: u32,
    pub effective_character_hash: u32,
    pub language: i32,
    pub inventory_revision: u64,
    pub inventory_dirty: bool,
    pub edit_allowed: bool,
    pub virtual_slot_count: u32,
    pub virtual_slot_capacity: u32,
}

#[derive(Debug, Clone)]
pub struct StateResponse {
    pub state: NativeState,
    pub pending_virtual_slot_count: i32,
    pub runtime_message: String,
}

#[derive(Debug, Clone, Serialize)]
pub struct GemData {
    pub trait1: u32,
    pub trait1_level: i32,
    pub trait2: u32,
    pub trait2_level: i32,
    pub gem_id: u32,
    pub worn_by: u32,
    pub sigil_level: i32,
    pub slot_id: u32,
    pub flags: u32,
}

#[derive(Debug, Clone)]
pub struct NativeInventoryItem {
    pub gem: GemData,
    pub equipped: bool,
    pub required_character_hash: u32,
    pub virtual_owner_character_hash: u32,
    pub virtual_owner_slot: i32,
    pub label: String,
}

#[derive(Debug, Clone)]
pub struct PresetCharacterSelection {
    pub character_hash: u32,
    pub slots: [u32; VIRTUAL_SLOT_CAPACITY],
}

#[derive(Debug, Clone, Serialize)]
pub struct NativePresetSlotResult {
    pub character_hash: u32,
    pub virtual_slot: i32,
    pub requested_slot_id: u32,
    pub owner_character_hash: u32,
    pub status: i32,
}

#[derive(Debug)]
struct FrameHeader {
    magic: u32,
    protocol_version: u16,
    command: u16,
    request_id: u32,
    status: i32,
    payload_size: u32,
}

pub struct ProtocolClient<T> {
    stream: Option<T>,
    next_request_id: u32,
}

impl<T: Read + Write> ProtocolClient<T> {
    pub fn new(stream: T) -> Self {
        Self {
            stream: Some(stream),
            next_request_id: 1,
        }
    }

    pub fn hello(&mut self) -> Result<HelloResponse, String> {
        let payload = self.request(COMMAND_HELLO, &[])?;
        let mut reader = Reader::new(&payload);
        let result = HelloResponse {
            native_abi_version: reader.u32()?,
            process_id: reader.u32()?,
            initialized: reader.i32()? != 0,
            hooks_ready: reader.i32()? != 0,
        };
        reader.finish()?;
        Ok(result)
    }

    pub fn get_state(&mut self) -> Result<StateResponse, String> {
        let payload = self.request(COMMAND_GET_STATE, &[])?;
        if payload.len() < 284 {
            return self.fail(format!(
                "GetState response is too short: {} bytes.",
                payload.len()
            ));
        }
        let mut reader = Reader::new(&payload);
        let abi_version = reader.u32()?;
        let struct_size = reader.u32()?;
        if abi_version != NATIVE_ABI_VERSION || struct_size != 276 {
            return self.fail(format!(
                "Unsupported native state ABI {abi_version} with size {struct_size}."
            ));
        }
        let initialized = reader.i32()? != 0;
        let hooks_ready = reader.i32()? != 0;
        let shutting_down = reader.i32()? != 0;
        let runtime_message_is_error = reader.i32()? != 0;
        let ui_selected_character_hash = reader.u32()?;
        let effective_character_hash = reader.u32()?;
        reader.skip(88)?;
        let language = reader.i32()?;
        reader.skip(16)?;
        let inventory_revision = reader.u64()?;
        let inventory_dirty = reader.i32()? != 0;
        let edit_allowed = reader.i32()? != 0;
        reader.skip(112)?;
        let virtual_slot_count = reader.u32()?;
        let virtual_slot_capacity = reader.u32()?;
        let pending_virtual_slot_count = reader.i32()?;
        let message_size = reader.u32()? as usize;
        if message_size > MAX_PAYLOAD_SIZE || reader.remaining() != message_size {
            return self.fail("GetState runtime-message length is invalid.".to_string());
        }
        let runtime_message = reader.utf8(message_size)?;
        reader.finish()?;
        Ok(StateResponse {
            state: NativeState {
                initialized,
                hooks_ready,
                shutting_down,
                runtime_message_is_error,
                ui_selected_character_hash,
                effective_character_hash,
                language,
                inventory_revision,
                inventory_dirty,
                edit_allowed,
                virtual_slot_count,
                virtual_slot_capacity,
            },
            pending_virtual_slot_count,
            runtime_message,
        })
    }

    pub fn refresh_inventory(&mut self) -> Result<Vec<NativeInventoryItem>, String> {
        let payload = self.request(COMMAND_REFRESH_INVENTORY, &[])?;
        let mut reader = Reader::new(&payload);
        let count = reader.u32()? as usize;
        if count > 100_000 {
            return self.fail(format!("Inventory item count is unreasonable: {count}."));
        }
        let mut items = Vec::with_capacity(count);
        for _ in 0..count {
            let gem = GemData {
                trait1: reader.u32()?,
                trait1_level: reader.i32()?,
                trait2: reader.u32()?,
                trait2_level: reader.i32()?,
                gem_id: reader.u32()?,
                worn_by: reader.u32()?,
                sigil_level: reader.i32()?,
                slot_id: reader.u32()?,
                flags: reader.u32()?,
            };
            let equipped = reader.u32()? != 0;
            let required_character_hash = reader.u32()?;
            let virtual_owner_character_hash = reader.u32()?;
            let virtual_owner_slot = reader.i32()?;
            let label_size = reader.u32()? as usize;
            if label_size > reader.remaining() {
                return self.fail("Inventory label length exceeds its frame.".to_string());
            }
            let label = reader.utf8(label_size)?;
            items.push(NativeInventoryItem {
                gem,
                equipped,
                required_character_hash,
                virtual_owner_character_hash,
                virtual_owner_slot,
                label,
            });
        }
        reader.finish()?;
        Ok(items)
    }

    pub fn get_selection(&mut self, character_hash: u32) -> Result<[u32; 24], String> {
        let payload = self.request(COMMAND_GET_SELECTION, &character_hash.to_le_bytes())?;
        let mut reader = Reader::new(&payload);
        let mut slots = [0; VIRTUAL_SLOT_CAPACITY];
        for slot in &mut slots {
            *slot = reader.u32()?;
        }
        reader.finish()?;
        Ok(slots)
    }

    pub fn set_selection(
        &mut self,
        character_hash: u32,
        virtual_slot: i32,
        inventory_slot_id: u32,
    ) -> Result<(), String> {
        let mut payload = Vec::with_capacity(12);
        payload.extend_from_slice(&character_hash.to_le_bytes());
        payload.extend_from_slice(&virtual_slot.to_le_bytes());
        payload.extend_from_slice(&inventory_slot_id.to_le_bytes());
        let response = self.request(COMMAND_SET_SELECTION, &payload)?;
        if !response.is_empty() {
            return self.fail("SetSelection returned an unexpected payload.".to_string());
        }
        Ok(())
    }

    pub fn apply_preset(
        &mut self,
        selections: &[PresetCharacterSelection],
    ) -> Result<Vec<NativePresetSlotResult>, String> {
        if selections.is_empty() || selections.len() > PRESET_CHARACTER_CAPACITY {
            return Err("A preset request must contain 1 through 32 characters.".to_string());
        }
        let mut payload = Vec::with_capacity(4 + selections.len() * 100);
        payload.extend_from_slice(&(selections.len() as u32).to_le_bytes());
        for selection in selections {
            payload.extend_from_slice(&selection.character_hash.to_le_bytes());
            for slot in selection.slots {
                payload.extend_from_slice(&slot.to_le_bytes());
            }
        }
        let response = self.request(COMMAND_APPLY_PRESET, &payload)?;
        let mut reader = Reader::new(&response);
        let count = reader.u32()? as usize;
        if count > PRESET_CHARACTER_CAPACITY * VIRTUAL_SLOT_CAPACITY {
            return self.fail("ApplyPreset result count exceeds the protocol limit.".to_string());
        }
        let mut results = Vec::with_capacity(count);
        for _ in 0..count {
            results.push(NativePresetSlotResult {
                character_hash: reader.u32()?,
                virtual_slot: reader.i32()?,
                requested_slot_id: reader.u32()?,
                owner_character_hash: reader.u32()?,
                status: reader.i32()?,
            });
        }
        reader.finish()?;
        Ok(results)
    }

    #[allow(dead_code)]
    pub fn request_apply(&mut self, character_hash: u32) -> Result<u32, String> {
        let payload = self.request(COMMAND_REQUEST_APPLY, &character_hash.to_le_bytes())?;
        let mut reader = Reader::new(&payload);
        let generation = reader.u32()?;
        reader.finish()?;
        Ok(generation)
    }

    pub fn set_language(&mut self, language: i32) -> Result<(), String> {
        let payload = self.request(COMMAND_SET_LANGUAGE, &language.to_le_bytes())?;
        if !payload.is_empty() {
            return self.fail("SetLanguage returned an unexpected payload.".to_string());
        }
        Ok(())
    }

    pub fn request_virtual_slot_count(&mut self, slot_count: i32) -> Result<i32, String> {
        let payload = self.request(
            COMMAND_REQUEST_VIRTUAL_SLOT_COUNT,
            &slot_count.to_le_bytes(),
        )?;
        let mut reader = Reader::new(&payload);
        let result = reader.i32()?;
        reader.finish()?;
        Ok(result)
    }

    pub fn get_pending_virtual_slot_count(&mut self) -> Result<i32, String> {
        let payload = self.request(COMMAND_GET_PENDING_VIRTUAL_SLOT_COUNT, &[])?;
        let mut reader = Reader::new(&payload);
        let result = reader.i32()?;
        reader.finish()?;
        Ok(result)
    }

    fn request(&mut self, command: u16, payload: &[u8]) -> Result<Vec<u8>, String> {
        if payload.len() > MAX_PAYLOAD_SIZE {
            return self.fail("IPC request exceeds the 8 MiB limit.".to_string());
        }
        let request_id = self.next_request_id;
        self.next_request_id = self.next_request_id.wrapping_add(1).max(1);
        let header = FrameHeader {
            magic: FRAME_MAGIC,
            protocol_version: PROTOCOL_VERSION,
            command,
            request_id,
            status: 0,
            payload_size: payload.len() as u32,
        };
        let encoded = encode_header(&header);
        let stream = self
            .stream
            .as_mut()
            .ok_or_else(|| "The IPC connection is no longer usable.".to_string())?;
        if let Err(error) = stream
            .write_all(&encoded)
            .and_then(|_| stream.write_all(payload))
            .and_then(|_| stream.flush())
        {
            return self.fail(format!("IPC write failed: {error}"));
        }

        let mut response_header = [0_u8; FRAME_HEADER_SIZE];
        if let Err(error) = stream.read_exact(&mut response_header) {
            return self.fail(format!("IPC header read failed: {error}"));
        }
        let response = decode_header(&response_header)?;
        if response.magic != FRAME_MAGIC
            || response.protocol_version != PROTOCOL_VERSION
            || response.command != command
            || response.request_id != request_id
            || response.payload_size as usize > MAX_PAYLOAD_SIZE
        {
            return self.fail("IPC response header failed validation.".to_string());
        }
        let mut response_payload = vec![0_u8; response.payload_size as usize];
        if let Err(error) = stream.read_exact(&mut response_payload) {
            return self.fail(format!("IPC payload read failed: {error}"));
        }
        if response.status != 0 {
            return self.fail(format!(
                "Native Agent rejected command {command} with status {}.",
                response.status
            ));
        }
        Ok(response_payload)
    }

    fn fail<R>(&mut self, message: String) -> Result<R, String> {
        self.stream = None;
        Err(message)
    }
}

fn encode_header(header: &FrameHeader) -> [u8; FRAME_HEADER_SIZE] {
    let mut output = [0_u8; FRAME_HEADER_SIZE];
    output[0..4].copy_from_slice(&header.magic.to_le_bytes());
    output[4..6].copy_from_slice(&header.protocol_version.to_le_bytes());
    output[6..8].copy_from_slice(&header.command.to_le_bytes());
    output[8..12].copy_from_slice(&header.request_id.to_le_bytes());
    output[12..16].copy_from_slice(&header.status.to_le_bytes());
    output[16..20].copy_from_slice(&header.payload_size.to_le_bytes());
    output
}

fn decode_header(bytes: &[u8; FRAME_HEADER_SIZE]) -> Result<FrameHeader, String> {
    let mut reader = Reader::new(bytes);
    let header = FrameHeader {
        magic: reader.u32()?,
        protocol_version: reader.u16()?,
        command: reader.u16()?,
        request_id: reader.u32()?,
        status: reader.i32()?,
        payload_size: reader.u32()?,
    };
    reader.finish()?;
    Ok(header)
}

struct Reader<'a> {
    bytes: &'a [u8],
    offset: usize,
}

impl<'a> Reader<'a> {
    fn new(bytes: &'a [u8]) -> Self {
        Self { bytes, offset: 0 }
    }

    fn remaining(&self) -> usize {
        self.bytes.len().saturating_sub(self.offset)
    }

    fn take<const N: usize>(&mut self) -> Result<[u8; N], String> {
        if self.remaining() < N {
            return Err("IPC payload ended before the expected structure.".to_string());
        }
        let mut value = [0_u8; N];
        value.copy_from_slice(&self.bytes[self.offset..self.offset + N]);
        self.offset += N;
        Ok(value)
    }

    fn skip(&mut self, count: usize) -> Result<(), String> {
        if self.remaining() < count {
            return Err("IPC payload ended before the expected structure.".to_string());
        }
        self.offset += count;
        Ok(())
    }

    fn u16(&mut self) -> Result<u16, String> {
        Ok(u16::from_le_bytes(self.take()?))
    }

    fn u32(&mut self) -> Result<u32, String> {
        Ok(u32::from_le_bytes(self.take()?))
    }

    fn i32(&mut self) -> Result<i32, String> {
        Ok(i32::from_le_bytes(self.take()?))
    }

    fn u64(&mut self) -> Result<u64, String> {
        Ok(u64::from_le_bytes(self.take()?))
    }

    fn utf8(&mut self, count: usize) -> Result<String, String> {
        if self.remaining() < count {
            return Err("UTF-8 field exceeds its IPC payload.".to_string());
        }
        let value = std::str::from_utf8(&self.bytes[self.offset..self.offset + count])
            .map_err(|error| format!("IPC text is not valid UTF-8: {error}"))?
            .to_string();
        self.offset += count;
        Ok(value)
    }

    fn finish(&self) -> Result<(), String> {
        if self.offset == self.bytes.len() {
            Ok(())
        } else {
            Err(format!(
                "IPC payload contains {} unexpected trailing bytes.",
                self.bytes.len() - self.offset
            ))
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::{Cursor, Error, ErrorKind};

    #[test]
    fn frame_header_round_trips_exactly() {
        let header = FrameHeader {
            magic: FRAME_MAGIC,
            protocol_version: PROTOCOL_VERSION,
            command: COMMAND_GET_STATE,
            request_id: 41,
            status: -5,
            payload_size: 284,
        };
        let decoded = decode_header(&encode_header(&header)).unwrap();
        assert_eq!(decoded.magic, header.magic);
        assert_eq!(decoded.request_id, 41);
        assert_eq!(decoded.status, -5);
        assert_eq!(decoded.payload_size, 284);
    }

    #[test]
    fn state_response_matches_the_packed_native_layout() {
        let message = b"hooks ready";
        let mut payload = vec![0_u8; 284];
        payload[0..4].copy_from_slice(&NATIVE_ABI_VERSION.to_le_bytes());
        payload[4..8].copy_from_slice(&276_u32.to_le_bytes());
        payload[8..12].copy_from_slice(&1_i32.to_le_bytes());
        payload[12..16].copy_from_slice(&1_i32.to_le_bytes());
        payload[20..24].copy_from_slice(&1_i32.to_le_bytes());
        payload[24..28].copy_from_slice(&0x1020_3040_u32.to_le_bytes());
        payload[28..32].copy_from_slice(&0x5060_7080_u32.to_le_bytes());
        payload[120..124].copy_from_slice(&1_i32.to_le_bytes());
        payload[140..148].copy_from_slice(&0x0102_0304_0506_0708_u64.to_le_bytes());
        payload[148..152].copy_from_slice(&1_i32.to_le_bytes());
        payload[152..156].copy_from_slice(&1_i32.to_le_bytes());
        payload[268..272].copy_from_slice(&8_u32.to_le_bytes());
        payload[272..276].copy_from_slice(&24_u32.to_le_bytes());
        payload[276..280].copy_from_slice(&12_i32.to_le_bytes());
        payload[280..284].copy_from_slice(&(message.len() as u32).to_le_bytes());
        payload.extend_from_slice(message);

        let mut response = encode_header(&FrameHeader {
            magic: FRAME_MAGIC,
            protocol_version: PROTOCOL_VERSION,
            command: COMMAND_GET_STATE,
            request_id: 1,
            status: 0,
            payload_size: payload.len() as u32,
        })
        .to_vec();
        response.extend_from_slice(&payload);

        let mut client = ProtocolClient::new(ScriptedStream::new(response));
        let snapshot = client.get_state().unwrap();
        assert!(snapshot.state.initialized);
        assert!(snapshot.state.hooks_ready);
        assert!(!snapshot.state.shutting_down);
        assert!(snapshot.state.runtime_message_is_error);
        assert_eq!(snapshot.state.ui_selected_character_hash, 0x1020_3040);
        assert_eq!(snapshot.state.effective_character_hash, 0x5060_7080);
        assert_eq!(snapshot.state.language, 1);
        assert_eq!(snapshot.state.inventory_revision, 0x0102_0304_0506_0708);
        assert!(snapshot.state.inventory_dirty);
        assert!(snapshot.state.edit_allowed);
        assert_eq!(snapshot.state.virtual_slot_count, 8);
        assert_eq!(snapshot.state.virtual_slot_capacity, 24);
        assert_eq!(snapshot.pending_virtual_slot_count, 12);
        assert_eq!(snapshot.runtime_message, "hooks ready");
    }

    #[test]
    fn selection_requires_exactly_twenty_four_slots() {
        let mut response = Vec::new();
        response.extend_from_slice(&encode_header(&FrameHeader {
            magic: FRAME_MAGIC,
            protocol_version: PROTOCOL_VERSION,
            command: COMMAND_GET_SELECTION,
            request_id: 1,
            status: 0,
            payload_size: 96,
        }));
        for value in 0_u32..24 {
            response.extend_from_slice(&value.to_le_bytes());
        }
        let mut client = ProtocolClient::new(ScriptedStream::new(response));
        let slots = client.get_selection(0x1234).unwrap();
        assert_eq!(slots[0], 0);
        assert_eq!(slots[23], 23);
    }

    #[test]
    fn bad_response_invalidates_the_connection() {
        let response = encode_header(&FrameHeader {
            magic: 0,
            protocol_version: PROTOCOL_VERSION,
            command: COMMAND_HELLO,
            request_id: 1,
            status: 0,
            payload_size: 0,
        })
        .to_vec();
        let mut client = ProtocolClient::new(ScriptedStream::new(response));
        assert!(client.hello().is_err());
        assert!(client.hello().is_err());
    }

    struct ScriptedStream {
        input: Cursor<Vec<u8>>,
        output: Vec<u8>,
    }

    impl ScriptedStream {
        fn new(input: Vec<u8>) -> Self {
            Self {
                input: Cursor::new(input),
                output: Vec::new(),
            }
        }
    }

    impl Read for ScriptedStream {
        fn read(&mut self, buffer: &mut [u8]) -> std::io::Result<usize> {
            self.input.read(buffer)
        }
    }

    impl Write for ScriptedStream {
        fn write(&mut self, buffer: &[u8]) -> std::io::Result<usize> {
            if buffer.is_empty() {
                return Err(Error::new(ErrorKind::WriteZero, "empty write"));
            }
            self.output.extend_from_slice(buffer);
            Ok(buffer.len())
        }

        fn flush(&mut self) -> std::io::Result<()> {
            Ok(())
        }
    }
}
