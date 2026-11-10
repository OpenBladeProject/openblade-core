//! Win32 ABI for the synchronous adapter. The supervised product has no caller.
#![allow(unsafe_code)]

use std::{cell::Cell, ffi::c_void, mem::size_of, ptr, time::Instant};

use windows_sys::Win32::{
    Foundation::{GetLastError, LPARAM, LRESULT, WPARAM},
    System::{
        LibraryLoader::{
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            GetModuleHandleExW,
        },
        RemoteDesktop::{ProcessIdToSessionId, WTSGetActiveConsoleSessionId},
    },
    UI::{
        Input::KeyboardAndMouse::{
            GetAsyncKeyState, GetKeyboardLayout, INPUT, INPUT_0, INPUT_KEYBOARD, KEYBDINPUT,
            KEYEVENTF_KEYUP, KEYEVENTF_SCANCODE, MAPVK_VSC_TO_VK_EX, MapVirtualKeyExW, SendInput,
        },
        WindowsAndMessaging::{
            CallNextHookEx, DispatchMessageW, GetForegroundWindow, GetMessageW,
            GetWindowThreadProcessId, HC_ACTION, HHOOK, KBDLLHOOKSTRUCT, KillTimer, MSG,
            PM_NOREMOVE, PeekMessageW, SPI_GETKEYBOARDSPEED, SetTimer, SetWindowsHookExW,
            SystemParametersInfoW, TranslateMessage, UnhookWindowsHookEx, WH_KEYBOARD_LL, WM_TIMER,
        },
    },
};

use super::super::{Batch, Key, Observation, Stroke, Submission};
use super::{
    Admission, Context, HookEvent, Input, Messages, Output, OwnershipAdmission, Reason, Retirement,
    Route, run,
};

/// The isolated supervisor must supply current registry/controller evidence and
/// a fast nonblocking stop check that includes loss of supervision.
pub(super) trait Host {
    fn admission(&mut self) -> Admission<'_>;
    fn stop_requested(&self) -> bool;
}

/// Must run on the owning helper thread for its full lifetime.
pub(super) fn run_windows(
    host: &mut dyn Host,
    ownership: OwnershipAdmission,
) -> Result<Option<Retirement>, Reason> {
    run(
        WindowsInput {
            host,
            started: Instant::now(),
        },
        WindowsMessages,
        ownership,
    )
}

thread_local! {
    // WH_KEYBOARD_LL calls back on the installing thread. Store a thin pointer
    // to the borrowed callback reference only while USER32 pumps this thread.
    static ACTIVE: Cell<*mut c_void> = const { Cell::new(ptr::null_mut()) };
}

struct Restore<'a>(&'a Cell<*mut c_void>, *mut c_void);

impl Drop for Restore<'_> {
    fn drop(&mut self) {
        self.0.set(self.1);
    }
}

fn with_route<T>(mut route: &mut Route<'_>, pump: impl FnOnce() -> T) -> T {
    ACTIVE.with(|active| {
        let previous = active.replace(ptr::from_mut(&mut route).cast());
        let _restore = Restore(active, previous);
        pump()
    })
}

unsafe extern "system" fn callback(code: i32, message: WPARAM, data: LPARAM) -> LRESULT {
    if code == HC_ACTION as i32 && data != 0 {
        let suppress = ACTIVE.with(|active| {
            let route = active.replace(ptr::null_mut());
            if route.is_null() {
                return false;
            }
            let _restore = Restore(active, route);
            // SAFETY: USER32 supplies a live packet at HC_ACTION. The erased
            // reference belongs to with_route on this thread. Removing it from
            // ACTIVE prevents a nested call from borrowing it again.
            let packet = unsafe { &*(data as *const KBDLLHOOKSTRUCT) };
            let route = unsafe { &mut *route.cast::<&mut Route<'_>>() };
            route(HookEvent {
                scan: packet.scanCode,
                flags: packet.flags,
                message,
            })
        });
        if suppress {
            return 1;
        }
    }

    // SAFETY: Preserve the original arguments and the next hook's return.
    // CallNextHookEx ignores the handle.
    unsafe { CallNextHookEx(ptr::null_mut(), code, message, data) }
}

struct WindowsMessages;

impl Messages for WindowsMessages {
    type Hook = HHOOK;
    type Message = MSG;

    fn install(&mut self) -> Result<HHOOK, u32> {
        let mut message = MSG::default();
        let mut module = ptr::null_mut();
        // SAFETY: The queue probe has writable storage. FROM_ADDRESS uses the
        // callback address to identify its loaded module without a new reference.
        unsafe {
            PeekMessageW(&mut message, ptr::null_mut(), 0, 0, PM_NOREMOVE);
            if GetModuleHandleExW(
                GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS
                    | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                callback as *const u16,
                &mut module,
            ) == 0
            {
                return Err(GetLastError());
            }
            let hook = SetWindowsHookExW(WH_KEYBOARD_LL, Some(callback), module, 0);
            if hook.is_null() {
                Err(GetLastError())
            } else {
                Ok(hook)
            }
        }
    }

    fn start_timer(&mut self) -> Result<usize, u32> {
        // SAFETY: A thread timer with no callback posts WM_TIMER to this queue.
        let timer = unsafe { SetTimer(ptr::null_mut(), 0, 10, None) };
        if timer == 0 {
            Err(last_error())
        } else {
            Ok(timer)
        }
    }

    fn receive(&mut self, route: &mut Route<'_>) -> Result<Option<MSG>, u32> {
        let mut message = MSG::default();
        // SAFETY: USER32 can fill message while the route lease remains in scope.
        match with_route(route, || unsafe {
            GetMessageW(&mut message, ptr::null_mut(), 0, 0)
        }) {
            -1 => Err(last_error()),
            0 => Ok(None),
            _ => Ok(Some(message)),
        }
    }

    fn timer_id(message: &MSG) -> Option<usize> {
        (message.hwnd.is_null() && message.message == WM_TIMER).then_some(message.wParam)
    }

    fn dispatch(&mut self, message: MSG, route: &mut Route<'_>) {
        // SAFETY: message was retrieved on this thread and stays live during dispatch.
        with_route(route, || unsafe {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        });
    }

    fn unhook(&mut self, hook: HHOOK) -> Result<(), u32> {
        // SAFETY: run consumes the owned handle once, after the last route lease.
        if unsafe { UnhookWindowsHookEx(hook) } == 0 {
            Err(last_error())
        } else {
            Ok(())
        }
    }

    fn kill_timer(&mut self, timer: usize) -> Result<(), u32> {
        // SAFETY: This is the ID of the timer installed on the calling thread.
        if unsafe { KillTimer(ptr::null_mut(), timer) } == 0 {
            Err(last_error())
        } else {
            Ok(())
        }
    }
}

fn last_error() -> u32 {
    // SAFETY: Read this thread's diagnostic from the immediately preceding call.
    unsafe { GetLastError() }
}

struct WindowsInput<'a> {
    host: &'a mut dyn Host,
    started: Instant,
}

