//! Admission for the fixed Windows VM fixture only.
#![allow(unsafe_code)]

use openblade_windows_host::configuration_writer::{
    fixture::VM_DATA_ROOT, protected::ProtectedStorage,
};
use std::{
    env,
    ffi::OsStr,
    fs::File,
    io,
    mem::{MaybeUninit, size_of, size_of_val},
    os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle},
    path::Path,
    ptr::null_mut,
};
use windows_sys::Win32::{
    Security::{
        GetTokenInformation, IsWellKnownSid, TOKEN_QUERY, TOKEN_USER, TokenUser, WinLocalSystemSid,
    },
    System::{
        Services::{
            CloseServiceHandle, OpenSCManagerW, OpenServiceW, QUERY_SERVICE_CONFIGW,
            QueryServiceConfigW, QueryServiceStatusEx, SC_HANDLE, SC_MANAGER_CONNECT,
            SC_STATUS_PROCESS_INFO, SERVICE_DEMAND_START, SERVICE_QUERY_CONFIG,
            SERVICE_QUERY_STATUS, SERVICE_START_PENDING, SERVICE_STATUS_PROCESS,
            SERVICE_WIN32_OWN_PROCESS,
        },
        Threading::{GetCurrentProcess, GetCurrentProcessId, OpenProcessToken},
    },
};

pub(super) const NAME: &str = "OpenBladeRustPhase5jVm";
pub(super) const ROOT: &str = r"C:\OpenBladeRustPhase5jVm";
pub(super) const IMAGE: &str = r"C:\OpenBladeRustPhase5jVm\OpenBladeRustPhase5jVm.exe";
const IMAGE_NAME: &str = "OpenBladeRustPhase5jVm.exe";
pub(super) const START_MARKER: &str = "start.release";
pub(super) const RENDERER_MARKER: &str = "renderer.release";

pub(super) fn require_process() -> io::Result<()> {
    if env::args_os().nth(1).is_some() || env::current_exe()?.as_os_str() != OsStr::new(IMAGE) {
        return Err(denied(
            "unexpected VM fixture executable or process arguments",
        ));
    }

    let mut token = null_mut();
    // SAFETY: GetCurrentProcess supplies a pseudohandle and token is writable.
    if unsafe { OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) } == 0 {
        return Err(io::Error::last_os_error());
    }
    // SAFETY: OpenProcessToken returned an owned token handle.
    let token = unsafe { OwnedHandle::from_raw_handle(token) };
    let mut user = [0usize; 128 / size_of::<usize>()];
    let mut returned = 0;
    // SAFETY: user is aligned for TOKEN_USER and can hold the structure and its SID.
    if unsafe {
        GetTokenInformation(
            token.as_raw_handle(),
            TokenUser,
            user.as_mut_ptr().cast(),
            size_of_val(&user) as u32,
            &mut returned,
        )
    } == 0
    {
        return Err(io::Error::last_os_error());
    }
    if returned < size_of::<TOKEN_USER>() as u32 {
        return Err(denied("invalid VM fixture token user"));
    }
    // SAFETY: Windows returned a TOKEN_USER and owns the SID layout within user.
    let system = unsafe {
        let user = &*user.as_ptr().cast::<TOKEN_USER>();
        !user.User.Sid.is_null() && IsWellKnownSid(user.User.Sid, WinLocalSystemSid) != 0
    };
    if !system {
        return Err(denied("VM fixture requires a LocalSystem process"));
    }
    Ok(())
}

/// Only SCM's exact service name and no additional StartService arguments.
///
/// # Safety
/// argc and argv must come from the live ServiceMain callback.
pub(super) unsafe fn callback_matches(argc: u32, argv: *mut *mut u16) -> bool {
    if argc != 1 || argv.is_null() {
        return false;
    }
    // SAFETY: SCM supplies one pointer to a NUL-terminated service name.
    let name = unsafe { *argv };
    if name.is_null() {
        return false;
    }
    for (index, expected) in NAME.encode_utf16().chain([0]).enumerate() {
        // SAFETY: stop at the first mismatch, including an early terminator.
        if unsafe { *name.add(index) } != expected {
            return false;
        }
    }
    true
}

pub(super) struct FixtureAdmission {
    root: ProtectedStorage,
    _image: File,
}

impl FixtureAdmission {
    pub(super) fn marker(&self, name: &str) -> io::Result<bool> {
        if !marker_allowed(name) {
            return Err(denied("unknown VM fixture marker"));
        }
        let Some(marker) = self.root.open_vm_file(name)? else {
            return Ok(false);
        };
        if marker.metadata()?.len() != 0 {
            return Err(denied("VM fixture marker must be empty"));
        }
        Ok(true)
    }
}

fn marker_allowed(name: &str) -> bool {
    matches!(name, START_MARKER | RENDERER_MARKER)
}

/// Call only from the admitted SCM callback after reporting START_PENDING.
pub(super) fn admit() -> io::Result<(FixtureAdmission, ProtectedStorage)> {
    require_process()?;
    require_registration()?;
    let root = ProtectedStorage::open_vm_root(Path::new(ROOT))?;
    let image = root
        .open_vm_file(IMAGE_NAME)?
        .ok_or_else(|| denied("missing protected VM fixture image"))?;
    let data = ProtectedStorage::open_vm_root(Path::new(VM_DATA_ROOT))?;
    Ok((
        FixtureAdmission {
            root,
            _image: image,
        },
        data,
    ))
}

struct ServiceHandle(SC_HANDLE);

impl ServiceHandle {
    fn new(handle: SC_HANDLE) -> io::Result<Self> {
        if handle.is_null() {
            Err(io::Error::last_os_error())
        } else {
            Ok(Self(handle))
        }
    }
}

