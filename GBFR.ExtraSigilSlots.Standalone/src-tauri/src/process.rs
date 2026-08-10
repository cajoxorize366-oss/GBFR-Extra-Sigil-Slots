use serde::Serialize;
use std::ffi::OsString;
use std::mem::{size_of, zeroed};
use std::os::windows::ffi::OsStringExt;
use std::path::PathBuf;
use windows_sys::Win32::Foundation::{CloseHandle, HANDLE, INVALID_HANDLE_VALUE};
use windows_sys::Win32::System::Diagnostics::ToolHelp::{
    CreateToolhelp32Snapshot, Module32FirstW, Module32NextW, Process32FirstW, Process32NextW,
    MODULEENTRY32W, PROCESSENTRY32W, TH32CS_SNAPMODULE, TH32CS_SNAPMODULE32, TH32CS_SNAPPROCESS,
};
use windows_sys::Win32::System::Threading::{
    OpenProcess, QueryFullProcessImageNameW, PROCESS_QUERY_LIMITED_INFORMATION,
};

pub const GAME_EXECUTABLE_NAME: &str = "granblue_fantasy_relink.exe";
pub const AGENT_MODULE_NAME: &str = "GBFR.ExtraSigilSlots.Native.dll";

#[derive(Debug, Clone, Serialize)]
pub struct GameProcess {
    pub pid: u32,
    pub executable_name: String,
    pub executable_path: Option<String>,
    pub agent_loaded: bool,
}

#[derive(Debug, Clone)]
pub struct ModuleInfo {
    pub base: usize,
    pub path: PathBuf,
}

pub fn list_game_processes() -> Result<Vec<GameProcess>, String> {
    let snapshot = Snapshot::new(TH32CS_SNAPPROCESS, 0)?;
    let mut entry: PROCESSENTRY32W = unsafe { zeroed() };
    entry.dwSize = size_of::<PROCESSENTRY32W>() as u32;
    let mut found = Vec::new();
    let mut has_entry = unsafe { Process32FirstW(snapshot.handle, &mut entry) } != 0;
    while has_entry {
        let executable_name = utf16_z(&entry.szExeFile);
        if executable_name.eq_ignore_ascii_case(GAME_EXECUTABLE_NAME) {
            let pid = entry.th32ProcessID;
            found.push(GameProcess {
                pid,
                executable_name,
                executable_path: query_process_path(pid)
                    .ok()
                    .map(|path| path.to_string_lossy().into_owned()),
                agent_loaded: find_module_base(pid, AGENT_MODULE_NAME)
                    .ok()
                    .flatten()
                    .is_some(),
            });
        }
        has_entry = unsafe { Process32NextW(snapshot.handle, &mut entry) } != 0;
    }
    found.sort_by_key(|process| process.pid);
    Ok(found)
}

pub fn verify_game_process(pid: u32) -> Result<GameProcess, String> {
    let process = list_game_processes()?
        .into_iter()
        .find(|process| process.pid == pid)
        .ok_or_else(|| format!("PID {pid} is not a running {GAME_EXECUTABLE_NAME} process."))?;
    if !process
        .executable_name
        .eq_ignore_ascii_case(GAME_EXECUTABLE_NAME)
    {
        return Err(format!(
            "PID {pid} changed identity before connection could be established."
        ));
    }
    Ok(process)
}

pub fn find_module_base(pid: u32, module_name: &str) -> Result<Option<usize>, String> {
    Ok(find_module(pid, module_name)?.map(|module| module.base))
}

pub fn find_module(pid: u32, module_name: &str) -> Result<Option<ModuleInfo>, String> {
    let snapshot = Snapshot::new(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid)?;
    let mut entry: MODULEENTRY32W = unsafe { zeroed() };
    entry.dwSize = size_of::<MODULEENTRY32W>() as u32;
    let mut has_entry = unsafe { Module32FirstW(snapshot.handle, &mut entry) } != 0;
    while has_entry {
        if utf16_z(&entry.szModule).eq_ignore_ascii_case(module_name) {
            return Ok(Some(ModuleInfo {
                base: entry.modBaseAddr as usize,
                path: PathBuf::from(OsString::from_wide(
                    &entry.szExePath[..entry
                        .szExePath
                        .iter()
                        .position(|unit| *unit == 0)
                        .unwrap_or(entry.szExePath.len())],
                )),
            }));
        }
        has_entry = unsafe { Module32NextW(snapshot.handle, &mut entry) } != 0;
    }
    Ok(None)
}

fn query_process_path(pid: u32) -> Result<PathBuf, String> {
    let handle = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid) };
    if handle.is_null() {
        return Err(last_error(format!("OpenProcess({pid}) for path query")));
    }
    let handle = OwnedHandle(handle);
    let mut buffer = vec![0_u16; 32_768];
    let mut size = buffer.len() as u32;
    if unsafe { QueryFullProcessImageNameW(handle.0, 0, buffer.as_mut_ptr(), &mut size) } == 0 {
        return Err(last_error(format!("QueryFullProcessImageNameW({pid})")));
    }
    buffer.truncate(size as usize);
    Ok(PathBuf::from(OsString::from_wide(&buffer)))
}

fn utf16_z(value: &[u16]) -> String {
    let length = value
        .iter()
        .position(|unit| *unit == 0)
        .unwrap_or(value.len());
    OsString::from_wide(&value[..length])
        .to_string_lossy()
        .into_owned()
}

fn last_error(context: String) -> String {
    format!("{context} failed: {}", std::io::Error::last_os_error())
}

struct Snapshot {
    handle: HANDLE,
}

impl Snapshot {
    fn new(flags: u32, pid: u32) -> Result<Self, String> {
        let handle = unsafe { CreateToolhelp32Snapshot(flags, pid) };
        if handle == INVALID_HANDLE_VALUE {
            return Err(last_error(format!(
                "CreateToolhelp32Snapshot(flags=0x{flags:X}, pid={pid})"
            )));
        }
        Ok(Self { handle })
    }
}

impl Drop for Snapshot {
    fn drop(&mut self) {
        unsafe {
            CloseHandle(self.handle);
        }
    }
}

struct OwnedHandle(HANDLE);

impl Drop for OwnedHandle {
    fn drop(&mut self) {
        unsafe {
            CloseHandle(self.0);
        }
    }
}
