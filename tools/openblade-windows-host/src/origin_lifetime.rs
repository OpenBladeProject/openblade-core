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
}
impl Default for LifecycleState {
    // Match managed LowPowerGate's initial clear low-power latch. This is not
    // an independent awake measurement: only a confirmed suspend enters low power.
    // Display-off is not sleep (ServiceLifecycleRefreshLoop.cs:134-138).
    fn default() -> Self {
        Self {
            epoch: 1,
            awake: true,
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
}
