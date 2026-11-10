#![allow(unsafe_code)]

use windows_sys::Win32::System::{
    RemoteDesktop::ProcessIdToSessionId, Services::SERVICE_AUTO_START,
    Threading::GetCurrentProcessId,
};

use super::readiness::{ControlSession, prepare_installed_endpoint};
use super::{LaunchIdentity, NAME, ProcessEvidence, denied, dispatch_candidate, io, run_candidate};
use crate::scm_host::{CONTROLS, dispatch_named, registered_service, report};
use crate::scm_identity as identity;
use crate::service_install_identity::program_files;

pub(crate) fn entry() -> io::Result<()> {
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
        let _ = runtime.block_on(run_candidate(
            &CONTROLS,
            stop,
            process_evidence,
            |launch| {
                identity::query_registration(NAME, &launch.image_command(), SERVICE_AUTO_START)
            },
            admit_candidate,
            prepare_installed_endpoint,
            |status| report(handle, status),
        ));
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
        image: std::env::current_exe()?,
        pid,
        local_system: identity::is_local_system()?,
        session,
    })
}

pub(super) struct AdmittedRuntime {
    // The admission provider must bind this authority to the admitted launch.
    control_session: ControlSession,
}

impl AdmittedRuntime {
    pub(super) fn into_control_session(self) -> ControlSession {
        self.control_session
    }
}

fn admit_candidate(_: &LaunchIdentity) -> io::Result<AdmittedRuntime> {
    // service_image::ProtectedServiceImage needs an authenticated expected
    // build digest. This entry has no provider for one. Startup also needs
    // independent proof that the retained staged generation is executing.
    // A trusted control-session provider is also required for caller access.
    Err(denied(
        "installed build, loaded generation or control-session evidence is unavailable",
    ))
}
