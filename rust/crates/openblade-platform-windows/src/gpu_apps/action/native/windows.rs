//! Win32 bindings for the retained-handle close backend. Every process read
//! and action uses a handle from `open`; no request value becomes a path,
//! window or command.
#![allow(unsafe_code)]

use super::{Exit, Held, ProcessApi, Terminated, WindowClose};
use crate::process_query::{
    image_path, open_process, open_process_token, process_times, token_identity,
};
use std::{
    os::windows::io::{AsRawHandle, OwnedHandle},
    ptr::null_mut,
    sync::atomic::{AtomicBool, Ordering::Relaxed},
    time::{Duration, Instant},
};
use windows_sys::{
    Wdk::System::Threading::{NtQueryInformationProcess, ProcessBasicInformation},
    Win32::{
        Foundation::{
            ERROR_ACCESS_DENIED, ERROR_INVALID_PARAMETER, GetLastError, HANDLE, HWND, LPARAM,
            WAIT_OBJECT_0, WAIT_TIMEOUT,
        },
        Security::{
            GetTokenInformation, SECURITY_MAX_SID_SIZE, TOKEN_MANDATORY_LABEL, TokenIntegrityLevel,
        },
        System::Threading::{
            GetCurrentProcess, GetProcessId, PROCESS_BASIC_INFORMATION, PROCESS_SYNCHRONIZE,
            PROCESS_TERMINATE, TerminateProcess, WaitForSingleObject,
        },
        UI::WindowsAndMessaging::{
            EnumWindows, GW_OWNER, GWL_STYLE, GetWindow, GetWindowLongW, GetWindowThreadProcessId,
            IsWindowVisible, PostMessageW, WM_CLOSE, WS_DISABLED,
        },
    },
    core::BOOL,
};

const WAIT_SLICE: Duration = Duration::from_millis(25);

pub(super) struct WindowsProcesses;

impl ProcessApi for WindowsProcesses {
    type Process = OwnedHandle;

    fn open(&mut self, process_id: i32, terminate: bool) -> Result<Option<OwnedHandle>, ()> {
        let process_id = u32::try_from(process_id).map_err(|_| ())?;
        let access = PROCESS_SYNCHRONIZE | if terminate { PROCESS_TERMINATE } else { 0 };
        match open_process(process_id, access) {
            Ok(process) => Ok(Some(process)),
            // OpenProcess reports a PID without a process object this way.
            Err(ERROR_INVALID_PARAMETER) => Ok(None),
            Err(_) => Err(()),
        }
    }

    fn identity(&mut self, process: &OwnedHandle) -> Option<Held> {
        let handle = process.as_raw_handle();
        // SAFETY: The held process handle has synchronize access.
        match unsafe { WaitForSingleObject(handle, 0) } {
            WAIT_OBJECT_0 => return Some(Held::exited()),
            WAIT_TIMEOUT => {}
            _ => return None,
        }
        // SAFETY: The process handle is held for this call.
        let process_id = i32::try_from(unsafe { GetProcessId(handle) })
            .ok()
            .filter(|id| *id > 0)?;
        let (created, _) = process_times(process)?;
        let (user_sid, session_id) = token_identity(handle)?;
        Some(Held {
            process_id,
            created_filetime: i64::try_from(created).ok()?,
            user_sid,
            session_id,
            image_path: String::from_utf16(&image_path(process)?).ok()?,
            parent_process_id: parent_process_id(handle)?,
            exited: false,
        })
    }

    fn terminate(&mut self, process: &OwnedHandle) -> Terminated {
        let handle = process.as_raw_handle();
        // SAFETY: The held handle has terminate access. -1 matches managed Kill.
        if unsafe { TerminateProcess(handle, u32::MAX) } != 0 {
            return Terminated::Submitted;
        }
        // SAFETY: Read the error of the failed call, then poll the held handle.
        if unsafe { GetLastError() } == ERROR_ACCESS_DENIED
            && unsafe { WaitForSingleObject(handle, 0) } == WAIT_OBJECT_0
        {
            Terminated::Exited
        } else {
            Terminated::Failed
        }
    }

    fn wait_exit(
        &mut self,
        process: &OwnedHandle,
        timeout: Duration,
        cancelled: &AtomicBool,
    ) -> Exit {
        let deadline = Instant::now() + timeout;
        loop {
            let remaining = deadline.saturating_duration_since(Instant::now());
            let slice = remaining.min(WAIT_SLICE).as_millis() as u32;
            // SAFETY: The held process handle has synchronize access.
            match unsafe { WaitForSingleObject(process.as_raw_handle(), slice) } {
                WAIT_OBJECT_0 => return Exit::Exited,
                WAIT_TIMEOUT if !remaining.is_zero() && !cancelled.load(Relaxed) => {}
                _ => return Exit::Unconfirmed,
            }
        }
    }

    fn child_ids(&mut self, parent: i32) -> Vec<i32> {
        let mut children = Vec::new();
        crate::process_snapshot::visit_processes(|entry| {
            if i32::try_from(entry.th32ParentProcessID) == Ok(parent)
                && let Ok(child) = i32::try_from(entry.th32ProcessID)
                && child > 0
            {
                children.push(child);
            }
            true
        });
        children
    }

