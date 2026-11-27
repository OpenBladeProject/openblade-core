//! The production registration and clock (managed
//! `WindowsSessionPowerNotificationRegistration` and `Stopwatch`). Tests never
//! construct the registration.
//!
//! Windows holds the relay as the callback context from a successful
//! registration until a successful unregistration. When unregistration fails,
//! Windows may still call back, so the context is kept alive for the rest of
//! the process, as managed roots its callback; the relay is already disposed,
//! so such a callback returns at once.
#![allow(unsafe_code)]
use std::{
    ffi::c_void,
    io,
    panic::{AssertUnwindSafe, catch_unwind},
    ptr,
    sync::Arc,
};

use openblade_platform_windows::agent_diagnostics::{
    Event, SessionPowerRelayNotice as Notice, record,
};
use windows_sys::Win32::{
    System::{
        Performance::{QueryPerformanceCounter, QueryPerformanceFrequency},
        Power::{
            DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS, HPOWERNOTIFY,
            PowerRegisterSuspendResumeNotification, PowerUnregisterSuspendResumeNotification,
        },
    },
    UI::WindowsAndMessaging::DEVICE_NOTIFY_CALLBACK,
};

use super::Relay;

/// The current QPC count.
pub(super) fn counter() -> io::Result<i64> {
    let mut count = 0;
    // SAFETY: the count is an owned i64 the call writes.
    if unsafe { QueryPerformanceCounter(&mut count) } == 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(count)
}

/// QPC counts per second.
pub(super) fn frequency() -> io::Result<i64> {
    let mut frequency = 0;
    // SAFETY: the frequency is an owned i64 the call writes.
    if unsafe { QueryPerformanceFrequency(&mut frequency) } == 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(frequency)
}

/// The relay clock. QPC does not fail once `counter` has succeeded; a failure
/// would read as zero, which leaves every transition without budget.
pub(super) fn now() -> i64 {
    counter().unwrap_or(0)
}

/// One `PowerRegisterSuspendResumeNotification` callback registration.
pub(super) struct Registration {
    handle: HPOWERNOTIFY,
    context: *const Relay,
}

impl Registration {
    /// Register `relay` for suspend and resume callbacks, or the Win32 error.
    pub(super) fn register(relay: Arc<Relay>) -> Result<Self, u32> {
        let context = Arc::into_raw(relay);
        let recipient = DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS {
            Callback: Some(callback),
            Context: context.cast_mut().cast(),
        };
        let mut handle = ptr::null_mut();
        // SAFETY: `recipient` is valid for the call and `handle` is owned.
        // `context` stays alive until unregistration succeeds.
        let code = unsafe {
            PowerRegisterSuspendResumeNotification(
                DEVICE_NOTIFY_CALLBACK,
                (&raw const recipient).cast_mut().cast(),
                &mut handle,
            )
        };
        if code != 0 {
            // SAFETY: Windows refused the registration and holds no context.
            drop(unsafe { Arc::from_raw(context) });
            return Err(code);
        }
        Ok(Self {
            handle: handle as HPOWERNOTIFY,
            context,
        })
    }
}

impl Drop for Registration {
    fn drop(&mut self) {
        // SAFETY: the handle came from a successful registration and is
        // unregistered only here.
        let code = unsafe { PowerUnregisterSuspendResumeNotification(self.handle) };
        if code == 0 {
            // SAFETY: Windows no longer holds the context and calls back no
            // more, so this releases the reference `register` gave it.
            drop(unsafe { Arc::from_raw(self.context) });
        } else {
            record(Event::SessionPowerRelay(Notice::UnregistrationFailed(code)));
        }
    }
}

unsafe extern "system" fn callback(
    context: *const c_void,
    kind: u32,
    _setting: *const c_void,
) -> u32 {
    // Never unwind into Windows.
    let _ = catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: the context is the relay `register` leaked to Windows, alive
        // until unregistration succeeds.
        let relay = unsafe { &*context.cast::<Relay>() };
        if let Some(expired) = relay.on_notification(kind) {
            record(Event::SessionPowerRelay(Notice::Abandoned {
                sequence: expired.sequence,
                suspending: expired.suspending,
            }));
        }
    }));
    0
}
