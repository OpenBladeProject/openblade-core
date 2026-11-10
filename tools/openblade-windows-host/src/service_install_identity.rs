//! Fixed Service path identity shared by the native entry and protected reader.

#![allow(unsafe_code)]

use std::{ffi::OsString, io, os::windows::ffi::OsStringExt, path::PathBuf, ptr::null_mut, slice};

use windows_sys::Win32::{
    System::Com::CoTaskMemFree,
    UI::Shell::{FOLDERID_ProgramFiles, KF_FLAG_DONT_VERIFY, SHGetKnownFolderPath},
};

pub const INSTALL_DIRECTORY: &str = "OpenBlade";
pub const IMAGE_NAME: &str = "OpenBlade.Service.exe";

/// Resolves the fixed known folder without consulting environment overrides.
pub fn program_files() -> io::Result<PathBuf> {
    known_folder(&FOLDERID_ProgramFiles)
}

pub(crate) fn known_folder(folder_id: &windows_sys::core::GUID) -> io::Result<PathBuf> {
    let mut path = null_mut();
    // SAFETY: Windows supplies a task-allocated NUL-terminated path on success.
    let result = unsafe {
        SHGetKnownFolderPath(folder_id, KF_FLAG_DONT_VERIFY as u32, null_mut(), &mut path)
    };
    let folder = if result >= 0 && !path.is_null() {
        let mut len = 0;
        // SAFETY: The successful query returned a live NUL-terminated string.
        while unsafe { *path.add(len) } != 0 {
            len += 1;
        }
        // SAFETY: len excludes the terminator and the allocation remains live.
        Ok(PathBuf::from(OsString::from_wide(unsafe {
            slice::from_raw_parts(path, len)
        })))
    } else {
        Err(io::Error::new(
            io::ErrorKind::PermissionDenied,
            "Windows known folder is unavailable",
        ))
    };
    // SAFETY: The returned allocation, if present, belongs to this caller.
    unsafe { CoTaskMemFree(path.cast()) };
    folder
}
