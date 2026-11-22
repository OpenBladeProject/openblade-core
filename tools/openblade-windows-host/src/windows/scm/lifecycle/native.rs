#![allow(unsafe_code)]

use windows_sys::Win32::System::Power::{GetSystemPowerStatus, SYSTEM_POWER_STATUS};

use super::Observation;

pub(crate) fn observe() -> Observation {
    let mut status = SYSTEM_POWER_STATUS::default();
    // SAFETY: status is writable for the duration of the call.
    let success = unsafe { GetSystemPowerStatus(&mut status) };
    Observation::from_windows(
        (success != 0).then_some(status.ACLineStatus),
        crate::console_session::observe(),
    )
}
