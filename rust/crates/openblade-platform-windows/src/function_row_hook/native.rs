//! Win32 hook ABI, thread-local callback lease and message pump.
#![allow(unsafe_code)]

use std::{cell::Cell, ptr};
use windows_sys::Win32::{
    Foundation::{GetLastError, LPARAM, LRESULT, WAIT_FAILED, WPARAM},
    System::LibraryLoader::{
        GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        GetModuleHandleExW,
    },
    UI::{
        Input::KeyboardAndMouse::{GetAsyncKeyState, VK_F1},
        WindowsAndMessaging::{
            CallNextHookEx, DispatchMessageW, HC_ACTION, HHOOK, KBDLLHOOKSTRUCT, LLKHF_EXTENDED,
            LLKHF_INJECTED, LLKHF_LOWER_IL_INJECTED, MSG, MWMO_INPUTAVAILABLE,
            MsgWaitForMultipleObjectsEx, PM_REMOVE, PeekMessageW, QS_ALLINPUT, SetWindowsHookExW,
            TranslateMessage, UnhookWindowsHookEx, WH_KEYBOARD_LL, WM_KEYDOWN, WM_KEYUP, WM_QUIT,
            WM_SYSKEYDOWN, WM_SYSKEYUP,
        },
    },
};

use super::{Callback, Instant, Messages, Stroke};

const ROW_SCANS: [u32; 12] = [
    0x3b, 0x3c, 0x3d, 0x3e, 0x3f, 0x40, 0x41, 0x42, 0x43, 0x44, 0x57, 0x58,
];

fn edge(message: WPARAM) -> Option<bool> {
    match u32::try_from(message).ok()? {
        WM_KEYDOWN | WM_SYSKEYDOWN => Some(true),
        WM_KEYUP | WM_SYSKEYUP => Some(false),
        _ => None,
    }
}

fn decode(down: bool, packet: &KBDLLHOOKSTRUCT) -> Option<Stroke> {
    if packet.flags & (LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED | LLKHF_EXTENDED) != 0 {
        return None;
    }
    let index = packet.vkCode.checked_sub(u32::from(VK_F1))? as usize;
    let &scan = ROW_SCANS.get(index)?;
    (packet.scanCode == scan).then_some(Stroke {
        scan_code: scan as u16,
        down,
    })
}

thread_local! {
    // WH_KEYBOARD_LL is delivered on the installing thread. Only its pump
    // lends the callback state to Windows, and nested invocations pass through.
    static ACTIVE: Cell<*mut Callback> = const { Cell::new(ptr::null_mut()) };
}

struct Restore<'a>(&'a Cell<*mut Callback>, *mut Callback);

impl Drop for Restore<'_> {
    fn drop(&mut self) {
        self.0.set(self.1);
    }
}

unsafe extern "system" fn callback(code: i32, message: WPARAM, data: LPARAM) -> LRESULT {
    hook_result(
        code,
        message,
        data,
        |down, data| {
            ACTIVE.with(|active| {
                let state = active.replace(ptr::null_mut());
                if state.is_null() {
                    return false;
                }
                let _restore = Restore(active, state);
                // SAFETY: At HC_ACTION Windows supplies a live KBDLLHOOKSTRUCT.
                let packet = unsafe { &*(data as *const KBDLLHOOKSTRUCT) };
                route_packet(down, packet, |stroke, key| {
                    // The callback precedes the update for this edge. As in the
                    // managed hook, consult the high bit only as a prior-held hint.
                    // The current transition is supplied exclusively by wParam.
                    // SAFETY: key is one of VK_F1 through VK_F12.
                    let was_down = stroke.down && unsafe { GetAsyncKeyState(key as i32) } < 0;
                    // SAFETY: pump lends this state to the current thread, and
                    // ACTIVE is cleared until this mutable borrow ends.
                    unsafe { &mut *state }.route(stroke, was_down, Instant::now())
                })
            })
        },
        || {
            // SAFETY: Forward the original callback arguments unchanged. Windows
            // ignores the hook handle for CallNextHookEx.
            unsafe { CallNextHookEx(ptr::null_mut(), code, message, data) }
        },
    )
}

fn hook_result(
    code: i32,
    message: WPARAM,
    data: LPARAM,
    process: impl FnOnce(bool, LPARAM) -> bool,
    next: impl FnOnce() -> LRESULT,
) -> LRESULT {
    if code == HC_ACTION as i32
        && data != 0
        && let Some(down) = edge(message)
        && process(down, data)
    {
        1
    } else {
        next()
    }
}

fn route_packet(
    down: bool,
    packet: &KBDLLHOOKSTRUCT,
    route: impl FnOnce(Stroke, u32) -> bool,
) -> bool {
    decode(down, packet).is_some_and(|stroke| route(stroke, packet.vkCode))
}

pub(super) struct WindowsMessages;

impl Messages for WindowsMessages {
    type Handle = HHOOK;

    fn now(&self) -> Instant {
        Instant::now()
    }

