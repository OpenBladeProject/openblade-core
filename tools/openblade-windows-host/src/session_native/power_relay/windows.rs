//! The production registration and clock (managed
//! `WindowsSessionPowerNotificationRegistration` and `Stopwatch`). Tests drive
//! the registration only through a fake `Notifications`.
//!
//! The registration functions come from the System32 powrprof.dll at run
//! time, through the shared loader, rather than from a load-time import. Once
//! a registration succeeds the library is never released: Windows calls back
//! through powrprof, and the callback returns into its code.
//!
//! Windows holds a slot, allocated per registration and never freed, so a
//! callback can always reach it, however late it runs. The slot holds the
//! relay while registered. A callback locks it only to clone the relay out,
//! and works on its clone. A successful unregistration takes the relay out of
//! the slot, so a later callback finds nothing and returns, and a callback
//! still running keeps its own clone alive. When unregistration fails the
//! relay stays in the slot for the rest of the process, as managed roots its
//! callback; it is already disposed, so such a callback returns at once.
#![allow(unsafe_code)]
use std::{
    cell::Cell,
    ffi::c_void,
    io,
    panic::{AssertUnwindSafe, catch_unwind},
    ptr,
    sync::{Arc, Mutex, PoisonError},
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

use super::Relay;
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
    /// Released on drop only while no registration has succeeded.
    library: Cell<Option<Library>>,
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
            library: Cell::new(Some(library)),
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
        if code != 0 {
            return Err(code);
        }
        // Windows may call back into powrprof from now on, even after a
        // failed unregistration, so the module stays loaded for the process.
        std::mem::forget(self.library.take());
        Ok(handle)
    }

    fn unregister(&self, handle: *mut c_void) -> u32 {
        // SAFETY: the handle came from a successful registration.
        unsafe { (self.unregister)(handle) }
    }
}

/// What Windows holds as the callback context: the relay while registered.
type Slot = Mutex<Option<Arc<Relay>>>;

/// One callback registration, released on drop.
pub(super) struct Registration<N: Notifications = System> {
    notifications: N,
    handle: *mut c_void,
    slot: &'static Slot,
}

impl Registration {
    /// Register `relay` for suspend and resume callbacks, or the Win32 error.
    pub(super) fn register(relay: Arc<Relay>) -> Result<Self, u32> {
        Self::with(System::load()?, relay)
    }
}

impl<N: Notifications> Registration<N> {
    fn with(notifications: N, relay: Arc<Relay>) -> Result<Self, u32> {
        let slot = Box::into_raw(Box::new(Mutex::new(Some(relay))));
        match notifications.register(callback, slot.cast()) {
            Ok(handle) => Ok(Self {
                notifications,
                handle,
                // SAFETY: Windows now holds the slot, which is never freed.
                slot: unsafe { &*slot },
            }),
            Err(code) => {
                // SAFETY: Windows refused the registration and holds no slot.
                drop(unsafe { Box::from_raw(slot) });
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
        let relay = self
            .slot
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .take();
        drop(relay);
    }
}

unsafe extern "system" fn callback(
    context: *const c_void,
    kind: u32,
    _setting: *const c_void,
) -> u32 {
    // SAFETY: the context is a slot `with` gave Windows, which is never freed.
    let slot = unsafe { &*context.cast::<Slot>() };
    // Never unwind into Windows.
    let _ = catch_unwind(AssertUnwindSafe(|| {
        let Some(relay) = slot.lock().unwrap_or_else(PoisonError::into_inner).clone() else {
            return;
        };
        if let Some(expired) = relay.on_notification(kind) {
            record(Event::SessionPowerRelay(Notice::Abandoned {
                sequence: expired.sequence,
                suspending: expired.suspending,
            }));
        }
    }));
    0
}

#[cfg(test)]
mod tests;
