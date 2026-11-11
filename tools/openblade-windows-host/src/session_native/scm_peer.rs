//! Standard-user readonly SCM/TCB-bound Service generation. Never firmware Authority.
#![allow(unsafe_code)]
use super::service_client::{
    InstalledServiceAuthority, InstalledServiceGeneration, ServiceAuthorityError,
};
use crate::{
    loaded_origin::Origin,
    native_package::{self, InstalledPackage},
    scm_identity::Registration,
};
use std::{
    cell::{Cell, RefCell},
    ffi::OsString,
    io,
    num::NonZeroU32,
    os::windows::{
        ffi::OsStringExt,
        io::{AsRawHandle, FromRawHandle, OwnedHandle},
    },
    sync::Arc,
};
use windows_sys::Win32::{
    Foundation::{FILETIME, WAIT_OBJECT_0, WAIT_TIMEOUT},
    System::{
        Services::{SERVICE_AUTO_START, SERVICE_RUNNING, SERVICE_WIN32_OWN_PROCESS},
        Threading::{
            GetProcessId, GetProcessTimes, OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION,
            PROCESS_SYNCHRONIZE, QueryFullProcessImageNameW, WaitForSingleObject,
        },
    },
};

pub(super) struct WindowsInstalledServiceAuthority {
    origin: Arc<Origin>,
}
impl WindowsInstalledServiceAuthority {
    pub(super) fn from_agent(origin: Arc<Origin>) -> io::Result<Self> {
        origin.verify()?;
        if origin.session == 0 || !origin.committed()? {
            return Err(deny());
        }
        Ok(Self { origin })
    }
}
struct Generation {
    origin: Arc<Origin>,
    package: RefCell<InstalledPackage>,
    registration: Registration,
    process: OwnedHandle,
    pid: NonZeroU32,
    created: u64,
    image: std::path::PathBuf,
    command: OsString,
    state: Cell<u8>,
}
impl InstalledServiceAuthority for WindowsInstalledServiceAuthority {
    fn acquire_generation(
        &self,
    ) -> Result<Box<dyn InstalledServiceGeneration + '_>, ServiceAuthorityError> {
        self.acquire()
            .map(|g| Box::new(g) as Box<dyn InstalledServiceGeneration>)
            .map_err(ServiceAuthorityError::Denied)
    }
}
impl WindowsInstalledServiceAuthority {
    fn acquire(&self) -> io::Result<Generation> {
        self.origin.verify()?;
        let mut package = native_package::read_installed_package()?;
        if package.agent_path != self.origin.image {
            return Err(deny());
        }
        let command = OsString::from(format!("\"{}\"", package.service_path.display()));
        let registration = Registration::open("OpenBlade")?;
        let first = registration.query(&command, SERVICE_AUTO_START)?;
        let pid = require_running(first)?;
        // The one and only PID open. Inheritance is false and no token rights
        // or process QUERY_INFORMATION are requested by this readonly supplier.
        let raw = unsafe {
            OpenProcess(
                PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SYNCHRONIZE,
                0,
                pid.get(),
            )
        };
        if raw.is_null() {
            return Err(io::Error::last_os_error());
        }
        let process = unsafe { OwnedHandle::from_raw_handle(raw) };
        wait(&process).map_err(as_io)?;
        let (actual, created, image) = metadata(&process)?;
        wait(&process).map_err(as_io)?;
        if actual != pid.get() || image != package.service_path || created == 0 {
            return Err(deny());
        }
        package.require_current()?;
        if require_running(registration.query(&command, SERVICE_AUTO_START)?)? != pid {
            return Err(deny());
        }
        self.origin.verify()?;
        let generation = Generation {
            origin: self.origin.clone(),
            package: RefCell::new(package),
            registration,
            process,
            pid,
            created,
            image,
            command,
            state: Cell::new(0),
        };
        generation.verify_live_process().map_err(as_io)?;
        Ok(generation)
    }
}
impl InstalledServiceGeneration for Generation {
    fn verify_live_process(&self) -> Result<NonZeroU32, ServiceAuthorityError> {
        match self.state.get() {
            1 => return Err(ServiceAuthorityError::Retired),
            2 => return Err(ServiceAuthorityError::Denied(deny())),
            _ => (),
        }
        let result = (|| {
            wait(&self.process)?;
            let observation = (|| {
                self.origin.verify()?;
                self.package.borrow_mut().require_current()?;
                if require_running(self.registration.query(&self.command, SERVICE_AUTO_START)?)?
                    != self.pid
                {
                    return Err(deny());
                }
                let (pid, created, image) = metadata(&self.process)?;
                if pid != self.pid.get() || created != self.created || image != self.image {
                    return Err(deny());
                }
                self.package.borrow_mut().require_current()?;
                Ok(self.pid)
            })();
            wait(&self.process)?;
            observation.map_err(ServiceAuthorityError::Denied)
        })();
        if let Err(error) = &result {
            self.state
                .set(if matches!(error, ServiceAuthorityError::Retired) {
                    1
                } else {
                    2
                });
        }
        result
    }
}
fn require_running(s: crate::scm_identity::ScmEvidence) -> io::Result<NonZeroU32> {
    if !s.matches_config
        || s.service_type != SERVICE_WIN32_OWN_PROCESS
        || s.state != SERVICE_RUNNING
    {
        return Err(deny());
    }
    NonZeroU32::new(s.process_id).ok_or_else(deny)
}
fn wait(process: &OwnedHandle) -> Result<(), ServiceAuthorityError> {
    match unsafe { WaitForSingleObject(process.as_raw_handle(), 0) } {
        WAIT_TIMEOUT => Ok(()),
        WAIT_OBJECT_0 => Err(ServiceAuthorityError::Retired),
        _ => Err(ServiceAuthorityError::Denied(deny())),
    }
}
fn metadata(process: &OwnedHandle) -> io::Result<(u32, u64, std::path::PathBuf)> {
    let mut created = FILETIME::default();
    let mut exit = FILETIME::default();
    let mut kernel = FILETIME::default();
    let mut user = FILETIME::default();
    let mut text = vec![0u16; 32768];
    let mut n = text.len() as u32;
    let pid = unsafe { GetProcessId(process.as_raw_handle()) };
    if pid == 0
        || unsafe {
            GetProcessTimes(
                process.as_raw_handle(),
                &mut created,
                &mut exit,
                &mut kernel,
                &mut user,
            )
        } == 0
        || unsafe {
            QueryFullProcessImageNameW(process.as_raw_handle(), 0, text.as_mut_ptr(), &mut n)
        } == 0
    {
        return Err(io::Error::last_os_error());
    }
    if n == 0 || n as usize >= text.len() {
        return Err(deny());
    }
    Ok((
        pid,
        (created.dwHighDateTime as u64) << 32 | created.dwLowDateTime as u64,
        OsString::from_wide(&text[..n as usize]).into(),
    ))
}
fn as_io(e: ServiceAuthorityError) -> io::Error {
    match e {
        ServiceAuthorityError::Retired => io::ErrorKind::ConnectionAborted.into(),
        ServiceAuthorityError::Unavailable => io::ErrorKind::WouldBlock.into(),
        ServiceAuthorityError::Denied(e) => e,
    }
}
fn deny() -> io::Error {
    io::Error::new(
        io::ErrorKind::PermissionDenied,
        "readonly Service generation evidence lost or unavailable",
    )
}
