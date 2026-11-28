#![allow(unsafe_code)]

#[cfg(any(test, feature = "scratch-candidate-receipt"))]
use std::rc::Rc;
use std::{
    future::Future,
    io,
    mem::{offset_of, size_of, size_of_val},
    os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle},
    pin::Pin,
    ptr::{null, null_mut},
    task::{Context, Poll, ready},
    time::Duration,
};

use tokio::{
    io::ReadBuf,
    time::{Sleep, sleep},
};
use windows_sys::Win32::{
    Foundation::{
        ERROR_BROKEN_PIPE, ERROR_NO_DATA, ERROR_PIPE_CONNECTED, ERROR_PIPE_LISTENING,
        ERROR_PIPE_NOT_CONNECTED, HANDLE, INVALID_HANDLE_VALUE,
    },
    Security::{
        GetTokenInformation, IsWellKnownSid, SID_AND_ATTRIBUTES, TOKEN_GROUPS, TOKEN_USER,
        TokenGroups, TokenSessionId, TokenUser, WinInteractiveSid, WinLocalSystemSid,
        WinRemoteLogonIdSid,
    },
    Storage::FileSystem::{
        CreateFileW, FILE_FLAG_FIRST_PIPE_INSTANCE, OPEN_EXISTING, PIPE_ACCESS_DUPLEX, ReadFile,
        SECURITY_IDENTIFICATION, SECURITY_SQOS_PRESENT, WriteFile,
    },
    System::Pipes::{
        ConnectNamedPipe, CreateNamedPipeW, GetNamedPipeClientProcessId, PIPE_NOWAIT,
        PIPE_REJECT_REMOTE_CLIENTS, PeekNamedPipe, SetNamedPipeHandleState,
    },
};

#[path = "readiness_peer.rs"]
mod peer;
#[cfg(feature = "service-lifecycle-shutdown-v1")]
pub(crate) use peer::system_or_administrator;
pub(crate) use peer::{Caller, ControlSession, Logon, Pending, Stream};
#[cfg(feature = "service-lifecycle-shutdown-v1")]
use windows_sys::Win32::Security::{
    CheckTokenMembership, CreateWellKnownSid, SECURITY_MAX_SID_SIZE, WELL_KNOWN_SID_TYPE,
    WinBuiltinAdministratorsSid,
};

#[cfg(all(test, feature = "installed-service-candidate"))]
use super::{Endpoint, probe_id, windows_control_session};
use crate::windows_protected_path::denied;
#[cfg(all(test, feature = "installed-service-candidate"))]
use tokio::time::timeout;
#[cfg(feature = "installed-service-candidate")]
use windows_sys::Win32::Foundation::ERROR_PIPE_BUSY;

#[path = "../../../pipe_primitives.rs"]
mod primitives;
use primitives::{LocalAllocation, check, descriptor, peer_token};

// PipeSecurityFactory.CreateForInteractiveUsers. PipeAccessRule adds Synchronize
// to allowed rules. The Users ACE excludes FILE_CREATE_PIPE_INSTANCE.
const FULL_CONTROL: u32 = 0x001f_019f;
const READ_WRITE_SYNC: u32 = 0x0012_019b;
const BUFFER: u32 = 8192;
#[cfg(feature = "installed-service-candidate")]
pub(super) const MAX_CONNECTIONS: usize = 14;
const RETRY: Duration = Duration::from_millis(20);

pub(super) struct NativeEndpoint {
    path: Vec<u16>,
    descriptor: LocalAllocation,
    // Keep the first instance connected to our parked client for the entire
    // endpoint lifetime. Each real exchange owns a separate handle, so a
    // canceled read or partial frame can never carry over to another caller.
    claim: Option<(OwnedHandle, OwnedHandle)>,
    server_session: u32,
    control_session: ControlSession,
    instances: u32,
}

impl NativeEndpoint {
    #[cfg(feature = "scratch-candidate-receipt")]
    pub(crate) fn open_scratch(id: &str) -> io::Result<(Self, u32)> {
        use windows_sys::Win32::System::RemoteDesktop::ProcessIdToSessionId;

        if !crate::service_frame::is_guid(id) {
            return Err(denied("invalid scratch readiness endpoint ID"));
        }
        let mut session = 0;
        // SAFETY: The PID is ours and the output is writable.
        check(unsafe { ProcessIdToSessionId(std::process::id(), &mut session) })?;
        let endpoint = Self::claim(
            &format!(r"\\.\pipe\OpenBlade.Rust.CandidateReceipt.Test.{id}"),
            descriptor("D:P(D;;0x1f019f;;;NU)(A;;0x1f019f;;;OW)")?,
            session,
            Rc::new(|| Ok(None)),
            2,
        )?;
        Ok((endpoint, session))
    }

