//! Sticky loaded-origin outcome shared by ingress and publication checks.
use std::{
    io,
    sync::{
        Mutex,
        atomic::{AtomicU8, Ordering},
    },
};
const LIVE: u8 = 0;
const STOP: u8 = 1;
const LOST: u8 = 2;
#[derive(Default)]
pub(crate) struct Lifetime(AtomicU8);
impl Lifetime {
    pub(crate) fn stop(&self) {
        let _ = self
            .0
            .compare_exchange(LIVE, STOP, Ordering::AcqRel, Ordering::Acquire);
    }
    pub(crate) fn lose_trust(&self) {
        self.0.store(LOST, Ordering::Release);
    }
    pub(crate) fn verify_owned_stop(&self) -> io::Result<()> {
        if self.0.load(Ordering::Acquire) == STOP {
            Ok(())
        } else {
            Err(io::ErrorKind::PermissionDenied.into())
        }
    }
    pub(crate) fn verify(&self) -> io::Result<()> {
        match self.0.load(Ordering::Acquire) {
            LIVE => Ok(()),
            STOP => Err(io::Error::new(io::ErrorKind::Interrupted, ExpectedStop)),
            _ => Err(io::Error::new(
                io::ErrorKind::PermissionDenied,
                "loaded origin trust was lost",
            )),
        }
    }
}
#[derive(Debug)]
struct ExpectedStop;
impl std::fmt::Display for ExpectedStop {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("loaded origin revoked for expected STOP")
    }
}
impl std::error::Error for ExpectedStop {}
pub(crate) fn expected_stop(error: &io::Error) -> bool {
    error
        .get_ref()
        .is_some_and(|cause| cause.is::<ExpectedStop>())
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn trust_loss_is_sticky_and_generic_errors_are_not_expected_stop() {
        let state = Lifetime::default();
        state.lose_trust();
        state.stop();
        assert!(!expected_stop(&state.verify().unwrap_err()));
        let state = Lifetime::default();
        state.stop();
        assert!(expected_stop(&state.verify().unwrap_err()));
        state.lose_trust();
        state.stop();
        assert!(!expected_stop(&state.verify().unwrap_err()));
        assert!(!expected_stop(&io::Error::new(
            io::ErrorKind::Interrupted,
            "stop"
        )));
        assert!(!expected_stop(&io::Error::from(
            io::ErrorKind::PermissionDenied
        )));
    }
}

/// Callback-established active boundary. Unknown events fence old operations but
/// never establish resume; fresh device/session/controller proof is still required.
#[derive(Default)]
pub(crate) struct Lifecycle(Mutex<LifecycleState>);
struct LifecycleState {
    epoch: u64,
    awake: bool,
    // The last sampled Windows ACLineStatus kind (1 battery, 2 AC)
    // and how many times a sample has changed it.
    power_kind: Option<u8>,
    power_generation: u64,
}
impl Default for LifecycleState {
    // Match managed LowPowerGate's initial clear low-power latch. This is not
    // an independent awake measurement: only a confirmed suspend enters low power.
    // Display-off is not sleep (ServiceLifecycleRefreshLoop.cs:134-138).
    fn default() -> Self {
        Self {
            epoch: 1,
            awake: true,
            power_kind: None,
            power_generation: 0,
        }
    }
}
impl Lifecycle {
    pub(crate) fn verify_health(&self) -> io::Result<()> {
        self.0
            .lock()
            .map(|_| ())
            .map_err(|_| io::Error::other("lifecycle state lock poisoned"))
    }
    pub(crate) fn ingress(&self, code: u32, event: u32) {
        use windows_sys::Win32::System::Services::{
            SERVICE_CONTROL_POWEREVENT, SERVICE_CONTROL_SESSIONCHANGE,
        };
        if !matches!(
            code,
            SERVICE_CONTROL_POWEREVENT | SERVICE_CONTROL_SESSIONCHANGE
        ) {
            return;
        }
        let Ok(mut state) = self.0.lock() else {
            return;
        };
        if state.epoch == 0 {
            return;
        } // Exhaustion is sticky; never revive epoch one.
        state.epoch = state.epoch.checked_add(1).unwrap_or(0);
        match (code, event) {
            (SERVICE_CONTROL_POWEREVENT, 4) => state.awake = false, // PBT_APMSUSPEND
            (SERVICE_CONTROL_POWEREVENT, 7 | 18) => state.awake = true, // resume suspend/automatic
            // Session notices fence old work; their payload never authorizes a user.
            // Fresh exact WTSInfoEx state is checked by the mutation supplier.
            _ => {}
        }
    }
    pub(crate) fn active(&self) -> Option<u64> {
        let state = self.0.lock().ok()?;
        (state.epoch != 0 && state.awake).then_some(state.epoch)
    }
    /// Records a sampled Windows power kind and returns the power generation,
    /// which advances only when a sample changes the kind (managed
    /// `ServiceControlRuntime.windowsPowerGeneration`). A failed sample (`None`)
    /// is not a power boundary. Unlike the lifecycle epoch, session notices and
    /// power notices that keep the kind never advance it.
    #[cfg_attr(not(feature = "service-thermal-mutation-v1"), allow(dead_code))]
    pub(crate) fn observe_power_kind(&self, kind: Option<u8>) -> Option<u64> {
        let mut state = self.0.lock().ok()?;
        if let Some(kind) = kind {
            if state.power_kind.is_some_and(|prior| prior != kind) {
                state.power_generation = state.power_generation.saturating_add(1);
            }
            state.power_kind = Some(kind);
        }
        Some(state.power_generation)
    }
}
/// The current Windows ACLineStatus kind, or `None` when the read fails or
/// reports an unknown status.
#[cfg(feature = "service-thermal-mutation-v1")]
#[cfg_attr(test, allow(dead_code))]
#[allow(unsafe_code)]
pub(crate) fn sample_power_kind() -> Option<u8> {
    use windows_sys::Win32::System::Power::{GetSystemPowerStatus, SYSTEM_POWER_STATUS};
    let mut status = SYSTEM_POWER_STATUS::default();
    // SAFETY: writable native output.
    if unsafe { GetSystemPowerStatus(&mut status) } == 0 {
        return None;
    }
    power_kind(status.ACLineStatus)
}
/// Windows ACLineStatus as a power kind, as managed `CaptureControlPowerSource`
/// classifies it. An unknown status (255 or any other value) is a failed
/// capture, not a power boundary: managed
/// `WindowsPowerStateProvider.MapPowerSource` throws, and
/// `CaptureControlPowerSource` then keeps the generation.
#[cfg(any(test, feature = "service-thermal-mutation-v1"))]
pub(crate) fn power_kind(ac_line_status: u8) -> Option<u8> {
    match ac_line_status {
        0 => Some(1),
        1 => Some(2),
        _ => None,
    }
}

