//! Windows Application Restart registration for the installed Agent, as the
//! managed Session Agent does (`WindowsApplicationRestartRegistration`): the
//! same `--application-restart` command line and flags 0, so Windows restarts
//! the Agent after a crash, hang, update or reboot, and a deliberate stop
//! unregisters first so that it is not restarted. Registration is best effort:
//! a failure is not fatal and leaves the Agent unregistered.
#![allow(unsafe_code)]

use windows_sys::Win32::System::Recovery::{
    RegisterApplicationRestart, UnregisterApplicationRestart,
};

pub(super) const ARGUMENT: &str = "--application-restart";
/// Restart on every condition Windows supports (managed `RestartFlags`).
const FLAGS: u32 = 0;

/// The two Windows calls, so a test can observe them without a registration.
pub(super) trait Api {
    fn register(&self, command_line: &[u16], flags: u32) -> i32;
    fn unregister(&self) -> i32;
}

pub(super) struct Windows;

impl Api for Windows {
    fn register(&self, command_line: &[u16], flags: u32) -> i32 {
        debug_assert_eq!(command_line.last(), Some(&0));
        // SAFETY: a NUL-terminated UTF-16 string that outlives the call; Windows
        // copies it.
        unsafe { RegisterApplicationRestart(command_line.as_ptr(), flags) }
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
        let command_line: Vec<u16> = ARGUMENT.encode_utf16().chain([0]).collect();
        self.registered = self.api.register(&command_line, FLAGS) >= 0;
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
    use std::cell::RefCell;

    use super::*;

    #[derive(Default)]
    struct Recorder {
        calls: RefCell<Vec<(String, u32)>>,
        unregistered: RefCell<u32>,
        result: i32,
    }

    impl Api for &Recorder {
        fn register(&self, command_line: &[u16], flags: u32) -> i32 {
            let text = String::from_utf16(&command_line[..command_line.len() - 1]).unwrap();
            self.calls.borrow_mut().push((text, flags));
            self.result
        }

        fn unregister(&self) -> i32 {
            *self.unregistered.borrow_mut() += 1;
            self.result
        }
    }

    #[test]
    fn registers_managed_command_line_and_flags_once() {
        let api = Recorder::default();
        let mut registration = Registration::new(&api);
        assert!(registration.try_register());
        assert!(registration.try_register());
        assert_eq!(
            *api.calls.borrow(),
            [("--application-restart".to_owned(), 0)]
        );
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
