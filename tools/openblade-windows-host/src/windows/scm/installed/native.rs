#![allow(unsafe_code)]

use std::{ffi::OsString, os::windows::ffi::OsStringExt, path::PathBuf, ptr::null_mut, slice};

use windows_sys::Win32::{
    System::{
        Com::CoTaskMemFree, RemoteDesktop::ProcessIdToSessionId, Services::SERVICE_AUTO_START,
        Threading::GetCurrentProcessId,
    },
    UI::Shell::{FOLDERID_ProgramFiles, KF_FLAG_DONT_VERIFY, SHGetKnownFolderPath},
};

use super::readiness::prepare_installed_endpoint;
use super::{LaunchIdentity, NAME, ProcessEvidence, denied, dispatch_candidate, io, run_candidate};
use crate::scm_host::{CONTROLS, dispatch_named, registered_service, report};
use crate::scm_identity as identity;

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

fn program_files() -> io::Result<PathBuf> {
    let mut path = null_mut();
    // SAFETY: Windows supplies a task-allocated NUL-terminated path on success.
    // DONT_VERIFY resolves the known folder without opening it.
    let result = unsafe {
        SHGetKnownFolderPath(
            &FOLDERID_ProgramFiles,
            KF_FLAG_DONT_VERIFY as u32,
            null_mut(),
            &mut path,
        )
    };
    let folder = if result >= 0 && !path.is_null() {
        let mut len = 0;
        // SAFETY: A successful known-folder query returned a live UTF-16 string.
        while unsafe { *path.add(len) } != 0 {
            len += 1;
        }
        // SAFETY: len excludes the terminator and the memory is still owned here.
        Ok(PathBuf::from(OsString::from_wide(unsafe {
            slice::from_raw_parts(path, len)
        })))
    } else {
        Err(denied("Program Files known folder is unavailable"))
    };
    // SAFETY: Windows requires this allocation to be freed, including on failure.
    unsafe { CoTaskMemFree(path.cast()) };
    folder
}

fn admit_candidate(_: &LaunchIdentity) -> io::Result<()> {
    // Integration seam for independent installed image digest and protected
    // path admission, plus the installed controller/ownership prerequisites.
    // The signed development ZIP proves none of these for this process.
    Err(denied("installed candidate prerequisites are unavailable"))
}
