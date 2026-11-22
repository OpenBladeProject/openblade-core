//! The Service's power-setting notification registrations.
//!
//! Managed subscribes to suspend/resume, GUID_ACDC_POWER_SOURCE and
//! GUID_CONSOLE_DISPLAY_STATE with DEVICE_NOTIFY_CALLBACK
//! (WindowsServiceLifecycleEventSource.cs:571-648). The native Service
//! registers the same two settings against its SCM status handle with
//! DEVICE_NOTIFY_SERVICE_HANDLE instead, so each change arrives at the one
//! SCM handler as SERVICE_CONTROL_POWEREVENT / PBT_POWERSETTINGCHANGE with a
//! POWERBROADCAST_SETTING payload: the same payload managed reads, through
//! the handler that already orders every power and session notice against
//! the lifecycle epoch. Suspend and resume need no registration: the
//! accepted SERVICE_ACCEPT_POWEREVENT control already delivers PBT_APMSUSPEND
//! and both resumes there. Managed's HID device-interface registration is a
//! device-discovery trigger, not a power notice, and stays out of this slice.
//!
//! Registration is all or nothing, as the managed constructor is: a failed
//! registration unregisters what it registered and fails Service start;
//! `Supplier` places both against the SCM reports.
//! Close unregisters once, in managed's order (power source, then console
//! display), attempts both even when the first fails, and keeps returning
//! the first outcome (managed `WindowsLifecycleRegistrationCleanup`).
use std::{io, sync::Mutex};

/// The two settings, in registration order.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) enum Setting {
    PowerSource,
    ConsoleDisplay,
}

pub(super) const SETTINGS: [Setting; 2] = [Setting::PowerSource, Setting::ConsoleDisplay];

/// The registration calls, injected so tests never reach the real API.
pub(super) trait Api {
    type Handle: Copy;
    /// A registration handle, or the Win32 error.
    fn register(&self, setting: Setting) -> Result<Self::Handle, u32>;
    /// Unregisters one handle, or returns the Win32 error.
    fn unregister(&self, handle: Self::Handle) -> Result<(), u32>;
}

struct Slots<H> {
    handles: Vec<H>,
    // Managed attempts cleanup once and rethrows its failure after that.
    outcome: Option<Vec<u32>>,
}

pub(super) struct Registrations<A: Api> {
    api: A,
    slots: Mutex<Slots<A::Handle>>,
}

