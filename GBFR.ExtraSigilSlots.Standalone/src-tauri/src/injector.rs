use crate::process::{find_module, find_module_base, verify_game_process, AGENT_MODULE_NAME};
use std::ffi::{c_void, CString, OsStr};
use std::mem::transmute;
use std::os::windows::ffi::OsStrExt;
use std::path::{Path, PathBuf};
use std::ptr::{null, null_mut};
use std::thread::sleep;
use std::time::{Duration, Instant};
use windows_sys::Win32::Foundation::{CloseHandle, FreeLibrary, HANDLE, WAIT_OBJECT_0};
use windows_sys::Win32::System::Diagnostics::Debug::WriteProcessMemory;
use windows_sys::Win32::System::LibraryLoader::{
    GetModuleHandleW, GetProcAddress, LoadLibraryExW, DONT_RESOLVE_DLL_REFERENCES,
};
use windows_sys::Win32::System::Memory::{
    VirtualAllocEx, VirtualFreeEx, MEM_COMMIT, MEM_RELEASE, MEM_RESERVE, PAGE_READWRITE,
};
use windows_sys::Win32::System::Threading::{
    CreateRemoteThread, GetExitCodeThread, IsWow64Process2, OpenProcess, WaitForSingleObject,
    PROCESS_CREATE_THREAD, PROCESS_QUERY_INFORMATION, PROCESS_VM_OPERATION, PROCESS_VM_READ,
    PROCESS_VM_WRITE,
};

const BOOTSTRAP_MAGIC: u32 = 0x4252_4647;
const PROTOCOL_VERSION: u16 = 1;
const BOOTSTRAP_SIZE: usize = 2064;
const DATA_DIRECTORY_CAPACITY: usize = 1024;
const IMAGE_FILE_MACHINE_UNKNOWN: u16 = 0;
const IMAGE_FILE_MACHINE_AMD64: u16 = 0x8664;
const REMOTE_WAIT_MS: u32 = 30_000;

pub fn ensure_agent(pid: u32, dll_path: &Path, data_directory: &Path) -> Result<bool, String> {
    verify_game_process(pid)?;
    let dll_path = absolute_existing_file(dll_path)?;
    let data_directory = absolute_directory(data_directory)?;
    let process = open_target_process(pid)?;
    validate_x64(process.0, pid)?;

    if let Some(existing) = find_module(pid, AGENT_MODULE_NAME)? {
        invoke_bootstrap(
            process.0,
            pid,
            existing.base,
            &existing.path,
            &data_directory,
        )?;
        verify_game_process(pid)?;
        return Ok(false);
    }

    inject_library(process.0, pid, &dll_path)?;
    let module = wait_for_module(pid, AGENT_MODULE_NAME, Duration::from_secs(5))?;
    invoke_bootstrap(process.0, pid, module.base, &dll_path, &data_directory)?;
    verify_game_process(pid)?;
    Ok(true)
}

fn inject_library(process: HANDLE, pid: u32, dll_path: &Path) -> Result<(), String> {
    let dll_wide = wide_null(dll_path.as_os_str());
    let bytes = as_bytes(&dll_wide);
    let mut remote_path = RemoteAllocation::new(process, bytes.len())?;
    remote_path.write(bytes)?;

    let remote_kernel32 = find_module_base(pid, "kernel32.dll")?
        .ok_or_else(|| format!("PID {pid} has no visible kernel32.dll module."))?;
    let kernel32_name = wide_null(OsStr::new("kernel32.dll"));
    let local_kernel32 = unsafe { GetModuleHandleW(kernel32_name.as_ptr()) };
    if local_kernel32.is_null() {
        return Err(last_error("GetModuleHandleW(kernel32.dll)"));
    }
    let load_library_name = CString::new("LoadLibraryW").expect("static string has no NUL");
    let load_library = unsafe { GetProcAddress(local_kernel32, load_library_name.as_ptr().cast()) }
        .ok_or_else(|| last_error("GetProcAddress(LoadLibraryW)"))? as usize;
    let remote_load_library = remote_kernel32
        .checked_add(load_library - local_kernel32 as usize)
        .ok_or_else(|| "Remote LoadLibraryW address overflowed.".to_string())?;

    let thread = create_remote_thread(process, remote_load_library, remote_path.address)?;
    match wait_remote_thread(thread.0) {
        Ok(_) => Ok(()),
        Err(error) => {
            remote_path.leak();
            Err(error)
        }
    }
}

