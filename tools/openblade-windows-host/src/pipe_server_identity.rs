#![allow(unsafe_code)]

use std::{io, os::windows::io::RawHandle};

use windows_sys::Win32::System::Pipes::{GetNamedPipeServerProcessId, GetNamedPipeServerSessionId};

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
