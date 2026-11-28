//! The production supplier: the current user's configured AC or DC power mode
//! (managed `WindowsPowerModeController`). Tests never construct it.
//!
//! Managed binds the four powrprof functions lazily (`DllImport`), so a
//! Windows build without them fails only the apply, with
//! `EntryPointNotFoundException`. They are resolved here at each apply from the
//! System32 copy of powrprof.dll for the same reason, through the shared
//! loader.
#![allow(unsafe_code)]
use std::sync::atomic::{AtomicBool, Ordering::SeqCst};

use windows_sys::core::GUID;

use super::{Applied, Failure, Mode, Supplier, Supply};
use crate::session_native::powrprof::{Function, Library};

/// Managed `BestPowerEfficiencyGuid`.
const BEST_POWER_EFFICIENCY: u128 = 0x961c_c777_2547_4f9d_8174_7d86_181b_8a7a;
/// Managed `Guid.Empty`: Windows' Balanced overlay.
const BALANCED: u128 = 0;
/// Managed `BestPerformanceGuid`.
const BEST_PERFORMANCE: u128 = 0xded5_74b5_45a0_4f42_8737_4634_5c09_c238;

type Get = unsafe extern "system" fn(*mut GUID) -> u32;
type Set = unsafe extern "system" fn(*const GUID) -> u32;

pub(super) struct UserPowerModes;

impl Supplier for UserPowerModes {
    fn apply(&mut self, supply: Supply, mode: Mode, canceled: &AtomicBool) -> Applied {
        if canceled.load(SeqCst) {
            return Applied::Canceled;
        }
        let Some(library) = Library::load() else {
            return Applied::Failed(Failure::ApiUnavailable);
        };
        let names: (&[u8], &[u8]) = match supply {
            Supply::Ac => (
                b"PowerGetUserConfiguredACPowerMode\0",
                b"PowerSetUserConfiguredACPowerMode\0",
            ),
            Supply::Dc => (
                b"PowerGetUserConfiguredDCPowerMode\0",
                b"PowerSetUserConfiguredDCPowerMode\0",
            ),
        };
        let (Some(get), Some(set)) = (library.function(names.0), library.function(names.1)) else {
            return Applied::Failed(Failure::ApiUnavailable);
        };
        // SAFETY: both exports have exactly these documented signatures.
        let (get, set) = unsafe {
            (
                std::mem::transmute::<Function, Get>(get),
                std::mem::transmute::<Function, Set>(set),
            )
        };
        let read = || {
            let mut current = GUID::from_u128(0);
            // SAFETY: Windows writes one GUID into the owned value.
            match unsafe { get(&mut current) } {
                0 => Ok(bits(&current)),
                code => Err(code),
            }
        };
        let requested = match mode {
            Mode::BestPowerEfficiency => BEST_POWER_EFFICIENCY,
            Mode::Balanced => BALANCED,
            Mode::BestPerformance => BEST_PERFORMANCE,
        };
        match read() {
            Ok(current) if current == requested => {
                return Applied::Succeeded { changed: false };
            }
            Ok(_) => (),
            Err(code) => return Applied::Failed(Failure::ReadFailed(code)),
        }
        if canceled.load(SeqCst) {
            return Applied::Canceled;
        }
        let guid = GUID::from_u128(requested);
        // SAFETY: Windows reads one GUID from the owned value.
        let code = unsafe { set(&guid) };
        if code != 0 {
            return Applied::Failed(Failure::SetFailed(code));
        }
        // This confirms only the user's configured mode. Windows can still
        // select a different effective mode while another system signal is active.
        match read() {
            Ok(confirmed) if confirmed == requested => Applied::Succeeded { changed: true },
            Ok(_) => Applied::Failed(Failure::NotRetained),
            Err(code) => Applied::Failed(Failure::ConfirmFailed(code)),
        }
    }
}

fn bits(guid: &GUID) -> u128 {
    (u128::from(guid.data1) << 96)
        | (u128::from(guid.data2) << 80)
        | (u128::from(guid.data3) << 64)
        | u128::from(u64::from_be_bytes(guid.data4))
}

#[cfg(test)]
mod tests {
    use super::*;

    // The GUID layout only; no Windows function is called.
    #[test]
    fn mode_guids_round_trip_the_managed_values() {
        for value in [BEST_POWER_EFFICIENCY, BALANCED, BEST_PERFORMANCE] {
            assert_eq!(bits(&GUID::from_u128(value)), value);
        }
        assert_eq!(
            format!("{:032x}", BEST_POWER_EFFICIENCY),
            "961cc77725474f9d81747d86181b8a7a"
        );
        assert_eq!(
            format!("{:032x}", BEST_PERFORMANCE),
            "ded574b545a04f42873746345c09c238"
        );
    }
}