impl<A: Api> Registrations<A> {
    pub(super) fn register(api: A) -> io::Result<Self> {
        let registrations = Self {
            api,
            slots: Mutex::new(Slots {
                handles: Vec::with_capacity(SETTINGS.len()),
                outcome: None,
            }),
        };
        for setting in SETTINGS {
            match registrations.api.register(setting) {
                Ok(handle) => registrations.lock().handles.push(handle),
                Err(code) => {
                    let cleanup = registrations.close();
                    let error = io::Error::from_raw_os_error(code as i32);
                    return Err(match cleanup {
                        Ok(()) => error,
                        Err(cleanup) => io::Error::other(format!(
                            "power-setting registration failed ({error}); cleanup failed ({cleanup})"
                        )),
                    });
                }
            }
        }
        Ok(registrations)
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, Slots<A::Handle>> {
        self.slots.lock().unwrap_or_else(|error| error.into_inner())
    }

    /// Unregisters every handle once. Later calls return the same outcome.
    pub(super) fn close(&self) -> io::Result<()> {
        let mut slots = self.lock();
        let failures = match &slots.outcome {
            Some(failures) => failures.clone(),
            None => {
                let failures: Vec<u32> = std::mem::take(&mut slots.handles)
                    .into_iter()
                    .filter_map(|handle| self.api.unregister(handle).err())
                    .collect();
                slots.outcome = Some(failures.clone());
                failures
            }
        };
        if failures.is_empty() {
            Ok(())
        } else {
            Err(io::Error::other(format!(
                "power-setting unregistration failed: {failures:?}"
            )))
        }
    }
}

impl<A: Api> Drop for Registrations<A> {
    fn drop(&mut self) {
        // The Service closes before reporting SERVICE_STOPPED; this covers an
        // exit that never reached that report.
        let _ = self.close();
    }
}

/// When the Service registers and unregisters, around its own SCM reports.
///
/// Registration follows a successful SERVICE_RUNNING report, whose accepted
/// controls include SERVICE_ACCEPT_POWEREVENT. Windows sends each setting's
/// current value when it is registered. SCM is expected to drop a power
/// event the current status does not accept (unverified; attended-only), so
/// registering while START_PENDING (which accepts nothing) could lose the
/// initial console display state. Managed's callback registration has no
/// accept mask and receives that first value. A registration failure is
/// returned from the RUNNING report, which `run_candidate` treats as a failed
/// start: it retires the endpoint and reports SERVICE_STOPPED with a failure.
/// SCM therefore sees RUNNING, then STOPPED (failed); managed's constructor
/// throws before RUNNING is ever reported.
///
/// Unregistration precedes the SERVICE_STOPPED report, because SCM may end
/// the process once it reads it. Its failure never changes that report:
/// managed's lifecycle source rethrows it only from host disposal, after the
/// Windows service lifetime reported SERVICE_STOPPED, so a requested stop
/// still reports its own outcome and the failure actions do not restart it.
/// The failure is kept for the caller.
pub(super) struct Supplier<A: Api> {
    api: Option<A>,
    registrations: Option<Registrations<A>>,
    cleanup_failure: Option<io::Error>,
}

impl<A: Api> Supplier<A> {
    pub(super) fn new(api: A) -> Self {
        Self {
            api: Some(api),
            registrations: None,
            cleanup_failure: None,
        }
    }

    /// Reports one SCM status (`state` is its `dwCurrentState`) through
    /// `report`, registering after RUNNING and unregistering before STOPPED.
    pub(super) fn report(
        &mut self,
        state: u32,
        report: impl FnOnce() -> io::Result<()>,
    ) -> io::Result<()> {
        use windows_sys::Win32::System::Services::{SERVICE_RUNNING, SERVICE_STOPPED};
        if state == SERVICE_STOPPED {
            // Nothing registers after the terminal report is decided.
            self.api = None;
            if let Some(registrations) = self.registrations.take()
                && let Err(error) = registrations.close()
            {
                self.cleanup_failure = Some(error);
            }
            return report();
        }
        report()?;
        if state == SERVICE_RUNNING
            && let Some(api) = self.api.take()
        {
            self.registrations = Some(Registrations::register(api)?);
        }
        Ok(())
    }

    /// The unregistration failure of the terminal report, if any.
    pub(super) fn take_cleanup_failure(&mut self) -> Option<io::Error> {
        self.cleanup_failure.take()
    }
}

/// RegisterPowerSettingNotification against the SCM status handle.
#[cfg(not(test))]
pub(super) mod native {
    #![allow(unsafe_code)]

    use windows_sys::Win32::{
        Foundation::GetLastError,
        System::{
            Power::{
                HPOWERNOTIFY, RegisterPowerSettingNotification, UnregisterPowerSettingNotification,
            },
            Services::SERVICE_STATUS_HANDLE,
            SystemServices::{GUID_ACDC_POWER_SOURCE, GUID_CONSOLE_DISPLAY_STATE},
        },
        UI::WindowsAndMessaging::DEVICE_NOTIFY_SERVICE_HANDLE,
    };

    use super::{Api, Setting};

    pub(in super::super) struct ServiceHandle(pub(in super::super) SERVICE_STATUS_HANDLE);

    impl Api for ServiceHandle {
        type Handle = HPOWERNOTIFY;

