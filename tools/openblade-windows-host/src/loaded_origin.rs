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
    sync::{Arc, Mutex},
};
use windows_sys::Win32::System::{
    LibraryLoader::{GetModuleFileNameW, LOAD_LIBRARY_SEARCH_SYSTEM32, SetDefaultDllDirectories},
    Services::{
        SERVICE_AUTO_START, SERVICE_RUNNING, SERVICE_START_PENDING, SERVICE_STOP_PENDING,
        SERVICE_WIN32_OWN_PROCESS,
    },
};

pub(crate) use crate::origin_lifetime as lifetime;

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
    lifetime: lifetime::Lifetime,
    pub(crate) lifecycle: lifetime::Lifecycle,
}
/// Origin admission takes no arguments: only the program name. A launch that
/// supplies any, including a restart registered with a command line, is denied.
pub(crate) fn arguments_admitted(arguments: impl IntoIterator<Item = std::ffi::OsString>) -> bool {
    arguments.into_iter().nth(1).is_none()
}

impl Origin {
    pub(crate) fn acquire(role: &'static str, compiled: &[u8]) -> io::Result<Arc<Self>> {
        // Static CRT removes the external VC runtime prerequisite. This bounded
        // initial recipe uses only inbox system imports; later optional backends
        // retain their own explicit module policy, with no global CIG policy.
        if unsafe { SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32) } == 0 {
            return Err(io::Error::last_os_error());
        }
        if !arguments_admitted(std::env::args_os()) {
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
            lifetime: lifetime::Lifetime::default(),
            lifecycle: lifetime::Lifecycle::default(),
        });
        value.verify()?;
        Ok(value)
    }
    pub(crate) fn verify(&self) -> io::Result<()> {
        self.verify_mode(false)
    }
    pub(crate) fn verify_owned_stop(&self) -> io::Result<()> {
        self.verify_mode(true)
    }
    /// The loaded origin's lifetime, without the package, process and SCM
    /// verification of `verify`: whether its trust is still live, which a
    /// revocation, a failed verification and Stop end at once. A stream frame
    /// checks it between two origin ticks (`authorize_original_frame`), whose
    /// `verify` covers the rest.
    #[cfg(feature = "service-lighting-v1")]
    pub(crate) fn lifetime(&self) -> &lifetime::Lifetime {
        &self.lifetime
    }
    fn verify_mode(&self, stop: bool) -> io::Result<()> {
        let check = || {
            if stop {
                self.lifetime.verify_owned_stop()
            } else {
                self.lifetime.verify()
            }
        };
        check()?;
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
        result.and_then(|()| check())
    }
    // Callback ingress and any failed/unknown observation latch before coalescing.
    // This makes no claim about transients never delivered or sampled.
    pub(crate) fn revoke(&self) {
        self.lifetime.lose_trust();
    }
    pub(crate) fn revoke_for_stop(&self) {
        self.lifetime.stop();
    }
    pub(crate) fn confirm_metadata_drain(&self) -> io::Result<()> {
        // Called only after the actual listener and metadata workers drained.
        // Revocation does not prevent cleanup through these original held guards.
        self.package
            .lock()
            .map_err(|_| deny())?
            .record_metadata_drain(self.pid, self.created)
    }
    #[cfg(feature = "service-thermal-mutation-v1")]
    pub(crate) fn mutation_artifact(&self) -> io::Result<String> {
        self.verify()?;
        let package = self.package.lock().map_err(|_| deny())?;
        let fields = crate::native_package::lines(
            package.identity("service"),
            "OpenBlade.NativeArtifact.v2",
            8,
        )?;
        if fields[7]
            != if cfg!(feature = "service-temporary-charge-v1") {
                "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1,service-temporary-charge-v1"
            } else if cfg!(feature = "service-battery-policy-v1") {
                "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2,service-battery-policy-v1"
            } else if cfg!(feature = "service-fan-mutation-v2") {
                "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1,service-fan-mutation-v2"
            } else {
                "private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1"
            }
        {
            return Err(deny());
        }
        Ok(fields[0].to_owned())
    }
    pub(crate) fn mailbox_mapping(&self) -> io::Result<Vec<u8>> {
        self.verify()?;
        self.package.lock().map_err(|_| deny())?.mailbox_mapping()
    }
    pub(crate) fn confirm_transport_drain(&self) -> io::Result<()> {
        self.package
            .lock()
            .map_err(|_| deny())?
            .record_transport_drain(self.pid, self.created)
    }
    pub(crate) fn owned_stop_committed(&self) -> io::Result<bool> {
        self.verify_owned_stop()?;
        Ok(self.package.lock().map_err(|_| deny())?.is_committed())
    }
    pub(crate) fn owned_stop_mapping(&self) -> io::Result<Vec<u8>> {
        self.verify_owned_stop()?;
        self.package.lock().map_err(|_| deny())?.mailbox_mapping()
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
