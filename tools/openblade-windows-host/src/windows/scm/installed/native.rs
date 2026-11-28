#![allow(unsafe_code)]

use windows_sys::Win32::System::{
    RemoteDesktop::ProcessIdToSessionId, Services::SERVICE_AUTO_START,
    Threading::GetCurrentProcessId,
};

use super::readiness::prepare_installed_endpoint;
use crate::loaded_origin::Origin;
use std::sync::{Arc, Mutex};
static ENTRY_ORIGIN: Mutex<Option<Arc<Origin>>> = Mutex::new(None);
use super::{LaunchIdentity, NAME, ProcessEvidence, denied, dispatch_candidate, io, run_candidate};
use crate::scm_host::{CONTROLS, dispatch_named, registered_service, report};
use crate::scm_identity as identity;
use crate::service_install_identity::program_files;

pub(crate) fn entry(origin: Arc<Origin>) -> io::Result<()> {
    let mut slot = ENTRY_ORIGIN
        .lock()
        .map_err(|_| denied("entry origin lock lost"))?;
    if slot.is_some() {
        return Err(denied("entry origin cannot be replaced"));
    }
    *slot = Some(origin);
    drop(slot);
    crate::scm_host::set_ingress(|code, event, setting| {
        let slot = ENTRY_ORIGIN.lock().ok()?;
        let origin = slot.as_ref()?;
        // Managed samples the Windows power kind at every control capture;
        // a power notice is native's capture between requests, so an
        // unplug and replug between them still advances the generation.
        #[cfg(feature = "service-thermal-mutation-v1")]
        if code == windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT {
            origin
                .lifecycle
                .observe_power_kind(crate::origin_lifetime::sample_power_kind());
        }
        // A suspend hold is returned and waited on by `scm_host::control`
        // after this slot's lock is released.
        crate::scm_host::installed_notice(&origin.lifecycle, code, event, setting, || {
            origin.revoke_for_stop()
        })
    })?;
    dispatch_candidate(process_evidence, || dispatch_named(NAME, service_main))
}

unsafe extern "system" fn service_main(argc: u32, argv: *mut *mut u16) {
    // SAFETY: SCM supplied this argv for the duration of this callback.
    if !unsafe { identity::callback_matches(NAME, argc, argv) } {
        // Own-process SCM dispatch can occur under another registration.
        std::process::exit(1);
    }
    if std::panic::catch_unwind(service).is_err() {
        std::process::abort();
    }
}

fn service() {
    registered_service(NAME, |runtime, handle, stop| {
        // Power-setting changes arrive at the SCM handler, which forwards
        // them to the lifecycle. The supplier registers once RUNNING is
        // reported and unregisters before STOPPED is.
        #[cfg(feature = "service-thermal-mutation-v1")]
        let mut notices = super::power_notifications::Supplier::new(
            super::power_notifications::native::ServiceHandle(handle),
        );
        let _ = runtime.block_on(run_candidate(
            &CONTROLS,
            stop,
            process_evidence,
            |launch| {
                identity::query_registration(NAME, &launch.image_command(), SERVICE_AUTO_START)
            },
            admit_candidate,
            prepare_installed_endpoint,
            |status| {
                #[cfg(feature = "service-thermal-mutation-v1")]
                return notices.report(status.dwCurrentState, || report(handle, status));
                #[cfg(not(feature = "service-thermal-mutation-v1"))]
                report(handle, status)
            },
        ));
        // The Service has no native diagnostics sink yet. Managed's matching
        // cleanup exception also escapes only after SERVICE_STOPPED, and the
        // process exit releases any registration that failed to unregister.
        #[cfg(feature = "service-thermal-mutation-v1")]
        let _ = notices.take_cleanup_failure();
    });
}

fn process_evidence() -> io::Result<ProcessEvidence> {
    // SAFETY: The current PID call takes no inputs, and session is writable.
    let pid = unsafe { GetCurrentProcessId() };
    let mut session = 0;
    if unsafe { ProcessIdToSessionId(pid, &mut session) } == 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(ProcessEvidence {
        has_arguments: std::env::args_os().nth(1).is_some(),
        program_files: program_files()?,
        selected_image: Some(
            ENTRY_ORIGIN
                .lock()
                .map_err(|_| denied("entry origin lost"))?
                .as_ref()
                .ok_or_else(|| denied("eager origin missing"))?
                .image
                .clone(),
        ),
        image: std::env::current_exe()?,
        pid,
        local_system: identity::is_local_system()?,
        session,
    })
}

pub(super) struct AdmittedRuntime {
    origin: Arc<Origin>,
}
impl AdmittedRuntime {
    pub(super) fn verify_launch(self, launch: &LaunchIdentity) -> io::Result<Arc<Origin>> {
        self.origin.verify()?;
        if self.origin.pid != launch.pid || self.origin.image != launch.image {
            return Err(denied("installed origin differs"));
        }
        Ok(self.origin)
    }
}
fn admit_candidate(launch: &LaunchIdentity) -> io::Result<AdmittedRuntime> {
    let origin = ENTRY_ORIGIN
        .lock()
        .map_err(|_| denied("entry origin lost"))?
        .as_ref()
        .ok_or_else(|| denied("eager origin unavailable"))?
        .clone();
    origin.verify()?;
    if origin.pid != launch.pid {
        return Err(denied("origin belongs to another process"));
    }
    // Firmware remains explicitly denied: no full adapter/activity supplier or
    // coordinated managed drain handoff exists. Metadata never calls the HID worker.
    Ok(AdmittedRuntime { origin })
}
