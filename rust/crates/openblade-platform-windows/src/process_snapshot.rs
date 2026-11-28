//! Bounded read-only Toolhelp traversal shared by GPU and controller inventories.
#![allow(unsafe_code)]

use std::{
    mem::size_of,
    os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle},
};

use windows_sys::Win32::{
    Foundation::{ERROR_NO_MORE_FILES, GetLastError, INVALID_HANDLE_VALUE},
    System::Diagnostics::ToolHelp::{
        CreateToolhelp32Snapshot, PROCESSENTRY32W, Process32FirstW, Process32NextW,
        TH32CS_SNAPPROCESS,
    },
};

const MAX_ENTRIES: usize = 65_536;

/// True only when Windows reports normal exhaustion. Visitors can stop early.
pub(crate) fn visit_processes(mut visit: impl FnMut(&PROCESSENTRY32W) -> bool) -> bool {
    // SAFETY: TH32CS_SNAPPROCESS takes a read-only system process snapshot.
    let raw = unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) };
    if raw == INVALID_HANDLE_VALUE {
        return false;
    }
    // SAFETY: A successful snapshot is a uniquely owned handle.
    let snapshot = unsafe { OwnedHandle::from_raw_handle(raw) };
    let mut entry = PROCESSENTRY32W {
        dwSize: size_of::<PROCESSENTRY32W>() as u32,
        ..Default::default()
    };
    // SAFETY: The snapshot is held and the entry has the required size.
    let mut found = unsafe { Process32FirstW(snapshot.as_raw_handle(), &mut entry) };
    for _ in 0..MAX_ENTRIES {
        if found == 0 {
            // SAFETY: Read the thread error from the preceding failed Toolhelp call.
            return unsafe { GetLastError() } == ERROR_NO_MORE_FILES;
        }
        if !visit(&entry) {
            return false;
        }
        // SAFETY: The snapshot and correctly sized entry remain live.
        found = unsafe { Process32NextW(snapshot.as_raw_handle(), &mut entry) };
    }
    // SAFETY: Check the last advance to distinguish exhaustion at the bound.
    found == 0 && unsafe { GetLastError() } == ERROR_NO_MORE_FILES
}
