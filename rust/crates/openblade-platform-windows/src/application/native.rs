#![allow(unsafe_code)]

use std::num::NonZeroU32;
use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};
use std::ptr::null_mut;

use openblade_application_reporter::ProcessIdentity;
use windows_sys::Win32::Foundation::{FILETIME, WAIT_TIMEOUT};
use windows_sys::Win32::Storage::FileSystem::GetFullPathNameW;
use windows_sys::Win32::System::RemoteDesktop::{
    ProcessIdToSessionId, WTS_CONNECTSTATE_CLASS, WTSActive, WTSConnectState, WTSFreeMemory,
    WTSGetActiveConsoleSessionId, WTSQuerySessionInformationW,
};
use windows_sys::Win32::System::StationsAndDesktops::{
    CloseDesktop, DESKTOP_READOBJECTS, GetUserObjectInformationW, HDESK, OpenInputDesktop, UOI_NAME,
};
use windows_sys::Win32::System::Threading::{
    GetCurrentProcessId, GetProcessId, GetProcessTimes, OpenProcess,
    PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_SYNCHRONIZE, QueryFullProcessImageNameW,
    WaitForSingleObject,
};
use windows_sys::Win32::UI::WindowsAndMessaging::{GetForegroundWindow, GetWindowThreadProcessId};

use super::{Desktop, Lifetime, Owner, path, process_identity};

#[link(name = "icu", kind = "raw-dylib")]
unsafe extern "C" {
    fn u_toupper(code_point: i32) -> i32;
}

pub struct Windows;
pub(super) struct Process(OwnedHandle);

impl Desktop for Windows {
    type Process = Process;

    fn active_local_session(&mut self) -> Option<NonZeroU32> {
        // SAFETY: no arguments. Console ID is u32::MAX during transitions.
        let (own_pid, console) = unsafe { (GetCurrentProcessId(), WTSGetActiveConsoleSessionId()) };
        let session = self.process_session(NonZeroU32::new(own_pid)?)?;
        if session.get() != console || console == u32::MAX {
            return None;
        }
        (wts_active(session) && default_input_desktop()).then_some(session)
    }

    fn foreground(&mut self) -> usize {
        // SAFETY: no arguments, returned HWND is used as an opaque token.
        unsafe { GetForegroundWindow() as usize }
    }

    fn owner(&mut self, window: usize) -> Option<Owner> {
        let mut pid = 0;
        // SAFETY: HWND is an opaque token and pid is writable.
        let thread = unsafe { GetWindowThreadProcessId(window as _, &mut pid) };
        Some(Owner {
            thread: NonZeroU32::new(thread)?,
            pid: NonZeroU32::new(pid)?,
        })
    }

    fn process_session(&mut self, pid: NonZeroU32) -> Option<NonZeroU32> {
        let mut session = 0;
        // SAFETY: session is writable.
        if unsafe { ProcessIdToSessionId(pid.get(), &mut session) } == 0 {
            return None;
        }
        NonZeroU32::new(session)
    }

    fn open_query_and_synchronize(&mut self, pid: NonZeroU32) -> Option<Process> {
        // SAFETY: OpenProcess accepts a PID and returns a newly owned handle on success.
        let handle = unsafe {
            OpenProcess(
                PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SYNCHRONIZE,
                0,
                pid.get(),
            )
        };
        if handle.is_null() {
            return None;
        }
        // SAFETY: nonnull OpenProcess success; ownership is transferred once.
        Some(Process(unsafe { OwnedHandle::from_raw_handle(handle) }))
    }

    fn full_path(&mut self, input: &str) -> Option<String> {
        let terminated: Vec<_> = input.encode_utf16().chain([0]).collect();
        let mut output = vec![u16::MAX; path::PATH_BUFFER_UNITS];
        // SAFETY: terminated input and a separate writable buffer with its exact size.
        let length = unsafe {
            GetFullPathNameW(
                terminated.as_ptr(),
                output.len() as u32,
                output.as_mut_ptr(),
                null_mut(),
            )
        };
        path::decode(&output, length as usize)
    }