        fn register(&self, setting: Setting) -> Result<HPOWERNOTIFY, u32> {
            let guid = match setting {
                Setting::PowerSource => GUID_ACDC_POWER_SOURCE,
                Setting::ConsoleDisplay => GUID_CONSOLE_DISPLAY_STATE,
            };
            // SAFETY: the status handle is the live registration of this
            // service, and the GUID outlives the call.
            let handle = unsafe {
                RegisterPowerSettingNotification(self.0, &guid, DEVICE_NOTIFY_SERVICE_HANDLE)
            };
            if handle == 0 {
                // SAFETY: reads this thread's last error.
                Err(unsafe { GetLastError() })
            } else {
                Ok(handle)
            }
        }

        fn unregister(&self, handle: HPOWERNOTIFY) -> Result<(), u32> {
            // SAFETY: the handle came from a successful registration and is
            // unregistered once.
            if unsafe { UnregisterPowerSettingNotification(handle) } == 0 {
                // SAFETY: reads this thread's last error.
                Err(unsafe { GetLastError() })
            } else {
                Ok(())
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use std::cell::RefCell;

    use super::*;

    #[derive(Default)]
    struct Fake {
        fail_register: Option<Setting>,
        fail_unregister: Vec<usize>,
        calls: RefCell<Vec<String>>,
    }

    impl Api for &Fake {
        type Handle = usize;

        fn register(&self, setting: Setting) -> Result<usize, u32> {
            self.calls
                .borrow_mut()
                .push(format!("register {setting:?}"));
            if self.fail_register == Some(setting) {
                return Err(5);
            }
            Ok(SETTINGS.iter().position(|s| *s == setting).unwrap() + 10)
        }

        fn unregister(&self, handle: usize) -> Result<(), u32> {
            self.calls.borrow_mut().push(format!("unregister {handle}"));
            if self.fail_unregister.contains(&handle) {
                Err(handle as u32)
            } else {
                Ok(())
            }
        }
    }

    fn calls(fake: &Fake) -> Vec<String> {
        fake.calls.borrow().clone()
    }

    use windows_sys::Win32::System::Services::{
        SERVICE_RUNNING, SERVICE_START_PENDING, SERVICE_STOP_PENDING, SERVICE_STOPPED,
    };

    fn report(supplier: &mut Supplier<&Fake>, fake: &Fake, state: u32) -> io::Result<()> {
        supplier.report(state, || {
            fake.calls.borrow_mut().push(format!("report {state}"));
            Ok(())
        })
    }

    #[test]
    fn the_supplier_registers_after_running_and_unregisters_before_stopped() {
        let fake = Fake::default();
        let mut supplier = Supplier::new(&fake);
        for state in [SERVICE_START_PENDING, SERVICE_RUNNING, SERVICE_STOP_PENDING] {
            report(&mut supplier, &fake, state).unwrap();
        }
        assert_eq!(
            fake.calls.borrow().len(),
            5,
            "STOP_PENDING unregisters nothing"
        );
        report(&mut supplier, &fake, SERVICE_STOPPED).unwrap();
        assert!(supplier.take_cleanup_failure().is_none());
        drop(supplier);
        assert_eq!(
            calls(&fake),
            [
                "report 2",
                "report 4",
                "register PowerSource",
                "register ConsoleDisplay",
                "report 3",
                "unregister 10",
                "unregister 11",
                "report 1"
            ]
        );
    }

    #[test]
    fn a_failed_running_report_registers_nothing() {
        let fake = Fake::default();
        let mut supplier = Supplier::new(&fake);
        let failed = supplier.report(SERVICE_RUNNING, || Err(io::ErrorKind::BrokenPipe.into()));
        assert_eq!(failed.unwrap_err().kind(), io::ErrorKind::BrokenPipe);
        report(&mut supplier, &fake, SERVICE_STOPPED).unwrap();
        assert_eq!(calls(&fake), ["report 1"]);
    }

    #[test]
    fn a_failed_registration_fails_the_running_report_and_leaves_nothing_to_close() {
        let fake = Fake {
            fail_register: Some(Setting::ConsoleDisplay),
            ..Fake::default()
        };
        let mut supplier = Supplier::new(&fake);
        let failed = report(&mut supplier, &fake, SERVICE_RUNNING).unwrap_err();
        assert_eq!(failed.raw_os_error(), Some(5));
        report(&mut supplier, &fake, SERVICE_STOPPED).unwrap();
        assert!(supplier.take_cleanup_failure().is_none());
        assert_eq!(
            calls(&fake),
            [
                "report 4",
                "register PowerSource",
                "register ConsoleDisplay",
                "unregister 10",
                "report 1"
            ]
        );
    }

    #[test]
    fn an_unregistration_failure_keeps_the_stopped_report_and_is_kept_for_the_caller() {
        let fake = Fake {
            fail_unregister: vec![10],
            ..Fake::default()
        };
        let mut supplier = Supplier::new(&fake);
        report(&mut supplier, &fake, SERVICE_RUNNING).unwrap();
        // The stop's own outcome is reported unchanged.
        report(&mut supplier, &fake, SERVICE_STOPPED).unwrap();
        assert_eq!(
            calls(&fake)[3..],
            ["unregister 10", "unregister 11", "report 1"]
        );
        assert!(supplier.take_cleanup_failure().is_some());
        assert!(supplier.take_cleanup_failure().is_none());
    }

    #[test]
    fn a_stop_before_running_never_registers() {
        let fake = Fake::default();
        let mut supplier = Supplier::new(&fake);
        report(&mut supplier, &fake, SERVICE_START_PENDING).unwrap();
        report(&mut supplier, &fake, SERVICE_STOPPED).unwrap();
        report(&mut supplier, &fake, SERVICE_RUNNING).unwrap();
        assert_eq!(calls(&fake), ["report 2", "report 1", "report 4"]);
    }

    #[test]
    fn registers_both_settings_and_unregisters_them_once_in_order() {
        let fake = Fake::default();
        let registrations = Registrations::register(&fake).unwrap();
        registrations.close().unwrap();
        registrations.close().unwrap();
        drop(registrations);
        assert_eq!(
            calls(&fake),
            [
                "register PowerSource",
                "register ConsoleDisplay",
                "unregister 10",
                "unregister 11"
            ]
        );
    }

    #[test]
    fn drop_unregisters_a_registration_that_was_never_closed() {
        let fake = Fake::default();
        drop(Registrations::register(&fake).unwrap());
        assert_eq!(calls(&fake)[2..], ["unregister 10", "unregister 11"]);
    }

    #[test]
    fn a_failed_registration_unregisters_what_it_registered_and_fails() {
        let fake = Fake {
            fail_register: Some(Setting::ConsoleDisplay),
            ..Fake::default()
        };
        let error = Registrations::register(&fake).err().unwrap();
        assert_eq!(error.raw_os_error(), Some(5));
        assert_eq!(
            calls(&fake),
            [
                "register PowerSource",
                "register ConsoleDisplay",
                "unregister 10"
            ]
        );

        let first = Fake {
            fail_register: Some(Setting::PowerSource),
            ..Fake::default()
        };
        assert!(Registrations::register(&first).is_err());
        assert_eq!(calls(&first), ["register PowerSource"]);
    }

    #[test]
    fn a_failed_cleanup_after_a_failed_registration_reports_both() {
        let fake = Fake {
            fail_register: Some(Setting::ConsoleDisplay),
            fail_unregister: vec![10],
            ..Fake::default()
        };
        let error = Registrations::register(&fake).err().unwrap();
        assert_eq!(error.raw_os_error(), None);
        assert!(error.to_string().contains("cleanup failed"), "{error}");
    }

    #[test]
    fn a_failed_unregistration_still_attempts_the_other_and_is_kept() {
        let fake = Fake {
            fail_unregister: vec![10],
            ..Fake::default()
        };
        let registrations = Registrations::register(&fake).unwrap();
        assert!(registrations.close().is_err());
        assert!(registrations.close().is_err(), "the first outcome is kept");
        drop(registrations);
        assert_eq!(calls(&fake)[2..], ["unregister 10", "unregister 11"]);
    }
}
