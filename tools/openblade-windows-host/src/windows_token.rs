//! Windows token primitives shared by private pipe admission and the standalone Agent.
#![allow(unsafe_code)]

use std::{
    ffi::c_void,
    io,
    mem::{size_of, size_of_val},
    os::windows::io::{AsRawHandle, BorrowedHandle, FromRawHandle, OwnedHandle},
    ptr::null_mut,
};

use windows_sys::Win32::{
    Foundation::{HANDLE, LUID, LocalFree},
    Security::{
        Authorization::ConvertSidToStringSidW, GetTokenInformation, SECURITY_MAX_SID_SIZE,
        TOKEN_QUERY, TOKEN_STATISTICS, TOKEN_USER, TokenPrimary, TokenSessionId, TokenStatistics,
        TokenUser,
    },
    System::Threading::{GetCurrentProcess, OpenProcessToken},
};

pub struct Identity {
    pub(crate) sid: String,
    pub(crate) authentication: LUID,
    pub(crate) session: u32,
    pub(crate) primary: bool,
}

impl Identity {
    pub(crate) fn read(token: &OwnedHandle) -> io::Result<Self> {
        let mut statistics = TOKEN_STATISTICS::default();
        let mut session = 0u32;
        let mut returned = 0;
        // SAFETY: each query writes the structure appropriate for its information
        // class into its writable, aligned output and cannot outlive the call.
        unsafe {
            check(GetTokenInformation(
                token.as_raw_handle(),
                TokenStatistics,
                (&mut statistics as *mut TOKEN_STATISTICS).cast(),
                size_of_val(&statistics) as u32,
                &mut returned,
            ))?;
            check(GetTokenInformation(
                token.as_raw_handle(),
                TokenSessionId,
                (&mut session as *mut u32).cast(),
                size_of_val(&session) as u32,
                &mut returned,
            ))?;
        }
        Ok(Self {
            sid: user_sid(token)?,
            authentication: statistics.AuthenticationId,
            session,
            primary: statistics.TokenType == TokenPrimary,
        })
    }
}

fn user_sid(token: &OwnedHandle) -> io::Result<String> {
    // A TOKEN_USER header plus the largest Win32 SID (SECURITY_MAX_SID_SIZE).
    // Using usize storage ensures alignment for TOKEN_USER on both architectures.
    let mut buffer = [0usize;
        (size_of::<TOKEN_USER>() + SECURITY_MAX_SID_SIZE as usize).div_ceil(size_of::<usize>())];
    let mut returned = 0;
    let mut text = null_mut();
    // SAFETY: TokenUser writes a TOKEN_USER and points its SID into the aligned
    // buffer. Windows guarantees the converted SID string is NUL-terminated.
    let sid = unsafe {
        check(GetTokenInformation(
            token.as_raw_handle(),
            TokenUser,
            buffer.as_mut_ptr().cast(),
            size_of_val(&buffer) as u32,
            &mut returned,
        ))?;
        let user = &*buffer.as_ptr().cast::<TOKEN_USER>();
        check(ConvertSidToStringSidW(user.User.Sid, &mut text))?;
        let allocation = LocalAllocation(text.cast());
        let length = (0..).find(|&offset| *text.add(offset) == 0).unwrap();
        let sid = String::from_utf16(std::slice::from_raw_parts(text, length));
        drop(allocation);
        sid
    };
    sid.map_err(io::Error::other)
}

pub(crate) fn process_token() -> io::Result<OwnedHandle> {
    // SAFETY: the current-process pseudohandle is borrowed only, never closed.
    open_process_token(unsafe { GetCurrentProcess() })
}

pub(crate) fn process_token_for(process: BorrowedHandle<'_>) -> io::Result<OwnedHandle> {
    open_process_token(process.as_raw_handle())
}

fn open_process_token(process: HANDLE) -> io::Result<OwnedHandle> {
    let mut token: HANDLE = null_mut();
    // SAFETY: a borrowed process/pseudohandle and a scoped writable output.
    unsafe {
        check(OpenProcessToken(process, TOKEN_QUERY, &mut token))?;
        Ok(OwnedHandle::from_raw_handle(token))
    }
}

pub(crate) struct LocalAllocation(pub(crate) *mut c_void);

impl Drop for LocalAllocation {
    fn drop(&mut self) {
        // SAFETY: owned allocation returned by one of the security conversion APIs.
        unsafe { LocalFree(self.0) };
    }
}

pub(crate) fn check(success: i32) -> io::Result<()> {
    if success == 0 {
        Err(io::Error::last_os_error())
    } else {
        Ok(())
    }
}