    #[cfg(all(not(test), feature = "installed-service-candidate"))]
    pub(super) fn open_managed(
        path: &str,
        admitted_pid: u32,
        control_session: ControlSession,
    ) -> io::Result<Self> {
        if admitted_pid != std::process::id() {
            return Err(denied("installed listener requires the admitted process"));
        }
        // The exclusive parked instance is outside the connection budget.
        Self::claim(
            path,
            managed_descriptor()?,
            0,
            control_session,
            1 + MAX_CONNECTIONS as u32,
        )
    }

    fn claim(
        path: &str,
        descriptor: LocalAllocation,
        server_session: u32,
        control_session: ControlSession,
        instances: u32,
    ) -> io::Result<Self> {
        let mut endpoint = Self {
            path: path.encode_utf16().chain([0]).collect(),
            descriptor,
            claim: None,
            server_session,
            control_session,
            instances,
        };
        let server = endpoint.create(true)?;
        let client = endpoint.open_client()?;
        let pid = std::process::id();
        if caller_pid(&server)? != Some(pid) || server_pid(&client, server_session)? != pid {
            return Err(denied("foreign peer on the first installed pipe instance"));
        }
        endpoint.claim = Some((server, client));
        Ok(endpoint)
    }

    fn create(&self, first: bool) -> io::Result<OwnedHandle> {
        // SAFETY: the path and explicit protected descriptor are live for
        // CreateNamedPipeW. NOWAIT makes connect/read/write submit no pending I/O.
        owned(unsafe {
            CreateNamedPipeW(
                self.path.as_ptr(),
                PIPE_ACCESS_DUPLEX
                    | if first {
                        FILE_FLAG_FIRST_PIPE_INSTANCE
                    } else {
                        0
                    },
                PIPE_NOWAIT | PIPE_REJECT_REMOTE_CLIENTS,
                self.instances,
                BUFFER,
                BUFFER,
                0,
                &self.descriptor.security_attributes(),
            )
        })
    }

    fn open_client(&self) -> io::Result<OwnedHandle> {
        // SAFETY: the fixed local path is NUL-terminated. Identification is
        // sufficient to query evidence, and cannot delegate the probe's authority.
        let client = owned(unsafe {
            CreateFileW(
                self.path.as_ptr(),
                READ_WRITE_SYNC,
                0,
                null(),
                OPEN_EXISTING,
                SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION,
                null_mut(),
            )
        })?;
        // SAFETY: the handle is open and the mode pointer is valid for the call.
        check(unsafe {
            SetNamedPipeHandleState(client.as_raw_handle(), &PIPE_NOWAIT, null(), null())
        })?;
        Ok(client)
    }
}

#[cfg(feature = "installed-service-candidate")]
fn managed_descriptor() -> io::Result<LocalAllocation> {
    descriptor(&format!(
        "D:P(D;;0x{FULL_CONTROL:x};;;NU)(A;;0x{FULL_CONTROL:x};;;SY)(A;;0x{FULL_CONTROL:x};;;BA)(A;;0x{READ_WRITE_SYNC:x};;;BU)"
    ))
}

impl NativeEndpoint {
    fn verify_claim(&self) -> io::Result<()> {
        let (server, client) = self
            .claim
            .as_ref()
            .ok_or_else(|| denied("installed pipe retired"))?;
        let pid = std::process::id();
        if caller_pid(server)? != Some(pid) || server_pid(client, self.server_session)? != pid {
            return Err(denied("installed pipe claim lost"));
        }
        Ok(())
    }

    #[cfg(feature = "installed-service-candidate")]
    pub(super) fn verify_managed_listener(&self, expected_path: &str) -> io::Result<()> {
        self.verify_claim()?;
        if !self
            .path
            .iter()
            .copied()
            .eq(expected_path.encode_utf16().chain([0]))
        {
            return Err(denied("unexpected installed pipe path"));
        }
        Ok(())
    }

