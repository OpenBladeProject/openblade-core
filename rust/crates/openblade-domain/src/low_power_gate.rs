//! Managed `LowPowerGate` (`src/OpenBlade.Service/LowPowerGate.cs:3-133`):
//! the one low-power latch every write admission and timer consults.
//!
//! The generation's low bit is the latch, and each boundary adds one, so a
//! later suspend after a resume has a new odd generation even when a
//! preparation waited on hardware throughout. Signals are ordered by their
//! machine QPC timestamp, which every supplier reads from the same clock: a
//! delayed suspend cannot undo a newer resume or DisplayOn. Pure state; the
//! host owns the lock, the clock and change notification.

use core::num::NonZeroU64;

/// `TimeSpan.TicksPerSecond`, the unit managed `GetElapsedTime` returns.
const TICKS_PER_SECOND: i128 = 10_000_000;

/// A resume this long after the last boundary with no retained generation is
/// a new resume (`LowPowerGate.cs:103-112`): the process may have missed the
/// suspend before it.
const NEW_RESUME_TICKS: i128 = 2 * TICKS_PER_SECOND;

/// One power signal, as managed suppliers call `TryApply`.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Signal {
    /// PBT_APMSUSPEND or a relayed suspend: `TryApply(true, t)`.
    Suspend,
    /// PBT_APMRESUMESUSPEND, PBT_APMRESUMEAUTOMATIC or a relayed resume:
    /// `TryApply(false, t, newResume: true)`.
    Resume,
    /// A GUID_CONSOLE_DISPLAY_STATE transition from off to on or dimmed:
    /// `TryApply(false, t)`. Managed raises it only for that transition
    /// (`ClassifyConsoleDisplayTransition`,
    /// `WindowsServiceLifecycleEventSource.cs:539-547`), never for dimmed to
    /// on or a repeated on. It clears the latch, recovering a missed resume,
    /// but never re-enters it. Display-off is not a gate signal
    /// (`WindowsServiceLifecycleEventSource.cs:354-368`).
    DisplayOn,
}

/// Managed `LowPowerTransition`: a generation and the QPC timestamp of the
/// boundary that set it (zero until the first accepted signal).
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Transition {
    pub generation: u64,
    pub timestamp: i64,
}

impl Transition {
    /// `LowPowerTransition.Suspending`.
    pub const fn suspending(self) -> bool {
        self.generation & 1 != 0
    }
}

/// An accepted signal: the transition after it, and whether the generation
/// changed (managed raises `TransitionChanged` exactly then).
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Accepted {
    pub transition: Transition,
    pub changed: bool,
}

/// What a thermal write is, as the low-power clause of managed
/// `ScopedThermalWriteBlockReason` (`ServiceControlRuntime.cs:15410-15421`)
/// reads it, plus the keyboard Driver-mode release that managed runs in the
/// same preparation under the same generation check. Managed runs other
/// cleanup with the gate set under its own admission, such as the fan-stop
/// release (`ServiceControlRuntime.FanStop.cs:319-339`); it is not a
/// `WriteScope`.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum WriteScope {
    /// Every scoped thermal request, reconciliation and shutdown write:
    /// refused while the latch is set.
    Ordinary,
    /// The low-power preparation's owned cleanup for the suspend whose odd
    /// generation it captured, and nothing else: the handback to firmware
    /// Automatic fans (managed `PriorAuthorityCleanup` over `[Fans]` with
    /// `FanMode.Automatic`) and the keyboard's return to Normal mode (managed
    /// `ReleaseDriverModeCoreAsync("low power")`, admitted while
    /// `lowPowerGate.Generation == generation`). Admitted only while that
    /// exact suspend is still current, so it ends at the next resume,
    /// DisplayOn or later suspend, as managed `lowPowerFanCleanupAdmission`
    /// does through `IsActiveForGeneration`. The caller's own ownership,
    /// device and controller checks still apply, as managed composes them
    /// into the same predicate.
    LowPowerCleanup { generation: u64 },
}

/// What `TryApply`'s stale clause reads: the latest accepted timestamp and
/// whether the latch is set. A reader that must not take the lock the gate
/// lives under can test a copy (`refuses`) instead of applying a signal.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct View {
    pub latest: i64,
    pub active: bool,
}

