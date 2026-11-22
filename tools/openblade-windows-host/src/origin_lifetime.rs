//! Sticky loaded-origin outcome shared by ingress and publication checks.
use openblade_domain::low_power_gate::{LowPowerGate, Signal, Transition, WriteScope};
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

/// Callback-established activity: one lifecycle epoch and the one low-power
/// gate. Every power and session notice advances the epoch, which fences
/// operations captured before it. The gate (managed `LowPowerGate`, ported in
/// `openblade_domain::low_power_gate`) is the only low-power latch: every
/// write admission, capture and timer reads it here, under the same lock as
/// the epoch. Unknown events fence old operations but never establish
/// resume; fresh device/session/controller proof is still required.
#[derive(Default)]
pub(crate) struct Lifecycle(Mutex<LifecycleState>);
struct LifecycleState {
    epoch: u64,
    gate: LowPowerGate,
    // The last sampled Windows ACLineStatus kind (1 battery, 2 AC)
    // and how many times a sample has changed it.
    power_kind: Option<u8>,
    power_generation: u64,
}
impl Default for LifecycleState {
    // Managed LowPowerGate starts clear. This is not an independent awake
    // measurement: only a confirmed suspend enters low power. Display-off is
    // not sleep (ServiceLifecycleRefreshLoop.cs:134-138).
    fn default() -> Self {
        Self {
            epoch: 1,
            gate: LowPowerGate::new(qpc::frequency()),
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
    /// The SCM control callback: its `signal_for` signal, if any, at the
    /// callback's QPC time.
    pub(crate) fn ingress(&self, code: u32, event: u32) {
        self.record(code, signal_for(code, event), qpc::now());
    }
    /// One power or session notice: advance the epoch, then apply its gate
    /// signal, if any, at `timestamp`. This is the entry a power-setting
    /// supplier uses. It passes `Signal::DisplayOn` only for a display
    /// transition from off to on or dimmed, classified as managed
    /// `ClassifyConsoleDisplayTransition` does. Display-off, dimmed-to-on and
    /// AC/DC notices carry no signal and only fence.
    pub(crate) fn record(&self, code: u32, signal: Option<Signal>, timestamp: i64) {
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
        if let Some(signal) = signal {
            // A stale signal changes nothing but has still fenced. Nothing
            // subscribes to transitions yet; a subscriber is notified after
            // this lock is released, as managed raises TransitionChanged.
            let _ = state.gate.apply(signal, timestamp);
        }
    }
    /// The epoch a write of this scope pins now, or `None` while the gate
    /// refuses that scope. A capture pins it and every per-write
    /// authorization compares it again, so a write is refused once any
    /// later notice fenced it or the gate set (managed
    /// `ScopedThermalWriteBlockReason`'s low-power clause).
    pub(crate) fn epoch_for(&self, write: WriteScope) -> Option<u64> {
        let state = self.0.lock().ok()?;
        (state.epoch != 0 && state.gate.admits(write)).then_some(state.epoch)
    }
    /// The epoch an ordinary operation pins, or `None` in low power.
    pub(crate) fn active(&self) -> Option<u64> {
        self.epoch_for(WriteScope::Ordinary)
    }
    /// The current gate transition, for timers that ask whether low power is
    /// active outside a write (managed `IsActive`), or capture a suspend
    /// generation and later test it (`IsActiveForGeneration`). `None` when
    /// the lifecycle is unreadable or exhausted: treat it as low power. Per-write
    /// admission stays authoritative.
    #[cfg_attr(not(test), allow(dead_code))] // Stage 4b timers and D1 cleanup read it.
    pub(crate) fn low_power(&self) -> Option<Transition> {
        let state = self.0.lock().ok()?;
        (state.epoch != 0).then(|| state.gate.transition())
    }
    /// Whether a timer must not arm or must stop now: the gate is set, or
    /// the lifecycle is unreadable or exhausted. This is the fail-closed
    /// form of `low_power()` for callers that need only the answer.
    #[cfg_attr(not(test), allow(dead_code))] // Stage 4b timers read it.
    pub(crate) fn low_power_or_unknown(&self) -> bool {
        self.low_power()
            .is_none_or(|transition| transition.suspending())
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
/// The gate signal of an SCM control, as managed
/// `ClassifySuspendResumeNotification` maps PBT events: PBT_APMSUSPEND
/// suspends, and PBT_APMRESUMESUSPEND or PBT_APMRESUMEAUTOMATIC resumes.
/// Only power events signal. Session notices fence old work, and their
/// payload never authorizes a user; the mutation supplier checks fresh exact
/// WTSInfoEx state.
fn signal_for(code: u32, event: u32) -> Option<Signal> {
    use windows_sys::Win32::{
        System::Services::SERVICE_CONTROL_POWEREVENT,
        UI::WindowsAndMessaging::{PBT_APMRESUMEAUTOMATIC, PBT_APMRESUMESUSPEND, PBT_APMSUSPEND},
    };
    match (code, event) {
        (SERVICE_CONTROL_POWEREVENT, PBT_APMSUSPEND) => Some(Signal::Suspend),
        (SERVICE_CONTROL_POWEREVENT, PBT_APMRESUMESUSPEND | PBT_APMRESUMEAUTOMATIC) => {
            Some(Signal::Resume)
        }
        _ => None,
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

/// The machine QPC clock that managed `TimeProvider.System` reads and every
/// gate supplier stamps with, so their signals order against each other.
/// Tests use a strictly increasing counter at 10 MHz: back-to-back test
/// notices never share a timestamp, and none reaches the two-second
/// new-resume window unless it passes its own timestamps to `record`.
mod qpc {
    use std::num::NonZeroU64;
    #[cfg(not(test))]
    #[allow(unsafe_code)]
    pub(super) fn now() -> i64 {
        let mut count = 0;
        // SAFETY: writable output. It cannot fail on Windows XP and later.
        unsafe { windows_sys::Win32::System::Performance::QueryPerformanceCounter(&mut count) };
        count
    }
    #[cfg(not(test))]
    #[allow(unsafe_code)]
    pub(super) fn frequency() -> NonZeroU64 {
        let mut frequency = 0;
        // SAFETY: writable output. It cannot fail on Windows XP and later,
        // and the frequency is fixed at boot. A zero would only disable the
        // gate's new-resume window, never its latch or ordering.
        unsafe {
            windows_sys::Win32::System::Performance::QueryPerformanceFrequency(&mut frequency)
        };
        u64::try_from(frequency)
            .ok()
            .and_then(NonZeroU64::new)
            .unwrap_or(NonZeroU64::MAX)
    }
    #[cfg(test)]
    pub(super) fn now() -> i64 {
        static NEXT: std::sync::atomic::AtomicI64 = std::sync::atomic::AtomicI64::new(0);
        NEXT.fetch_add(1, std::sync::atomic::Ordering::Relaxed) + 1
    }
    #[cfg(test)]
    pub(super) const fn frequency() -> NonZeroU64 {
        NonZeroU64::new(10_000_000).unwrap()
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
    const POWER: u32 = windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT;
    const SECOND: i64 = 10_000_000;
    fn generation(lifecycle: &Lifecycle) -> u64 {
        lifecycle.low_power().unwrap().generation
    }
    #[test]
    fn scm_suspend_and_resume_drive_the_one_gate_and_other_notices_only_fence() {
        use windows_sys::Win32::System::Services::SERVICE_CONTROL_SESSIONCHANGE;
        let lifecycle = Lifecycle::default();
        assert_eq!(lifecycle.low_power().map(|t| t.suspending()), Some(false));
        for (code, event, expected, epoch) in [
            (POWER, 4, 1, None),
            (SERVICE_CONTROL_SESSIONCHANGE, 7, 1, None),
            (POWER, 10, 1, None),
            (POWER, 32787, 1, None),
            (POWER, 7, 2, Some(6)),
            (POWER, 4, 3, None),
            (POWER, 18, 4, Some(8)),
            (SERVICE_CONTROL_SESSIONCHANGE, 8, 4, Some(9)),
            // A session notice whose event number is PBT_APMSUSPEND only fences.
            (SERVICE_CONTROL_SESSIONCHANGE, 4, 4, Some(10)),
            // A code outside power and session neither fences nor signals.
            (1, 4, 4, Some(10)),
        ] {
            lifecycle.ingress(code, event);
            assert_eq!(generation(&lifecycle), expected, "{code}/{event}");
            assert_eq!(lifecycle.active(), epoch, "{code}/{event}");
        }
    }
    #[test]
    fn only_power_suspend_and_both_resumes_signal_the_gate() {
        use windows_sys::Win32::System::Services::{
            SERVICE_CONTROL_SESSIONCHANGE, SERVICE_CONTROL_STOP,
        };
        for (code, event, signal) in [
            (POWER, 4, Some(Signal::Suspend)),
            (POWER, 7, Some(Signal::Resume)),
            (POWER, 18, Some(Signal::Resume)),
            // Power status, power setting and unknown PBT events only fence.
            (POWER, 10, None),
            (POWER, 32787, None),
            (POWER, 6, None),
            (SERVICE_CONTROL_SESSIONCHANGE, 4, None),
            (SERVICE_CONTROL_SESSIONCHANGE, 7, None),
            (SERVICE_CONTROL_SESSIONCHANGE, 18, None),
            (SERVICE_CONTROL_STOP, 4, None),
        ] {
            assert_eq!(signal_for(code, event), signal, "{code}/{event}");
        }
    }
    #[test]
    fn low_power_or_unknown_fails_closed() {
        let lifecycle = Lifecycle::default();
        assert!(!lifecycle.low_power_or_unknown());
        lifecycle.record(POWER, Some(Signal::Suspend), 1);
        assert!(lifecycle.low_power_or_unknown());
        lifecycle.record(POWER, Some(Signal::Resume), 2);
        assert!(!lifecycle.low_power_or_unknown());
        // Exhausted, then unreadable: both read as low power.
        lifecycle.0.lock().unwrap().epoch = 0;
        assert!(lifecycle.low_power_or_unknown());
        let lifecycle = Lifecycle::default();
        let _ = std::panic::catch_unwind(|| {
            let _guard = lifecycle.0.lock().unwrap();
            panic!("poison the lifecycle lock");
        });
        assert!(lifecycle.0.is_poisoned());
        assert!(lifecycle.low_power_or_unknown());
    }
    #[test]
    fn a_stale_suspend_cannot_set_the_gate_but_still_fences() {
        let lifecycle = Lifecycle::default();
        lifecycle.record(POWER, Some(Signal::Suspend), 100);
        lifecycle.record(POWER, Some(Signal::Resume), 300);
        let epoch = lifecycle.active().unwrap();
        // Delivered late, then at the resume's own timestamp.
        lifecycle.record(POWER, Some(Signal::Suspend), 200);
        lifecycle.record(POWER, Some(Signal::Suspend), 300);
        assert_eq!(lifecycle.active(), Some(epoch + 2));
        assert_eq!(generation(&lifecycle), 2);
        // An older resume cannot clear a newer suspend either.
        lifecycle.record(POWER, Some(Signal::Suspend), 400);
        lifecycle.record(POWER, Some(Signal::Resume), 350);
        assert_eq!(lifecycle.active(), None);
        assert_eq!(generation(&lifecycle), 3);
    }
    #[test]
    fn a_later_resume_without_a_suspend_re_enters_the_resume_generation() {
        let lifecycle = Lifecycle::default();
        lifecycle.record(POWER, Some(Signal::Suspend), SECOND);
        lifecycle.record(POWER, Some(Signal::Resume), 2 * SECOND);
        // A duplicate resume within two seconds keeps the generation.
        lifecycle.record(POWER, Some(Signal::Resume), 4 * SECOND);
        assert_eq!(generation(&lifecycle), 2);
        lifecycle.record(POWER, Some(Signal::Resume), 4 * SECOND + 1);
        assert_eq!(generation(&lifecycle), 4);
        // DisplayOn recovers a missed resume but never re-enters.
        lifecycle.record(POWER, Some(Signal::Suspend), 5 * SECOND);
        lifecycle.record(POWER, Some(Signal::DisplayOn), 6 * SECOND);
        lifecycle.record(POWER, Some(Signal::DisplayOn), 9 * SECOND);
        assert_eq!(generation(&lifecycle), 6);
        assert!(lifecycle.active().is_some());
    }
    #[test]
    fn the_gate_refuses_every_write_but_the_captured_automatic_fan_cleanup() {
        let lifecycle = Lifecycle::default();
        let request = lifecycle.active().unwrap();
        // Per-write authorization compares the pinned epoch with the gate's
        // admission, so the gate alone refuses even an unfenced epoch.
        lifecycle.0.lock().unwrap().gate.apply(Signal::Suspend, 1);
        assert_eq!(lifecycle.epoch_for(WriteScope::Ordinary), None);
        assert_eq!(lifecycle.active(), None);
        let cleanup = WriteScope::LowPowerFanAutomatic {
            generation: lifecycle.low_power().unwrap().generation,
        };
        assert_eq!(lifecycle.epoch_for(cleanup), Some(request));
        assert_eq!(
            lifecycle.epoch_for(WriteScope::LowPowerFanAutomatic { generation: 3 }),
            None
        );
        // The resume ends the cleanup scope and fences the request's epoch.
        lifecycle.record(POWER, Some(Signal::Resume), 2);
        assert_eq!(lifecycle.epoch_for(cleanup), None);
        assert_eq!(lifecycle.active(), Some(request + 1));
        // A later suspend has a new generation; the old cleanup stays closed.
        lifecycle.record(POWER, Some(Signal::Suspend), 3);
        assert_eq!(lifecycle.epoch_for(cleanup), None);
        // Exhaustion closes every scope and the timer view.
        lifecycle.0.lock().unwrap().epoch = 0;
        let current = WriteScope::LowPowerFanAutomatic { generation: 3 };
        assert_eq!(lifecycle.epoch_for(current), None);
        assert_eq!(lifecycle.low_power(), None);
    }
}
