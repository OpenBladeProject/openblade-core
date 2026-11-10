#![allow(unsafe_code)]

// These primitives do not select an endpoint, authorize a caller, or route IPC.
use std::{
    ffi::c_void,
    io,
    mem::size_of,
    os::windows::io::{FromRawHandle, OwnedHandle, RawHandle},
    ptr::null_mut,
};

use windows_sys::Win32::{
    Foundation::{HANDLE, LocalFree},
    Security::{
        Authorization::{ConvertStringSecurityDescriptorToSecurityDescriptorW, SDDL_REVISION_1},
        RevertToSelf, SECURITY_ATTRIBUTES, TOKEN_QUERY,
    },
    System::{
        Pipes::ImpersonateNamedPipeClient,
        Threading::{GetCurrentThread, OpenThreadToken},
    },
};

pub(super) struct LocalAllocation(pub(super) *mut c_void);

impl Drop for LocalAllocation {
    fn drop(&mut self) {
        // SAFETY: this allocation came from a Win32 LocalAlloc-based API.
        unsafe { LocalFree(self.0) };
    }
}

pub(super) fn descriptor(sddl: &str) -> io::Result<LocalAllocation> {
    let sddl: Vec<u16> = sddl.encode_utf16().chain([0]).collect();
    let mut descriptor = null_mut();
    // SAFETY: input is NUL-terminated and output is writable for the call.
    check(unsafe {
        ConvertStringSecurityDescriptorToSecurityDescriptorW(
            sddl.as_ptr(),
            SDDL_REVISION_1,
            &mut descriptor,
            null_mut(),
        )
    })?;
    Ok(LocalAllocation(descriptor))
}

impl LocalAllocation {
    pub(super) fn security_attributes(&self) -> SECURITY_ATTRIBUTES {
        SECURITY_ATTRIBUTES {
            nLength: size_of::<SECURITY_ATTRIBUTES>() as u32,
            lpSecurityDescriptor: self.0,
            bInheritHandle: 0,
        }
    }
}

pub(super) fn peer_token(pipe: RawHandle) -> io::Result<OwnedHandle> {
    let mut token: HANDLE = null_mut();
    // SAFETY: callers pass a borrowed connected server pipe after a read.
    // The thread cannot yield while impersonating. Abort if revert fails.
    unsafe {
        check(ImpersonateNamedPipeClient(pipe))?;
        let opened = check(OpenThreadToken(
            GetCurrentThread(),
            TOKEN_QUERY,
            1,
            &mut token,
        ));
        if RevertToSelf() == 0 {
            std::process::abort();
        }
        opened?;
        Ok(OwnedHandle::from_raw_handle(token))
    }
}

pub(super) fn check(success: i32) -> io::Result<()> {
    if success == 0 {
        Err(io::Error::last_os_error())
    } else {
        Ok(())
    }
}
