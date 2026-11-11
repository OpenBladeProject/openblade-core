//! Retained kernel process-instance metadata, NOT installed release or loaded-image proof.
//!
//! No PID/path reopen, authority epoch, or installed provider belongs here. An upstream
//! launch/package owner must supply the original handle under its own reviewed contract.
//! Process query rights do not grant TOKEN_QUERY on a separate LocalSystem token.
//! A future supplier must establish that access without weakening token checks or
//! silently elevating the Agent. Capture and service-peer verification remain unwired.
//! Pipe PID/session checks below are correlation only: historical NPFS creator-object
//! retention and a pre-capture PID-reuse premise have not been established here.
#![allow(unsafe_code, dead_code)] // Prerequisite plumbing; no installed supplier is wired.

use std::{
    io,
    mem::size_of_val,
    os::windows::io::{AsHandle, AsRawHandle, BorrowedHandle, FromRawHandle, OwnedHandle},
    ptr::null_mut,
};

use windows_sys::{
    Wdk::{
        Foundation::{NtQueryObject, ObjectBasicInformation},
        System::Threading::{NtQueryInformationProcess, ProcessSessionInformation},
    },
    Win32::{
        Foundation::{
            DuplicateHandle, FILETIME, HANDLE, INVALID_HANDLE_VALUE, WAIT_OBJECT_0, WAIT_TIMEOUT,
        },
        System::{
            Threading::{
                GetCurrentProcess, GetProcessId, GetProcessTimes, OpenProcess,
                PROCESS_QUERY_INFORMATION, PROCESS_SYNCHRONIZE, QueryFullProcessImageNameW,
                WaitForSingleObject,
            },
            WindowsProgramming::PUBLIC_OBJECT_BASIC_INFORMATION,
        },
    },
};

use crate::windows_token::{Identity, check, process_token_for};

const REQUIRED_ACCESS: u32 = PROCESS_QUERY_INFORMATION | PROCESS_SYNCHRONIZE;

#[derive(Debug)]
pub(crate) enum InstanceError {
    Exited,
    Denied(io::Error),
}

impl From<io::Error> for InstanceError {
    fn from(error: io::Error) -> Self {
        Self::Denied(error)
    }
}

type Result<T> = std::result::Result<T, InstanceError>;

#[derive(Debug, PartialEq, Eq)]
struct Snapshot {
    pid: u32,
    created: u64,
    // QueryFullProcessImageName is diagnostic/comparison metadata only. No file
    // is opened here, and these characters do not authenticate loaded bytes.
    image_path: Vec<u16>,
    process_session: u32,
    token_session: u32,
    sid: String,
    authentication: (i32, u32),
    primary: bool,
}

impl Snapshot {
    fn validate(&self) -> io::Result<()> {
        if self.pid == 0
            || self.created == 0
            || self.image_path.is_empty()
            || self.image_path.contains(&0)
            || self.sid.is_empty()
            || !self.primary
            || self.process_session != self.token_session
        {
            return Err(denied("invalid process-instance metadata"));
        }
        Ok(())
    }

    fn require_service(&self) -> io::Result<()> {
        self.validate()?;
        if self.sid != "S-1-5-18" || self.process_session != 0 || self.token_session != 0 {
            return Err(denied(
                "service process requires LocalSystem in session zero",
            ));
        }
        Ok(())
    }
}

#[derive(Default)]
enum State {
    #[default]
    Live,
    Exited,
    Denied,
}

impl State {
    fn require_live(&self) -> Result<()> {
        match self {
            Self::Live => Ok(()),
            Self::Exited => Err(InstanceError::Exited),
            Self::Denied => Err(denied("process-instance evidence was lost").into()),
        }
    }

    fn remember<T>(&mut self, result: Result<T>) -> Result<T> {
        match &result {
            Err(InstanceError::Exited) => *self = Self::Exited,
            Err(InstanceError::Denied(_)) => *self = Self::Denied,
            Ok(_) => {}
        }
        result
    }