    pub(crate) fn accept(&self) -> Pending<'_, Box<dyn Stream>> {
        Box::pin(async {
            self.verify_claim()?;
            loop {
                let pipe = self.create(false)?;
                loop {
                    // SAFETY: this live server handle is in nonblocking mode.
                    match check(unsafe { ConnectNamedPipe(pipe.as_raw_handle(), null_mut()) }) {
                        Ok(()) => (),
                        Err(e) if code(&e) == Some(ERROR_PIPE_CONNECTED) => {
                            return Ok(Box::new(NativeStream::new(
                                pipe,
                                Side::Server(self.control_session.clone()),
                            )) as Box<dyn Stream>);
                        }
                        Err(e) if code(&e) == Some(ERROR_PIPE_LISTENING) => (),
                        Err(e) if code(&e) == Some(ERROR_NO_DATA) => break,
                        Err(e) => return Err(e),
                    }
                    sleep(RETRY).await;
                }
                // A client connected and left before accept. Never reuse it.
                drop(pipe);
                sleep(RETRY).await;
            }
        })
    }

    #[cfg(feature = "installed-service-candidate")]
    pub(super) fn connect_probe(&self) -> Pending<'_, Box<dyn Stream>> {
        Box::pin(async {
            self.verify_claim()?;
            loop {
                match self.open_client() {
                    Ok(pipe) => {
                        return Ok(Box::new(NativeStream::new(
                            pipe,
                            Side::Client(self.server_session),
                        )) as Box<dyn Stream>);
                    }
                    Err(e) if code(&e) == Some(ERROR_PIPE_BUSY) => sleep(RETRY).await,
                    Err(e) => return Err(e),
                }
            }
        })
    }

    pub(crate) fn retire(&mut self) -> Pending<'_, ()> {
        Box::pin(async {
            // Accept and exchange futures are already dropped by the driver.
            // No overlapped operations or background workers own these handles.
            drop(self.claim.take());
            Ok(())
        })
    }
}

fn owned(raw: HANDLE) -> io::Result<OwnedHandle> {
    if raw == INVALID_HANDLE_VALUE {
        return Err(io::Error::last_os_error());
    }
    // SAFETY: this is the successful result of a pipe create or open.
    Ok(unsafe { OwnedHandle::from_raw_handle(raw) })
}

fn code(error: &io::Error) -> Option<u32> {
    error.raw_os_error().map(|code| code as u32)
}

fn caller_pid(pipe: &OwnedHandle) -> io::Result<Option<u32>> {
    let mut pid = 0;
    // SAFETY: the handle is live and the PID output is writable.
    check(unsafe { GetNamedPipeClientProcessId(pipe.as_raw_handle(), &mut pid) })?;
    Ok((pid > 0 && pid <= i32::MAX as u32).then_some(pid))
}

fn server_pid(pipe: &OwnedHandle, expected_session: u32) -> io::Result<u32> {
    // SAFETY: the pipe is borrowed and PeekNamedPipe receives no buffers.
    unsafe {
        // The PID metadata alone need not establish that the peer is still live.
        check(PeekNamedPipe(
            pipe.as_raw_handle(),
            null_mut(),
            0,
            null_mut(),
            null_mut(),
            null_mut(),
        ))?;
    }
    let (pid, session) = crate::pipe_server_identity::server_identity(pipe.as_raw_handle())?;
    if session != expected_session {
        return Err(denied("unverified installed pipe server identity"));
    }
    Ok(pid)
}

enum Side {
    Server(ControlSession),
    Client(u32),
}

struct NativeStream {
    pipe: OwnedHandle,
    side: Side,
    read_bytes: usize,
    read_retry: Option<Pin<Box<Sleep>>>,
    write_retry: Option<Pin<Box<Sleep>>>,
}

impl NativeStream {
    fn new(pipe: OwnedHandle, side: Side) -> Self {
        Self {
            pipe,
            side,
            read_bytes: 0,
            read_retry: None,
            write_retry: None,
        }
    }

    fn wait(retry: &mut Option<Pin<Box<Sleep>>>, cx: &mut Context<'_>) -> Poll<()> {
        if let Some(timer) = retry {
            ready!(timer.as_mut().poll(cx));
            *retry = None;
        }
        Poll::Ready(())
    }

    fn retry<T>(retry: &mut Option<Pin<Box<Sleep>>>, cx: &mut Context<'_>) -> Poll<T> {
        let mut timer = Box::pin(sleep(RETRY));
        if timer.as_mut().poll(cx).is_ready() {
            cx.waker().wake_by_ref();
        }
        *retry = Some(timer);
        Poll::Pending
    }
}

