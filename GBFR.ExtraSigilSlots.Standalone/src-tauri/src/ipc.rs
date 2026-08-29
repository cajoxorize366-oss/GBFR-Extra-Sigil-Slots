use crate::protocol::{
    HelloResponse, NativeInventoryItem, NativePresetSlotResult, PresetCharacterSelection,
    ProtocolClient, StateResponse,
};
use std::fs::{File, OpenOptions};
use std::os::windows::ffi::OsStrExt;
use std::path::Path;
use std::thread::sleep;
use std::time::{Duration, Instant};
use windows_sys::Win32::System::Pipes::WaitNamedPipeW;

const CONNECT_ATTEMPT_WAIT_MS: u32 = 250;
const CONNECT_RETRY_INTERVAL: Duration = Duration::from_millis(75);

pub struct PipeClient {
    client: ProtocolClient<File>,
}

impl PipeClient {
    fn connect_with_timeout(pid: u32, timeout_ms: u32) -> Result<Self, String> {
        let path = pipe_name(pid);
        wait_for_pipe(&path, timeout_ms)?;
        let file = OpenOptions::new()
            .read(true)
            .write(true)
            .open(&path)
            .map_err(|error| format!("Opening Agent pipe for PID {pid} failed: {error}"))?;
        Ok(Self {
            client: ProtocolClient::new(file),
        })
    }

    pub fn connect_ready(pid: u32, timeout: Duration) -> Result<(Self, HelloResponse), String> {
        let started = Instant::now();
        loop {
            let last_error = match Self::connect_with_timeout(pid, CONNECT_ATTEMPT_WAIT_MS) {
                Ok(mut pipe) => match pipe.hello() {
                    Ok(hello) => return Ok((pipe, hello)),
                    Err(error) => error,
                },
                Err(error) => error,
            };

            if started.elapsed() >= timeout {
                return Err(format!(
                    "Agent for PID {pid} did not become ready within {} seconds. Last attempt: {last_error}",
                    timeout.as_secs()
                ));
            }
            sleep(CONNECT_RETRY_INTERVAL);
        }
    }

    pub fn hello(&mut self) -> Result<HelloResponse, String> {
        self.client.hello()
    }

    pub fn tick(&mut self) -> Result<(), String> {
        self.client.tick()
    }

    pub fn get_state(&mut self) -> Result<StateResponse, String> {
        self.client.get_state()
    }

    pub fn refresh_inventory(&mut self) -> Result<Vec<NativeInventoryItem>, String> {
        self.client.refresh_inventory()
    }

    pub fn get_selection(&mut self, character_hash: u32) -> Result<[u32; 24], String> {
        self.client.get_selection(character_hash)
    }

    pub fn set_selection(
        &mut self,
        character_hash: u32,
        virtual_slot: i32,
        inventory_slot_id: u32,
    ) -> Result<(), String> {
        self.client
            .set_selection(character_hash, virtual_slot, inventory_slot_id)
    }

    pub fn apply_preset(
        &mut self,
        selections: &[PresetCharacterSelection],
    ) -> Result<Vec<NativePresetSlotResult>, String> {
        self.client.apply_preset(selections)
    }

    #[allow(dead_code)]
    pub fn request_apply(&mut self, character_hash: u32) -> Result<u32, String> {
        self.client.request_apply(character_hash)
    }

    pub fn set_language(&mut self, language: i32) -> Result<(), String> {
        self.client.set_language(language)
    }

    pub fn request_virtual_slot_count(&mut self, slot_count: i32) -> Result<i32, String> {
        self.client.request_virtual_slot_count(slot_count)
    }

    pub fn get_pending_virtual_slot_count(&mut self) -> Result<i32, String> {
        self.client.get_pending_virtual_slot_count()
    }
}

pub fn pipe_name(pid: u32) -> String {
    format!(r"\\.\pipe\GBFR.ExtraSigilSlots.Standalone.{pid}")
}

fn wait_for_pipe(path: &str, timeout_ms: u32) -> Result<(), String> {
    let wide = Path::new(path)
        .as_os_str()
        .encode_wide()
        .chain(Some(0))
        .collect::<Vec<_>>();
    if unsafe { WaitNamedPipeW(wide.as_ptr(), timeout_ms) } == 0 {
        return Err(format!(
            "Agent pipe {path} was unavailable: {}",
            std::io::Error::last_os_error()
        ));
    }
    Ok(())
}