    // Private seam shared by concrete acquisition/checks and pure tests. A failed
    // metadata lookup is still followed by a wait; only a signaled process proves
    // exit. Unknown/failed waits deny. Once lost, no closure is invoked again.
    fn bracket<T>(
        &mut self,
        mut wait: impl FnMut() -> Result<()>,
        read: impl FnOnce() -> Result<T>,
    ) -> Result<T> {
        self.require_live()?;
        self.remember(wait())?;
        let result = read();
        self.remember(wait())?;
        self.remember(result)
    }
}

pub(crate) struct RetainedProcessInstance {
    process: OwnedHandle,
    snapshot: Snapshot,
    state: State,
}

impl RetainedProcessInstance {
    pub(crate) fn capture_expected(
        pid: u32,
        created: u64,
        image: &std::path::Path,
        session: u32,
    ) -> io::Result<Self> {
        // Open exactly once, then retain and compare that original object. This
        // privileged helper preserves actual primary-token checks.
        let handle = unsafe { OpenProcess(REQUIRED_ACCESS, 0, pid) };
        if handle.is_null() {
            return Err(io::Error::last_os_error());
        }
        let handle = unsafe { OwnedHandle::from_raw_handle(handle) };
        let mut instance = Self::capture(handle.as_handle()).map_err(instance_io)?;
        use std::os::windows::ffi::OsStrExt;
        if instance.snapshot.pid != pid
            || instance.snapshot.created != created
            || instance.snapshot.image_path != image.as_os_str().encode_wide().collect::<Vec<_>>()
            || instance.snapshot.process_session != session
        {
            return Err(denied("original startup process differs"));
        }
        let token = process_token_for(instance.process.as_handle())?;
        let mut elevation = windows_sys::Win32::Security::TOKEN_ELEVATION::default();
        let mut returned = 0;
        unsafe {
            check(windows_sys::Win32::Security::GetTokenInformation(
                token.as_raw_handle(),
                windows_sys::Win32::Security::TokenElevation,
                (&mut elevation as *mut windows_sys::Win32::Security::TOKEN_ELEVATION).cast(),
                size_of_val(&elevation) as u32,
                &mut returned,
            ))?;
        }
        if instance.snapshot.sid != "S-1-5-18" && elevation.TokenIsElevated == 0 {
            return Err(denied("startup owner is not privileged"));
        }
        instance.check_live().map_err(instance_io)?;
        Ok(instance)
    }
    pub(crate) fn capture_self() -> io::Result<Self> {
        let handle = unsafe { OpenProcess(REQUIRED_ACCESS, 0, std::process::id()) };
        if handle.is_null() {
            return Err(io::Error::last_os_error());
        }
        let handle = unsafe { OwnedHandle::from_raw_handle(handle) };
        Self::capture(handle.as_handle()).map_err(instance_io)
    }
    pub(crate) fn creation(&self) -> u64 {
        self.snapshot.created
    }
    pub(crate) fn session(&self) -> u32 {
        self.snapshot.process_session
    }
    pub(crate) fn image(&self) -> std::path::PathBuf {
        use std::os::windows::ffi::OsStringExt;
        std::ffi::OsString::from_wide(&self.snapshot.image_path).into()
    }

    pub(crate) fn capture(original: BorrowedHandle<'_>) -> Result<Self> {
        // Validate the borrowed object's type and rights before DuplicateHandle:
        // duplication must not turn a restricted caller handle into added authority.
        let (process, snapshot) = capture_checked(
            || validate_original(original),
            || duplicate(original),
            |process| wait(process.as_raw_handle()),
            |process| snapshot(process.as_handle()).map_err(Into::into),
        )?;
        Ok(Self {
            process,
            snapshot,
            state: State::Live,
        })
    }

    pub(crate) fn check_live(&mut self) -> Result<()> {
        self.state.require_live()?;
        self.state
            .remember(check_snapshot(&self.process, &self.snapshot))
    }

    // This checks actual token and process evidence, not a caller-selected role.
    pub(crate) fn require_service(&mut self) -> Result<()> {
        self.check_live()?;
        let result = self.snapshot.require_service().map_err(Into::into);
        self.state.remember(result)
    }