impl WindowsInput<'_> {
    fn now(&self) -> u64 {
        u64::try_from(self.started.elapsed().as_nanos() / 100).unwrap_or(u64::MAX)
    }
}

impl Input for WindowsInput<'_> {
    fn admission(&mut self) -> Admission<'_> {
        let console = active_console();
        let mut snapshot = self.host.admission();
        snapshot.active_console &= console;
        snapshot
    }

    fn stop_requested(&self) -> bool {
        self.host.stop_requested()
    }

    fn keyboard_speed(&self) -> Option<u32> {
        let mut speed = 0u32;
        // SAFETY: SPI_GETKEYBOARDSPEED writes a UINT to this out-pointer.
        (unsafe {
            SystemParametersInfoW(SPI_GETKEYBOARDSPEED, 0, ptr::from_mut(&mut speed).cast(), 0)
        } != 0)
            .then_some(speed)
    }

    fn released(&self, context: Context) -> (bool, bool) {
        if !context.valid() || !(1..=255).all(key_released) {
            return (false, false);
        }

        // SAFETY: The layout comes from GetKeyboardLayout. Invalid mappings
        // yield zero, which cannot admit an all-released observation.
        let [a, d] = [0x1e, 0x20]
            .map(|scan| unsafe { MapVirtualKeyExW(scan, MAPVK_VSC_TO_VK_EX, context.layout as _) });
        let mapped = (1..=255).contains(&a)
            && (1..=255).contains(&d)
            && a != d
            && key_released(a as i32)
            && key_released(d as i32);
        (true, mapped)
    }
}

impl Output for WindowsInput<'_> {
    fn observe(&mut self) -> Observation {
        Observation {
            stop_requested: self.host.stop_requested(),
            active_console: active_console(),
            context: foreground_context(),
            at: self.now(),
        }
    }

    fn submit(&mut self, batch: Batch) -> Submission {
        let tag = (0x4f42_5354_0000_0000u64 | u64::from(std::process::id())) as usize;
        let mut inputs = [INPUT::default(); 2];
        match batch {
            Batch::One(stroke) => inputs[0] = encode(stroke, tag),
            Batch::Pair(first, second) => inputs = [encode(first, tag), encode(second, tag)],
        }

        // SAFETY: inputs holds exactly the initialized prefix named by count.
        // USER32 returns the exact insertion count, including a zero or short
        // count. The policy owns the failure decision and never replays it.
        let accepted = unsafe {
            SendInput(
                batch.count() as u32,
                inputs.as_ptr(),
                size_of::<INPUT>() as i32,
            )
        };
        Submission::Count {
            accepted: accepted as usize,
            completed_at: self.now(),
        }
    }
}

fn encode(stroke: Stroke, tag: usize) -> INPUT {
    let scan = match stroke.key {
        Key::A => 0x1e,
        Key::D => 0x20,
    };
    INPUT {
        r#type: INPUT_KEYBOARD,
        Anonymous: INPUT_0 {
            ki: KEYBDINPUT {
                wScan: scan,
                dwFlags: KEYEVENTF_SCANCODE | if stroke.down { 0 } else { KEYEVENTF_KEYUP },
                dwExtraInfo: tag,
                ..KEYBDINPUT::default()
            },
        },
    }
}

fn active_console() -> bool {
    let mut current = 0u32;
    // SAFETY: current is writable. The active console API needs no arguments.
    unsafe {
        ProcessIdToSessionId(std::process::id(), &mut current) != 0
            && current != 0
            && current != u32::MAX
            && current == WTSGetActiveConsoleSessionId()
    }
}

fn foreground_context() -> Context {
    // SAFETY: HWND is only passed back to USER32. A vanished window returns
    // thread ID zero and must not fall back to this helper's keyboard layout.
    unsafe {
        let foreground = GetForegroundWindow();
        if foreground.is_null() {
            return Context::default();
        }
        let thread = GetWindowThreadProcessId(foreground, ptr::null_mut());
        let layout = if thread == 0 {
            0
        } else {
            GetKeyboardLayout(thread) as usize
        };
        Context {
            foreground: foreground as usize,
            layout,
        }
    }
}

fn key_released(key: i32) -> bool {
    // SAFETY: Callers pass a checked virtual-key code.
    unsafe { GetAsyncKeyState(key) >= 0 }
}
