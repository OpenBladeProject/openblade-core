//! Sticky loaded-origin outcome shared by ingress and publication checks.
use openblade_domain::{
    low_power_gate::{LowPowerGate, Signal, Transition, View, WriteScope},
    power_setting::{ConsoleDisplay, PowerSetting, power_event_signal},
};
use std::{
    io,
    sync::{
        Arc, Mutex,
        atomic::{AtomicU8, AtomicU64, Ordering},
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
/// gate. Every power and session notice but a power-setting read advances the
/// epoch, which fences operations captured before it. The gate (managed `LowPowerGate`, ported in
/// `openblade_domain::low_power_gate`) is the only low-power latch: every
/// write admission, capture and timer reads it here, under the same lock as
/// the epoch. Unknown events fence old operations but never establish
/// resume; fresh device/session/controller proof is still required.
///
/// The fields are the state, the wake of each new suspend generation
/// (`suspended`), the hold of that generation's SCM callback
/// (`arm_suspend_hold`) and the gate's view published after each notice
/// (`gate_view`).
#[derive(Default)]
pub(crate) struct Lifecycle(
    Mutex<LifecycleState>,
    Arc<tokio::sync::Notify>,
    Arc<SuspendHold>,
    AtomicU64,
);
/// The published `gate_view` of an unreadable or exhausted lifecycle. Every
/// packed view is below it: a latest stamp needs at most 62 bits.
const UNKNOWN_VIEW: u64 = u64::MAX;
/// Packs a gate view into one word, the latest stamp above the latch bit,
/// so a reader never sees the stamp of one notice with another's latch. A
/// stamp outside 0..2^62 (QPC counts up from boot, so none is) packs as
/// unknown. The default word, 0, is the clear gate's initial view.
fn pack_view(view: Option<View>) -> u64 {
    match view {
        Some(View { latest, active }) if (0..1 << 62).contains(&latest) => {
            (latest as u64) << 1 | u64::from(active)
        }
        _ => UNKNOWN_VIEW,
    }
}
mod suspend_hold;
use suspend_hold::SuspendHold;
pub(crate) use suspend_hold::{HoldArm, PendingHold};
/// Marks a low-power cleanup's fence (`Lifecycle::fence_for`). Epochs count
/// up from 1 and are exhausted only at `u64::MAX` (`checked_add`), so an
/// epoch could carry this bit only after 2^63 notices, which no process
/// receives. Each admission compares a fence with its own scope's fence.
#[cfg_attr(not(feature = "service-keyboard-driver-mode-v1"), allow(dead_code))]
const CLEANUP_GENERATION: u64 = 1 << 63;
struct LifecycleState {
    epoch: u64,
    gate: LowPowerGate,
    // The last sampled Windows ACLineStatus kind (1 battery, 2 AC)
    // and how many times a sample has changed it.
    power_kind: Option<u8>,
    power_generation: u64,
    // The last valid GUID_CONSOLE_DISPLAY_STATE read, unknown until the first.
    display: ConsoleDisplay,
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
            display: ConsoleDisplay::unknown(),
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
    /// An SCM control without a power-setting payload, at the callback's QPC
    /// time. It never waits on a hold.
    #[cfg(test)]
    pub(crate) fn ingress(&self, code: u32, event: u32) {
        let _ = self.notice(code, event, None);
    }
    /// The SCM control callback with its decoded power-setting payload, if
    /// any, at the callback's QPC time. A console display read updates the
    /// display state and signals `DisplayOn` only for off to on or dimmed
    /// (managed `ClassifyConsoleDisplayTransition`). A power-setting read
    /// never fences (see `fences`). The display state and the gate share this
    /// lock, so a display read classifies against the read the gate saw
    /// before it. A PBT_APMSUSPEND that starts a new suspend generation
    /// returns that generation's hold while a preparer has armed it
    /// (`arm_suspend_hold`); the caller waits on it after releasing every
    /// lock it holds. No other notice returns one.
    pub(crate) fn notice(
        &self,
        code: u32,
        event: u32,
        setting: Option<PowerSetting>,
    ) -> Option<PendingHold> {
        self.apply(code, qpc::now(), fences(code, event), |display| {
            signal_for(code, event, display, setting)
        })
    }
    /// One power or session notice with an already classified gate signal:
    /// advance the epoch, then apply the signal, if any, at `timestamp`.
    #[cfg(test)]
    pub(crate) fn record(&self, code: u32, signal: Option<Signal>, timestamp: i64) {
        let _ = self.apply(code, timestamp, true, |_| signal);
    }
    /// Arms the hold of each new suspend generation's SCM callback for the
    /// preparer on this thread: until it records an outcome for that
    /// generation (`HoldArm::prepared`), at most `cap` from the notice's QPC
    /// stamp. `None` when another preparer armed it. Dropping the arm
    /// disarms it.
    #[cfg_attr(
        not(any(test, feature = "service-keyboard-driver-mode-v1")),
        allow(dead_code)
    )]
    pub(crate) fn arm_suspend_hold(&self, cap: std::time::Duration) -> Option<HoldArm> {
        self.2.arm(cap)
    }
    /// Exhausts the epoch, as `u64::MAX` fencing notices would: the gate then
    /// reads unknown (`low_power() == None`) and nothing is admitted again.
    #[cfg(all(test, feature = "service-profile-triggers-v1"))]
    pub(crate) fn exhaust(&self) {
        if let Ok(mut state) = self.0.lock() {
            state.epoch = 0;
            self.3.store(UNKNOWN_VIEW, Ordering::Release);
        }
    }
    /// The gate's view as the last notice left it, read without the lock the
    /// SCM callback takes, or `None` when the lifecycle is exhausted. The
    /// Session Agent's relay compares a report with it and never applies one
    /// (`session_power_relay`), so SCM alone drives the boundary.
    #[cfg_attr(not(feature = "service-session-power-relay-v1"), allow(dead_code))]
    pub(crate) fn gate_view(&self) -> Option<View> {
        let word = self.3.load(Ordering::Acquire);
        (word != UNKNOWN_VIEW).then_some(View {
            latest: (word >> 1) as i64,
            active: word & 1 != 0,
        })
    }
    fn apply(
        &self,
        code: u32,
        timestamp: i64,
        fences: bool,
        signal: impl FnOnce(&mut ConsoleDisplay) -> Option<Signal>,
    ) -> Option<PendingHold> {
        use windows_sys::Win32::System::Services::{
            SERVICE_CONTROL_POWEREVENT, SERVICE_CONTROL_SESSIONCHANGE,
        };
        if !matches!(
            code,
            SERVICE_CONTROL_POWEREVENT | SERVICE_CONTROL_SESSIONCHANGE
        ) {
            return None;
        }
        let mut state = self.0.lock().ok()?;
        if state.epoch == 0 {
            return None;
        } // Exhaustion is sticky; never revive epoch one.
        if fences {
            state.epoch = state.epoch.checked_add(1).unwrap_or(0);
        }
        // A stale signal changes nothing but may have fenced. A new suspend
        // generation wakes the low-power cleanup after this lock is
        // released, as managed raises TransitionChanged to its coordinator
        // (`ServiceSessionPowerTransitionCoordinator`), and is held from
        // the notice's stamp, the same QPC stamp the preparation ages.
        let accepted =
            signal(&mut state.display).and_then(|signal| state.gate.apply(signal, timestamp));
        // Published under the lock, so views are stored in notice order.
        self.3.store(
            pack_view((state.epoch != 0).then(|| state.gate.view())),
            Ordering::Release,
        );
        drop(state);
        let transition = accepted
            .filter(|accepted| accepted.changed && accepted.transition.suspending())?
            .transition;
        // One stored permit: a suspend while the waiter is busy is seen at
        // its next wait, never lost.
        self.1.notify_one();
        self.2
            .accept(transition.generation, self.age(transition.timestamp))
    }
    /// Notified once for each new suspend generation (`apply`). Its waiter
    /// reads the transition itself (`low_power`), so a stale or coalesced
    /// notification admits nothing.
    #[cfg_attr(not(feature = "service-keyboard-driver-mode-v1"), allow(dead_code))]
    pub(crate) fn suspended(&self) -> Arc<tokio::sync::Notify> {
        self.1.clone()
    }
    /// How long ago the QPC `timestamp` of a gate transition was, read on the
    /// same clock every notice stamps with. `None` for a timestamp in the
    /// future. Managed measures a transition's age the same way
    /// (`ServiceSessionPowerTransitionCoordinator`, `TimeProvider.GetElapsedTime`).
    pub(crate) fn age(&self, timestamp: i64) -> Option<std::time::Duration> {
        let ticks = u128::try_from(i128::from(qpc::now()) - i128::from(timestamp)).ok()?;
        let nanos = ticks * 1_000_000_000 / u128::from(qpc::frequency().get());
        Some(std::time::Duration::from_nanos(
            u64::try_from(nanos).unwrap_or(u64::MAX),
        ))
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
    /// What an owned cleanup of this scope pins now, or `None` while the
    /// gate refuses it. Owned Stop pins the ordinary epoch (`epoch_for`), so
    /// any later notice fences it. A low-power cleanup pins its suspend
    /// generation only, as managed's `IsActiveForGeneration` does: a session
    /// notice during the suspend moves the epoch and leaves the cleanup
    /// admitted, and the generation's end (a resume, DisplayOn or another
    /// suspend) refuses it. `CLEANUP_GENERATION` keeps that value apart from
    /// every epoch, so it never equals an ordinary admission.
    #[cfg_attr(not(feature = "service-keyboard-driver-mode-v1"), allow(dead_code))]
    pub(crate) fn fence_for(&self, write: WriteScope) -> Option<u64> {
        let state = self.0.lock().ok()?;
        if state.epoch == 0 || !state.gate.admits(write) {
            return None;
        }
        Some(match write {
            WriteScope::Ordinary => state.epoch,
            WriteScope::LowPowerCleanup { generation } => generation | CLEANUP_GENERATION,
        })
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
    #[cfg_attr(
        not(any(test, feature = "service-profile-triggers-v1")),
        allow(dead_code)
    )] // Request triggers read it; Stage 4b timers and D1 cleanup will.
    pub(crate) fn low_power(&self) -> Option<Transition> {
        let state = self.0.lock().ok()?;
        (state.epoch != 0).then(|| state.gate.transition())
    }
    /// The gate's generation, which advances at each suspend and each resume
    /// (a display-on that recovers a missed resume counts as one), and never
    /// at another session or power notice, unlike the epoch. Two reads the
    /// gate admitted see different values only if a resume came between
    /// them. `None` when the lifecycle is unreadable or exhausted.
    #[cfg(any(test, feature = "service-live-readings-v1"))]
    pub(crate) fn resume_generation(&self) -> Option<u64> {
        self.low_power().map(|transition| transition.generation)
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
/// Whether an SCM control advances the epoch. A power-setting read
/// (PBT_POWERSETTINGCHANGE: console display or AC/DC) does not; the gate
/// signal still applies.
///
/// Managed fences nothing on a display read: its display triggers only toggle
/// lighting suppression and HyperBoost display-off preparation. It does fence
/// the AC/DC read. That callback is its only `PowerChanged` publisher
/// (WindowsServiceLifecycleEventSource.cs:695), and
/// `HandlePowerSourceChangedAsync` bumps `powerAdmissionEpoch` and
/// `pendingPowerAdmissions` (ServiceControlRuntime.cs:12327-12328). Native's
/// fence for that source change is PBT_APMPOWERSTATUSCHANGE, which still
/// advances the epoch, so fencing the setting read as well would be a
/// duplicate that abandons work captured between the two notices.
fn fences(code: u32, event: u32) -> bool {
    use windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT;
    !(code == SERVICE_CONTROL_POWEREVENT
        && event == openblade_domain::power_setting::PBT_POWERSETTINGCHANGE)
}
/// The gate signal of an SCM control (`openblade_domain::power_setting`,
/// pinned against managed): PBT_APMSUSPEND suspends, PBT_APMRESUMESUSPEND or
/// PBT_APMRESUMEAUTOMATIC resumes, and a console display read from off to on
/// or dimmed is display-on. Only power events signal. Session notices fence
/// old work, and their payload never authorizes a user; the mutation
/// supplier checks fresh exact WTSInfoEx state.
fn signal_for(
    code: u32,
    event: u32,
    display: &mut ConsoleDisplay,
    setting: Option<PowerSetting>,
) -> Option<Signal> {
    use windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT;
    (code == SERVICE_CONTROL_POWEREVENT)
        .then(|| power_event_signal(display, event, setting))
        .flatten()
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
    /// A low-power cleanup's fence is its suspend generation: a lock or
    /// unlock during the suspend moves the epoch, which fences owned Stop,
    /// and leaves the cleanup admitted; the resume refuses it.
    #[test]
    fn a_low_power_cleanup_fences_on_its_generation_only() {
        use windows_sys::Win32::System::Services::{
            SERVICE_CONTROL_POWEREVENT, SERVICE_CONTROL_SESSIONCHANGE,
        };
        let lifecycle = Lifecycle::default();
        let ordinary = lifecycle.fence_for(WriteScope::Ordinary);
        assert_eq!(ordinary, lifecycle.active());
        lifecycle.ingress(SERVICE_CONTROL_SESSIONCHANGE, 7);
        assert_ne!(lifecycle.fence_for(WriteScope::Ordinary), ordinary);
        lifecycle.ingress(SERVICE_CONTROL_POWEREVENT, 4);
        let generation = lifecycle.low_power().unwrap().generation;
        let cleanup = WriteScope::LowPowerCleanup { generation };
        let epoch = lifecycle.epoch_for(cleanup);
        let fence = lifecycle.fence_for(cleanup);
        assert_eq!(fence, Some(generation | CLEANUP_GENERATION));
        assert_eq!(lifecycle.fence_for(WriteScope::Ordinary), None);
        lifecycle.ingress(SERVICE_CONTROL_SESSIONCHANGE, 8);
        lifecycle.ingress(SERVICE_CONTROL_SESSIONCHANGE, 7);
        assert_ne!(lifecycle.epoch_for(cleanup), epoch);
        assert_eq!(lifecycle.fence_for(cleanup), fence);
        assert_eq!(
            lifecycle.fence_for(WriteScope::LowPowerCleanup {
                generation: generation + 2
            }),
            None
        );
        lifecycle.ingress(SERVICE_CONTROL_POWEREVENT, 18);
        assert_eq!(lifecycle.fence_for(cleanup), None);
        assert!(lifecycle.fence_for(WriteScope::Ordinary).is_some());
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
    /// Lock, unlock, a power status change and a power-setting read fence
    /// the epoch (or not) but leave the resume generation; a suspend and
    /// resume advance it, and an exhausted lifecycle has none.
    #[test]
    fn only_suspend_and_resume_advance_the_resume_generation() {
        use windows_sys::Win32::System::Services::SERVICE_CONTROL_SESSIONCHANGE;
        let lifecycle = Lifecycle::default();
        assert_eq!(lifecycle.resume_generation(), Some(0));
        for (code, event) in [
            (SERVICE_CONTROL_SESSIONCHANGE, 7),
            (SERVICE_CONTROL_SESSIONCHANGE, 8),
            (POWER, 10),
            (POWER, 32787),
        ] {
            lifecycle.ingress(code, event);
            assert_eq!(lifecycle.resume_generation(), Some(0), "{code}/{event}");
        }
        assert_eq!(lifecycle.active(), Some(4));
        lifecycle.ingress(POWER, 4);
        lifecycle.ingress(POWER, 7);
        assert_eq!(lifecycle.resume_generation(), Some(2));
        assert!(lifecycle.active().is_some());
        let exhausted = Lifecycle::default();
        exhausted.0.lock().unwrap().epoch = 0;
        assert_eq!(exhausted.resume_generation(), None);
    }
    #[test]
    fn scm_suspend_and_resume_drive_the_one_gate_and_other_notices_carry_no_signal() {
        use windows_sys::Win32::System::Services::SERVICE_CONTROL_SESSIONCHANGE;
        let lifecycle = Lifecycle::default();
        assert_eq!(lifecycle.low_power().map(|t| t.suspending()), Some(false));
        for (code, event, expected, epoch) in [
            (POWER, 4, 1, None),
            (SERVICE_CONTROL_SESSIONCHANGE, 7, 1, None),
            (POWER, 10, 1, None),
            // A power-setting read neither fences nor, without a payload, signals.
            (POWER, 32787, 1, None),
            (POWER, 7, 2, Some(5)),
            (POWER, 32787, 2, Some(5)),
            (POWER, 4, 3, None),
            (POWER, 18, 4, Some(7)),
            (SERVICE_CONTROL_SESSIONCHANGE, 8, 4, Some(8)),
            // A session notice whose event number is PBT_APMSUSPEND only fences.
            (SERVICE_CONTROL_SESSIONCHANGE, 4, 4, Some(9)),
            // A session notice whose event number is PBT_POWERSETTINGCHANGE fences.
            (SERVICE_CONTROL_SESSIONCHANGE, 32787, 4, Some(10)),
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
            // Power status, power setting and unknown PBT events carry no signal.
            (POWER, 10, None),
            (POWER, 32787, None),
            (POWER, 6, None),
            (SERVICE_CONTROL_SESSIONCHANGE, 4, None),
            (SERVICE_CONTROL_SESSIONCHANGE, 7, None),
            (SERVICE_CONTROL_SESSIONCHANGE, 18, None),
            (SERVICE_CONTROL_STOP, 4, None),
        ] {
            let mut display = ConsoleDisplay::unknown();
            assert_eq!(
                signal_for(code, event, &mut display, None),
                signal,
                "{code}/{event}"
            );
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
    fn the_gate_view_follows_every_notice_and_fails_closed() {
        let view = |latest, active| Some(View { latest, active });
        let lifecycle = Lifecycle::default();
        assert_eq!(lifecycle.gate_view(), view(0, false));
        lifecycle.record(POWER, Some(Signal::Suspend), 100);
        assert_eq!(lifecycle.gate_view(), view(100, true));
        // A stale signal and a notice without one leave it as it was.
        lifecycle.record(POWER, Some(Signal::Resume), 90);
        lifecycle.record(
            windows_sys::Win32::System::Services::SERVICE_CONTROL_SESSIONCHANGE,
            None,
            150,
        );
        assert_eq!(lifecycle.gate_view(), view(100, true));
        lifecycle.record(POWER, Some(Signal::Resume), 300);
        assert_eq!(lifecycle.gate_view(), view(300, false));
        // The view is the gate's own, under the same lock.
        assert_eq!(
            lifecycle.gate_view(),
            Some(lifecycle.0.lock().unwrap().gate.view())
        );
        // A stamp the word cannot hold, and exhaustion, read as unknown.
        assert_eq!(pack_view(view(-1, false)), UNKNOWN_VIEW);
        assert_eq!(pack_view(view(1 << 62, true)), UNKNOWN_VIEW);
        assert_eq!(pack_view(view((1 << 62) - 1, true)), u64::MAX >> 1);
        lifecycle.0.lock().unwrap().epoch = u64::MAX;
        lifecycle.record(POWER, Some(Signal::Suspend), 400);
        assert_eq!(lifecycle.gate_view(), None);
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
    fn the_gate_refuses_every_write_but_the_captured_low_power_cleanup() {
        let lifecycle = Lifecycle::default();
        let request = lifecycle.active().unwrap();
        // Per-write authorization compares the pinned epoch with the gate's
        // admission, so the gate alone refuses even an unfenced epoch.
        lifecycle.0.lock().unwrap().gate.apply(Signal::Suspend, 1);
        assert_eq!(lifecycle.epoch_for(WriteScope::Ordinary), None);
        assert_eq!(lifecycle.active(), None);
        let cleanup = WriteScope::LowPowerCleanup {
            generation: lifecycle.low_power().unwrap().generation,
        };
        assert_eq!(lifecycle.epoch_for(cleanup), Some(request));
        assert_eq!(
            lifecycle.epoch_for(WriteScope::LowPowerCleanup { generation: 3 }),
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
        let current = WriteScope::LowPowerCleanup { generation: 3 };
        assert_eq!(lifecycle.epoch_for(current), None);
        assert_eq!(lifecycle.low_power(), None);
    }
    const SETTING: u32 = openblade_domain::power_setting::PBT_POWERSETTINGCHANGE;
    fn display(level: u32) -> Option<PowerSetting> {
        Some(PowerSetting::ConsoleDisplay(level))
    }
    fn gate(lifecycle: &Lifecycle) -> (u64, bool) {
        let transition = lifecycle.low_power().unwrap();
        (transition.generation, transition.suspending())
    }
    #[test]
    fn a_modern_standby_display_off_without_suspend_leaves_the_gate_clear_and_fences_nothing() {
        let lifecycle = Lifecycle::default();
        lifecycle.notice(POWER, SETTING, display(1));
        let on = lifecycle.active().unwrap();
        // Modern Standby turns the display off and sends no PBT_APMSUSPEND.
        // Managed classifies that as DisplayOff, which never reaches the gate
        // and, like every power-setting read, fences nothing.
        lifecycle.notice(POWER, SETTING, display(0));
        assert_eq!(gate(&lifecycle), (0, false));
        assert_eq!(lifecycle.active(), Some(on));
        // An AC/DC read in standby and the wake's display-on fence nothing;
        // the unplug's PBT_APMPOWERSTATUSCHANGE does.
        lifecycle.notice(POWER, SETTING, Some(PowerSetting::PowerSource(1)));
        assert_eq!(lifecycle.active(), Some(on));
        lifecycle.notice(POWER, 10, None);
        lifecycle.notice(POWER, SETTING, display(1));
        assert_eq!(gate(&lifecycle), (0, false));
        assert_eq!(lifecycle.active(), Some(on + 1));
        assert_eq!(lifecycle.0.lock().unwrap().display.state(), Some(1));
    }
    #[test]
    fn only_a_power_setting_read_skips_the_fence() {
        use windows_sys::Win32::System::Services::{
            SERVICE_CONTROL_SESSIONCHANGE, SERVICE_CONTROL_STOP,
        };
        for (code, event, fenced) in [
            (POWER, SETTING, false),
            (POWER, 4, true),
            (POWER, 7, true),
            (POWER, 10, true),
            (POWER, SETTING + 1, true),
            (SERVICE_CONTROL_SESSIONCHANGE, SETTING, true),
            (SERVICE_CONTROL_STOP, SETTING, true),
        ] {
            assert_eq!(fences(code, event), fenced, "{code}/{event}");
        }
    }
    #[test]
    fn display_on_after_an_observed_off_recovers_a_missed_resume() {
        let lifecycle = Lifecycle::default();
        lifecycle.notice(POWER, 4, None);
        assert_eq!(gate(&lifecycle), (1, true));
        // The first read has no previous state, so it cannot clear the gate.
        lifecycle.notice(POWER, SETTING, display(1));
        assert_eq!(gate(&lifecycle), (1, true));
        lifecycle.notice(POWER, SETTING, display(0));
        // A session notice never reads or classifies a display payload.
        lifecycle.notice(
            windows_sys::Win32::System::Services::SERVICE_CONTROL_SESSIONCHANGE,
            SETTING,
            display(2),
        );
        // A refused payload reaches the lifecycle as no setting at all.
        lifecycle.notice(POWER, SETTING, None);
        assert_eq!(gate(&lifecycle), (1, true));
        assert_eq!(lifecycle.active(), None);
        lifecycle.notice(POWER, SETTING, display(2));
        assert_eq!(gate(&lifecycle), (2, false));
        assert!(lifecycle.active().is_some());
    }
    #[test]
    fn a_display_read_after_exhaustion_neither_signals_nor_revives() {
        let lifecycle = Lifecycle::default();
        lifecycle.notice(POWER, 4, None);
        lifecycle.notice(POWER, SETTING, display(0));
        lifecycle.0.lock().unwrap().epoch = u64::MAX;
        // A power-setting read does not fence, so it cannot exhaust the epoch.
        lifecycle.notice(POWER, SETTING, display(0));
        assert_eq!(lifecycle.0.lock().unwrap().epoch, u64::MAX);
        lifecycle.notice(POWER, 10, None);
        lifecycle.notice(POWER, SETTING, display(1));
        assert_eq!(lifecycle.low_power(), None);
        assert!(lifecycle.low_power_or_unknown());
        assert_eq!(lifecycle.active(), None);
    }
    /// Each new suspend generation stores one wake for the low-power
    /// preparation; a repeated suspend, a resume or a power notice that keeps
    /// the system active stores none.
    #[tokio::test]
    async fn a_new_suspend_generation_wakes_the_preparation_once() {
        use windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT as POWER;
        let woken = async |wake: &tokio::sync::Notify| {
            tokio::time::timeout(std::time::Duration::from_millis(20), wake.notified())
                .await
                .is_ok()
        };
        let lifecycle = Lifecycle::default();
        let wake = lifecycle.suspended();
        lifecycle.ingress(POWER, 10);
        assert!(!woken(&wake).await);
        lifecycle.ingress(POWER, 4);
        assert!(woken(&wake).await);
        lifecycle.ingress(POWER, 4);
        assert!(
            !woken(&wake).await,
            "a repeated suspend is the same generation"
        );
        lifecycle.ingress(POWER, 18);
        assert!(!woken(&wake).await);
        lifecycle.ingress(POWER, 4);
        assert!(woken(&wake).await);
    }
    /// A transition's age is read on the notice clock; a future timestamp
    /// has none.
    #[test]
    fn a_transition_age_reads_the_notice_clock() {
        let lifecycle = Lifecycle::default();
        let age = lifecycle.age(qpc::now() - 15_000_000).unwrap();
        let expected = std::time::Duration::from_millis(1500);
        assert!(
            age >= expected && age < expected + std::time::Duration::from_millis(1),
            "{age:?}"
        );
        assert_eq!(lifecycle.age(i64::MAX), None);
    }
    /// Only a PBT_APMSUSPEND that starts a new suspend generation is held,
    /// and only while a preparer has armed the hold: never a resume, a
    /// power status change, a power-setting read (even a DisplayOn), a
    /// session notice (even one numbered 4), another control, a repeated or
    /// stale suspend, or any notice after exhaustion.
    #[test]
    fn only_a_new_suspend_generation_is_held_while_armed() {
        use windows_sys::Win32::System::Services::{
            SERVICE_CONTROL_SESSIONCHANGE, SERVICE_CONTROL_STOP,
        };
        let held = |lifecycle: &Lifecycle, code, event, setting| {
            lifecycle
                .notice(code, event, setting)
                .map(|hold| hold.generation())
        };
        let lifecycle = std::sync::Arc::new(Lifecycle::default());
        assert_eq!(held(&lifecycle, POWER, 4, None), None, "unarmed");
        lifecycle.ingress(POWER, 18);
        // Armed on another thread, as the preparer's runtime thread is.
        let arm = {
            let lifecycle = lifecycle.clone();
            std::thread::spawn(move || {
                lifecycle.arm_suspend_hold(std::time::Duration::from_millis(1500))
            })
            .join()
            .unwrap()
            .unwrap()
        };
        assert!(
            lifecycle
                .arm_suspend_hold(std::time::Duration::from_millis(1500))
                .is_none()
        );
        for (code, event, setting) in [
            (POWER, 10, None),
            (POWER, SETTING, display(0)),
            (POWER, SETTING, Some(PowerSetting::PowerSource(1))),
            (SERVICE_CONTROL_SESSIONCHANGE, 4, None),
            (SERVICE_CONTROL_SESSIONCHANGE, 6, None),
            (SERVICE_CONTROL_STOP, 4, None),
            (1, 4, None),
        ] {
            assert_eq!(
                held(&lifecycle, code, event, setting),
                None,
                "{code}/{event}"
            );
        }
        assert_eq!(held(&lifecycle, POWER, 4, None), Some(3));
        assert_eq!(
            held(&lifecycle, POWER, 4, None),
            None,
            "the same generation"
        );
        for (code, event, setting) in [
            (POWER, 10, None),
            (SERVICE_CONTROL_SESSIONCHANGE, 7, None),
            (POWER, 7, None),
            (POWER, 4, None),
            (POWER, 18, None),
            (POWER, SETTING, display(0)),
            (POWER, 4, None),
            (POWER, SETTING, display(1)),
        ] {
            // Resumes, a DisplayOn and the suspends between them: each
            // suspend is a new generation and only those are held.
            let generation = held(&lifecycle, code, event, setting);
            assert_eq!(
                generation.is_some(),
                code == POWER && event == 4,
                "{code}/{event}"
            );
        }
        // A stale suspend changes no generation.
        lifecycle.record(POWER, Some(Signal::Suspend), 1);
        assert_eq!(generation(&lifecycle), 8);
        // A generation the preparer has recorded is not held.
        arm.prepared(9);
        assert_eq!(held(&lifecycle, POWER, 4, None), None);
        assert_eq!(generation(&lifecycle), 9);
        lifecycle.ingress(POWER, 18);
        assert_eq!(held(&lifecycle, POWER, 4, None), Some(11));
        lifecycle.0.lock().unwrap().epoch = 0;
        lifecycle.ingress(POWER, 18);
        assert_eq!(held(&lifecycle, POWER, 4, None), None, "exhausted");
        // Disarmed, a new generation is not held.
        let lifecycle = Lifecycle::default();
        drop(lifecycle.arm_suspend_hold(std::time::Duration::from_millis(1500)));
        assert_eq!(held(&lifecycle, POWER, 4, None), None, "disarmed");
    }
    /// The hold lasts from the notice's stamp: a suspend whose notice is
    /// older than the cap is not held at all.
    #[test]
    fn a_suspend_older_than_the_cap_is_not_held() {
        let lifecycle = std::sync::Arc::new(Lifecycle::default());
        let _arm = {
            let lifecycle = lifecycle.clone();
            std::thread::spawn(move || {
                lifecycle.arm_suspend_hold(std::time::Duration::from_micros(1))
            })
            .join()
            .unwrap()
            .unwrap()
        };
        // Advance the notice clock past the stamp by more than the cap.
        let stamp = qpc::now();
        for _ in 0..20 {
            qpc::now();
        }
        assert!(
            lifecycle
                .apply(POWER, stamp, true, |_| Some(Signal::Suspend))
                .is_none()
        );
        assert_eq!(generation(&lifecycle), 1);
    }
}