fn invoke_bootstrap(
    process: HANDLE,
    pid: u32,
    remote_module_base: usize,
    module_path: &Path,
    data_directory: &Path,
) -> Result<(), String> {
    let bootstrap_rva = export_rva(module_path, "GBFR20_StandaloneBootstrap")?;
    let remote_bootstrap = remote_module_base
        .checked_add(bootstrap_rva)
        .ok_or_else(|| "Remote bootstrap address overflowed.".to_string())?;
    let configuration = bootstrap_configuration(data_directory)?;
    let mut remote_configuration = RemoteAllocation::new(process, configuration.len())?;
    remote_configuration.write(&configuration)?;
    let thread = create_remote_thread(process, remote_bootstrap, remote_configuration.address)?;
    let exit_code = match wait_remote_thread(thread.0) {
        Ok(exit_code) => exit_code,
        Err(error) => {
            remote_configuration.leak();
            return Err(error);
        }
    };
    if exit_code != 1 {
        return Err(format!(
            "Native Agent bootstrap in PID {pid} returned {exit_code}; the game may already be owned by Reloaded-II or the data directory was rejected."
        ));
    }
    Ok(())
}

fn bootstrap_configuration(data_directory: &Path) -> Result<Vec<u8>, String> {
    let directory = data_directory.as_os_str().encode_wide().collect::<Vec<_>>();
    if directory.is_empty() || directory.len() >= DATA_DIRECTORY_CAPACITY {
        return Err(format!(
            "Standalone data directory must contain 1 through {} UTF-16 code units.",
            DATA_DIRECTORY_CAPACITY - 1
        ));
    }
    let mut bytes = vec![0_u8; BOOTSTRAP_SIZE];
    bytes[0..4].copy_from_slice(&BOOTSTRAP_MAGIC.to_le_bytes());
    bytes[4..6].copy_from_slice(&PROTOCOL_VERSION.to_le_bytes());
    bytes[8..12].copy_from_slice(&(BOOTSTRAP_SIZE as u32).to_le_bytes());
    bytes[12..16].copy_from_slice(&std::process::id().to_le_bytes());
    for (index, unit) in directory.into_iter().enumerate() {
        let offset = 16 + index * 2;
        bytes[offset..offset + 2].copy_from_slice(&unit.to_le_bytes());
    }
    Ok(bytes)
}

fn export_rva(module_path: &Path, export_name: &str) -> Result<usize, String> {
    let module_wide = wide_null(module_path.as_os_str());
    let module = unsafe {
        LoadLibraryExW(
            module_wide.as_ptr(),
            null_mut(),
            DONT_RESOLVE_DLL_REFERENCES,
        )
    };
    if module.is_null() {
        return Err(last_error(format!(
            "LoadLibraryExW({})",
            module_path.display()
        )));
    }
    let module = LocalModule(module);
    let export_name = CString::new(export_name).map_err(|error| error.to_string())?;
    let export = unsafe { GetProcAddress(module.0, export_name.as_ptr().cast()) }
        .ok_or_else(|| last_error("GetProcAddress(GBFR20_StandaloneBootstrap)"))?
        as usize;
    export
        .checked_sub(module.0 as usize)
        .ok_or_else(|| "Bootstrap export address precedes its module base.".to_string())
}

fn open_target_process(pid: u32) -> Result<OwnedHandle, String> {
    let access = PROCESS_CREATE_THREAD
        | PROCESS_QUERY_INFORMATION
        | PROCESS_VM_OPERATION
        | PROCESS_VM_READ
        | PROCESS_VM_WRITE;
    let handle = unsafe { OpenProcess(access, 0, pid) };
    if handle.is_null() {
        return Err(last_error(format!(
            "OpenProcess({pid}); run the controller at the same integrity level as the game"
        )));
    }
    Ok(OwnedHandle(handle))
}

fn validate_x64(process: HANDLE, pid: u32) -> Result<(), String> {
    let mut process_machine = 0_u16;
    let mut native_machine = 0_u16;
    if unsafe { IsWow64Process2(process, &mut process_machine, &mut native_machine) } == 0 {
        return Err(last_error(format!("IsWow64Process2({pid})")));
    }
    if process_machine != IMAGE_FILE_MACHINE_UNKNOWN || native_machine != IMAGE_FILE_MACHINE_AMD64 {
        return Err(format!(
            "PID {pid} is not a native x64 process (processMachine=0x{process_machine:04X}, nativeMachine=0x{native_machine:04X})."
        ));
    }
    Ok(())
}

