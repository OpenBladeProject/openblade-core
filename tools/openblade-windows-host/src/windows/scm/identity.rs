//! Read-only native evidence shared by the two explicitly enabled SCM roles.
#![allow(unsafe_code)]

use std::{
    ffi::OsStr,
    io,
    mem::{MaybeUninit, size_of, size_of_val},
    os::windows::{
        ffi::OsStrExt,
        io::{AsRawHandle, FromRawHandle, OwnedHandle},
    },
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
            SC_STATUS_PROCESS_INFO, SERVICE_QUERY_CONFIG, SERVICE_QUERY_STATUS,
            SERVICE_STATUS_PROCESS, SERVICE_WIN32_OWN_PROCESS,
        },
        Threading::{GetCurrentProcess, OpenProcessToken},
    },
};

pub(crate) fn is_local_system() -> io::Result<bool> {
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
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "invalid service token user",
        ));
    }
    // SAFETY: Windows returned a TOKEN_USER and owns the SID layout within user.
    let system = unsafe {
        let user = &*user.as_ptr().cast::<TOKEN_USER>();
        !user.User.Sid.is_null() && IsWellKnownSid(user.User.Sid, WinLocalSystemSid) != 0
    };
    Ok(system)
}

/// Only SCM's exact service name and no additional StartService arguments.
///
/// # Safety
/// argc and argv must come from the live ServiceMain callback.
pub(crate) unsafe fn callback_matches(expected_name: &str, argc: u32, argv: *mut *mut u16) -> bool {
    if argc != 1 || argv.is_null() {
        return false;
    }
    // SAFETY: SCM supplies one pointer to a NUL-terminated service name.
    let name = unsafe { *argv };
    if name.is_null() {
        return false;
    }
    for (index, expected) in expected_name.encode_utf16().chain([0]).enumerate() {
        // SAFETY: stop at the first mismatch, including an early terminator.
        if unsafe { *name.add(index) } != expected {
            return false;
        }
    }
    true
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

pub(crate) struct ScmEvidence {
    pub(crate) process_id: u32,
    pub(crate) service_type: u32,
    pub(crate) state: u32,
    pub(crate) matches_config: bool,
}

pub(crate) struct Registration {
    _manager: ServiceHandle,
    service: ServiceHandle,
}
impl Registration {
    pub(crate) fn open(name: &str) -> io::Result<Self> {
        let name: Vec<_> = name.encode_utf16().chain([0]).collect();
        let manager = ServiceHandle::new(unsafe {
            OpenSCManagerW(std::ptr::null(), std::ptr::null(), SC_MANAGER_CONNECT)
        })?;
        let service = ServiceHandle::new(unsafe {
            OpenServiceW(
                manager.0,
                name.as_ptr(),
                SERVICE_QUERY_CONFIG | SERVICE_QUERY_STATUS,
            )
        })?;
        Ok(Self {
            _manager: manager,
            service,
        })
    }
    pub(crate) fn query(
        &self,
        expected_command: &OsStr,
        expected_start: u32,
    ) -> io::Result<ScmEvidence> {
        // QueryServiceConfigW documents an 8 KiB maximum response, including
        // the strings referenced from the structure.
        #[repr(align(8))]
        struct ConfigBuffer([u16; 4096]);
        let mut buffer = ConfigBuffer([0; 4096]);
        let mut needed = 0;
        // SAFETY: the aligned buffer is writable and sized to the documented maximum.
        if unsafe {
            QueryServiceConfigW(
                self.service.0,
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
        let matches_config = config.dwServiceType == SERVICE_WIN32_OWN_PROCESS
            && config.dwStartType == expected_start
            && config_string_matches(&buffer.0, config.lpBinaryPathName, expected_command)
            && config_string_matches(
                &buffer.0,
                config.lpServiceStartName,
                OsStr::new("LocalSystem"),
            );

        let mut status = MaybeUninit::<SERVICE_STATUS_PROCESS>::uninit();
        // SAFETY: status points to aligned, writable storage for SC_STATUS_PROCESS_INFO.
        if unsafe {
            QueryServiceStatusEx(
                self.service.0,
                SC_STATUS_PROCESS_INFO,
                status.as_mut_ptr().cast(),
                size_of::<SERVICE_STATUS_PROCESS>() as u32,
                &mut needed,
            )
        } == 0
        {
            return Err(io::Error::last_os_error());
        }
        // SAFETY: the successful query initialized status.
        let status = unsafe { status.assume_init() };
        Ok(ScmEvidence {
            process_id: status.dwProcessId,
            service_type: status.dwServiceType,
            state: status.dwCurrentState,
            matches_config,
        })
    }
}
pub(crate) fn query_registration(
    name: &str,
    expected_command: &OsStr,
    expected_start: u32,
) -> io::Result<ScmEvidence> {
    Registration::open(name)?.query(expected_command, expected_start)
}

pub(crate) fn config_string_matches(buffer: &[u16], value: *const u16, expected: &OsStr) -> bool {
    let Some(offset) = value.addr().checked_sub(buffer.as_ptr().addr()) else {
        return false;
    };
    if offset % size_of::<u16>() != 0 {
        return false;
    }
    let Some(actual) = buffer.get(offset / size_of::<u16>()..) else {
        return false;
    };
    actual
        .iter()
        .copied()
        .zip(expected.encode_wide().chain([0]))
        .all(|(actual, expected)| actual == expected)
        && actual.len() > expected.encode_wide().count()
}
