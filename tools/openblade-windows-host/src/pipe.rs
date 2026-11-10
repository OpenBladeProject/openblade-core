#![allow(unsafe_code)]

use std::{
    ffi::c_void,
    fs::{File, OpenOptions},
    io,
    mem::{size_of, size_of_val},
    os::windows::fs::OpenOptionsExt,
    os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle},
    ptr::null_mut,
    sync::Arc,
};

use tokio::{
    net::windows::named_pipe::{NamedPipeClient, NamedPipeServer, ServerOptions},
    time::{sleep, timeout},
};
use windows_sys::Win32::{
    Foundation::{
        ERROR_BROKEN_PIPE, ERROR_NO_DATA, ERROR_PIPE_BUSY, ERROR_PIPE_NOT_CONNECTED, HANDLE,
        INVALID_HANDLE_VALUE, LUID, LocalFree,
    },
    Security::{
        Authorization::{
            ConvertSidToStringSidW, ConvertStringSecurityDescriptorToSecurityDescriptorW,
            SDDL_REVISION_1,
        },
        GetTokenInformation, PSECURITY_DESCRIPTOR, RevertToSelf, SECURITY_ATTRIBUTES,
        SECURITY_MAX_SID_SIZE, TOKEN_QUERY, TOKEN_STATISTICS, TOKEN_USER, TokenSessionId,
        TokenStatistics, TokenUser,
    },
    Storage::FileSystem::{
        FILE_FLAG_FIRST_PIPE_INSTANCE, PIPE_ACCESS_DUPLEX, SECURITY_IDENTIFICATION,
    },
    System::{
        Pipes::{
            CreateNamedPipeW, GetNamedPipeClientProcessId, GetNamedPipeClientSessionId,
            GetNamedPipeServerProcessId, GetNamedPipeServerSessionId, ImpersonateNamedPipeClient,
            PIPE_REJECT_REMOTE_CLIENTS,
        },
        Threading::{GetCurrentProcess, GetCurrentThread, OpenProcessToken, OpenThreadToken},
    },
};

const PIPE_BUFFER_BYTES: u32 = 8192;

pub struct Endpoint {
    path: String,
    instances: u32,
    descriptor: LocalAllocation,
    pub identity: Arc<Identity>,
}

impl Endpoint {
    pub fn new(path: String, instances: u32) -> io::Result<Self> {
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
            instances,
            descriptor: LocalAllocation(descriptor),
            identity: Arc::new(identity),
        })
    }

    pub fn claim(&self) -> io::Result<(OwnedHandle, File)> {
        // A synchronous instance makes no I/O submissions, so dropping the
        // claim does not depend on Tokio draining overlapped read completions.
        let path: Vec<_> = self.path.encode_utf16().chain([0]).collect();
        // SAFETY: both the path and explicit descriptor live through this call.
        let server = unsafe {
            CreateNamedPipeW(
                path.as_ptr(),
                PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE,
                PIPE_REJECT_REMOTE_CLIENTS,
                self.instances,
                PIPE_BUFFER_BYTES,
                PIPE_BUFFER_BYTES,
                0,
                &self.security_attributes(),
            )
        };
        if server == INVALID_HANDLE_VALUE {
            return Err(io::Error::last_os_error());
        }
        // SAFETY: a successful CreateNamedPipeW transfers an owned pipe handle.
        let server = unsafe { OwnedHandle::from_raw_handle(server) };
        let client = OpenOptions::new()
            .read(true)
            .write(true)
            .security_qos_flags(SECURITY_IDENTIFICATION)
            .open(&self.path)?;

        // The client's open connects it before any server-side I/O. Confirm
        // that this instance is paired with our process before publishing ready.
        let mut pid = 0;
        // SAFETY: the server is borrowed and the output is valid for this call.
        check(unsafe { GetNamedPipeClientProcessId(server.as_raw_handle(), &mut pid) })?;
        if pid != std::process::id() {
            return Err(denied());
        }
        Ok((server, client))
    }

    pub fn listen(&self) -> io::Result<NamedPipeServer> {
        let mut attributes = self.security_attributes();
        // SAFETY: the Win32 SECURITY_ATTRIBUTES and its descriptor remain valid
        // for synchronous CreateNamedPipeW. Tokio owns a successful pipe handle.
        unsafe {
            ServerOptions::new()
                .reject_remote_clients(true)
                .max_instances(self.instances as usize)
                .in_buffer_size(PIPE_BUFFER_BYTES)
                .out_buffer_size(PIPE_BUFFER_BYTES)
                .create_with_security_attributes_raw(
                    &self.path,
                    (&mut attributes as *mut SECURITY_ATTRIBUTES).cast(),
                )
        }
    }

    pub async fn next_listener(&self) -> io::Result<NamedPipeServer> {
        use std::time::Duration;

        timeout(Duration::from_secs(2), async {
            loop {
                match self.listen() {
                    Err(error) if error.raw_os_error() == Some(ERROR_PIPE_BUSY as i32) => {
                        // A retired overlapped operation can briefly retain its handle.
                        sleep(Duration::from_millis(20)).await;
                    }
                    result => return result,
                }
            }
        })
        .await?
    }

    fn security_attributes(&self) -> SECURITY_ATTRIBUTES {
        SECURITY_ATTRIBUTES {
            nLength: size_of::<SECURITY_ATTRIBUTES>() as u32,
            lpSecurityDescriptor: self.descriptor.0,
            bInheritHandle: 0,
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

pub struct Identity {
    sid: String,
    authentication: LUID,
    session: u32,
}

impl Identity {
    pub fn fixture_owner(&self) -> String {
        format!(
            "{}:{}:{}:{}",
            self.sid, self.session, self.authentication.HighPart, self.authentication.LowPart
        )
    }

    pub fn authorize_server(&self, pipe: &NamedPipeClient, expected_pid: u32) -> io::Result<()> {
        let mut pid = 0;
        let mut session = 0;
        // SAFETY: the client pipe is borrowed and both output slots are valid.
        unsafe {
            check(GetNamedPipeServerProcessId(pipe.as_raw_handle(), &mut pid))?;
            check(GetNamedPipeServerSessionId(
                pipe.as_raw_handle(),
                &mut session,
            ))?;
        }
        if expected_pid == 0 || pid != expected_pid || session == 0 || session != self.session {
            return Err(denied());
        }
        Ok(())
    }

    pub fn authorize(&self, pipe: &NamedPipeServer) -> io::Result<()> {
        // Verify the connecting process session independently of the read token.
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

pub fn stale_connection(error: &io::Error) -> bool {
    matches!(
        error.raw_os_error().map(|code| code as u32),
        Some(ERROR_BROKEN_PIPE | ERROR_NO_DATA | ERROR_PIPE_NOT_CONNECTED)
    )
}