    fn install(&mut self) -> Result<HHOOK, u32> {
        let mut module = ptr::null_mut();
        // SAFETY: FROM_ADDRESS treats the pointer as a code address, and the
        // out-pointer is writable. The module stays loaded while this code runs.
        let found = unsafe {
            GetModuleHandleExW(
                GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS
                    | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                callback as *const u16,
                &mut module,
            )
        };
        if found == 0 {
            // SAFETY: Read the failed operation's error from this thread.
            return Err(unsafe { GetLastError() });
        }
        // SAFETY: The callback matches HOOKPROC. WH_KEYBOARD_LL with thread
        // zero is serviced on this thread and does not inject a DLL.
        let hook = unsafe { SetWindowsHookExW(WH_KEYBOARD_LL, Some(callback), module, 0) };
        if hook.is_null() {
            // SAFETY: Read the failed operation's error from this thread.
            Err(unsafe { GetLastError() })
        } else {
            Ok(hook)
        }
    }

    fn pump(&mut self, callback: &mut Callback) -> Result<bool, u32> {
        ACTIVE.with(|active| {
            let _restore = Restore(active, active.replace(ptr::from_mut(callback)));
            // SAFETY: No kernel handles are supplied. Waiting at most 10 ms
            // lets this thread check cancellation without posting to a stale ID.
            let wait = unsafe {
                MsgWaitForMultipleObjectsEx(0, ptr::null(), 10, QS_ALLINPUT, MWMO_INPUTAVAILABLE)
            };
            if wait == WAIT_FAILED {
                // SAFETY: Read the failed wait's error from this thread.
                return Err(unsafe { GetLastError() });
            }
            let mut message = MSG::default();
            // SAFETY: MSG is writable. A null HWND selects this thread's queue.
            if unsafe { PeekMessageW(&mut message, ptr::null_mut(), 0, 0, PM_REMOVE) } == 0 {
                return Ok(true);
            }
            if message.message == WM_QUIT {
                return Ok(false);
            }
            // SAFETY: The initialized MSG came from this thread's message queue.
            unsafe {
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }
            Ok(true)
        })
    }

    fn uninstall(&mut self, hook: HHOOK) -> Result<(), u32> {
        // SAFETY: This is the single handle installed by this run. The pump's
        // loan has ended, so any late callback passes through.
        if unsafe { UnhookWindowsHookEx(hook) } == 0 {
            // SAFETY: Read the failed operation's error from this thread.
            Err(unsafe { GetLastError() })
        } else {
            Ok(())
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn native_routes_only_canonical_uninjected_function_keys() {
        for (index, scan) in ROW_SCANS.into_iter().enumerate() {
            let mut packet = KBDLLHOOKSTRUCT {
                vkCode: u32::from(VK_F1) + index as u32,
                scanCode: scan,
                ..KBDLLHOOKSTRUCT::default()
            };
            for (message, down) in [
                (WM_KEYDOWN, true),
                (WM_SYSKEYDOWN, true),
                (WM_KEYUP, false),
                (WM_SYSKEYUP, false),
            ] {
                assert!(route_packet(
                    edge(message as WPARAM).unwrap(),
                    &packet,
                    |stroke, _| {
                        assert_eq!(
                            stroke,
                            Stroke {
                                scan_code: scan as u16,
                                down
                            }
                        );
                        true
                    }
                ));
            }
            for flags in [LLKHF_INJECTED, LLKHF_LOWER_IL_INJECTED, LLKHF_EXTENDED] {
                packet.flags = flags;
                assert!(!route_packet(true, &packet, |_, _| panic!(
                    "ineligible flags"
                )));
            }
            packet.flags = 0;
            packet.vkCode = 0x41;
            assert!(!route_packet(true, &packet, |_, _| panic!("unrelated VK")));
            packet.vkCode = u32::from(VK_F1) + index as u32;
            packet.scanCode += 1;
            assert!(!route_packet(true, &packet, |_, _| panic!("wrong scan")));
        }
        assert_eq!(edge(WM_QUIT as WPARAM), None);
    }

    #[test]
    fn hook_chain_receives_unhandled_events_and_its_result_is_returned() {
        for (code, message, data) in [
            (-1, WM_KEYDOWN, 42),
            (1, WM_KEYDOWN, 42),
            (0, WM_QUIT, 42),
            (0, WM_KEYDOWN, 0),
        ] {
            assert_eq!(
                hook_result(
                    code,
                    message as WPARAM,
                    data,
                    |_, _| panic!("invalid callback"),
                    || -9
                ),
                -9
            );
        }
        assert_eq!(
            hook_result(
                0,
                WM_SYSKEYUP as WPARAM,
                42,
                |down, data| {
                    assert!(!down);
                    assert_eq!(data, 42);
                    false
                },
                || 9
            ),
            9
        );
        assert_eq!(
            hook_result(
                0,
                WM_KEYDOWN as WPARAM,
                42,
                |down, _| down,
                || panic!("suppressed")
            ),
            1
        );
    }

    #[test]
    fn windows_callback_and_lifecycle_symbols_link_without_calling_them() {
        std::hint::black_box(callback as unsafe extern "system" fn(i32, WPARAM, LPARAM) -> LRESULT);
        std::hint::black_box(WindowsMessages::install);
        std::hint::black_box(WindowsMessages::pump);
        std::hint::black_box(WindowsMessages::uninstall);
    }
}
