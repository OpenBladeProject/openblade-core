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
