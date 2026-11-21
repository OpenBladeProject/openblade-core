//! Windows Application Restart registration for the installed Agent, as the
//! managed Session Agent does (`WindowsApplicationRestartRegistration`), with
//! flags 0 so Windows restarts the Agent after a crash, hang, update or reboot
//! once it has run for at least 60 seconds. A deliberate stop unregisters first
//! so it is not restarted. Registration is best effort: a failure is not fatal
//! and leaves the Agent unregistered.
//!
//! Managed registers `--application-restart`. Native Origin admission accepts
//! no arguments at all, so the native Agent registers no command line: the
//! documented NULL `pwzCommandline` restarts it with the program name alone,
//! which is exactly what admission requires. Argument admission stays unchanged.
#![allow(unsafe_code)]

use windows_sys::Win32::System::Recovery::{
    RegisterApplicationRestart, UnregisterApplicationRestart,
};

/// Restart on every condition Windows supports (managed `RestartFlags`).
const FLAGS: u32 = 0;

/// The two Windows calls, so a test can observe them without a registration.
pub(super) trait Api {
    /// `None` is a NULL command line: the restarted process has no arguments.
    fn register(&self, command_line: Option<&[u16]>, flags: u32) -> i32;
    fn unregister(&self) -> i32;
}

pub(super) struct Windows;

impl Api for Windows {
    fn register(&self, command_line: Option<&[u16]>, flags: u32) -> i32 {
        debug_assert!(command_line.is_none_or(|text| text.last() == Some(&0)));
        // SAFETY: NULL, or a NUL-terminated UTF-16 string that outlives the
        // call; Windows copies it.
        unsafe {
            RegisterApplicationRestart(
                command_line.map_or(std::ptr::null(), <[u16]>::as_ptr),
                flags,
            )
        }
    }

    fn unregister(&self) -> i32 {
        // SAFETY: no arguments.
        unsafe { UnregisterApplicationRestart() }
    }
}

pub(super) struct Registration<A: Api> {
    api: A,
    registered: bool,
}

impl<A: Api> Registration<A> {
    pub(super) fn new(api: A) -> Self {
        Self {
            api,
            registered: false,
        }
    }

    /// Idempotent. A negative HRESULT leaves the Agent unregistered.
    pub(super) fn try_register(&mut self) -> bool {
        if self.registered {
            return true;
        }
        self.registered = self.api.register(None, FLAGS) >= 0;
        self.registered
    }

    /// Only after a registration succeeded; a failure leaves it registered.
    pub(super) fn try_unregister(&mut self) -> bool {
        if !self.registered {
            return true;
        }
        if self.api.unregister() < 0 {
            return false;
        }
        self.registered = false;
        true
    }
}

#[cfg(test)]
mod tests {
    use std::{cell::RefCell, ffi::OsString};

    use super::*;

    #[derive(Default)]
    struct Recorder {
        /// The command line of each registration, `None` for NULL.
        calls: RefCell<Vec<(Option<String>, u32)>>,
        unregistered: RefCell<u32>,
        result: i32,
    }

    impl Api for &Recorder {
        fn register(&self, command_line: Option<&[u16]>, flags: u32) -> i32 {
            let text =
                command_line.map(|line| String::from_utf16(&line[..line.len() - 1]).unwrap());
            self.calls.borrow_mut().push((text, flags));
            self.result
        }

        fn unregister(&self) -> i32 {
            *self.unregistered.borrow_mut() += 1;
            self.result
        }
    }

    /// What Windows hands the restarted process: the program name, then the
    /// registered command line split on spaces, or nothing for NULL.
    fn restarted_arguments(command_line: Option<&str>) -> Vec<OsString> {
        std::iter::once(OsString::from("OpenBlade.SessionAgent.exe"))
            .chain(
                command_line
                    .into_iter()
                    .flat_map(str::split_whitespace)
                    .map(OsString::from),
            )
            .collect()
    }

    #[test]
    fn registers_no_command_line_with_managed_flags_once() {
        let api = Recorder::default();
        let mut registration = Registration::new(&api);
        assert!(registration.try_register());
        assert!(registration.try_register());
        assert_eq!(*api.calls.borrow(), [(None, 0)]);
    }

    #[test]
    fn the_registered_restart_passes_origin_argument_admission() {
        let api = Recorder::default();
        Registration::new(&api).try_register();
        let (command_line, _) = api.calls.borrow()[0].clone();
        assert!(crate::loaded_origin::arguments_admitted(
            restarted_arguments(command_line.as_deref())
        ));
        // Managed's own argument would be denied by the same rule, which is why
        // native registers none.
        assert!(!crate::loaded_origin::arguments_admitted(
            restarted_arguments(Some("--application-restart"))
        ));
        assert!(crate::loaded_origin::arguments_admitted(
            restarted_arguments(None)
        ));
    }

    #[test]
    fn a_failed_registration_stays_unregistered_and_retries() {
        let api = Recorder {
            result: -1,
            ..Recorder::default()
        };
        let mut registration = Registration::new(&api);
        assert!(!registration.try_register());
        assert!(registration.try_unregister());
        assert_eq!(*api.unregistered.borrow(), 0);
        assert!(!registration.try_register());
        assert_eq!(api.calls.borrow().len(), 2);
    }

    #[test]
    fn a_deliberate_stop_unregisters_once_and_never_before_registering() {
        let api = Recorder::default();
        let mut registration = Registration::new(&api);
        assert!(registration.try_unregister());
        assert_eq!(*api.unregistered.borrow(), 0);
        assert!(registration.try_register());
        assert!(registration.try_unregister());
        assert!(registration.try_unregister());
        assert_eq!(*api.unregistered.borrow(), 1);
    }
}