impl Drop for ServiceHandle {
    fn drop(&mut self) {
        // SAFETY: this wrapper owns an SCM database or service handle.
        unsafe { CloseServiceHandle(self.0) };
    }
}

fn require_registration() -> io::Result<()> {
    let name: Vec<_> = NAME.encode_utf16().chain([0]).collect();
    // SAFETY: null pointers select the active services database on the local machine.
    let manager = ServiceHandle::new(unsafe {
        OpenSCManagerW(std::ptr::null(), std::ptr::null(), SC_MANAGER_CONNECT)
    })?;
    // SAFETY: manager is live and name is NUL-terminated.
    let service = ServiceHandle::new(unsafe {
        OpenServiceW(
            manager.0,
            name.as_ptr(),
            SERVICE_QUERY_CONFIG | SERVICE_QUERY_STATUS,
        )
    })?;

    // QueryServiceConfigW documents an 8 KiB maximum response, including
    // the strings referenced from the structure.
    #[repr(align(8))]
    struct ConfigBuffer([u16; 4096]);
    let mut buffer = ConfigBuffer([0; 4096]);
    let mut needed = 0;
    // SAFETY: the aligned buffer is writable and sized to the documented maximum.
    if unsafe {
        QueryServiceConfigW(
            service.0,
            buffer.0.as_mut_ptr().cast(),
            size_of_val(&buffer.0) as u32,
            &mut needed,
        )
    } == 0
    {
        return Err(io::Error::last_os_error());
    }
    // SAFETY: the successful query initialized the aligned structure.
    let config = unsafe { &*buffer.0.as_ptr().cast::<QUERY_SERVICE_CONFIGW>() };
    if config.dwServiceType != SERVICE_WIN32_OWN_PROCESS
        || config.dwStartType != SERVICE_DEMAND_START
        || !config_string_matches(&buffer.0, config.lpBinaryPathName, IMAGE)
        || !config_string_matches(&buffer.0, config.lpServiceStartName, "LocalSystem")
    {
        return Err(denied("unexpected VM fixture service registration"));
    }

    let mut status = MaybeUninit::<SERVICE_STATUS_PROCESS>::uninit();
    // SAFETY: status points to aligned, writable storage for SC_STATUS_PROCESS_INFO.
    if unsafe {
        QueryServiceStatusEx(
            service.0,
            SC_STATUS_PROCESS_INFO,
            status.as_mut_ptr().cast(),
            size_of::<SERVICE_STATUS_PROCESS>() as u32,
            &mut needed,
        )
    } == 0
    {
        return Err(io::Error::last_os_error());
    }
    // SAFETY: the successful query initialized status and GetCurrentProcessId takes no inputs.
    let (status, pid) = unsafe { (status.assume_init(), GetCurrentProcessId()) };
    // SCM can omit the PID in START_PENDING. ServiceMain's exact callback
    // identity and the registration checks above still apply.
    if status.dwCurrentState != SERVICE_START_PENDING
        || status.dwServiceType != SERVICE_WIN32_OWN_PROCESS
        || (status.dwProcessId != 0 && status.dwProcessId != pid)
    {
        return Err(denied("unexpected VM fixture service status"));
    }
    Ok(())
}

fn config_string_matches(buffer: &[u16], value: *const u16, expected: &str) -> bool {
    let Some(offset) = value.addr().checked_sub(buffer.as_ptr().addr()) else {
        return false;
    };
    if offset % size_of::<u16>() != 0 {
        return false;
    }
    let Some(actual) = buffer.get(offset / size_of::<u16>()..) else {
        return false;
    };
    let expected = expected.encode_utf16().chain([0]);
    actual
        .iter()
        .copied()
        .take(expected.clone().count())
        .eq(expected)
}

fn denied(reason: &'static str) -> io::Error {
    io::Error::new(io::ErrorKind::PermissionDenied, reason)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn callback_accepts_only_the_fixed_service_name() {
        for (name, count, expected) in [
            (NAME, 1, true),
            (NAME, 2, false),
            ("OpenBladeRustReadOnlyDev", 1, false),
            ("", 1, false),
        ] {
            let mut name: Vec<_> = name.encode_utf16().chain([0]).collect();
            let mut argv = [name.as_mut_ptr(), null_mut()];
            // SAFETY: argv has count entries and the first is NUL-terminated.
            assert_eq!(
                unsafe { callback_matches(count, argv.as_mut_ptr()) },
                expected
            );
        }
    }

    #[test]
    fn configured_image_is_exact_and_bounded() {
        for (value, expected) in [
            (IMAGE.to_owned(), true),
            (format!("{IMAGE} --fixture"), false),
            (format!("\"{IMAGE}\""), false),
            (IMAGE.to_ascii_lowercase(), false),
        ] {
            let buffer: Vec<_> = value.encode_utf16().chain([0]).collect();
            assert_eq!(
                config_string_matches(&buffer, buffer.as_ptr(), IMAGE),
                expected
            );
            assert!(!config_string_matches(
                &buffer[..buffer.len() - 1],
                buffer.as_ptr(),
                IMAGE
            ));
        }
        assert!(!config_string_matches(&[0], std::ptr::null(), IMAGE));
    }

    #[test]
    fn markers_are_two_fixed_leaf_names() {
        for name in [START_MARKER, RENDERER_MARKER] {
            assert!(marker_allowed(name));
        }
        for name in [
            "",
            "START.RELEASE",
            "start.release:stream",
            r"..\start.release",
            IMAGE,
        ] {
            assert!(!marker_allowed(name));
        }
    }
}
