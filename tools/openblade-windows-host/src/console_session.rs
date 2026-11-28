#![allow(unsafe_code)]

use windows_sys::Win32::System::RemoteDesktop::WTSGetActiveConsoleSessionId;

pub(crate) fn observe() -> Option<u32> {
    // SAFETY: The API takes no arguments and acquires no resources.
    from_windows(unsafe { WTSGetActiveConsoleSessionId() })
}

pub(crate) fn from_windows(session: u32) -> Option<u32> {
    // Windows reports no attached console during attach/detach transitions.
    (session != u32::MAX).then_some(session)
}