fn create_remote_thread(
    process: HANDLE,
    start_address: usize,
    parameter: *mut c_void,
) -> Result<OwnedHandle, String> {
    let start =
        unsafe { transmute::<usize, unsafe extern "system" fn(*mut c_void) -> u32>(start_address) };
    let thread =
        unsafe { CreateRemoteThread(process, null(), 0, Some(start), parameter, 0, null_mut()) };
    if thread.is_null() {
        return Err(last_error("CreateRemoteThread"));
    }
    Ok(OwnedHandle(thread))
}

fn wait_remote_thread(thread: HANDLE) -> Result<u32, String> {
    let wait = unsafe { WaitForSingleObject(thread, REMOTE_WAIT_MS) };
    if wait != WAIT_OBJECT_0 {
        return Err(format!(
            "Remote thread did not finish within {} seconds (wait=0x{wait:X}).",
            REMOTE_WAIT_MS / 1000
        ));
    }
    let mut exit_code = 0_u32;
    if unsafe { GetExitCodeThread(thread, &mut exit_code) } == 0 {
        return Err(last_error("GetExitCodeThread"));
    }
    Ok(exit_code)
}

fn wait_for_module(
    pid: u32,
    module_name: &str,
    timeout: Duration,
) -> Result<crate::process::ModuleInfo, String> {
    let started = Instant::now();
    loop {
        if let Some(module) = find_module(pid, module_name)? {
            return Ok(module);
        }
        if started.elapsed() >= timeout {
            return Err(format!(
                "{module_name} did not appear in PID {pid} after injection."
            ));
        }
        sleep(Duration::from_millis(50));
    }
}

fn absolute_existing_file(path: &Path) -> Result<PathBuf, String> {
    let path = path
        .canonicalize()
        .map_err(|error| format!("Agent DLL {} is unavailable: {error}", path.display()))?;
    if !path.is_file() {
        return Err(format!("Agent DLL path is not a file: {}", path.display()));
    }
    Ok(path)
}

fn absolute_directory(path: &Path) -> Result<PathBuf, String> {
    std::fs::create_dir_all(path).map_err(|error| {
        format!(
            "Creating Standalone data directory {} failed: {error}",
            path.display()
        )
    })?;
    path.canonicalize().map_err(|error| {
        format!(
            "Resolving Standalone data directory {} failed: {error}",
            path.display()
        )
    })
}

fn wide_null(value: &OsStr) -> Vec<u16> {
    value.encode_wide().chain(Some(0)).collect()
}

fn as_bytes(units: &[u16]) -> &[u8] {
    unsafe { std::slice::from_raw_parts(units.as_ptr().cast(), std::mem::size_of_val(units)) }
}

fn last_error(context: impl AsRef<str>) -> String {
    format!(
        "{} failed: {}",
        context.as_ref(),
        std::io::Error::last_os_error()
    )
}

struct OwnedHandle(HANDLE);

impl Drop for OwnedHandle {
    fn drop(&mut self) {
        unsafe {
            CloseHandle(self.0);
        }
    }
}

struct LocalModule(*mut c_void);

impl Drop for LocalModule {
    fn drop(&mut self) {
        unsafe {
            FreeLibrary(self.0);
        }
    }
}

struct RemoteAllocation {
    process: HANDLE,
    address: *mut c_void,
    leaked: bool,
}

impl RemoteAllocation {
    fn new(process: HANDLE, size: usize) -> Result<Self, String> {
        let address = unsafe {
            VirtualAllocEx(
                process,
                null(),
                size,
                MEM_COMMIT | MEM_RESERVE,
                PAGE_READWRITE,
            )
        };
        if address.is_null() {
            return Err(last_error("VirtualAllocEx"));
        }
        Ok(Self {
            process,
            address,
            leaked: false,
        })
    }

    fn write(&self, bytes: &[u8]) -> Result<(), String> {
        let mut written = 0_usize;
        if unsafe {
            WriteProcessMemory(
                self.process,
                self.address,
                bytes.as_ptr().cast(),
                bytes.len(),
                &mut written,
            )
        } == 0
            || written != bytes.len()
        {
            return Err(last_error(format!(
                "WriteProcessMemory({}/{})",
                written,
                bytes.len()
            )));
        }
        Ok(())
    }

    fn leak(&mut self) {
        self.leaked = true;
    }
}

impl Drop for RemoteAllocation {
    fn drop(&mut self) {
        if !self.leaked {
            unsafe {
                VirtualFreeEx(self.process, self.address, 0, MEM_RELEASE);
            }
        }
    }
}