impl View {
    /// Whether `TryApply` refuses a signal raised at `timestamp`
    /// (`LowPowerGate.cs:84-90`): it is older than the latest accepted one,
    /// or it is a same-timestamp suspend while the gate is clear, even one
    /// that was already clear when that suspend was raised. A delayed
    /// suspend cannot undo a newer resume or DisplayOn.
    pub const fn refuses(self, suspend: bool, timestamp: i64) -> bool {
        timestamp < self.latest || (timestamp == self.latest && suspend && !self.active)
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct LowPowerGate {
    frequency: NonZeroU64,
    latest: i64,
    boundary: i64,
    retained: Option<u64>,
    state: u64,
}

impl LowPowerGate {
    /// A clear gate (managed's initial state) over a QPC clock ticking
    /// `frequency` times a second.
    pub const fn new(frequency: NonZeroU64) -> Self {
        Self {
            frequency,
            latest: 0,
            boundary: 0,
            retained: None,
            state: 0,
        }
    }

    /// `IsActive`.
    pub const fn is_active(&self) -> bool {
        self.state & 1 != 0
    }

    /// `Generation`.
    pub const fn generation(&self) -> u64 {
        self.state
    }

    /// `IsActiveForGeneration`: this exact suspend is still current.
    pub const fn is_active_for_generation(&self, generation: u64) -> bool {
        generation & 1 != 0 && self.state == generation
    }

    /// `CurrentTransition`.
    pub const fn transition(&self) -> Transition {
        Transition {
            generation: self.state,
            timestamp: self.boundary,
        }
    }

    /// The low-power clause of `ScopedThermalWriteBlockReason`: whether the
    /// gate admits this write now.
    pub const fn admits(&self, write: WriteScope) -> bool {
        match write {
            WriteScope::Ordinary => !self.is_active(),
            WriteScope::LowPowerCleanup { generation } => self.is_active_for_generation(generation),
        }
    }

    /// `RetainResumeGeneration`: while a resume recovery for this clear
    /// generation is queued or running, later resume notices belong to it.
    pub fn retain_resume_generation(&mut self, generation: u64) {
        if generation & 1 == 0 && self.state == generation {
            self.retained = Some(generation);
        }
    }

    /// `ReleaseResumeGeneration`.
    pub fn release_resume_generation(&mut self, generation: u64) {
        if self.retained == Some(generation) {
            self.retained = None;
        }
    }

    /// What the stale clause of `apply` reads now.
    pub const fn view(&self) -> View {
        View {
            latest: self.latest,
            active: self.is_active(),
        }
    }

    /// `TryApply`, or `None` for a stale signal, which changes nothing.
    pub fn apply(&mut self, signal: Signal, timestamp: i64) -> Option<Accepted> {
        let active = signal == Signal::Suspend;
        if self.view().refuses(active, timestamp) {
            return None;
        }
        let previous = self.state;
        self.latest = timestamp;
        if self.is_active() != active {
            self.boundary = timestamp;
            self.state = self.state.wrapping_add(1);
        } else if signal == Signal::Resume
            && self.retained != Some(self.state)
            && self.boundary > 0
            && self.elapsed_ticks(timestamp) > NEW_RESUME_TICKS
        {
            self.boundary = timestamp;
            self.state = self.state.wrapping_add(2);
        } else if self.boundary == 0 {
            self.boundary = timestamp;
        }
        Some(Accepted {
            transition: self.transition(),
            changed: self.state != previous,
        })
    }

    /// `TimeProvider.GetElapsedTime(boundary, timestamp)` in `TimeSpan`
    /// ticks, truncated as managed truncates. Managed scales by a `double`
    /// factor; this is exact, which can differ only within one tick of the
    /// two-second edge on a clock whose frequency does not divide ten million.
    fn elapsed_ticks(&self, timestamp: i64) -> i128 {
        (i128::from(timestamp) - i128::from(self.boundary)) * TICKS_PER_SECOND
            / i128::from(self.frequency.get())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const HZ: NonZeroU64 = NonZeroU64::new(10_000_000).unwrap();
    const SECOND: i64 = 10_000_000;

    fn applied(gate: &mut LowPowerGate, signal: Signal, at: i64) -> (u64, bool) {
        let accepted = gate.apply(signal, at).expect("accepted");
        assert_eq!(accepted.transition, gate.transition());
        (accepted.transition.generation, accepted.changed)
    }

    #[test]
    fn each_boundary_advances_the_generation_and_the_low_bit_is_the_latch() {
        let mut gate = LowPowerGate::new(HZ);
        assert!(!gate.is_active());
        assert_eq!(applied(&mut gate, Signal::Suspend, 100), (1, true));
        assert!(gate.is_active() && gate.transition().suspending());
        assert!(gate.is_active_for_generation(1));
        assert_eq!(gate.transition().timestamp, 100);
        assert_eq!(applied(&mut gate, Signal::Resume, 150), (2, true));
        assert!(!gate.is_active() && !gate.is_active_for_generation(2));
        assert_eq!(applied(&mut gate, Signal::Suspend, 160), (3, true));
        assert!(!gate.is_active_for_generation(1) && gate.is_active_for_generation(3));
    }

    #[test]
    fn an_older_or_same_timestamp_suspend_cannot_undo_a_clear() {
        let mut gate = LowPowerGate::new(HZ);
        applied(&mut gate, Signal::Suspend, 100);
        applied(&mut gate, Signal::Resume, 300);
        assert_eq!(gate.apply(Signal::Suspend, 200), None);
        assert_eq!(gate.apply(Signal::Suspend, 300), None);
        assert_eq!(gate.generation(), 2);
        // A same-timestamp resume or a repeated suspend is not stale.
        assert_eq!(applied(&mut gate, Signal::Suspend, 301), (3, true));
        assert_eq!(applied(&mut gate, Signal::Suspend, 301), (3, false));
        assert_eq!(applied(&mut gate, Signal::Resume, 301), (4, true));
        // The initial latest timestamp is zero.
        let mut gate = LowPowerGate::new(HZ);
        assert_eq!(gate.apply(Signal::Suspend, 0), None);
        assert_eq!(gate.apply(Signal::Resume, -1), None);
        assert_eq!(applied(&mut gate, Signal::DisplayOn, 0), (0, false));
    }

    #[test]
    fn an_older_resume_or_display_on_cannot_clear_a_newer_suspend() {
        let mut gate = LowPowerGate::new(HZ);
        applied(&mut gate, Signal::Suspend, 200);
        assert_eq!(gate.apply(Signal::Resume, 100), None);
        assert_eq!(gate.apply(Signal::DisplayOn, 199), None);
        assert!(gate.is_active_for_generation(1));
        assert_eq!(applied(&mut gate, Signal::DisplayOn, 200), (2, true));
    }

    #[test]
    fn a_resume_more_than_two_seconds_after_the_boundary_is_a_new_resume() {
        let mut gate = LowPowerGate::new(HZ);
        applied(&mut gate, Signal::Suspend, SECOND);
        applied(&mut gate, Signal::Resume, 2 * SECOND);
        // A duplicate within the window, and at exactly two seconds.
        assert_eq!(applied(&mut gate, Signal::Resume, 3 * SECOND), (2, false));
        assert_eq!(applied(&mut gate, Signal::Resume, 4 * SECOND), (2, false));
        // DisplayOn never re-enters.
        assert_eq!(
            applied(&mut gate, Signal::DisplayOn, 4 * SECOND + 1),
            (2, false)
        );
        assert_eq!(gate.transition().timestamp, 2 * SECOND);
        assert_eq!(
            applied(&mut gate, Signal::Resume, 4 * SECOND + 1),
            (4, true)
        );
        assert_eq!(gate.transition().timestamp, 4 * SECOND + 1);
        // The window runs from the new boundary.
        assert_eq!(
            applied(&mut gate, Signal::Resume, 6 * SECOND + 1),
            (4, false)
        );
    }

    #[test]
    fn a_retained_resume_generation_absorbs_later_resumes_until_released() {
        let mut gate = LowPowerGate::new(HZ);
        applied(&mut gate, Signal::Suspend, SECOND);
        applied(&mut gate, Signal::Resume, 2 * SECOND);
        // Odd and stale generations are not retained.
        gate.retain_resume_generation(1);
        gate.retain_resume_generation(4);
        assert_eq!(gate.retained, None);
        gate.retain_resume_generation(2);
        assert_eq!(applied(&mut gate, Signal::Resume, 5 * SECOND), (2, false));
        gate.release_resume_generation(4);
        assert_eq!(applied(&mut gate, Signal::Resume, 6 * SECOND), (2, false));
        gate.release_resume_generation(2);
        assert_eq!(applied(&mut gate, Signal::Resume, 7 * SECOND), (4, true));
    }

    #[test]
    fn a_first_resume_without_a_suspend_only_sets_the_boundary() {
        let mut gate = LowPowerGate::new(HZ);
        assert_eq!(applied(&mut gate, Signal::Resume, SECOND), (0, false));
        assert_eq!(gate.transition().timestamp, SECOND);
        assert_eq!(
            applied(&mut gate, Signal::Resume, 3 * SECOND + 1),
            (2, true)
        );
    }

    #[test]
    fn the_window_scales_by_the_clock_frequency() {
        let mut gate = LowPowerGate::new(NonZeroU64::new(3_000_000).unwrap());
        applied(&mut gate, Signal::Suspend, 1);
        applied(&mut gate, Signal::Resume, 2);
        assert_eq!(applied(&mut gate, Signal::Resume, 6_000_002), (2, false));
        assert_eq!(applied(&mut gate, Signal::Resume, 6_000_003), (4, true));
    }

    #[test]
    fn only_the_captured_suspend_admits_the_low_power_cleanup() {
        let mut gate = LowPowerGate::new(HZ);
        let cleanup = |generation| WriteScope::LowPowerCleanup { generation };
        assert!(gate.admits(WriteScope::Ordinary));
        // No suspend: the cleanup scope has nothing to clean up after.
        assert!(!gate.admits(cleanup(0)) && !gate.admits(cleanup(1)));
        applied(&mut gate, Signal::Suspend, 100);
        assert!(!gate.admits(WriteScope::Ordinary));
        assert!(gate.admits(cleanup(1)));
        assert!(!gate.admits(cleanup(3)) && !gate.admits(cleanup(0)));
        applied(&mut gate, Signal::Resume, 200);
        assert!(gate.admits(WriteScope::Ordinary));
        assert!(!gate.admits(cleanup(1)) && !gate.admits(cleanup(2)));
        applied(&mut gate, Signal::Suspend, 300);
        assert!(!gate.admits(cleanup(1)) && gate.admits(cleanup(3)));
    }

    #[test]
    fn the_generation_wraps_and_keeps_its_parity() {
        let mut gate = LowPowerGate::new(HZ);
        gate.state = u64::MAX;
        applied(&mut gate, Signal::Resume, 1);
        assert_eq!(gate.generation(), 0);
        assert!(!gate.is_active());
        // A new resume wraps as well.
        gate.state = u64::MAX - 1;
        assert_eq!(
            applied(&mut gate, Signal::Resume, 2 * SECOND + 2),
            (0, true)
        );
        assert!(!gate.is_active());
    }

    #[test]
    fn the_view_refuses_exactly_what_apply_refuses() {
        let mut gate = LowPowerGate::new(HZ);
        applied(&mut gate, Signal::Suspend, 100);
        applied(&mut gate, Signal::Resume, 300);
        for signal in [Signal::Suspend, Signal::Resume, Signal::DisplayOn] {
            for at in [-1, 0, 299, 300, 301] {
                let view = gate.view();
                let refused = view.refuses(signal == Signal::Suspend, at);
                assert_eq!(
                    refused,
                    gate.clone().apply(signal, at).is_none(),
                    "{signal:?} at {at}"
                );
            }
        }
        assert_eq!(
            gate.view(),
            View {
                latest: 300,
                active: false
            }
        );
        // A set latch admits a same-timestamp suspend.
        applied(&mut gate, Signal::Suspend, 400);
        assert!(!gate.view().refuses(true, 400) && gate.view().refuses(false, 399));
    }
}