    fn close_main_window(&mut self, process: &OwnedHandle, process_id: i32) -> WindowClose {
        let Ok(process_id) = u32::try_from(process_id) else {
            return WindowClose::NoWindow;
        };
        let mut finder = MainWindow {
            process_id,
            window: null_mut(),
        };
        // SAFETY: The callback only reads window metadata and writes `finder`,
        // which outlives this synchronous enumeration.
        unsafe { EnumWindows(Some(find_main_window), (&raw mut finder) as LPARAM) };
        let window = finder.window;
        // SAFETY: Window queries tolerate a window destroyed since enumeration.
        if window.is_null()
            || unsafe { GetWindowLongW(window, GWL_STYLE) } as u32 & WS_DISABLED != 0
            || window_process(window) != process_id
            || unsafe { WaitForSingleObject(process.as_raw_handle(), 0) } != WAIT_TIMEOUT
        {
            return WindowClose::NoWindow;
        }
        // SAFETY: WM_CLOSE carries no pointers.
        if unsafe { PostMessageW(window, WM_CLOSE, 0, 0) } != 0 {
            WindowClose::Posted
        } else {
            WindowClose::Failed
        }
    }

    fn current_process_id(&self) -> i32 {
        i32::try_from(std::process::id()).unwrap_or(0)
    }

    fn integrity(&mut self, process: &OwnedHandle) -> Option<u32> {
        token_integrity(process.as_raw_handle())
    }

    fn current_integrity(&mut self) -> Option<u32> {
        // SAFETY: The current-process pseudo-handle needs no closing.
        token_integrity(unsafe { GetCurrentProcess() })
    }
}

/// The RID of the token's mandatory label (`S-1-16-<RID>`). Any other shape
/// is unreadable, so the caller refuses the process.
fn token_integrity(process: HANDLE) -> Option<u32> {
    let token = open_process_token(process)?;
    const LABEL_BYTES: usize = size_of::<TOKEN_MANDATORY_LABEL>();
    const BUFFER_WORDS: usize =
        (LABEL_BYTES + SECURITY_MAX_SID_SIZE as usize).div_ceil(size_of::<usize>());
    let mut buffer = [0_usize; BUFFER_WORDS];
    let base = buffer.as_mut_ptr().cast::<u8>();
    let capacity = size_of_val(&buffer);
    let mut used = 0;
    // SAFETY: The initialized buffer is aligned for TOKEN_MANDATORY_LABEL and
    // has space for the maximum binary SID.
    if unsafe {
        GetTokenInformation(
            token.as_raw_handle(),
            TokenIntegrityLevel,
            base.cast(),
            capacity as u32,
            &mut used,
        )
    } == 0
        || !(LABEL_BYTES..=capacity).contains(&(used as usize))
    {
        return None;
    }
    // SAFETY: A complete aligned TOKEN_MANDATORY_LABEL was returned.
    let sid = unsafe { (*base.cast::<TOKEN_MANDATORY_LABEL>()).Label.Sid };
    let offset = sid
        .addr()
        .checked_sub(base.addr())
        .filter(|&offset| offset >= LABEL_BYTES)?;
    // SAFETY: The validated output length fits in the initialized buffer.
    let payload = unsafe { std::slice::from_raw_parts(base, used as usize) }.get(offset..)?;
    // Revision 1, one subauthority, SECURITY_MANDATORY_LABEL_AUTHORITY.
    let [1, 1, 0, 0, 0, 0, 0, 16, a, b, c, d, ..] = *payload else {
        return None;
    };
    Some(u32::from_le_bytes([a, b, c, d]))
}

/// Managed `MainWindowFinder`: the first enumerated top-level window of the
/// process that has no owner and is visible.
struct MainWindow {
    process_id: u32,
    window: HWND,
}

unsafe extern "system" fn find_main_window(window: HWND, finder: LPARAM) -> BOOL {
    // SAFETY: EnumWindows passes back the exclusive `MainWindow` pointer.
    let finder = unsafe { &mut *(finder as *mut MainWindow) };
    // SAFETY: Read-only queries of an enumerated window.
    if window_process(window) == finder.process_id
        && unsafe { GetWindow(window, GW_OWNER) }.is_null()
        && unsafe { IsWindowVisible(window) } != 0
    {
        finder.window = window;
        return 0;
    }
    1
}

fn window_process(window: HWND) -> u32 {
    let mut process_id = 0;
    // SAFETY: The output is writable; a destroyed window leaves it zero.
    unsafe { GetWindowThreadProcessId(window, &mut process_id) };
    process_id
}

/// The creator PID recorded in the held process, as managed `ParentProcessId`.
fn parent_process_id(process: HANDLE) -> Option<i32> {
    let mut information = PROCESS_BASIC_INFORMATION::default();
    let mut used = 0;
    // SAFETY: The output buffer is the SDK structure for this class and the
    // process handle is held with limited query access.
    let status = unsafe {
        NtQueryInformationProcess(
            process,
            ProcessBasicInformation,
            (&raw mut information).cast(),
            size_of::<PROCESS_BASIC_INFORMATION>() as u32,
            &mut used,
        )
    };
    if status < 0 || used as usize != size_of::<PROCESS_BASIC_INFORMATION>() {
        return None;
    }
    i32::try_from(information.InheritedFromUniqueProcessId).ok()
}