impl tokio::io::AsyncRead for NativeStream {
    fn poll_read(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        ready!(Self::wait(&mut self.read_retry, cx));
        let target = buf.initialize_unfilled();
        let mut read = 0;
        // SAFETY: synchronous nonblocking read into initialized writable memory.
        let result = check(unsafe {
            ReadFile(
                self.pipe.as_raw_handle(),
                target.as_mut_ptr(),
                target.len().min(u32::MAX as usize) as u32,
                &mut read,
                null_mut(),
            )
        });
        match result {
            Ok(()) => {
                buf.advance(read as usize);
                self.read_bytes = self.read_bytes.saturating_add(read as usize);
                Poll::Ready(Ok(()))
            }
            Err(e) if code(&e) == Some(ERROR_NO_DATA) => Self::retry(&mut self.read_retry, cx),
            Err(e) if matches!(code(&e), Some(ERROR_BROKEN_PIPE | ERROR_PIPE_NOT_CONNECTED)) => {
                Poll::Ready(Ok(()))
            }
            Err(e) => Poll::Ready(Err(e)),
        }
    }
}

impl tokio::io::AsyncWrite for NativeStream {
    fn poll_write(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &[u8],
    ) -> Poll<io::Result<usize>> {
        ready!(Self::wait(&mut self.write_retry, cx));
        let mut written = 0;
        // SAFETY: synchronous nonblocking write reads only the provided bytes.
        let result = check(unsafe {
            WriteFile(
                self.pipe.as_raw_handle(),
                buf.as_ptr(),
                buf.len().min(u32::MAX as usize) as u32,
                &mut written,
                null_mut(),
            )
        });
        match result {
            Ok(()) if written == 0 && !buf.is_empty() => Self::retry(&mut self.write_retry, cx),
            Ok(()) => Poll::Ready(Ok(written as usize)),
            Err(e) => Poll::Ready(Err(e)),
        }
    }

    fn poll_flush(self: Pin<&mut Self>, _: &mut Context<'_>) -> Poll<io::Result<()>> {
        // WriteFile already transferred the bytes into the kernel pipe buffer.
        // FlushFileBuffers would wait for an untrusted client to read them.
        Poll::Ready(Ok(()))
    }

    fn poll_shutdown(self: Pin<&mut Self>, _: &mut Context<'_>) -> Poll<io::Result<()>> {
        // No half-close for this transport; the exchange closes the handle.
        Poll::Ready(Ok(()))
    }
}

impl Stream for NativeStream {
    #[cfg(feature = "service-thermal-mutation-v1")]
    fn user_sid_after_read(&self) -> io::Result<String> {
        self.caller_after_read()?;
        let token = peer_token(self.pipe.as_raw_handle())?;
        Ok(crate::windows_token::Identity::read(&token)?.sid)
    }

    #[cfg(feature = "service-lifecycle-shutdown-v1")]
    fn system_or_administrator_after_read(&self) -> bool {
        // Both answers impersonate the client again after its request was
        // read: SYSTEM from its token's user, as the caller is classified,
        // then the membership of a freshly opened token.
        system_or_administrator(
            self.caller_after_read()
                .map(|caller| caller.logon == Logon::System),
            || token_member(&peer_token(self.pipe.as_raw_handle())?, SHUTDOWN_ROLE),
        )
    }

    #[cfg(feature = "service-lifecycle-shutdown-v1")]
    fn peer_closed(&mut self) -> bool {
        // PeekNamedPipe reads the pipe's state now, where a read may still be
        // waiting out its retry. A state that cannot be read is closed.
        // SAFETY: the pipe is borrowed and PeekNamedPipe receives no buffers.
        unsafe {
            PeekNamedPipe(
                self.pipe.as_raw_handle(),
                null_mut(),
                0,
                null_mut(),
                null_mut(),
                null_mut(),
            ) == 0
        }
    }

    fn server_pid(&self) -> io::Result<u32> {
        let Side::Client(session) = self.side else {
            return Err(denied("server PID requires the client handle"));
        };
        server_pid(&self.pipe, session)
    }

    fn caller_after_read(&self) -> io::Result<Caller> {
        if !matches!(self.side, Side::Server(_)) || self.read_bytes <= 4 {
            return Err(denied(
                "caller evidence requires a request read on the server handle",
            ));
        }
        let token = peer_token(self.pipe.as_raw_handle())?;
        let (logon, session) = token_context(&token)?;
        let pid = caller_pid(&self.pipe)?;
        Ok(Caller {
            pid,
            logon,
            session,
            control_session: None,
        })
    }

    fn control_session(&self) -> io::Result<Option<u32>> {
        let Side::Server(control_session) = &self.side else {
            return Err(denied("session authority requires the server handle"));
        };
        control_session()
    }
}

