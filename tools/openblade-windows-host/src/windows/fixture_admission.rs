//! Admission for the fixed Windows VM fixture only.
#![allow(unsafe_code)]

use openblade_windows_host::configuration_writer::{
    fixture::VM_DATA_ROOT, protected::ProtectedStorage,
};
use std::{env, ffi::OsStr, fs::File, io, path::Path};
use windows_sys::Win32::System::{
    Services::{SERVICE_DEMAND_START, SERVICE_START_PENDING, SERVICE_WIN32_OWN_PROCESS},
    Threading::GetCurrentProcessId,
};

use crate::scm_identity as identity;
#[cfg(test)]
use identity::config_string_matches;

pub(super) const NAME: &str = "OpenBladeRustPhase5jVm";
pub(super) const ROOT: &str = r"C:\OpenBladeRustPhase5jVm";
pub(super) const IMAGE: &str = r"C:\OpenBladeRustPhase5jVm\OpenBladeRustPhase5jVm.exe";
const IMAGE_NAME: &str = "OpenBladeRustPhase5jVm.exe";
pub(super) const START_MARKER: &str = "start.release";
pub(super) const RENDERER_MARKER: &str = "renderer.release";

pub(super) fn require_process() -> io::Result<()> {
    if env::args_os().nth(1).is_some() || env::current_exe()?.as_os_str() != OsStr::new(IMAGE) {
        return Err(denied(
            "unexpected VM fixture executable or process arguments",
        ));
    }

    if !identity::is_local_system()? {
        return Err(denied("VM fixture requires a LocalSystem process"));
    }
    Ok(())
}

/// # Safety
/// argc and argv must come from the live ServiceMain callback.
pub(super) unsafe fn callback_matches(argc: u32, argv: *mut *mut u16) -> bool {
    // SAFETY: The caller supplies live SCM arguments.
    unsafe { identity::callback_matches(NAME, argc, argv) }
}

pub(super) struct FixtureAdmission {
    root: ProtectedStorage,
    _image: File,
}

impl FixtureAdmission {
    pub(super) fn marker(&self, name: &str) -> io::Result<bool> {
        if !marker_allowed(name) {
            return Err(denied("unknown VM fixture marker"));
        }
        let Some(marker) = self.root.open_vm_file(name)? else {
            return Ok(false);
        };
        if marker.metadata()?.len() != 0 {
            return Err(denied("VM fixture marker must be empty"));
        }
        Ok(true)
    }
}

fn marker_allowed(name: &str) -> bool {
    matches!(name, START_MARKER | RENDERER_MARKER)
}

/// Call only from the admitted SCM callback after reporting START_PENDING.
pub(super) fn admit() -> io::Result<(FixtureAdmission, ProtectedStorage)> {
    require_process()?;
    require_registration()?;
    let root = ProtectedStorage::open_vm_root(Path::new(ROOT))?;
    let image = root
        .open_vm_file(IMAGE_NAME)?
        .ok_or_else(|| denied("missing protected VM fixture image"))?;
    let data = ProtectedStorage::open_vm_root(Path::new(VM_DATA_ROOT))?;
    Ok((
        FixtureAdmission {
            root,
            _image: image,
        },
        data,
    ))
}

fn require_registration() -> io::Result<()> {
    let scm = identity::query_registration(NAME, OsStr::new(IMAGE), SERVICE_DEMAND_START)?;
    if !scm.matches_config {
        return Err(denied("unexpected VM fixture service registration"));
    }
    // SAFETY: GetCurrentProcessId takes no inputs.
    let pid = unsafe { GetCurrentProcessId() };
    // SCM can omit the PID in START_PENDING. ServiceMain's exact callback
    // identity and the registration checks above still apply to the VM fixture.
    if scm.state != SERVICE_START_PENDING
        || scm.service_type != SERVICE_WIN32_OWN_PROCESS
        || (scm.process_id != 0 && scm.process_id != pid)
    {
        return Err(denied("unexpected VM fixture service status"));
    }
    Ok(())
}

fn denied(reason: &'static str) -> io::Error {
    io::Error::new(io::ErrorKind::PermissionDenied, reason)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::ptr::null_mut;

    #[test]
    fn callback_accepts_only_the_fixed_service_name() {
        for (name, count, expected) in [
            (NAME, 1, true),
            (NAME, 2, false),
            ("OpenBladeRustReadOnlyDev", 1, false),
            ("", 1, false),
        ] {
            let mut name: Vec<_> = name.encode_utf16().chain([0]).collect();
            let mut argv = [name.as_mut_ptr(), null_mut()];
            // SAFETY: argv has count entries and the first is NUL-terminated.
            assert_eq!(
                unsafe { callback_matches(count, argv.as_mut_ptr()) },
                expected
            );
        }
    }

    #[test]
    fn configured_image_is_exact_and_bounded() {
        for (value, expected) in [
            (IMAGE.to_owned(), true),
            (format!("{IMAGE} --fixture"), false),
            (format!("\"{IMAGE}\""), false),
            (IMAGE.to_ascii_lowercase(), false),
        ] {
            let buffer: Vec<_> = value.encode_utf16().chain([0]).collect();
            assert_eq!(
                config_string_matches(&buffer, buffer.as_ptr(), OsStr::new(IMAGE)),
                expected
            );
            assert!(!config_string_matches(
                &buffer[..buffer.len() - 1],
                buffer.as_ptr(),
                OsStr::new(IMAGE)
            ));
        }
        assert!(!config_string_matches(
            &[0],
            std::ptr::null(),
            OsStr::new(IMAGE)
        ));
    }

    #[test]
    fn markers_are_two_fixed_leaf_names() {
        for name in [START_MARKER, RENDERER_MARKER] {
            assert!(marker_allowed(name));
        }
        for name in [
            "",
            "START.RELEASE",
            "start.release:stream",
            r"..\start.release",
            IMAGE,
        ] {
            assert!(!marker_allowed(name));
        }
    }
}
