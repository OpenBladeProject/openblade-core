//! Eager exact loaded-build binding under privileged installer/SCM custody.
//! The non-reused protected namespace and original process object are both
//! required. Public package evidence is never firmware permission.
#![allow(unsafe_code, dead_code)]
use crate::{
    native_package::{self, InstalledPackage},
    retained_process::{RetainedProcessInstance, instance_io},
};
use std::{
    io,
    path::PathBuf,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
};
use windows_sys::Win32::System::{
    LibraryLoader::{GetModuleFileNameW, LOAD_LIBRARY_SEARCH_SYSTEM32, SetDefaultDllDirectories},
    Services::{
        SERVICE_AUTO_START, SERVICE_RUNNING, SERVICE_START_PENDING, SERVICE_STOP_PENDING,
        SERVICE_WIN32_OWN_PROCESS,
    },
};

pub(crate) struct Origin {
    package: Mutex<InstalledPackage>,
    process: Mutex<RetainedProcessInstance>,
    pub(crate) image: PathBuf,
    pub(crate) service_path: PathBuf,
    pub(crate) pid: u32,
    pub(crate) created: u64,
    pub(crate) session: u32,
    pub(crate) epoch: String,
    pub(crate) installation: String,
    role: &'static str,
    lost: AtomicBool,
}
impl Origin {
    pub(crate) fn acquire(role: &'static str, compiled: &[u8]) -> io::Result<Arc<Self>> {
        // Static CRT removes the external VC runtime prerequisite. This bounded
        // initial recipe uses only inbox system imports; later optional backends
        // retain their own explicit module policy, with no global CIG policy.
        if unsafe { SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32) } == 0 {
            return Err(io::Error::last_os_error());
        }
        if std::env::args_os().nth(1).is_some() {
            return Err(deny());
        }
        let mut process = RetainedProcessInstance::capture_self()?;
        if role == "service" {
            process.require_service().map_err(instance_io)?;
        } else if role != "agent" || process.session() == 0 {
            return Err(deny());
        }
        let mut path = vec![0u16; 32768];
        let n = unsafe {
            GetModuleFileNameW(std::ptr::null_mut(), path.as_mut_ptr(), path.len() as u32)
        } as usize;
        if n == 0 || n >= path.len() {
            return Err(deny());
        }
        use std::os::windows::ffi::OsStringExt;
        let image: PathBuf = std::ffi::OsString::from_wide(&path[..n]).into();
        if image != process.image() {
            return Err(deny());
        }
        let mut package = if role == "service" {
            native_package::read_startup_package()?
        } else {
            native_package::read_installed_package()?
        };
        let expected = if role == "service" {
            &package.service_path
        } else {
            &package.agent_path
        };
        if &image != expected || compiled != package.identity(role) {
            return Err(deny());
        }
        package.require_current()?;
        if role == "service" {
            package.begin_service_attempt(
                std::process::id(),
                process.creation(),
                process.session(),
                &image,
            )?;
        }
        let value = Arc::new(Self {
            service_path: package.service_path.clone(),
            pid: std::process::id(),
            created: process.creation(),
            session: process.session(),
            epoch: package.epoch_id().to_owned(),
            installation: package.installation().to_owned(),
            package: Mutex::new(package),
            process: Mutex::new(process),
            image,
            role,
            lost: AtomicBool::new(false),
        });
        value.verify()?;
        Ok(value)
    }
    pub(crate) fn verify(&self) -> io::Result<()> {
        if self.lost.load(Ordering::Acquire) {
            return Err(deny());
        }
        let result = (|| {
            self.package.lock().map_err(|_| deny())?.require_current()?;
            let mut process = self.process.lock().map_err(|_| deny())?;
            process.check_live().map_err(instance_io)?;
            if self.role == "service" {
                process.require_service().map_err(instance_io)?;
                let mut command = std::ffi::OsString::from("\"");
                command.push(&self.image);
                command.push("\"");
                let scm = crate::scm_identity::query_registration(
                    "OpenBlade",
                    &command,
                    SERVICE_AUTO_START,
                )?;
                if !scm.matches_config
                    || scm.service_type != SERVICE_WIN32_OWN_PROCESS
                    || !matches!(
                        scm.state,
                        SERVICE_START_PENDING | SERVICE_RUNNING | SERVICE_STOP_PENDING
                    )
                    || !(scm.process_id == self.pid
                        || scm.state == SERVICE_START_PENDING && scm.process_id == 0)
                {
                    return Err(deny());
                }
            }
            Ok(())
        })();
        if result.is_err() {
            self.revoke();
        }
        result
    }
    // Callback ingress and any failed/unknown observation latch before coalescing.
    // This makes no claim about transients never delivered or sampled.
    pub(crate) fn revoke(&self) {
        self.lost.store(true, Ordering::Release);
    }
    pub(crate) fn confirm_metadata_drain(&self) -> io::Result<()> {
        // Called only after the actual listener and metadata workers drained.
        // Revocation does not prevent cleanup through these original held guards.
        self.package
            .lock()
            .map_err(|_| deny())?
            .record_metadata_drain(self.pid, self.created)
    }
    pub(crate) fn committed(&self) -> io::Result<bool> {
        self.verify()?;
        Ok(self.package.lock().map_err(|_| deny())?.is_committed())
    }
}
fn deny() -> io::Error {
    io::Error::new(
        io::ErrorKind::PermissionDenied,
        "loaded native origin was lost or differs from the selected generation",
    )
}