/// A token's logon kind and session, as a request's caller is classified.
/// The lifecycle tick's console user is classified the same way
/// (`thermal_native::console_user`).
pub(super) fn token_context(token: &OwnedHandle) -> io::Result<(Logon, u32)> {
    use windows_sys::Win32::Security::TOKEN_INFORMATION_CLASS;
    fn query(
        token: &OwnedHandle,
        class: TOKEN_INFORMATION_CLASS,
        buf: &mut [usize],
    ) -> io::Result<usize> {
        let mut returned = 0;
        // SAFETY: the aligned buffer and size output are writable for the call.
        check(unsafe {
            GetTokenInformation(
                token.as_raw_handle(),
                class,
                buf.as_mut_ptr().cast(),
                size_of_val(buf) as u32,
                &mut returned,
            )
        })?;
        Ok(returned as usize)
    }

    let mut session = [0usize; 1];
    if query(token, TokenSessionId, &mut session)? != size_of::<u32>() {
        return Err(denied("unavailable installed caller session"));
    }
    let session = session[0] as u32;
    if session > i32::MAX as u32 {
        return Err(denied("invalid installed caller session"));
    }

    let mut user = [0usize; 128 / size_of::<usize>()];
    if query(token, TokenUser, &mut user)? < size_of::<TOKEN_USER>() {
        return Err(denied("unavailable installed caller SID"));
    }
    // SAFETY: TokenUser initialized the header and its SID in this allocation.
    let sid = unsafe { (*user.as_ptr().cast::<TOKEN_USER>()).User.Sid };
    if sid.is_null() {
        return Err(denied("missing installed caller SID"));
    }
    if unsafe { IsWellKnownSid(sid, WinLocalSystemSid) } != 0 {
        return Ok((Logon::System, session));
    }

    let mut length = 0;
    // SAFETY: the standard size query has no output buffer.
    let result = unsafe {
        GetTokenInformation(
            token.as_raw_handle(),
            TokenGroups,
            null_mut(),
            0,
            &mut length,
        )
    };
    if result != 0
        || code(&io::Error::last_os_error())
            != Some(windows_sys::Win32::Foundation::ERROR_INSUFFICIENT_BUFFER)
        || length == 0
        || length > 1024 * 1024
    {
        return Err(denied("unavailable installed caller token groups"));
    }
    let mut groups = vec![0usize; (length as usize).div_ceil(size_of::<usize>())];
    let used = query(token, TokenGroups, &mut groups)?;
    let start = offset_of!(TOKEN_GROUPS, Groups);
    if used < start {
        return Err(denied("invalid installed caller token groups"));
    }
    // SAFETY: TokenGroups returned a header, then a counted array of SID entries.
    let count = unsafe { *groups.as_ptr().cast::<u32>() } as usize;
    if count > (used - start) / size_of::<SID_AND_ATTRIBUTES>() {
        return Err(denied("truncated installed caller token groups"));
    }
    let entries = unsafe {
        std::slice::from_raw_parts(
            groups
                .as_ptr()
                .cast::<u8>()
                .add(start)
                .cast::<SID_AND_ATTRIBUTES>(),
            count,
        )
    };
    let mut interactive = false;
    let mut remote = false;
    for group in entries {
        if !enabled_group(group.Attributes) || group.Sid.is_null() {
            continue;
        }
        // SAFETY: the kernel populated each SID pointer in the live buffer.
        unsafe {
            interactive |= IsWellKnownSid(group.Sid, WinInteractiveSid) != 0;
            remote |= IsWellKnownSid(group.Sid, WinRemoteLogonIdSid) != 0;
        }
    }
    Ok((classify_logon(interactive, remote), session))
}

/// The group managed admits a shutdown from besides LocalSystem
/// (`WindowsBuiltInRole.Administrator`).
#[cfg(feature = "service-lifecycle-shutdown-v1")]
const SHUTDOWN_ROLE: WELL_KNOWN_SID_TYPE = WinBuiltinAdministratorsSid;

/// A well-known SID in an aligned buffer of `SECURITY_MAX_SID_SIZE` bytes.
#[cfg(feature = "service-lifecycle-shutdown-v1")]
type SidBuffer = [usize; (SECURITY_MAX_SID_SIZE as usize).div_ceil(size_of::<usize>())];

#[cfg(feature = "service-lifecycle-shutdown-v1")]
fn well_known_sid(kind: WELL_KNOWN_SID_TYPE) -> io::Result<SidBuffer> {
    let mut sid = SidBuffer::default();
    let mut size = size_of_val(&sid) as u32;
    // SAFETY: the buffer is writable for `size` bytes, and no domain is given.
    check(unsafe { CreateWellKnownSid(kind, null_mut(), sid.as_mut_ptr().cast(), &mut size) })?;
    Ok(sid)
}

