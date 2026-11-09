#![allow(unsafe_code)]

use std::{
    ffi::c_void,
    io,
    mem::{size_of, size_of_val},
    os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle},
    ptr::null_mut,
    sync::Arc,
};

use tokio::net::windows::named_pipe::{NamedPipeServer, ServerOptions};
use windows_sys::Win32::{
    Foundation::{HANDLE, LUID, LocalFree},
    Security::{
        Authorization::{
            ConvertSidToStringSidW, ConvertStringSecurityDescriptorToSecurityDescriptorW,
            SDDL_REVISION_1,
        },
        GetTokenInformation, PSECURITY_DESCRIPTOR, RevertToSelf, SECURITY_ATTRIBUTES,
        SECURITY_MAX_SID_SIZE, TOKEN_QUERY, TOKEN_STATISTICS, TOKEN_USER, TokenSessionId,
        TokenStatistics, TokenUser,
    },
    System::{
        Pipes::{GetNamedPipeClientSessionId, ImpersonateNamedPipeClient},
        Threading::{GetCurrentProcess, GetCurrentThread, OpenProcessToken, OpenThreadToken},
    },
};

use super::CLIENTS;

pub(super) struct Endpoint {
    path: String,
    descriptor: LocalAllocation,
    pub(super) identity: Arc<Identity>,
}

impl Endpoint {
    pub(super) fn new(path: String) -> io::Result<Self> {
        let identity = Identity::read(&process_token()?)?;
        // Explicit dev DACL. The managed duplex client uses generic write access,
        // which grants the same SID the ability to create additional instances.
        // Keep this endpoint inside the trusted same-user development boundary.
        let sddl: Vec<u16> = format!("D:P(A;;GA;;;{})", identity.sid)
            .encode_utf16()
            .chain([0])
            .collect();
        let mut descriptor: PSECURITY_DESCRIPTOR = null_mut();
        // SAFETY: SDDL is NUL-terminated and the out pointer remains valid for the call.
        check(unsafe {
            ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl.as_ptr(),
                SDDL_REVISION_1,
                &mut descriptor,
                null_mut(),
            )
        })?;
        Ok(Self {
            path,
            descriptor: LocalAllocation(descriptor),
            identity: Arc::new(identity),
        })
    }

    pub(super) fn listen(&self, first: bool) -> io::Result<NamedPipeServer> {
        let mut attributes = SECURITY_ATTRIBUTES {
            nLength: size_of::<SECURITY_ATTRIBUTES>() as u32,
            lpSecurityDescriptor: self.descriptor.0,
            bInheritHandle: 0,
        };
        // SAFETY: the Win32 SECURITY_ATTRIBUTES and its descriptor remain valid
        // for synchronous CreateNamedPipeW. Tokio owns a successful pipe handle.
        unsafe {
            ServerOptions::new()
                .first_pipe_instance(first)
                .reject_remote_clients(true)
                .max_instances(CLIENTS + 1)
                .in_buffer_size(8192)
                .out_buffer_size(8192)
                .create_with_security_attributes_raw(
                    &self.path,
                    (&mut attributes as *mut SECURITY_ATTRIBUTES).cast(),
                )
        }
    }
}

struct LocalAllocation(*mut c_void);

impl Drop for LocalAllocation {
    fn drop(&mut self) {
        // SAFETY: owned allocation returned by one of the SDDL conversion APIs.
        unsafe { LocalFree(self.0) };
    }
}

pub(super) struct Identity {
    sid: String,
    authentication: LUID,
    session: u32,
}

impl Identity {
    pub(super) fn authorize(&self, pipe: &NamedPipeServer) -> io::Result<()> {
        let mut session = 0;
        // SAFETY: the pipe is connected and borrowed, with a valid output slot.
        check(unsafe { GetNamedPipeClientSessionId(pipe.as_raw_handle(), &mut session) })?;
        if session != self.session {
            return Err(denied());
        }
        let peer = Self::read(&peer_token(pipe)?)?;
        if peer.sid != self.sid
            || peer.session != self.session
            || peer.authentication.LowPart != self.authentication.LowPart
            || peer.authentication.HighPart != self.authentication.HighPart
        {
            return Err(denied());
        }
        Ok(())
    }

    fn read(token: &OwnedHandle) -> io::Result<Self> {
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

fn process_token() -> io::Result<OwnedHandle> {
    let mut token: HANDLE = null_mut();
    // SAFETY: the pseudohandle is valid and the writable out pointer is scoped to the call.
    unsafe {
        check(OpenProcessToken(
            GetCurrentProcess(),
            TOKEN_QUERY,
            &mut token,
        ))?;
        Ok(OwnedHandle::from_raw_handle(token))
    }
}

fn peer_token(pipe: &NamedPipeServer) -> io::Result<OwnedHandle> {
    let mut token: HANDLE = null_mut();
    // SAFETY: the borrowed connected pipe has just supplied bytes. Only the
    // synchronous thread-token query runs impersonated. Revert before returning,
    // including on query failure, and terminate if reverting itself fails.
    unsafe {
        check(ImpersonateNamedPipeClient(pipe.as_raw_handle()))?;
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

fn denied() -> io::Error {
    io::Error::new(
        io::ErrorKind::PermissionDenied,
        "development client is outside the host logon",
    )
}

fn check(success: i32) -> io::Result<()> {
    if success == 0 {
        Err(io::Error::last_os_error())
    } else {
        Ok(())
    }
}
