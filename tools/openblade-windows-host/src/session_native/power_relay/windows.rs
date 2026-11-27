//! The production registration and clock (managed
//! `WindowsSessionPowerNotificationRegistration` and `Stopwatch`). Tests drive
//! the registration only through a fake `Notifications`.
//!
//! The registration functions come from the System32 powrprof.dll at run
//! time, through the shared loader, rather than from a load-time import.
//!
//! Windows holds the callback context from a successful registration until a
//! successful unregistration. A callback Windows woke before that may still be
//! running: once the relay is disposed it returns at once, so the context is
//! released only after every callback that entered it has left, waiting at
//! most the callback budget and a margin. When unregistration fails, or a
//! callback is still running after that wait, the context is kept alive for
//! the rest of the process, as managed roots its callback. A callback Windows
//! has dispatched but that has not yet entered the context is not counted;
//! unregistration is expected to have waited for those.
#![allow(unsafe_code)]
use std::{
    ffi::c_void,
    io,
    panic::{AssertUnwindSafe, catch_unwind},
    ptr,
    sync::{
        Arc,
        atomic::{AtomicUsize, Ordering::SeqCst},
    },
    thread,
    time::{Duration, Instant},
};

use openblade_platform_windows::agent_diagnostics::{
    Event, SessionPowerRelayNotice as Notice, record,
};
use windows_sys::Win32::{
    Foundation::{ERROR_MOD_NOT_FOUND, ERROR_PROC_NOT_FOUND},
    System::{
        Performance::{QueryPerformanceCounter, QueryPerformanceFrequency},
        Power::DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS,
    },
    UI::WindowsAndMessaging::DEVICE_NOTIFY_CALLBACK,
};

use super::{BUDGET, Relay};
use crate::session_native::powrprof::{Function, Library};

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

/// The Windows callback signature.
type Callback = unsafe extern "system" fn(*const c_void, u32, *const c_void) -> u32;

/// Suspend and resume callback registration.
pub(super) trait Notifications {
    /// Register `callback` with `context`: the handle, or the Win32 error.
    fn register(&self, callback: Callback, context: *mut c_void) -> Result<*mut c_void, u32>;
    /// Unregister `handle`: zero, or the Win32 error.
    fn unregister(&self, handle: *mut c_void) -> u32;
}

type Register = unsafe extern "system" fn(u32, *mut c_void, *mut *mut c_void) -> u32;
type Unregister = unsafe extern "system" fn(*mut c_void) -> u32;

/// `PowerRegisterSuspendResumeNotification` and its unregistration, held
/// with the library that exports them.
pub(super) struct System {
    register: Register,
    unregister: Unregister,
    _library: Library,
}

impl System {
    /// Resolve both functions, or the Win32 error that names what is missing.
    pub(super) fn load() -> Result<Self, u32> {
        let library = Library::load().ok_or(ERROR_MOD_NOT_FOUND)?;
        let (Some(register), Some(unregister)) = (
            library.function(b"PowerRegisterSuspendResumeNotification\0"),
            library.function(b"PowerUnregisterSuspendResumeNotification\0"),
        ) else {
            return Err(ERROR_PROC_NOT_FOUND);
        };
        // SAFETY: both exports have exactly these documented signatures, and
        // the library that holds them is kept with them.
        let (register, unregister) = unsafe {
            (
                std::mem::transmute::<Function, Register>(register),
                std::mem::transmute::<Function, Unregister>(unregister),
            )
        };
        Ok(Self {
            register,
            unregister,
            _library: library,
        })
    }
}

impl Notifications for System {
    fn register(&self, callback: Callback, context: *mut c_void) -> Result<*mut c_void, u32> {
        let recipient = DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS {
            Callback: Some(callback),
            Context: context,
        };
        let mut handle = ptr::null_mut();
        // SAFETY: `recipient` is valid for the call and `handle` is owned.
        let code = unsafe {
            (self.register)(
                DEVICE_NOTIFY_CALLBACK,
                (&raw const recipient).cast_mut().cast(),
                &mut handle,
            )
        };
        if code == 0 { Ok(handle) } else { Err(code) }
    }

    fn unregister(&self, handle: *mut c_void) -> u32 {
        // SAFETY: the handle came from a successful registration.
        unsafe { (self.unregister)(handle) }
    }
}

/// What Windows holds: the relay, and how many callbacks are inside it.
struct Context {
    relay: Arc<Relay>,
    in_flight: AtomicUsize,
}

/// A callback disposed relay returns at once; the margin covers its
/// diagnostics write and scheduling.
const DRAIN: Duration = BUDGET.saturating_add(Duration::from_millis(500));

/// One callback registration, released on drop.
pub(super) struct Registration<N: Notifications = System> {
    notifications: N,
    handle: *mut c_void,
    context: *const Context,
    drain: Duration,
}

impl Registration {
    /// Register `relay` for suspend and resume callbacks, or the Win32 error.
    pub(super) fn register(relay: Arc<Relay>) -> Result<Self, u32> {
        Self::with(System::load()?, relay, DRAIN)
    }
}

impl<N: Notifications> Registration<N> {
    fn with(notifications: N, relay: Arc<Relay>, drain: Duration) -> Result<Self, u32> {
        let context = Box::into_raw(Box::new(Context {
            relay,
            in_flight: AtomicUsize::new(0),
        }));
        match notifications.register(callback, context.cast()) {
            Ok(handle) => Ok(Self {
                notifications,
                handle,
                context,
                drain,
            }),
            Err(code) => {
                // SAFETY: Windows refused the registration and holds no context.
                drop(unsafe { Box::from_raw(context) });
                Err(code)
            }
        }
    }
}

impl<N: Notifications> Drop for Registration<N> {
    fn drop(&mut self) {
        let code = self.notifications.unregister(self.handle);
        if code != 0 {
            record(Event::SessionPowerRelay(Notice::UnregistrationFailed(code)));
            return;
        }
        // SAFETY: the context stays alive until it is released below.
        let in_flight = unsafe { &(*self.context).in_flight };
        let deadline = Instant::now() + self.drain;
        while in_flight.load(SeqCst) != 0 {
            if Instant::now() >= deadline {
                record(Event::SessionPowerRelay(Notice::CallbackRetained));
                return;
            }
            thread::sleep(Duration::from_millis(1));
        }
        // SAFETY: Windows calls back no more and no callback is inside the
        // context, so this releases the one `with` gave Windows.
        drop(unsafe { Box::from_raw(self.context.cast_mut()) });
    }
}

unsafe extern "system" fn callback(
    context: *const c_void,
    kind: u32,
    _setting: *const c_void,
) -> u32 {
    // SAFETY: the context is the one `with` gave Windows, alive until
    // unregistration succeeds and no callback is inside it.
    let context = unsafe { &*context.cast::<Context>() };
    context.in_flight.fetch_add(1, SeqCst);
    // Never unwind into Windows.
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if let Some(expired) = context.relay.on_notification(kind) {
            record(Event::SessionPowerRelay(Notice::Abandoned {
                sequence: expired.sequence,
                suspending: expired.suspending,
            }));
        }
    }));
    context.in_flight.fetch_sub(1, SeqCst);
    0
}

#[cfg(test)]
mod tests;