/// Whether the impersonation `token` is a member of the well-known group, as
/// managed `WindowsPrincipal.IsInRole` asks `CheckTokenMembership`: an enabled
/// group that is not deny-only, which a restricted token's restricting SIDs
/// must also hold. A UAC-filtered token holds Administrators deny-only, so it
/// is no member. A primary token is refused.
#[cfg(feature = "service-lifecycle-shutdown-v1")]
fn token_member(token: &OwnedHandle, group: WELL_KNOWN_SID_TYPE) -> io::Result<bool> {
    let mut sid = well_known_sid(group)?;
    let mut member = 0;
    // SAFETY: the token is live, the SID initialized and the output writable.
    check(unsafe {
        CheckTokenMembership(token.as_raw_handle(), sid.as_mut_ptr().cast(), &mut member)
    })?;
    Ok(member != 0)
}

fn enabled_group(attributes: u32) -> bool {
    // WindowsIdentity.Groups includes enabled groups, excluding logon ID and
    // deny-only entries. The latter must never grant interactive admission.
    const ENABLED: u32 = 0x4;
    const DENY_ONLY: u32 = 0x10;
    const LOGON_ID: u32 = 0xc000_0000;
    attributes & (ENABLED | DENY_ONLY | LOGON_ID) == ENABLED
}

fn classify_logon(interactive: bool, remote: bool) -> Logon {
    match (interactive, remote) {
        (_, true) => Logon::RemoteInteractive,
        (true, false) => Logon::Interactive,
        _ => Logon::Other,
    }
}

#[cfg(all(test, feature = "installed-service-candidate"))]
pub(super) fn private_endpoint(
    control_session: impl FnOnce(u32) -> ControlSession,
) -> (impl Fn() -> io::Result<NativeEndpoint>, u32) {
    use windows_sys::Win32::System::RemoteDesktop::ProcessIdToSessionId;

    let path = format!(
        r"\\.\pipe\OpenBlade.Rust.Readiness.Test.{}",
        probe_id().unwrap()
    );
    let mut session = 0;
    // SAFETY: the PID is the current process and session is writable.
    check(unsafe { ProcessIdToSessionId(std::process::id(), &mut session) }).unwrap();
    let control_session = control_session(session);
    let open = move || {
        NativeEndpoint::claim(
            &path,
            descriptor("D:P(A;;0x1f019f;;;OW)")?,
            session,
            Rc::clone(&control_session),
            2,
        )
    };
    (open, session)
}

#[cfg(all(test, feature = "installed-service-candidate"))]
mod tests {
    use super::*;
    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    use windows_sys::Win32::Security::{
        ACCESS_ALLOWED_ACE, GetAce, GetSecurityDescriptorControl, GetSecurityDescriptorDacl,
        SE_DACL_PROTECTED, WinBuiltinAdministratorsSid, WinBuiltinUsersSid, WinNetworkSid,
    };

    #[test]
    fn managed_pipe_security_factory_parity() {
        // PipeSecurityFactoryTests: protected, explicit rules. The per-user
        // fixture also checks PipeAccessRule adds Synchronize to ReadWrite.
        let sd = managed_descriptor().unwrap();
        let mut control = 0;
        let mut revision = 0;
        let mut present = 0;
        let mut defaulted = 0;
        let mut acl = null_mut();
        // SAFETY: all descriptor data and output slots are live for the calls.
        unsafe {
            check(GetSecurityDescriptorControl(
                sd.0,
                &mut control,
                &mut revision,
            ))
            .unwrap();
            check(GetSecurityDescriptorDacl(
                sd.0,
                &mut present,
                &mut acl,
                &mut defaulted,
            ))
            .unwrap();
            assert_ne!(control & SE_DACL_PROTECTED, 0);
            assert_ne!(present, 0);
            assert_eq!(defaulted, 0);
            assert!(!acl.is_null());
            assert_eq!((*acl).AceCount, 4);
            for (index, (kind, mask, sid)) in [
                (1, 0x1f019f, WinNetworkSid),
                (0, 0x1f019f, WinLocalSystemSid),
                (0, 0x1f019f, WinBuiltinAdministratorsSid),
                (0, 0x12019b, WinBuiltinUsersSid),
            ]
            .into_iter()
            .enumerate()
            {
                let mut raw = null_mut();
                check(GetAce(acl, index as u32, &mut raw)).unwrap();
                // Both access ACE types have the same binary layout.
                let ace = raw.cast::<ACCESS_ALLOWED_ACE>();
                assert_eq!((*ace).Header.AceType as u32, kind);
                assert_eq!((*ace).Header.AceFlags, 0);
                assert_eq!((*ace).Mask, mask);
                assert_ne!(IsWellKnownSid((&raw mut (*ace).SidStart).cast(), sid), 0);
            }
        }
    }

