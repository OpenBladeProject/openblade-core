#![allow(unsafe_code)]

use std::{
    cell::RefCell,
    io,
    ptr::{null, null_mut},
    sync::atomic::{AtomicBool, Ordering},
};

use windows_sys::Win32::{
    Foundation::WAIT_FAILED,
    UI::{
        Accessibility::{HWINEVENTHOOK, SetWinEventHook, UnhookWinEvent},
        WindowsAndMessaging::{
            DispatchMessageW, EVENT_SYSTEM_DESKTOPSWITCH, EVENT_SYSTEM_FOREGROUND, MSG,
            MWMO_INPUTAVAILABLE, MsgWaitForMultipleObjectsEx, PM_NOREMOVE, PM_REMOVE, PeekMessageW,
            QS_ALLINPUT, TranslateMessage, WINEVENT_OUTOFCONTEXT, WM_QUIT,
        },
    },
};

use super::{Callbacks, Notice, Registration};

/// A message wakes the wait at once, so this only bounds how long cancellation
/// can go unnoticed: the join in shutdown waits at most this long.
const PUMP_WAIT_MS: u32 = 250;

thread_local! {
    static CALLBACKS: RefCell<Option<Callbacks>> = const { RefCell::new(None) };
}

unsafe extern "system" fn callback(
    _: HWINEVENTHOOK,
    event: u32,
    window: *mut core::ffi::c_void,
    object: i32,
    child: i32,
    _: u32,
    _: u32,
) {
    let notice = match event {
        EVENT_SYSTEM_FOREGROUND if object == 0 && child == 0 => Notice::Window(window as usize),
        EVENT_SYSTEM_DESKTOPSWITCH => Notice::SampleCurrent,
        _ => return,
    };
    // Out-of-context callbacks run on their registration thread. This callback
    // performs no native call, so it cannot reenter while the mailbox is held.
    let _ = CALLBACKS.try_with(|slot| {
        if let Ok(slot) = slot.try_borrow()
            && let Some(callbacks) = slot.as_ref()
        {
            callbacks.record(notice);
        }
    });
}

pub(super) struct Hooks {
    hooks: [HWINEVENTHOOK; 2],
}

impl Hooks {
    pub(super) fn register(callbacks: Callbacks, stop: &AtomicBool) -> io::Result<Self> {
        let mut message = MSG::default();
        // SAFETY: writable MSG. Peek creates the thread's queue before registration.
        unsafe { PeekMessageW(&mut message, null_mut(), 0, 0, PM_NOREMOVE) };
        CALLBACKS.with(|slot| slot.replace(Some(callbacks)));
        let mut result = Self {
            hooks: [null_mut(); 2],
        };
        for (hook, event) in result
            .hooks
            .iter_mut()
            .zip([EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_DESKTOPSWITCH])
        {
            if stop.load(Ordering::Acquire) {
                return Err(io::Error::new(
                    io::ErrorKind::Interrupted,
                    "foreground registration cancelled",
                ));
            }
            // SAFETY: static callback, no injected module, current desktop events.
            *hook = unsafe {
                SetWinEventHook(
                    event,
                    event,
                    null_mut(),
                    Some(callback),
                    0,
                    0,
                    WINEVENT_OUTOFCONTEXT,
                )
            };
            if hook.is_null() {
                // SetWinEventHook documents a null failure, without a last-error contract.
                return Err(io::Error::other("foreground WinEvent registration failed"));
            }
        }
        Ok(result)
    }
}

impl Registration for Hooks {
    fn pump(&mut self) -> io::Result<bool> {
        let mut message = MSG::default();
        // SAFETY: the queue belongs to this thread and message is writable.
        if unsafe { PeekMessageW(&mut message, null_mut(), 0, 0, PM_REMOVE) } != 0 {
            if message.message == WM_QUIT {
                return Ok(false);
            }
            // SAFETY: initialized MSG from this thread's queue.
            unsafe {
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }
        // SAFETY: zero handles and a finite wait for this thread's queue.
        } else if unsafe {
            MsgWaitForMultipleObjectsEx(0, null(), PUMP_WAIT_MS, QS_ALLINPUT, MWMO_INPUTAVAILABLE)
        } == WAIT_FAILED
        {
            return Err(io::Error::last_os_error());
        }
        Ok(true)
    }
}

impl Drop for Hooks {
    fn drop(&mut self) {
        for hook in self.hooks.into_iter().filter(|hook| !hook.is_null()) {
            // SAFETY: this is the registration thread. Windows also removes
            // its remaining hooks when the thread terminates.
            unsafe { UnhookWinEvent(hook) };
        }
        let _ = CALLBACKS.try_with(|slot| slot.replace(None));
    }
}