    fn paths_equal(first: &str, second: &str) -> bool {
        // SAFETY: ICU accepts a Unicode scalar and returns its simple uppercase.
        path::ordinal_ignore_case(first, second, |scalar| unsafe {
            u_toupper(scalar as i32) as u32
        })
    }
}

impl Lifetime for Process {
    fn identity(&self) -> Option<ProcessIdentity> {
        let handle = self.0.as_raw_handle();
        let (mut creation, mut exit, mut kernel, mut user) = Default::default();
        // SAFETY: owned process handle and four distinct writable FILETIMEs.
        if unsafe { GetProcessTimes(handle, &mut creation, &mut exit, &mut kernel, &mut user) } == 0
        {
            return None;
        }
        let FILETIME {
            dwHighDateTime,
            dwLowDateTime,
        } = creation;
        let generation = (u64::from(dwHighDateTime) << 32) | u64::from(dwLowDateTime);
        // SAFETY: owned process handle; this PID is read from the handle, not the window.
        process_identity(unsafe { GetProcessId(handle) }, generation)
    }

    fn image_path(&self) -> Option<String> {
        let mut buffer = vec![u16::MAX; path::PATH_BUFFER_UNITS];
        let mut length = buffer.len() as u32;
        // SAFETY: owned process handle and writable buffer with its exact size.
        // Flags zero requests the Win32 executable path.
        if unsafe {
            QueryFullProcessImageNameW(self.0.as_raw_handle(), 0, buffer.as_mut_ptr(), &mut length)
        } == 0
        {
            return None;
        }
        path::decode(&buffer, length as usize)
    }

    fn alive(&self) -> bool {
        // SAFETY: this owned process handle has SYNCHRONIZE. Timeout zero never blocks.
        unsafe { WaitForSingleObject(self.0.as_raw_handle(), 0) == WAIT_TIMEOUT }
    }
}

struct WtsBuffer(*mut u16);

impl Drop for WtsBuffer {
    fn drop(&mut self) {
        // SAFETY: only constructed with an owned buffer returned by WTS.
        unsafe { WTSFreeMemory(self.0.cast()) }
    }
}

fn wts_active(session: NonZeroU32) -> bool {
    let mut ptr = null_mut();
    let mut bytes = 0;
    // SAFETY: current server, explicit session, and writable output locations.
    if unsafe {
        WTSQuerySessionInformationW(
            null_mut(),
            session.get(),
            WTSConnectState,
            &mut ptr,
            &mut bytes,
        )
    } == 0
        || ptr.is_null()
    {
        return false;
    }
    let buffer = WtsBuffer(ptr);
    // SAFETY: WTSConnectState returns this enum; only read if the length covers it.
    bytes as usize == size_of::<WTS_CONNECTSTATE_CLASS>()
        && unsafe { buffer.0.cast::<WTS_CONNECTSTATE_CLASS>().read_unaligned() == WTSActive }
}

struct InputDesktop(HDESK);

impl Drop for InputDesktop {
    fn drop(&mut self) {
        // SAFETY: only constructed with an owned input desktop handle.
        unsafe { CloseDesktop(self.0) };
    }
}

fn default_input_desktop() -> bool {
    // SAFETY: request only read access, without inheritance or switching desktops.
    let handle = unsafe { OpenInputDesktop(0, 0, DESKTOP_READOBJECTS) };
    if handle.is_null() {
        return false;
    }
    let desktop = InputDesktop(handle);
    let mut name = [u16::MAX; 256];
    let mut bytes = 0;
    // SAFETY: valid desktop handle and writable buffer with its size in bytes.
    if unsafe {
        GetUserObjectInformationW(
            desktop.0,
            UOI_NAME,
            name.as_mut_ptr().cast(),
            size_of_val(&name) as u32,
            &mut bytes,
        )
    } == 0
        || bytes as usize > size_of_val(&name)
    {
        return false;
    }
    let length = name.iter().position(|&unit| unit == 0);
    length
        .and_then(|length| path::decode(&name, length))
        .is_some_and(|name| name.eq_ignore_ascii_case("Default"))
}
