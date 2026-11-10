#![allow(unsafe_code)]

use std::{ffi::c_void, ptr::addr_of, time::Instant};

use windows_sys::{
    Win32::{
        System::{
            Power::{GetSystemPowerStatus, POWERBROADCAST_SETTING, SYSTEM_POWER_STATUS},
            Services::SERVICE_CONTROL_POWEREVENT,
            SystemServices::{GUID_ACDC_POWER_SOURCE, GUID_CONSOLE_DISPLAY_STATE},
        },
        UI::WindowsAndMessaging::PBT_POWERSETTINGCHANGE,
    },
    core::GUID,
};

use super::{Callbacks, Observation, PowerSetting};

pub(crate) fn observe() -> Observation {
    let mut status = SYSTEM_POWER_STATUS::default();
    // SAFETY: status is writable for the duration of the call.
    let success = unsafe { GetSystemPowerStatus(&mut status) };
    Observation::from_windows(
        (success != 0).then_some(status.ACLineStatus),
        crate::console_session::observe(),
    )
}

/// SAFETY: For PBT_POWERSETTINGCHANGE, a non-null event_data must be the live
/// SCM POWERBROADCAST_SETTING buffer with the payload size Windows supplies.
pub(crate) unsafe fn control(
    callbacks: &Callbacks,
    code: u32,
    event: u32,
    event_data: *const c_void,
) {
    let mut setting = None;
    if code == SERVICE_CONTROL_POWEREVENT
        && event == PBT_POWERSETTINGCHANGE
        && !event_data.is_null()
    {
        let data = event_data.cast::<POWERBROADCAST_SETTING>();
        // SAFETY: The caller supplies the SCM header. The payload is read
        // only for the recognized display GUID with a complete DWORD.
        unsafe {
            let guid = addr_of!((*data).PowerSetting).read_unaligned();
            let length = addr_of!((*data).DataLength).read_unaligned();
            if same_guid(guid, GUID_ACDC_POWER_SOURCE) && length >= 4 {
                setting = Some(PowerSetting::Source);
            } else if same_guid(guid, GUID_CONSOLE_DISPLAY_STATE) && length >= 4 {
                let level = addr_of!((*data).Data).cast::<u32>().read_unaligned();
                setting = Some(PowerSetting::Display(level));
            }
        }
    }
    // The WTS payload, including its session ID, is deliberately unused.
    callbacks.control(code, event, setting, Instant::now());
}

fn same_guid(a: GUID, b: GUID) -> bool {
    (a.data1, a.data2, a.data3, a.data4) == (b.data1, b.data2, b.data3, b.data4)
}
