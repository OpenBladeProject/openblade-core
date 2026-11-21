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
        Services::{
            SERVICE_AUTO_START, SERVICE_RUNNING, SERVICE_START_PENDING, SERVICE_STOP_PENDING,
            SERVICE_STOPPED, SERVICE_WIN32_OWN_PROCESS,
        },
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
        super::verify_origin(&origin)?;
        if origin.session == 0 || !origin.committed().map_err(super::origin_error)? {
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
    }
}
impl WindowsInstalledServiceAuthority {
    fn acquire(&self) -> Result<Generation, ServiceAuthorityError> {
        super::verify_origin(&self.origin)?;
        // A stopped or changing Service is unavailable whatever the package
        // says, and reading the package re-hashes it, so classify it first.
        // Only a running Service goes on to the full validation below.
        let mut package = before_package(
            Registration::open("OpenBlade")
                .and_then(|registration| {
                    registration.query(std::ffi::OsStr::new(""), SERVICE_AUTO_START)
                })
                .map(|scm| scm.state),
            native_package::read_installed_package,
        )?;
        if package.agent_path != self.origin.image {
            return Err(ServiceAuthorityError::Denied(deny()));
        }
        let mut command = OsString::from("\"");
        command.push(&package.service_path);
        command.push("\"");
        let registration = Registration::open("OpenBlade")?;
        let first = registration.query(&command, SERVICE_AUTO_START)?;
        let pid = classify_service(first, None)?;
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
            return Err(ServiceAuthorityError::Denied(io::Error::last_os_error()));
        }
        let process = unsafe { OwnedHandle::from_raw_handle(raw) };
        wait(&process)?;
        let (actual, created, image) = metadata(&process)?;
        wait(&process)?;
        if actual != pid.get() || image != package.service_path || created == 0 {
            return Err(ServiceAuthorityError::Denied(deny()));
        }
        package.require_current()?;
        if classify_service(registration.query(&command, SERVICE_AUTO_START)?, Some(pid))? != pid {
            return Err(ServiceAuthorityError::Denied(deny()));
        }
        super::verify_origin(&self.origin)?;
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
        generation.verify_live_process()?;
        Ok(generation)
    }
}
/// The Service state decides whether the package is worth reading. A Service
/// that is starting, stopping or stopped grants no authority either way, and its
/// configuration is validated in full once it runs. An unreadable state falls
/// through to the full path, which reports the same failure.
fn before_package<T>(
    state: io::Result<u32>,
    read: impl FnOnce() -> io::Result<T>,
) -> Result<T, ServiceAuthorityError> {
    if matches!(
        state,
        Ok(SERVICE_START_PENDING | SERVICE_STOP_PENDING | SERVICE_STOPPED)
    ) {
        return Err(ServiceAuthorityError::Unavailable);
    }
    Ok(read()?)
}
impl InstalledServiceGeneration for Generation {
    fn verify_live_process(&self) -> Result<NonZeroU32, ServiceAuthorityError> {
        verify_retained(
            &self.state,
            || wait(&self.process),
            || {
                super::verify_origin(&self.origin)?;
                self.package.borrow_mut().require_current()?;
                let scm = self.registration.query(&self.command, SERVICE_AUTO_START)?;
                let availability = classify_service(scm, Some(self.pid));
                let (pid, created, image) = metadata(&self.process)?;
                if pid != self.pid.get() || created != self.created || image != self.image {
                    return Err(ServiceAuthorityError::Denied(deny()));
                }
                self.package.borrow_mut().require_current()?;
                availability
            },
        )
    }
}
// Origin/package/query/identity errors stay denied. Operational availability is
// classified only after the exact immutable configuration has been validated.
pub(super) fn classify_service(
    s: crate::scm_identity::ScmEvidence,
    original: Option<NonZeroU32>,
) -> Result<NonZeroU32, ServiceAuthorityError> {
    if !s.matches_config
        || s.service_type != SERVICE_WIN32_OWN_PROCESS
        || original.is_some_and(|pid| s.process_id != 0 && s.process_id != pid.get())
    {
        return Err(ServiceAuthorityError::Denied(deny()));
    }
    match s.state {
        SERVICE_RUNNING => {
            NonZeroU32::new(s.process_id).ok_or_else(|| ServiceAuthorityError::Denied(deny()))
        }
        SERVICE_START_PENDING | SERVICE_STOP_PENDING | SERVICE_STOPPED => {
            Err(ServiceAuthorityError::Unavailable)
        }
        _ => Err(ServiceAuthorityError::Denied(deny())),
    }
}
fn verify_retained<T>(
    state: &Cell<u8>,
    wait_original: impl Fn() -> Result<(), ServiceAuthorityError>,
    observe: impl FnOnce() -> Result<T, ServiceAuthorityError>,
) -> Result<T, ServiceAuthorityError> {
    match state.get() {
        1 => return Err(ServiceAuthorityError::Retired),
        2 => return Err(ServiceAuthorityError::Denied(deny())),
        _ => (),
    }
    let result = (|| {
        wait_original()?;
        let observation = observe();
        // A temporary StopPending still waits on THIS object. Never reopen here.
        // A real mismatch remains sticky even if exit is observed simultaneously.
        if matches!(&observation, Err(ServiceAuthorityError::Denied(_))) {
            return observation;
        }
        wait_original()?;
        observation
    })();
    match &result {
        Err(ServiceAuthorityError::Retired) => state.set(1),
        Err(ServiceAuthorityError::Denied(_)) => state.set(2),
        _ => (),
    }
    result
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
fn deny() -> io::Error {
    io::Error::new(
        io::ErrorKind::PermissionDenied,
        "readonly Service generation evidence lost or unavailable",
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn a_stopped_or_changing_service_is_unavailable_without_reading_the_package() {
        for state in [SERVICE_START_PENDING, SERVICE_STOP_PENDING, SERVICE_STOPPED] {
            let reads = Cell::new(0);
            let result = before_package(Ok(state), || {
                reads.set(reads.get() + 1);
                Ok(())
            });
            assert!(matches!(result, Err(ServiceAuthorityError::Unavailable)));
            assert_eq!(reads.get(), 0);
        }
    }

    #[test]
    fn a_running_or_unreadable_service_still_reads_and_validates_the_package() {
        for state in [
            Ok(SERVICE_RUNNING),
            Err(io::ErrorKind::PermissionDenied.into()),
        ] {
            let reads = Cell::new(0);
            let result = before_package(state, || {
                reads.set(reads.get() + 1);
                Ok(7)
            });
            assert!(matches!(result, Ok(7)));
            assert_eq!(reads.get(), 1);
        }
        // A package that cannot be read stays a denial, not an unavailable Service.
        let denied = before_package(Ok(SERVICE_RUNNING), || -> io::Result<()> {
            Err(io::ErrorKind::InvalidData.into())
        });
        assert!(matches!(denied, Err(ServiceAuthorityError::Denied(_))));
    }

    fn scm(state: u32, pid: u32, matches_config: bool) -> crate::scm_identity::ScmEvidence {
        crate::scm_identity::ScmEvidence {
            process_id: pid,
            service_type: SERVICE_WIN32_OWN_PROCESS,
            state,
            matches_config,
        }
    }
    #[test]
    fn initial_pending_stopped_and_restart_are_typed_unavailable() {
        for state in [SERVICE_START_PENDING, SERVICE_STOP_PENDING, SERVICE_STOPPED] {
            assert!(matches!(
                classify_service(scm(state, 0, true), None),
                Err(ServiceAuthorityError::Unavailable)
            ));
            assert!(matches!(
                classify_service(scm(state, 0, false), None),
                Err(ServiceAuthorityError::Denied(_))
            ));
        }
        assert_eq!(
            classify_service(scm(SERVICE_RUNNING, 17, true), None)
                .unwrap_or_else(|_| panic!())
                .get(),
            17
        );
        for (state, pid) in [(SERVICE_RUNNING, 0), (99, 17)] {
            assert!(matches!(
                classify_service(scm(state, pid, true), None),
                Err(ServiceAuthorityError::Denied(_))
            ));
        }
        let mut wrong_type = scm(SERVICE_STOPPED, 0, true);
        wrong_type.service_type = 0x20;
        assert!(matches!(
            classify_service(wrong_type, None),
            Err(ServiceAuthorityError::Denied(_))
        ));
        let query_error: Result<(), ServiceAuthorityError> =
            Err(io::Error::new(io::ErrorKind::NotFound, "SCM query failed").into());
        assert!(matches!(query_error, Err(ServiceAuthorityError::Denied(_))));
    }
    #[test]
    fn retained_stop_pending_waits_original_exit_without_latching_unavailability() {
        let state = Cell::new(0);
        let pid = NonZeroU32::new(17).unwrap();
        assert!(matches!(
            verify_retained(
                &state,
                || Ok(()),
                || classify_service(scm(SERVICE_STOP_PENDING, 17, true), Some(pid))
            ),
            Err(ServiceAuthorityError::Unavailable)
        ));
        assert_eq!(state.get(), 0);
        assert!(matches!(
            verify_retained(
                &state,
                || Err(ServiceAuthorityError::Retired),
                || -> Result<NonZeroU32, ServiceAuthorityError> { panic!("must not reopen") }
            ),
            Err(ServiceAuthorityError::Retired)
        ));
        assert_eq!(state.get(), 1);
        assert!(matches!(
            verify_retained(&state, || panic!("never rebind retired object"), || Ok(pid)),
            Err(ServiceAuthorityError::Retired)
        ));
    }
    #[test]
    fn retained_actual_mismatch_remains_denied_across_pending_running_and_exit() {
        let state = Cell::new(0);
        let pid = NonZeroU32::new(17).unwrap();
        assert!(matches!(
            verify_retained(
                &state,
                || Ok(()),
                || classify_service(scm(SERVICE_RUNNING, 18, true), Some(pid))
            ),
            Err(ServiceAuthorityError::Denied(_))
        ));
        assert_eq!(state.get(), 2);
        assert!(matches!(
            verify_retained(
                &state,
                || Err(ServiceAuthorityError::Retired),
                || classify_service(scm(SERVICE_RUNNING, 17, true), Some(pid))
            ),
            Err(ServiceAuthorityError::Denied(_))
        ));
    }
}