    pub(crate) fn check_pipe_server(&mut self, pipe: BorrowedHandle<'_>) -> Result<()> {
        verify_service_peer(
            &mut self.state,
            &self.snapshot,
            || check_snapshot(&self.process, &self.snapshot),
            || crate::pipe_server_identity::server_identity(pipe.as_raw_handle()),
        )
    }
}

pub(crate) fn instance_io(error: InstanceError) -> io::Error {
    match error {
        InstanceError::Exited => io::ErrorKind::ConnectionAborted.into(),
        InstanceError::Denied(error) => error,
    }
}

fn check_snapshot(process: &OwnedHandle, expected: &Snapshot) -> Result<()> {
    State::Live.bracket(
        || wait(process.as_raw_handle()),
        || {
            let observed = snapshot(process.as_handle())?;
            compare_snapshot(expected, &observed)
        },
    )
}

fn compare_snapshot(expected: &Snapshot, observed: &Snapshot) -> Result<()> {
    observed.validate()?;
    if observed != expected {
        return Err(denied("retained process-instance metadata changed").into());
    }
    Ok(())
}

// Private seam; the concrete method supplies original-object checks and a query
// of this exact borrowed pipe. It never returns a reusable connection capability.
fn verify_service_peer(
    state: &mut State,
    expected: &Snapshot,
    mut check: impl FnMut() -> Result<()>,
    query: impl FnOnce() -> io::Result<(u32, u32)>,
) -> Result<()> {
    state.require_live()?;
    state.remember(check())?;
    state.remember(expected.require_service().map_err(Into::into))?;
    let peer = query();
    // Even a failed pipe query is followed by original-object readback. Only a
    // confirmed process exit is retirement; query/wait failures remain denial.
    state.remember(check())?;
    state.remember(
        peer.and_then(|peer| match_peer(expected, peer))
            .map_err(Into::into),
    )
}

// Generic only over newly acquired resources, so pure tests can observe Drop.
// Production acquisition above is concrete and cannot accept caller proof objects.
fn capture_checked<H>(
    validate: impl FnOnce() -> io::Result<()>,
    duplicate: impl FnOnce() -> io::Result<H>,
    mut wait: impl FnMut(&H) -> Result<()>,
    read: impl FnOnce(&H) -> Result<Snapshot>,
) -> Result<(H, Snapshot)> {
    validate()?;
    let owned = duplicate()?;
    let snapshot = State::Live.bracket(|| wait(&owned), || read(&owned))?;
    snapshot.validate()?;
    Ok((owned, snapshot))
}

fn match_peer(snapshot: &Snapshot, (pid, session): (u32, u32)) -> io::Result<()> {
    snapshot.require_service()?;
    if pid == 0 || pid != snapshot.pid || session != snapshot.process_session {
        return Err(denied(
            "connected pipe server differs from retained process",
        ));
    }
    Ok(())
}

fn validate_original(original: BorrowedHandle<'_>) -> io::Result<()> {
    let raw = original.as_raw_handle();
    if raw.is_null() || raw == INVALID_HANDLE_VALUE {
        return Err(denied("an original real process handle is required"));
    }
    let mut basic = PUBLIC_OBJECT_BASIC_INFORMATION::default();
    let mut returned = 0;
    // SAFETY: borrowed handle is not consumed; the aligned output has the documented
    // ObjectBasicInformation layout. No object-name buffer or variable pointer is used.
    ntcheck(unsafe {
        NtQueryObject(
            raw,
            ObjectBasicInformation,
            (&mut basic as *mut PUBLIC_OBJECT_BASIC_INFORMATION).cast(),
            size_of_val(&basic) as u32,
            &mut returned,
        )
    })?;
    if returned as usize != size_of_val(&basic) {
        return Err(denied("unknown handle access metadata"));
    }
    require_rights(basic.GrantedAccess)?;
    // GetProcessId validates the kernel object as a process before any wait (a wait
    // on an arbitrary event could consume its signal). It never opens that PID.
    process_id(raw)?;
    Ok(())
}

