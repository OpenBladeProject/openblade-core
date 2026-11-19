#![allow(unsafe_code)]

use std::{
    io,
    os::windows::io::{BorrowedHandle, RawHandle},
};

use windows_sys::Win32::System::Pipes::{GetNamedPipeServerProcessId, GetNamedPipeServerSessionId};

// Process-instance correlation only; installed release/loaded-image authority
// must be established separately. The borrow pins this exact connection handle.
#[allow(dead_code)]
pub(crate) fn verify_retained_service(
    pipe: BorrowedHandle<'_>,
    process: &mut crate::retained_process::RetainedProcessInstance,
) -> Result<(), crate::retained_process::InstanceError> {
    process.check_pipe_server(pipe)
}

// This is kernel metadata from the connected pipe. It needs independent proof.
pub(crate) fn server_identity(pipe: RawHandle) -> io::Result<(u32, u32)> {
    let mut pid = 0;
    let mut session = 0;
    // SAFETY: the caller borrows the pipe and both outputs are writable.
    unsafe {
        if GetNamedPipeServerProcessId(pipe, &mut pid) == 0
            || GetNamedPipeServerSessionId(pipe, &mut session) == 0
        {
            return Err(io::Error::last_os_error());
        }
    }
    if pid == 0 {
        return Err(io::ErrorKind::PermissionDenied.into());
    }
    Ok((pid, session))
}

/// Re-reads the retained pipe server's process and session ids.
pub(crate) type ServerIdentityMonitor = Box<dyn Fn() -> io::Result<(u32, u32)>>;

// Duplicate the actual connected pipe object for idle metadata checks while an
// async read borrows the stream. No name/PID reopen and no authority is minted.
pub(crate) fn retain_monitor(pipe: RawHandle) -> io::Result<ServerIdentityMonitor> {
    use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};
    use windows_sys::Win32::{
        Foundation::{DUPLICATE_SAME_ACCESS, DuplicateHandle},
        System::Threading::GetCurrentProcess,
    };
    let mut raw = std::ptr::null_mut();
    if unsafe {
        DuplicateHandle(
            GetCurrentProcess(),
            pipe,
            GetCurrentProcess(),
            &mut raw,
            0,
            0,
            DUPLICATE_SAME_ACCESS,
        )
    } == 0
    {
        return Err(io::Error::last_os_error());
    }
    let held = unsafe { OwnedHandle::from_raw_handle(raw) };
    Ok(Box::new(move || server_identity(held.as_raw_handle())))
}
