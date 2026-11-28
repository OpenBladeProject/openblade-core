//! Current-user environment reads for the selected native display mutation.
#![allow(unsafe_code)]
use std::{ffi::OsString, io, os::windows::ffi::OsStringExt, path::PathBuf, ptr::null_mut};
use windows_sys::Win32::{
    System::{
        Com::CoTaskMemFree,
        RemoteDesktop::{
            WTS_CONNECTSTATE_CLASS, WTSActive, WTSConnectState, WTSFreeMemory,
            WTSGetActiveConsoleSessionId, WTSQuerySessionInformationW,
        },
    },
    UI::Shell::{FOLDERID_LocalAppData, KF_FLAG_DONT_VERIFY, SHGetKnownFolderPath},
};

/// The managed Session Agent resolves `Environment.SpecialFolder.LocalApplicationData`
/// through the same known folder.
pub(super) fn local_app_data() -> io::Result<PathBuf> {
    let mut path = null_mut();
    // SAFETY: the known-folder API returns a task-allocated, NUL-terminated
    // path (or null on failure), released below on every path.
    let result = unsafe {
        SHGetKnownFolderPath(
            &FOLDERID_LocalAppData,
            KF_FLAG_DONT_VERIFY as u32,
            null_mut(),
            &mut path,
        )
    };
    let folder = if result >= 0 && !path.is_null() {
        // SAFETY: a successful call returned a NUL-terminated wide string.
        let text = unsafe {
            let length = (0..).find(|&i| *path.add(i) == 0).unwrap_or(0);
            OsString::from_wide(std::slice::from_raw_parts(path, length))
        };
        Ok(PathBuf::from(text))
    } else {
        Err(io::Error::other("local application data is unavailable"))
    };
    // SAFETY: CoTaskMemFree accepts null and the API's allocation.
    unsafe { CoTaskMemFree(path.cast()) };
    folder.and_then(|folder| {
        if folder.is_absolute() {
            Ok(folder)
        } else {
            Err(io::Error::other("local application data is not absolute"))
        }
    })
}

struct WtsBuffer(*mut u16);
impl Drop for WtsBuffer {
    fn drop(&mut self) {
        // SAFETY: only constructed with a buffer returned by WTS.
        unsafe { WTSFreeMemory(self.0.cast()) }
    }
}

/// The Agent's selected session is still the attached, active console. The
/// internal panel belongs to that session; a disconnected or switched-away
/// session must not submit a display mutation.
pub(super) fn active_console(session: u32) -> bool {
    // SAFETY: no arguments. Reports u32::MAX while attaching or detaching.
    let console = unsafe { WTSGetActiveConsoleSessionId() };
    if session == 0 || console != session {
        return false;
    }
    let mut buffer = null_mut();
    let mut bytes = 0;
    // SAFETY: current server, explicit session and writable output slots.
    if unsafe {
        WTSQuerySessionInformationW(
            null_mut(),
            session,
            WTSConnectState,
            &mut buffer,
            &mut bytes,
        )
    } == 0
        || buffer.is_null()
    {
        return false;
    }
    let buffer = WtsBuffer(buffer);
    // SAFETY: WTSConnectState returns this enum; read only if it fits.
    bytes as usize == size_of::<WTS_CONNECTSTATE_CLASS>()
        && unsafe { buffer.0.cast::<WTS_CONNECTSTATE_CLASS>().read_unaligned() } == WTSActive
}