    #[test]
    fn managed_request_access_policy_parity() {
        // ServiceRequestAccessPolicyTests: active local control, background and
        // remote status only, and non-interactive callers rejected.
        for (groups, session, control_session, access) in [
            ((true, false), 1, Some(1), Some(0)),
            ((true, false), 1, Some(2), Some(1)),
            ((true, false), 1, None, Some(1)),
            ((true, true), 1, Some(1), Some(1)),
            ((false, true), 1, None, Some(1)),
            ((false, false), 1, Some(1), None),
        ] {
            let caller = Caller {
                pid: None,
                logon: classify_logon(groups.0, groups.1),
                session,
                control_session,
            };
            assert_eq!(caller.access().ok(), access);
        }
        assert!(enabled_group(0x4));
        assert!(!enabled_group(0));
        assert!(!enabled_group(0x14));
        assert!(!enabled_group(0xc0000004));
    }

    #[tokio::test]
    async fn private_pipe_checks_same_handle_and_retires_canceled_accept() {
        timeout(Duration::from_secs(5), async {
            let pid = std::process::id();
            let (open, session) = private_endpoint(|_| windows_control_session());
            let mut endpoint = open().unwrap();
            assert!(open().is_err(), "duplicate first instance was admitted");
            let (server, client) =
                tokio::try_join!(endpoint.accept(), endpoint.connect_probe()).unwrap();
            let mut server = server;
            let mut client = client;
            assert_eq!(client.server_pid().unwrap(), pid);
            assert!(server.server_pid().is_err());
            assert!(client.caller_after_read().is_err());
            assert!(server.caller_after_read().is_err());

            client.write_all(b"probe").await.unwrap();
            let mut request = [0; 5];
            server.read_exact(&mut request).await.unwrap();
            assert_eq!(&request, b"probe");
            let caller = server.caller_after_read().unwrap();
            assert_eq!(caller.pid, Some(pid));
            assert_eq!(caller.session, session);
            let before = crate::console_session::observe();
            let control = server.control_session().unwrap();
            let after = crate::console_session::observe();
            if before == after {
                assert_eq!(control, before);
            }
            assert!(client.control_session().is_err());

            server.write_all(b"reply").await.unwrap();
            let mut reply = [0; 5];
            client.read_exact(&mut reply).await.unwrap();
            assert_eq!(&reply, b"reply");
            assert_eq!(client.server_pid().unwrap(), pid);
            server.write_all(b"last").await.unwrap();
            drop(server);
            // Ordinary status replies also have to survive server close
            // without waiting on an untrusted client's read.
            let mut last = [0; 4];
            client.read_exact(&mut last).await.unwrap();
            assert_eq!(&last, b"last");
            assert!(client.server_pid().is_err());
            drop(client);

            assert!(timeout(RETRY * 3, endpoint.accept()).await.is_err());
            endpoint.retire().await.unwrap();
            assert!(endpoint.listener().is_err());
            open().unwrap().retire().await.unwrap();
        })
        .await
        .unwrap();
    }

    /// The shutdown's admission over real tokens: membership is
    /// `CheckTokenMembership` of BUILTIN\Administrators, which a deny-only
    /// group and a restricted token's restricting SIDs refuse, and every
    /// token that cannot answer is no administrator.
    #[cfg(feature = "service-lifecycle-shutdown-v1")]
    mod shutdown_admission {
        use windows_sys::Win32::{
            Foundation::HANDLE,
            Security::{
                CreateRestrictedToken, DuplicateTokenEx, SecurityIdentification, TOKEN_DUPLICATE,
                TOKEN_QUERY, TokenImpersonation, WinBuiltinUsersSid, WinWorldSid,
            },
            System::Threading::{GetCurrentProcess, OpenProcessToken},
        };

        use super::*;

