#![allow(unsafe_code)]

use std::{
    fs::{File, OpenOptions},
    io,
    mem::size_of,
    os::windows::fs::OpenOptionsExt,
    os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle},
    ptr::null_mut,
    sync::Arc,
};

use tokio::{
    net::windows::named_pipe::{NamedPipeClient, ServerOptions},
    time::{sleep, timeout},
};
use windows_sys::Win32::{
    Foundation::{
        ERROR_BROKEN_PIPE, ERROR_NO_DATA, ERROR_PIPE_BUSY, ERROR_PIPE_NOT_CONNECTED, HANDLE,
        INVALID_HANDLE_VALUE,
    },
    Security::{
        Authorization::{ConvertStringSecurityDescriptorToSecurityDescriptorW, SDDL_REVISION_1},
        PSECURITY_DESCRIPTOR, RevertToSelf, SECURITY_ATTRIBUTES, TOKEN_QUERY,
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
        Threading::{GetCurrentThread, OpenThreadToken},
    },
};

pub use crate::windows_token::Identity;
use crate::windows_token::{LocalAllocation, check, process_token};

mod response;
pub use response::{Connection, MAX_WRITE_BYTES};

const PIPE_BUFFER_BYTES: u32 = 8192;
const DISPLAY_PREFIX: &str = "OpenBlade.Session.Display.v1";
const GPU_PREFIX: &str = "OpenBlade.Session.Gpu.v2";

/// Managed `SessionAgentPipeEndpoint.Create`: the prefix, then the upper-case
/// hex of the first 16 bytes of SHA-256 over `"{sid}|{session}"`.
fn session_pipe_path(prefix: &str, sid: &str, session: u32, suffix: &str) -> String {
    use sha2::{Digest, Sha256};
    let hash = Sha256::digest(format!("{sid}|{session}").as_bytes());
    let hash: String = hash[..16].iter().map(|b| format!("{b:02X}")).collect();
    format!(r"\\.\pipe\{prefix}.{hash}{suffix}")
}

pub struct Endpoint {
    path: String,
    instances: u32,
    descriptor: LocalAllocation,
    pub identity: Arc<Identity>,
}

impl Endpoint {
    pub fn new(path: String, instances: u32) -> io::Result<Self> {
        let identity = Identity::read(&process_token()?)?;
        // Protected one-SID DACL and explicit owner match the managed session pipe.
        // Generic write also permits this SID to create instances; caller SID,
        // session and authentication-LUID admission remain independently required.
        let sddl: Vec<u16> = format!("O:{0}D:P(A;;GA;;;{0})", identity.sid)
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

    /// Existing managed Battery.v1 name and protected one-SID ownership policy.
    /// No caller-supplied endpoint path or session evidence is accepted here.
    pub fn current_user_battery() -> io::Result<Self> {
        Self::current_user_session(DISPLAY_PREFIX, ".Battery.v1")
    }

    /// Fixed managed Display.v1 endpoint, query-only in the selected Agent.
    pub fn current_user_display() -> io::Result<Self> {
        Self::current_user_session(DISPLAY_PREFIX, "")
    }

    /// Fixed managed Gpu.v2 endpoint (`SessionAgentPipeEndpoint.CreateGpu`).
    pub fn current_user_gpu() -> io::Result<Self> {
        Self::current_user_session(GPU_PREFIX, "")
    }

    fn current_user_session(prefix: &'static str, suffix: &'static str) -> io::Result<Self> {
        let identity = Identity::read(&process_token()?)?;
        if identity.session == 0
            || !identity.primary
            || matches!(identity.sid.as_str(), "S-1-5-18" | "S-1-5-19" | "S-1-5-20")
        {
            return Err(denied());
        }
        let endpoint = Self::new(
            session_pipe_path(prefix, &identity.sid, identity.session, suffix),
            2,
        )?;
        if endpoint.identity.sid != identity.sid
            || endpoint.identity.session != identity.session
            || endpoint.identity.authentication.LowPart != identity.authentication.LowPart
            || endpoint.identity.authentication.HighPart != identity.authentication.HighPart
            || !endpoint.identity.primary
        {
            return Err(denied());
        }
        Ok(endpoint)
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

    pub fn listen(&self) -> io::Result<Connection> {
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
                .map(Connection::new)
        }
    }

    pub async fn next_listener(&self) -> io::Result<Connection> {
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

impl Identity {
    /// Session of the same process token used to create a private endpoint.
    pub fn fixture_session() -> io::Result<u32> {
        Ok(Self::read(&process_token()?)?.session)
    }

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

    pub fn authorize(&self, pipe: &impl AsRawHandle) -> io::Result<()> {
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
}

fn peer_token(pipe: &impl AsRawHandle) -> io::Result<OwnedHandle> {
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

pub fn stale_connection(error: &io::Error) -> bool {
    matches!(
        error.raw_os_error().map(|code| code as u32),
        Some(ERROR_BROKEN_PIPE | ERROR_NO_DATA | ERROR_PIPE_NOT_CONNECTED)
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn session_pipe_names_match_managed_endpoint_composition() {
        // SHA-256("S-1-5-21-1-2-3-1001|1")[..16], upper-case hex.
        let display = session_pipe_path(DISPLAY_PREFIX, "S-1-5-21-1-2-3-1001", 1, "");
        let gpu = session_pipe_path(GPU_PREFIX, "S-1-5-21-1-2-3-1001", 1, "");
        let hash = "9FEC1D0680D6490058B5B240382A51FD";
        assert_eq!(
            display,
            format!(r"\\.\pipe\OpenBlade.Session.Display.v1.{hash}")
        );
        assert_eq!(gpu, format!(r"\\.\pipe\OpenBlade.Session.Gpu.v2.{hash}"));
        assert_eq!(
            session_pipe_path(DISPLAY_PREFIX, "S-1-5-21-1-2-3-1001", 1, ".Battery.v1"),
            format!(r"\\.\pipe\OpenBlade.Session.Display.v1.{hash}.Battery.v1")
        );
    }
}