#[cfg(test)]
mod lifecycle_tests {
    use super::*;
    #[test]
    fn correction_lifecycle_overflow_is_sticky_and_display_status_cannot_resume() {
        use windows_sys::Win32::System::Services::{
            SERVICE_CONTROL_POWEREVENT, SERVICE_CONTROL_SESSIONCHANGE,
        };
        let lifecycle = Lifecycle::default();
        assert_eq!(lifecycle.active(), Some(1));
        lifecycle.ingress(SERVICE_CONTROL_POWEREVENT, 4);
        assert_eq!(lifecycle.active(), None);
        lifecycle.ingress(SERVICE_CONTROL_POWEREVENT, 32787);
        assert_eq!(lifecycle.active(), None);
        lifecycle.ingress(SERVICE_CONTROL_SESSIONCHANGE, 8);
        assert_eq!(lifecycle.active(), None);
        lifecycle.ingress(SERVICE_CONTROL_POWEREVENT, 18);
        assert!(lifecycle.active().is_some());
        lifecycle.0.lock().unwrap().epoch = u64::MAX;
        lifecycle.ingress(SERVICE_CONTROL_POWEREVENT, 10);
        assert_eq!(lifecycle.active(), None);
        lifecycle.ingress(SERVICE_CONTROL_POWEREVENT, 18);
        assert_eq!(lifecycle.active(), None);
    }
    #[test]
    fn power_generation_advances_only_on_a_changed_kind() {
        use windows_sys::Win32::System::Services::{
            SERVICE_CONTROL_POWEREVENT, SERVICE_CONTROL_SESSIONCHANGE,
        };
        let lifecycle = Lifecycle::default();
        assert_eq!(lifecycle.observe_power_kind(Some(2)), Some(0));
        // Lock, unlock and a status notice that keeps AC advance the epoch only.
        lifecycle.ingress(SERVICE_CONTROL_SESSIONCHANGE, 7);
        lifecycle.ingress(SERVICE_CONTROL_SESSIONCHANGE, 8);
        lifecycle.ingress(SERVICE_CONTROL_POWEREVENT, 10);
        assert_eq!(lifecycle.observe_power_kind(Some(2)), Some(0));
        // A failed sample is not a boundary.
        assert_eq!(lifecycle.observe_power_kind(None), Some(0));
        assert_eq!(lifecycle.observe_power_kind(Some(1)), Some(1));
        assert_eq!(lifecycle.observe_power_kind(Some(2)), Some(2));
        // An unknown ACLineStatus is a failed capture, not a boundary.
        assert_eq!(lifecycle.observe_power_kind(power_kind(255)), Some(2));
        assert_eq!(lifecycle.observe_power_kind(power_kind(1)), Some(2));
        assert_eq!(lifecycle.observe_power_kind(power_kind(0)), Some(3));
        assert_eq!(
            [0, 1, 2, 255].map(power_kind),
            [Some(1), Some(2), None, None],
            "ACLineStatus offline, online, out of range and unknown"
        );
    }
}