fn require_rights(granted: u32) -> io::Result<()> {
    if granted & REQUIRED_ACCESS != REQUIRED_ACCESS {
        return Err(denied(
            "original process handle lacks query or synchronize access",
        ));
    }
    Ok(())
}

fn duplicate(original: BorrowedHandle<'_>) -> io::Result<OwnedHandle> {
    let mut owned: HANDLE = null_mut();
    // SAFETY: the caller retains its original; no CLOSE_SOURCE or SAME_ACCESS flags.
    // The single noninheritable duplicate receives only the required query/wait rights.
    unsafe {
        check(DuplicateHandle(
            GetCurrentProcess(),
            original.as_raw_handle(),
            GetCurrentProcess(),
            &mut owned,
            REQUIRED_ACCESS,
            0,
            0,
        ))?;
        Ok(OwnedHandle::from_raw_handle(owned))
    }
}

fn process_id(process: HANDLE) -> io::Result<u32> {
    // SAFETY: kernel type validation of the borrowed/owned object, without reopening.
    let pid = unsafe { GetProcessId(process) };
    if pid == 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(pid)
}

fn snapshot(process: BorrowedHandle<'_>) -> io::Result<Snapshot> {
    let raw = process.as_raw_handle();
    let pid = process_id(raw)?;
    let mut created = FILETIME::default();
    let mut exit = FILETIME::default();
    let mut kernel = FILETIME::default();
    let mut user = FILETIME::default();
    let mut image_path = vec![0u16; 32768];
    let mut length = image_path.len() as u32;
    // ProcessSessionInformation is one ULONG SessionId, queried through this
    // original object, unlike ProcessIdToSessionId's PID-based lookup.
    let mut session = 0u32;
    let mut returned = 0;
    // SAFETY: all outputs are aligned, sized for their API class and scoped to
    // this call. The owned original process has QUERY_INFORMATION access.
    unsafe {
        check(GetProcessTimes(
            raw,
            &mut created,
            &mut exit,
            &mut kernel,
            &mut user,
        ))?;
        check(QueryFullProcessImageNameW(
            raw,
            0,
            image_path.as_mut_ptr(),
            &mut length,
        ))?;
        ntcheck(NtQueryInformationProcess(
            raw,
            ProcessSessionInformation,
            (&mut session as *mut u32).cast(),
            size_of_val(&session) as u32,
            &mut returned,
        ))?;
    }
    if length == 0
        || length as usize >= image_path.len()
        || returned as usize != size_of_val(&session)
    {
        return Err(denied("incomplete process metadata"));
    }
    image_path.truncate(length as usize);
    let token = process_token_for(process)?;
    let identity = Identity::read(&token)?;
    let snapshot = Snapshot {
        pid,
        created: ((created.dwHighDateTime as u64) << 32) | created.dwLowDateTime as u64,
        image_path,
        process_session: session,
        token_session: identity.session,
        sid: identity.sid,
        authentication: (
            identity.authentication.HighPart,
            identity.authentication.LowPart,
        ),
        primary: identity.primary,
    };
    snapshot.validate()?;
    Ok(snapshot)
}

fn wait(process: HANDLE) -> Result<()> {
    // SAFETY: only a validated retained process object is waited on; never blocks.
    classify_wait(unsafe { WaitForSingleObject(process, 0) })
}

fn classify_wait(result: u32) -> Result<()> {
    match result {
        WAIT_TIMEOUT => Ok(()),
        WAIT_OBJECT_0 => Err(InstanceError::Exited),
        // GetExitCodeProcess/STILL_ACTIVE is deliberately not used: 259 may be
        // an actual exit code. Failed or unknown waits never certify retirement.
        _ => Err(denied("original process wait failed or returned an unknown result").into()),
    }
}

fn ntcheck(status: i32) -> io::Result<()> {
    if status < 0 {
        Err(io::Error::other(format!(
            "native process query failed: NTSTATUS {status:#x}"
        )))
    } else {
        Ok(())
    }
}

fn denied(message: &'static str) -> io::Error {
    io::Error::new(io::ErrorKind::PermissionDenied, message)
}

#[cfg(test)]
#[path = "retained_process/tests.rs"]
mod tests;