        fn owned(token: HANDLE) -> OwnedHandle {
            // SAFETY: each caller passes a token the call just returned.
            unsafe { OwnedHandle::from_raw_handle(token) }
        }
        /// This process's primary token.
        fn primary() -> OwnedHandle {
            let mut token = null_mut();
            // SAFETY: the pseudo-handle needs no close and the output is writable.
            check(unsafe {
                OpenProcessToken(
                    GetCurrentProcess(),
                    TOKEN_QUERY | TOKEN_DUPLICATE,
                    &mut token,
                )
            })
            .unwrap();
            owned(token)
        }
        /// An identification-level impersonation token of `token`, as a pipe
        /// server opens its client's.
        fn identification(token: &OwnedHandle) -> OwnedHandle {
            let mut duplicate = null_mut();
            // SAFETY: the token is live, no attributes are given and the output is writable.
            check(unsafe {
                DuplicateTokenEx(
                    token.as_raw_handle(),
                    TOKEN_QUERY,
                    null(),
                    SecurityIdentification,
                    TokenImpersonation,
                    &mut duplicate,
                )
            })
            .unwrap();
            owned(duplicate)
        }
        /// `token` with `disabled` made deny-only and `restricting` as its
        /// restricting SIDs, as an impersonation token.
        fn restricted(
            token: &OwnedHandle,
            disabled: &[WELL_KNOWN_SID_TYPE],
            restricting: &[WELL_KNOWN_SID_TYPE],
        ) -> OwnedHandle {
            let entries = |kinds: &[WELL_KNOWN_SID_TYPE]| -> Vec<SidBuffer> {
                kinds
                    .iter()
                    .map(|&kind| well_known_sid(kind).unwrap())
                    .collect()
            };
            let (mut disabled, mut restricting) = (entries(disabled), entries(restricting));
            let attributes = |sids: &mut [SidBuffer]| -> Vec<SID_AND_ATTRIBUTES> {
                sids.iter_mut()
                    .map(|sid| SID_AND_ATTRIBUTES {
                        Sid: sid.as_mut_ptr().cast(),
                        Attributes: 0,
                    })
                    .collect()
            };
            let (disabled, restricting) = (attributes(&mut disabled), attributes(&mut restricting));
            let mut created = null_mut();
            // SAFETY: every SID array entry points at a live buffer for the call.
            check(unsafe {
                CreateRestrictedToken(
                    token.as_raw_handle(),
                    0,
                    disabled.len() as u32,
                    disabled.as_ptr(),
                    0,
                    null(),
                    restricting.len() as u32,
                    restricting.as_ptr(),
                    &mut created,
                )
            })
            .unwrap();
            identification(&owned(created))
        }

        #[test]
        fn the_shutdown_role_is_builtin_administrators() {
            assert_eq!(SHUTDOWN_ROLE, WinBuiltinAdministratorsSid);
        }

        #[test]
        fn membership_is_check_token_membership_and_fails_closed() {
            let own = primary();
            let token = identification(&own);
            assert_eq!(token_member(&token, WinWorldSid).ok(), Some(true));
            assert_eq!(token_member(&token, WinBuiltinUsersSid).ok(), Some(true));
            // A deny-only group is no membership, as a UAC-filtered token
            // holds Administrators.
            let filtered = restricted(
                &own,
                &[WinBuiltinUsersSid, WinBuiltinAdministratorsSid],
                &[],
            );
            assert_eq!(
                token_member(&filtered, WinBuiltinUsersSid).ok(),
                Some(false)
            );
            assert_eq!(token_member(&filtered, SHUTDOWN_ROLE).ok(), Some(false));
            // A restricting SID binds too, though Users stays in the groups.
            let limited = restricted(&own, &[], &[WinWorldSid]);
            assert_eq!(token_member(&limited, WinWorldSid).ok(), Some(true));
            assert_eq!(token_member(&limited, WinBuiltinUsersSid).ok(), Some(false));
            // A primary token cannot answer, so it is no administrator.
            assert!(token_member(&own, WinWorldSid).is_err());
            assert!(!system_or_administrator(Ok(false), || {
                token_member(&own, SHUTDOWN_ROLE)
            }));
        }

        /// A pipe client's admission is read from its own token, and only
        /// after its request: before it, no evidence admits. The client is
        /// this process, so its membership is this process's.
        #[tokio::test]
        async fn admission_reads_the_clients_token_after_its_request() {
            timeout(Duration::from_secs(5), async {
                let (open, _) = private_endpoint(|_| windows_control_session());
                let mut endpoint = open().unwrap();
                let (mut server, mut client) =
                    tokio::try_join!(endpoint.accept(), endpoint.connect_probe()).unwrap();
                assert!(!server.system_or_administrator_after_read());
                assert!(!server.peer_closed());
                client.write_all(b"probe").await.unwrap();
                let mut request = [0; 5];
                server.read_exact(&mut request).await.unwrap();
                let expected =
                    token_member(&identification(&primary()), WinBuiltinAdministratorsSid).unwrap();
                assert_eq!(server.system_or_administrator_after_read(), expected);
                assert!(!server.peer_closed());
                drop(client);
                assert!(server.peer_closed());
                drop(server);
                endpoint.retire().await.unwrap();
            })
            .await
            .unwrap();
        }
    }
}
