//! Confirmed thermal drift, held in memory (managed
//! `ConfirmedStateDriftTracker` and the automatic-transition resolver,
//! `ServiceControlRuntime.ApplyDesiredTransitionWithRetryAsync`).
//!
//! The tracker compares each fresh exact-device thermal reading with the
//! last one: a known value that changed is an external change of its
//! subsystem, and a reading that leaves a value unknown is no evidence. The
//! first reading after the Service starts only sets the baseline, so a
//! restart forgets every latch.
//!
//! Per subsystem, only what a reading confirmed is OpenBlade's own (`Own`,
//! managed's prior desired state): a write's confirming readback
//! (`own_performance`, `own_fans`), or a reconciliation's fresh reading that
//! already shows the intent it applies, or the preset an overwrite whose
//! intent has no destination leaves in place. A setter with no readback of its
//! own, such as one a step dropped at its bound or deadline submitted, is
//! settled by the next reading that reports its subsystem (`submitted`).
//! Saved intent that native did not apply is never OpenBlade's own.
//!
//! An automatic reconciliation (`Transition`) compares its own fresh reading
//! with that intent before any write. A subsystem that shows neither the new
//! intent nor OpenBlade's own confirmed state is an external change; with
//! nothing of its own confirmed, only the new intent is compared. Either
//! thermal subsystem changed outside OpenBlade excludes both from that
//! reconciliation (managed `PendingWriteConfirmationTracker.GetWriteExclusions`),
//! as an observed HyperBoost does. Exclusion only removes writes; every write
//! that remains keeps its own admission, readback and stop-on-ambiguity path.
//!
//! Native reads the selector pair and the manual-fan bit only, so fans
//! compare as manual or Automatic (managed compares the exact mode), and GPU
//! clock offsets, which native does not read, are never evidence.
use openblade_domain::{
    automation::UNCLASSIFIED_EXTERNAL,
    profile_set::FanMode,
    thermal_mutation::{CUSTOM, Destination, Preset},
    thermal_observation::{CustomPerformanceLevels, ObservedPerformance},
};
use std::sync::{Arc, Mutex};
use tokio::time::Instant;

/// Managed `DeviceSubsystem.Performance` and `DeviceSubsystem.Fans`.
pub(super) const PERFORMANCE: i32 = 0;
pub(super) const FANS: i32 = 1;
/// Managed `ResumeStateValidationTracker.ObservationWindow`: how long a
/// resume hold lasts, and how long after a gate boundary a power change is
/// applied as the resume's.
pub(super) const RESUME_WINDOW: std::time::Duration = std::time::Duration::from_secs(60);

/// Saved fan intent as native can compare it.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum Fans {
    Automatic,
    /// Manual fans native has a recipe for.
    Manual,
    /// Manual fans without a native recipe: ManualCurve, Stop, a battery
    /// class, or a destination other than Balanced or Performance
    /// (`fan_recipe::Plan::select`). Managed
    /// `ResolveExperimentalFanHardwareDesired` projects unsupported fan intent
    /// onto a confirmed Automatic reading, so it is no change there; a manual
    /// reading is not projected and still blocks.
    Unplanned,
}
impl Fans {
    /// The intent of saved fan `mode` at power class `power` toward the
    /// saved `destination`.
    pub(super) fn of(mode: FanMode, power: i32, destination: Option<Destination>) -> Self {
        if mode == FanMode::AUTOMATIC {
            Self::Automatic
        } else if (mode == FanMode::FIXED || mode == FanMode::MAXIMUM)
            && matches!(power, 0 | 2 | UNCLASSIFIED_EXTERNAL)
            && matches!(destination.map(Destination::selector), Some(0 | 2))
        {
            Self::Manual
        } else {
            Self::Unplanned
        }
    }
    /// Whether a reading of `manual` fans shows this intent.
    fn shown_by(self, manual: bool) -> bool {
        match self {
            Self::Manual => manual,
            Self::Automatic | Self::Unplanned => !manual,
        }
    }
}

/// The thermal intent one reconciliation applies: managed
/// `DesiredDeviceState.Performance` and `Fans`. A performance intent native
/// cannot resolve, such as an incomplete GPU clock policy, is `None`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) struct Desired {
    pub(super) performance: Option<Destination>,
    pub(super) fans: Fans,
}

/// How a reconciliation treats confirmed drift.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub(super) enum Mode {
    /// Managed `preserveObservedExternalChanges: true`: a power change, a
    /// request trigger, a save, the lease expiry and a deferred debt replayed
    /// at a gate boundary. A reading that shows the new intent or
    /// OpenBlade's own confirmed state is OpenBlade's own.
    WithPrior,
    /// Managed `preserveObservedExternalChanges: false`: a user's startup
    /// reconciliation and a user's activation apply over any drift.
    Overwrite,
}

/// Why a reconciliation writes nothing of either thermal subsystem.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum Exclusion {
    /// Managed `ExternalChangePreserved`.
    External,
    /// Managed `PreservedForHyperBoost`.
    HyperBoost,
}
impl Exclusion {
    /// Managed `ControlCoordinator` per-subsystem reply for `name`
    /// (`Performance` or `Fans`).
    pub(super) fn message(self, name: &str) -> String {
        let name = name.to_lowercase();
        match self {
            Self::External => format!(
                "Automatic {name} profile application is paused because the setting changed outside OpenBlade."
            ),
            Self::HyperBoost => format!(
                "Automatic {name} profile application was deferred because HyperBoost remains active; performance and fan changes are deferred until it exits."
            ),
        }
    }
}

/// A performance change that firmware may still settle into after a resume
/// (managed `PendingResumePerformanceDrift`).
#[derive(Clone, Copy, Debug)]
struct Hold {
    desired: Option<Destination>,
    expires: Instant,
}

/// What a reading confirmed as OpenBlade's own, per subsystem.
#[derive(Clone, Copy, Debug, Default)]
struct Own {
    performance: Option<Destination>,
    manual: Option<bool>,
}

#[derive(Debug, Default)]
pub(super) struct Tracker {
    performance: bool,
    fans: bool,
    preset: Option<i32>,
    custom: Option<CustomPerformanceLevels>,
    manual: Option<bool>,
    own: Own,
    hold: Option<Hold>,
    /// What OpenBlade may have written without a readback of its own: a
    /// step dropped at its bound or deadline, or failed after an intent.
    /// The next reading that reports the subsystem settles it.
    submitted: Own,
    /// A reading since the last entry OpenBlade confirmed showed firmware out
    /// of HyperBoost, so no HyperBoost seen from then on is that entry's
    /// (`Tracker::left_hyperboost`).
    #[cfg(feature = "service-hyperboost-v1")]
    left_hyperboost: bool,
    /// How many published readings it has observed
    /// (`Tracker::observe_published`).
    #[cfg(feature = "service-thermal-convergence-v1")]
    published: u64,
    /// The published reading the status still carries, by its number:
    /// `None` once a step reads the device after it, since a
    /// reconciliation publishes only its last step's snapshot, which need
    /// not carry an earlier step's reading. The convergence loop evaluates
    /// each such reading once (`convergence_loop`).
    #[cfg(feature = "service-thermal-convergence-v1")]
    evidence: Option<u64>,
}

/// The reading's preset and Custom levels, `None` when unknown: a HyperBoost
/// or maximum-fan reading has no preset.
fn preset(reading: ObservedPerformance) -> Option<i32> {
    reading.projection().0
}
/// The reading's manual-fan bit; a HyperBoost reading reports no fan mode.
fn manual(reading: ObservedPerformance) -> Option<bool> {
    (reading != ObservedPerformance::HyperBoostObserved).then(|| reading.projection().1 != 0)
}
/// Managed `PerformanceMatchesDesired` without GPU offsets, which native does
/// not read: the preset, and for Custom its levels.
fn matches(reading: ObservedPerformance, desired: Destination) -> bool {
    preset(reading) == Some(desired.wire())
        && desired
            .custom_levels()
            .is_none_or(|levels| reading.custom_levels() == Some(levels))
}
/// The destination a reading shows: its preset, or Custom with its levels.
/// `None` without a preset, or for Custom whose levels read nothing.
fn destination(reading: ObservedPerformance) -> Option<Destination> {
    match preset(reading)? {
        CUSTOM => reading.custom_levels().map(Destination::Custom),
        wire => Preset::from_wire(wire).map(Destination::Preset),
    }
}
/// Whether a subsystem is an external change, from whether the reading shows
/// the new intent and OpenBlade's own confirmed state, each `None` when
/// there is none to compare. With neither, the reading is no evidence and
/// the latch stays as observed.
fn changed(latched: bool, next: Option<bool>, own: Option<bool>) -> bool {
    match (next, own) {
        (Some(true), _) | (_, Some(true)) => false,
        (None, None) => latched,
        _ => true,
    }
}

impl Tracker {
    /// Managed `ConfirmedStateDriftTracker.Observe`.
    pub(super) fn observe(&mut self, reading: ObservedPerformance) {
        #[cfg(feature = "service-thermal-convergence-v1")]
        {
            self.evidence = None;
        }
        #[cfg(feature = "service-hyperboost-v1")]
        if reading != ObservedPerformance::HyperBoostObserved {
            self.left_hyperboost = true;
        }
        // The first reading after a possible submission shows whether it
        // landed: OpenBlade's own write if it shows what was written, else
        // nothing of OpenBlade's.
        let landed = match self.submitted.performance {
            Some(destination) if preset(reading).is_some() => {
                self.submitted.performance = None;
                matches(reading, destination).then_some(destination)
            }
            _ => None,
        };
        let landed_fans = match (self.submitted.manual, manual(reading)) {
            (Some(written), Some(read)) => {
                self.submitted.manual = None;
                (written == read).then_some(read)
            }
            _ => None,
        };
        if let Some(preset) = preset(reading) {
            if self.preset.is_some_and(|baseline| baseline != preset) {
                self.performance = true;
            }
            self.preset = Some(preset);
            if preset != CUSTOM {
                self.custom = None;
            } else if let Some(levels) = reading.custom_levels() {
                if self.custom.is_some_and(|baseline| baseline != levels) {
                    self.performance = true;
                }
                self.custom = Some(levels);
            }
        }
        if let Some(manual) = manual(reading) {
            if self.manual.is_some_and(|baseline| baseline != manual) {
                self.fans = true;
            }
            self.manual = Some(manual);
        }
        if let Some(destination) = landed {
            self.own_performance(destination);
        }
        if let Some(manual) = landed_fans {
            self.own_fans(manual);
        }
    }

    /// A setter of `destination` may be submitted: its intent is journaled.
    /// Until the next reading confirms or refutes it, a reading that shows
    /// it is OpenBlade's own write, not a change outside OpenBlade.
    pub(super) fn submitted(&mut self, destination: Destination) {
        self.submitted.performance = Some(destination);
    }

    /// A performance setter of `destination` may be submitted from the
    /// reading `from`. Each of its 0D/02 setters writes the fan mode with the
    /// selector, so from manual fans the pair also returns them to Automatic:
    /// a coupled fan write, which the readback that confirms the destination
    /// confirms as OpenBlade's own too (managed `ConfirmedCoupledWrites`).
    pub(super) fn submitted_from(
        &mut self,
        destination: Destination,
        from: Option<ObservedPerformance>,
    ) {
        if from.and_then(manual) == Some(true) {
            self.submitted_fans(false, Some(destination));
        } else {
            self.submitted(destination);
        }
    }

    /// A fan setter toward `manual` fans may be submitted, with the
    /// destination a return to Automatic carries: as `submitted`.
    pub(super) fn submitted_fans(&mut self, manual: bool, carried: Option<Destination>) {
        self.submitted.manual = Some(manual);
        if let Some(destination) = carried {
            self.submitted(destination);
        }
    }

    /// Managed `ConfirmIntentional(Performance, matched: true)`: a reading
    /// confirmed `destination` as OpenBlade's own. It becomes the baseline,
    /// clears the subsystem and ends any resume hold (managed clears it once
    /// a transition attempts Performance).
    pub(super) fn own_performance(&mut self, destination: Destination) {
        self.preset = Some(destination.wire());
        self.custom = destination.custom_levels();
        self.performance = false;
        self.own.performance = Some(destination);
        self.hold = None;
        self.submitted.performance = None;
        // An entry OpenBlade just confirmed starts a session nothing has
        // contradicted yet.
        #[cfg(feature = "service-hyperboost-v1")]
        if destination == Destination::HyperBoost {
            self.left_hyperboost = false;
        }
    }

    /// Whether a reading since the last entry OpenBlade confirmed showed
    /// firmware out of HyperBoost. Managed clears `ownsHyperBoost` at any
    /// published observation with HyperBoost inactive
    /// (`ServiceControlRuntime.cs` 18394-18405), so the history's ownership
    /// (`Storage::owns_hyperboost`) counts only while this is false: a
    /// HyperBoost that appears after such a reading was entered outside
    /// OpenBlade. Every reading this process takes passes `observe`, and a
    /// new process starts unset, so the first reading after a start decides:
    /// HyperBoost keeps the journal's ownership, anything else ends it.
    #[cfg(feature = "service-hyperboost-v1")]
    pub(super) fn left_hyperboost(&self) -> bool {
        self.left_hyperboost
    }

    /// A fresh reading no `observe` carried showed firmware out of
    /// HyperBoost: an explicit exit that found it already off.
    #[cfg(feature = "service-hyperboost-v1")]
    pub(super) fn leave_hyperboost(&mut self) {
        self.left_hyperboost = true;
    }

    /// Managed `ConfirmIntentional(Fans, matched: true)`: a reading confirmed
    /// `manual` fans as OpenBlade's own.
    pub(super) fn own_fans(&mut self, manual: bool) {
        self.manual = Some(manual);
        self.fans = false;
        self.own.manual = Some(manual);
        self.submitted.manual = None;
    }

    /// Managed `Observe` and then `ConfirmIntentional` for Performance and
    /// Fans on `reading` (`RestoreFirmwareExitedHyperBoostForPowerSourceTransitionAsync`):
    /// an exit found firmware already out of owned HyperBoost, so what it
    /// shows is OpenBlade's own and the transition after it compares from it.
    #[cfg(feature = "service-hyperboost-v1")]
    pub(super) fn own_reading(&mut self, reading: ObservedPerformance) {
        self.observe(reading);
        if let Some(destination) = destination(reading) {
            self.own_performance(destination);
        }
        if let Some(manual) = manual(reading) {
            self.own_fans(manual);
        }
    }

    /// A fan readback that confirmed OpenBlade's fan intent: observed, and
    /// its fans OpenBlade's own. A HyperBoost reading reports no fan mode.
    pub(super) fn own_fans_read(&mut self, readback: ObservedPerformance) {
        self.observe(readback);
        if let Some(manual) = manual(readback) {
            self.own_fans(manual);
        }
    }

    /// Ends a resume hold: a new gate boundary, a power change or low power
    /// (managed clears it there, or drops it at its next observation).
    pub(super) fn drop_hold(&mut self) {
        self.hold = None;
    }

    /// Managed `ReconcileResumePerformanceDrift` at a fresh status reading:
    /// within the window, a reading that shows the held destination clears
    /// the performance change as OpenBlade's own. A performance change no
    /// longer recorded drops the hold, and a new intent drops it at its
    /// reconciliation (`decide`). Managed also drops it while a competing
    /// controller is reported. Native's published status always carries a
    /// detection warning, so that term would drop every hold; settling only
    /// clears a latch for a reading of OpenBlade's own destination, and each
    /// later write keeps its own fresh competing-controller admission.
    pub(super) fn settle(&mut self, reading: ObservedPerformance, now: Instant) {
        let Some(hold) = self.hold else {
            return;
        };
        if now >= hold.expires || !self.performance {
            self.hold = None;
            return;
        }
        if let Some(desired) = hold.desired.filter(|desired| matches(reading, *desired)) {
            self.own_performance(desired);
        }
    }

    /// `observe` for a reading the status publishes as it is
    /// (`MailboxSource::observe`): the evidence a convergence evaluation
    /// reads.
    pub(super) fn observe_published(&mut self, reading: ObservedPerformance) {
        self.observe(reading);
        #[cfg(feature = "service-thermal-convergence-v1")]
        {
            self.published = self.published.wrapping_add(1);
            self.evidence = Some(self.published);
        }
    }
    /// The published reading the status still carries, by its number
    /// (`Tracker::observe_published`); `None` after a later step's read.
    #[cfg(feature = "service-thermal-convergence-v1")]
    pub(super) fn evidence(&self) -> Option<u64> {
        self.evidence
    }
    /// Whether a reading confirmed manual fans as OpenBlade's own and no
    /// fan change outside OpenBlade has been seen since: the tracker's part
    /// of managed `IsExactFanIntentOwned` (`Runtime::owns_exact_fans`).
    pub(super) fn owns_manual_fans(&self) -> bool {
        self.own.manual == Some(true) && !self.fans
    }

    /// The changed subsystems as managed publishes `ExternalChanges`: sorted
    /// by `DeviceSubsystem`.
    pub(super) fn external(&self) -> Vec<i32> {
        [(self.performance, PERFORMANCE), (self.fans, FANS)]
            .into_iter()
            .filter_map(|(changed, subsystem)| changed.then_some(subsystem))
            .collect()
    }

    /// One reconciliation's decision from its fresh `reading` before any
    /// write: managed `ResolveAutomaticWriteExclusions`. A subsystem the
    /// reading shows at `next` is OpenBlade's own from then on.
    fn decide(
        &mut self,
        reading: ObservedPerformance,
        next: Desired,
        mode: Mode,
        resume: bool,
        now: Instant,
    ) -> Option<Exclusion> {
        let was = self.performance;
        self.observe(reading);
        // Managed excludes both while HyperBoost is active or unknown,
        // whichever mode the transition runs in.
        if reading == ObservedPerformance::HyperBoostObserved {
            return Some(Exclusion::HyperBoost);
        }
        // A new intent ends a hold for another destination.
        if self
            .hold
            .is_some_and(|hold| hold.desired != next.performance)
        {
            self.hold = None;
        }
        if preset(reading).is_some() {
            let shown = next.performance.map(|desired| matches(reading, desired));
            if let (Some(true), Some(desired)) = (shown, next.performance) {
                self.own_performance(desired);
            } else if mode == Mode::WithPrior {
                let own = self.own.performance.map(|own| matches(reading, own));
                self.performance = changed(self.performance, shown, own);
            } else if next.performance.is_none()
                && let Some(left) = destination(reading)
            {
                // An overwrite whose intent has no destination, such as a GPU
                // clock effect native cannot apply, writes no preset and
                // leaves the reading's in place. That preset is OpenBlade's
                // own from then on, so a later intent compares with it
                // instead of pausing over state the overwrite accepted.
                self.own_performance(left);
            }
        }
        if let Some(manual) = manual(reading) {
            if next.fans.shown_by(manual) {
                self.own_fans(manual);
            } else if mode == Mode::WithPrior {
                let own = self.own.manual.map(|own| own == manual);
                self.fans = changed(self.fans, Some(false), own);
            }
        }
        // The startup reconciliation and an activation apply over drift;
        // their own readbacks, or the preset adopted above, then clear it.
        if mode == Mode::Overwrite {
            return None;
        }
        // Firmware can settle into the destination after the first read on
        // resume: keep writes paused until a fresh reading within the window
        // proves it (managed `pendingResumePerformanceDrift`, which needs a
        // prior intent the reading does not show).
        if resume && !was && self.performance && self.own.performance.is_some() {
            self.hold = Some(Hold {
                desired: next.performance,
                expires: now + RESUME_WINDOW,
            });
        }
        (self.performance || self.fans).then_some(Exclusion::External)
    }

    #[cfg(test)]
    pub(super) fn age_hold(&mut self, by: std::time::Duration) {
        if let Some(hold) = self.hold.as_mut() {
            hold.expires -= by;
        }
    }
}

/// A convergence attempt's write scope (`convergence_loop::Guard`): which
/// subsystems it declared and whether its reservation still admits a write.
/// A reconciliation without one is unscoped.
#[cfg(feature = "service-thermal-convergence-v1")]
pub(super) trait Scope: Send + Sync {
    /// Whether the subsystem's step runs at all.
    fn declares(&self, fans: bool) -> bool;
    /// Checked at every authorization: nothing has stopped the attempt.
    fn open(&self) -> bool;
    /// Checked before each setter's journaled intent: the reservation, the
    /// HyperBoost journal and the policy still admit this write.
    fn admits(&self, fans: bool) -> bool;
}

/// One reconciliation's drift decision, shared by its steps: the first step
/// to read firmware decides for both subsystems, and a later step reuses it.
/// `now` is the reconciliation's start on the runtime's clock, which stamps
/// any resume hold that a status reading later compares; a step's worker
/// keeps its own clock.
pub(super) struct Transition {
    tracker: Arc<Mutex<Tracker>>,
    mode: Mode,
    resume: bool,
    now: Instant,
    decided: Mutex<Option<Option<Exclusion>>>,
    #[cfg(feature = "service-thermal-convergence-v1")]
    scope: Option<Arc<dyn Scope>>,
}
impl Transition {
    pub(super) fn new(
        tracker: Arc<Mutex<Tracker>>,
        mode: Mode,
        resume: bool,
        now: Instant,
    ) -> Self {
        Self {
            tracker,
            mode,
            resume,
            now,
            decided: Mutex::new(None),
            #[cfg(feature = "service-thermal-convergence-v1")]
            scope: None,
        }
    }
    /// The same reconciliation, limited to a convergence attempt's scope.
    #[cfg(feature = "service-thermal-convergence-v1")]
    pub(super) fn scoped(mut self, scope: Option<Arc<dyn Scope>>) -> Self {
        self.scope = scope;
        self
    }
    /// Whether the performance (or, with `fans`, the fan) step runs.
    pub(super) fn declares(&self, fans: bool) -> bool {
        #[cfg(feature = "service-thermal-convergence-v1")]
        if let Some(scope) = &self.scope {
            return scope.declares(fans);
        }
        let _ = fans;
        true
    }
    /// Whether a step's authorization is still open (`Scope::open`).
    pub(super) fn open(&self) -> bool {
        #[cfg(feature = "service-thermal-convergence-v1")]
        if let Some(scope) = &self.scope {
            return scope.open();
        }
        true
    }
    /// Whether a setter's journaled intent is admitted (`Scope::admits`).
    pub(super) fn admits(&self, fans: bool) -> bool {
        #[cfg(feature = "service-thermal-convergence-v1")]
        if let Some(scope) = &self.scope {
            return scope.admits(fans);
        }
        let _ = fans;
        true
    }
    /// The exclusion for this reconciliation, decided once from the first
    /// step's fresh `reading` and its saved intent `next`.
    pub(super) fn decide(
        &self,
        reading: ObservedPerformance,
        next: Desired,
    ) -> std::io::Result<Option<Exclusion>> {
        let mut decided = self.decided.lock().map_err(|_| std::io::ErrorKind::Other)?;
        if let Some(decision) = *decided {
            return Ok(decision);
        }
        let decision = self
            .tracker
            .lock()
            .map_err(|_| std::io::ErrorKind::Other)?
            .decide(reading, next, self.mode, self.resume, self.now);
        *decided = Some(decision);
        Ok(decision)
    }
    /// The exclusion a step decided, `None` before any step decided.
    pub(super) fn exclusion(&self) -> Option<Exclusion> {
        self.decided
            .lock()
            .ok()
            .and_then(|decided| decided.flatten())
    }
    /// Whether a step's fresh reading was compared with the intent: it
    /// decided, and not for an observed HyperBoost, which compares nothing.
    pub(super) fn compared(&self) -> bool {
        self.decided
            .lock()
            .is_ok_and(|decided| matches!(*decided, Some(None | Some(Exclusion::External))))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ObservedPerformance as O;
    use openblade_domain::thermal_observation::CustomPerformanceLevel::{Low, Medium};

    fn desired(preset: Preset, fans: Fans) -> Desired {
        Desired {
            performance: Some(Destination::Preset(preset)),
            fans,
        }
    }
    fn baselined(reading: ObservedPerformance) -> Tracker {
        let mut tracker = Tracker::default();
        tracker.observe(reading);
        tracker
    }
    /// A tracker whose own confirmed state is `reading`, as a confirmed
    /// write of `preset` leaves it.
    fn owned(reading: ObservedPerformance, preset: Preset) -> Tracker {
        let mut tracker = baselined(reading);
        tracker.own_performance(Destination::Preset(preset));
        tracker.own_fans(manual(reading).unwrap());
        tracker
    }

    #[test]
    fn the_first_reading_only_sets_the_baseline_and_a_change_marks_its_subsystem() {
        let mut tracker = baselined(O::PerformanceManual);
        assert!(tracker.external().is_empty());
        tracker.observe(O::SilentManual);
        assert_eq!(tracker.external(), [PERFORMANCE]);
        tracker.observe(O::SilentAutomatic);
        assert_eq!(tracker.external(), [PERFORMANCE, FANS]);
        // A latch stays until OpenBlade's own readback clears it.
        tracker.observe(O::SilentAutomatic);
        assert_eq!(tracker.external(), [PERFORMANCE, FANS]);
    }

    #[test]
    fn an_unknown_reading_is_no_evidence() {
        let mut tracker = baselined(O::BalancedAutomatic);
        // Maximum fans report no preset; HyperBoost reports neither.
        tracker.observe(O::FixedMaximum);
        assert_eq!(tracker.external(), [FANS]);
        let mut tracker = baselined(O::BalancedAutomatic);
        tracker.observe(O::HyperBoostObserved);
        tracker.observe(O::BalancedAutomatic);
        assert!(tracker.external().is_empty());
    }

    #[test]
    fn custom_levels_are_compared_while_custom_is_read() {
        let custom = |gpu| O::Custom {
            manual: false,
            levels: Some(CustomPerformanceLevels { cpu: Low, gpu }),
        };
        let mut tracker = baselined(custom(Low));
        tracker.observe(custom(Medium));
        assert_eq!(tracker.external(), [PERFORMANCE]);
        // Manual High/High reads no levels: no performance evidence.
        let mut tracker = baselined(custom(Low));
        tracker.observe(O::Custom {
            manual: true,
            levels: None,
        });
        tracker.observe(custom(Low));
        assert_eq!(tracker.external(), [FANS]);
        let medium = Destination::Custom(CustomPerformanceLevels {
            cpu: Low,
            gpu: Medium,
        });
        assert!(matches(custom(Medium), medium));
        assert!(!matches(custom(Low), medium));
        assert!(!matches(O::BalancedAutomatic, medium));
        // An own Custom write is the baseline with its levels.
        let mut tracker = baselined(custom(Low));
        tracker.own_performance(medium);
        tracker.observe(custom(Medium));
        assert!(tracker.external().is_empty());
        tracker.observe(custom(Low));
        assert_eq!(tracker.external(), [PERFORMANCE]);
    }

    #[test]
    fn an_own_confirmation_is_the_baseline_and_clears_its_subsystem_only() {
        let mut tracker = baselined(O::BalancedAutomatic);
        tracker.observe(O::SilentManual);
        assert_eq!(tracker.external(), [PERFORMANCE, FANS]);
        tracker.own_performance(Destination::Preset(Preset::Performance));
        assert_eq!(tracker.external(), [FANS]);
        // Its readback is no change.
        tracker.observe(O::PerformanceManual);
        assert_eq!(tracker.external(), [FANS]);
        tracker.own_fans(false);
        assert!(tracker.external().is_empty());
        tracker.observe(O::PerformanceAutomatic);
        assert!(tracker.external().is_empty());
        tracker.observe(O::PerformanceManual);
        assert_eq!(tracker.external(), [FANS]);
        // A fan readback is observed and its fans are OpenBlade's own.
        let mut tracker = baselined(O::BalancedAutomatic);
        tracker.own_fans_read(O::PerformanceManual);
        assert_eq!(tracker.external(), [PERFORMANCE]);
        assert_eq!(tracker.own.manual, Some(true));
        tracker.own_fans_read(O::HyperBoostObserved);
        assert_eq!(tracker.own.manual, Some(true));
        assert_eq!(tracker.manual, Some(true));
    }

    #[test]
    fn the_next_reading_settles_a_possible_submission() {
        let performance = Destination::Preset(Preset::Performance);
        // It landed: OpenBlade's own write, not drift, even over a latch.
        let mut tracker = baselined(O::BalancedAutomatic);
        tracker.observe(O::SilentAutomatic);
        tracker.submitted(performance);
        tracker.observe(O::PerformanceAutomatic);
        assert!(tracker.external().is_empty());
        assert_eq!(tracker.own.performance, Some(performance));
        // It did not land: the reading is compared as usual and settles it,
        // so a later change to that destination is drift.
        let mut tracker = baselined(O::BalancedAutomatic);
        tracker.submitted(performance);
        tracker.observe(O::SilentAutomatic);
        assert_eq!(tracker.external(), [PERFORMANCE]);
        assert_eq!(tracker.own.performance, None);
        tracker.observe(O::PerformanceAutomatic);
        assert_eq!(tracker.external(), [PERFORMANCE]);
        // A reading without a preset or a fan mode leaves it pending.
        let mut tracker = baselined(O::BalancedAutomatic);
        tracker.submitted(performance);
        tracker.observe(O::HyperBoostObserved);
        tracker.observe(O::PerformanceAutomatic);
        assert!(tracker.external().is_empty());
        let mut tracker = baselined(O::BalancedAutomatic);
        tracker.submitted_fans(true, None);
        tracker.observe(O::HyperBoostObserved);
        tracker.observe(O::SilentManual);
        assert_eq!(tracker.external(), [PERFORMANCE]);
        assert_eq!(tracker.own.manual, Some(true));
        // A fan write that did not land settles nothing of OpenBlade's: the
        // manual fans read instead are no own state, so a reconciliation
        // toward Automatic fans compares them as a change.
        let mut tracker = baselined(O::PerformanceAutomatic);
        tracker.submitted_fans(false, None);
        tracker.observe(O::PerformanceManual);
        assert_eq!(tracker.own.manual, None);
        assert_eq!(tracker.submitted.manual, None);
        assert_eq!(
            tracker.decide(
                O::PerformanceManual,
                desired(Preset::Performance, Fans::Automatic),
                Mode::WithPrior,
                false,
                Instant::now()
            ),
            Some(Exclusion::External)
        );
        assert_eq!(tracker.external(), [FANS]);
        // A fan write and the destination its return to Automatic carries.
        let mut tracker = baselined(O::PerformanceManual);
        tracker.submitted_fans(false, Some(Destination::Preset(Preset::Balanced)));
        tracker.observe(O::BalancedAutomatic);
        assert!(tracker.external().is_empty());
        let mut tracker = baselined(O::PerformanceManual);
        tracker.submitted_fans(false, Some(Destination::Preset(Preset::Balanced)));
        tracker.observe(O::SilentManual);
        assert_eq!(tracker.external(), [PERFORMANCE]);
        tracker.observe(O::BalancedAutomatic);
        assert_eq!(tracker.external(), [PERFORMANCE, FANS]);
        // A performance pair written from manual fans returns them to
        // Automatic: its readback is OpenBlade's own for both subsystems.
        // From Automatic fans it expects the destination alone.
        let mut tracker = baselined(O::SilentManual);
        tracker.submitted_from(performance, Some(O::SilentManual));
        tracker.observe(O::PerformanceAutomatic);
        assert!(tracker.external().is_empty());
        assert_eq!(tracker.own.manual, Some(false));
        let mut tracker = baselined(O::SilentAutomatic);
        tracker.submitted_from(performance, Some(O::SilentAutomatic));
        assert_eq!(tracker.submitted.manual, None);
        let mut tracker = baselined(O::SilentAutomatic);
        tracker.submitted_from(performance, None);
        assert_eq!(tracker.submitted.manual, None);
        assert_eq!(tracker.submitted.performance, Some(performance));
        // An own confirmation settles it.
        let mut tracker = baselined(O::BalancedAutomatic);
        tracker.submitted_fans(true, Some(performance));
        tracker.own_performance(Destination::Preset(Preset::Silent));
        tracker.own_fans(false);
        tracker.observe(O::PerformanceManual);
        assert_eq!(tracker.external(), [PERFORMANCE, FANS]);
    }

    #[test]
    fn fan_intent_without_a_native_recipe_is_unplanned() {
        let balanced = Some(Destination::Preset(Preset::Balanced));
        let performance = Some(Destination::Preset(Preset::Performance));
        assert_eq!(Fans::of(FanMode::AUTOMATIC, 1, None), Fans::Automatic);
        assert_eq!(Fans::of(FanMode::FIXED, 0, balanced), Fans::Manual);
        assert_eq!(Fans::of(FanMode::MAXIMUM, 2, performance), Fans::Manual);
        assert_eq!(
            Fans::of(FanMode::FIXED, UNCLASSIFIED_EXTERNAL, performance),
            Fans::Manual
        );
        let custom = Some(Destination::Custom(CustomPerformanceLevels {
            cpu: Low,
            gpu: Low,
        }));
        for (mode, power, destination) in [
            (FanMode::MANUAL_CURVE, 0, balanced),
            (FanMode::STOP, 0, balanced),
            (FanMode::FIXED, 1, balanced),
            (FanMode::FIXED, 0, Some(Destination::Preset(Preset::Silent))),
            (
                FanMode::FIXED,
                0,
                Some(Destination::Preset(Preset::BatterySaver)),
            ),
            (FanMode::FIXED, 0, custom),
            (FanMode::FIXED, 0, None),
        ] {
            assert_eq!(
                Fans::of(mode, power, destination),
                Fans::Unplanned,
                "{mode:?} {power} {destination:?}"
            );
        }
        // Projected onto Automatic fans only.
        assert!(Fans::Unplanned.shown_by(false));
        assert!(!Fans::Unplanned.shown_by(true));
        assert!(Fans::Manual.shown_by(true));
        assert!(!Fans::Manual.shown_by(false));
        assert!(Fans::Automatic.shown_by(false));
        assert!(!Fans::Automatic.shown_by(true));
    }

    #[test]
    fn a_reconciliation_compares_the_new_intent_and_own_confirmed_state() {
        let now = Instant::now();
        let next = desired(Preset::BatterySaver, Fans::Automatic);
        // OpenBlade's own confirmed preset is not drift.
        let mut tracker = owned(O::PerformanceAutomatic, Preset::Performance);
        assert_eq!(
            tracker.decide(O::PerformanceAutomatic, next, Mode::WithPrior, false, now),
            None
        );
        // A reading of neither excludes both.
        assert_eq!(
            tracker.decide(O::SilentAutomatic, next, Mode::WithPrior, false, now),
            Some(Exclusion::External)
        );
        assert_eq!(tracker.external(), [PERFORMANCE]);
        // Showing the new intent clears it and makes it OpenBlade's own.
        assert_eq!(
            tracker.decide(O::BatterySaverAutomatic, next, Mode::WithPrior, false, now),
            None
        );
        assert_eq!(tracker.own.performance, next.performance);
        // Fans alone exclude both.
        let mut tracker = owned(O::PerformanceAutomatic, Preset::Performance);
        assert_eq!(
            tracker.decide(O::PerformanceManual, next, Mode::WithPrior, false, now),
            Some(Exclusion::External)
        );
        assert_eq!(tracker.external(), [FANS]);
        // Own manual fans are OpenBlade's own too.
        let mut tracker = owned(O::PerformanceManual, Preset::Performance);
        assert_eq!(
            tracker.decide(O::PerformanceManual, next, Mode::WithPrior, false, now),
            None
        );
    }

    #[test]
    fn without_own_state_only_the_new_intent_is_compared() {
        let now = Instant::now();
        let next = desired(Preset::Performance, Fans::Automatic);
        let mut tracker = baselined(O::BalancedAutomatic);
        assert_eq!(
            tracker.decide(O::BalancedAutomatic, next, Mode::WithPrior, false, now),
            Some(Exclusion::External)
        );
        assert_eq!(tracker.external(), [PERFORMANCE]);
        let mut tracker = baselined(O::PerformanceAutomatic);
        assert_eq!(
            tracker.decide(O::PerformanceAutomatic, next, Mode::WithPrior, false, now),
            None
        );
        assert_eq!(tracker.own.performance, next.performance);
        assert_eq!(tracker.own.manual, Some(false));
        // An intent native cannot resolve compares nothing: the latch stays
        // as observed, and OpenBlade's own state still counts.
        let unresolved = Desired {
            performance: None,
            fans: Fans::Automatic,
        };
        let mut tracker = baselined(O::BalancedAutomatic);
        assert_eq!(
            tracker.decide(
                O::BalancedAutomatic,
                unresolved,
                Mode::WithPrior,
                false,
                now
            ),
            None
        );
        tracker.observe(O::SilentAutomatic);
        assert_eq!(
            tracker.decide(O::SilentAutomatic, unresolved, Mode::WithPrior, false, now),
            Some(Exclusion::External)
        );
        let mut tracker = owned(O::BalancedAutomatic, Preset::Balanced);
        assert_eq!(
            tracker.decide(O::SilentAutomatic, unresolved, Mode::WithPrior, false, now),
            Some(Exclusion::External)
        );
        assert_eq!(
            tracker.decide(
                O::BalancedAutomatic,
                unresolved,
                Mode::WithPrior,
                false,
                now
            ),
            None
        );
    }

    #[test]
    fn an_unplanned_fan_intent_is_no_change_on_automatic_fans_and_blocks_on_manual_ones() {
        let now = Instant::now();
        let silent = desired(Preset::Silent, Fans::Unplanned);
        // Automatic firmware fans under a saved Silent + Fixed are projected,
        // so they are OpenBlade's own Automatic fans.
        let mut tracker = baselined(O::SilentAutomatic);
        assert_eq!(
            tracker.decide(O::SilentAutomatic, silent, Mode::Overwrite, false, now),
            None
        );
        assert_eq!(tracker.own.manual, Some(false));
        // The next intent, which native can apply, compares with them.
        let game = desired(Preset::Performance, Fans::Manual);
        assert_eq!(
            tracker.decide(O::SilentAutomatic, game, Mode::WithPrior, false, now),
            None
        );
        // Manual fans set outside OpenBlade are not projected and are not
        // OpenBlade's own Automatic fans.
        let mut tracker = owned(O::SilentAutomatic, Preset::Silent);
        tracker.observe(O::SilentManual);
        assert_eq!(
            tracker.decide(O::SilentManual, silent, Mode::WithPrior, false, now),
            Some(Exclusion::External)
        );
        assert_eq!(tracker.external(), [FANS]);
        assert_eq!(
            tracker.decide(
                O::SilentManual,
                desired(Preset::Silent, Fans::Automatic),
                Mode::WithPrior,
                false,
                now
            ),
            Some(Exclusion::External)
        );
    }

    #[test]
    fn hyperboost_excludes_both_and_activation_overwrites_drift() {
        let now = Instant::now();
        let next = desired(Preset::Balanced, Fans::Automatic);
        let mut tracker = owned(O::PerformanceAutomatic, Preset::Performance);
        for mode in [Mode::WithPrior, Mode::Overwrite] {
            assert_eq!(
                tracker.decide(O::HyperBoostObserved, next, mode, false, now),
                Some(Exclusion::HyperBoost)
            );
        }
        // The overwrite keeps the latches its reading does not show, until
        // its own readbacks clear them, and sets none.
        let mut tracker = baselined(O::PerformanceAutomatic);
        tracker.observe(O::SilentManual);
        assert_eq!(
            tracker.decide(O::SilentManual, next, Mode::Overwrite, false, now),
            None
        );
        assert_eq!(tracker.external(), [PERFORMANCE, FANS]);
        tracker.own_performance(Destination::Preset(Preset::Balanced));
        tracker.own_fans(false);
        assert!(tracker.external().is_empty());
        let mut tracker = owned(O::PerformanceAutomatic, Preset::Performance);
        assert_eq!(
            tracker.decide(O::SilentAutomatic, next, Mode::Overwrite, false, now),
            None
        );
        // Observed, not compared: the latch is the reading's own change.
        assert_eq!(tracker.external(), [PERFORMANCE]);
        let mut tracker = Tracker::default();
        assert_eq!(
            tracker.decide(O::SilentManual, next, Mode::Overwrite, false, now),
            None
        );
        assert!(tracker.external().is_empty());
        // A reading that shows the intent clears its latch.
        let mut tracker = baselined(O::PerformanceAutomatic);
        assert_eq!(
            tracker.decide(O::BalancedAutomatic, next, Mode::Overwrite, false, now),
            None
        );
        assert!(tracker.external().is_empty());
        assert_eq!(tracker.own.performance, next.performance);
    }

    #[test]
    fn an_overwrite_without_a_destination_owns_the_preset_it_leaves() {
        let now = Instant::now();
        let unresolved = Desired {
            performance: None,
            fans: Fans::Automatic,
        };
        let battery = desired(Preset::BatterySaver, Fans::Automatic);
        // A latch the overwrite accepted is cleared with the preset it left,
        // which a later intent then compares as OpenBlade's own.
        let mut tracker = baselined(O::BalancedAutomatic);
        tracker.observe(O::PerformanceAutomatic);
        assert_eq!(
            tracker.decide(
                O::PerformanceAutomatic,
                unresolved,
                Mode::Overwrite,
                false,
                now
            ),
            None
        );
        assert!(tracker.external().is_empty());
        assert_eq!(
            tracker.own.performance,
            Some(Destination::Preset(Preset::Performance))
        );
        assert_eq!(
            tracker.decide(
                O::PerformanceAutomatic,
                battery,
                Mode::WithPrior,
                false,
                now
            ),
            None
        );
        // Custom is owned with the levels it read; Custom without readable
        // levels, or a reading without a preset, owns nothing.
        let levels = CustomPerformanceLevels {
            cpu: Low,
            gpu: Medium,
        };
        let custom = O::Custom {
            manual: false,
            levels: Some(levels),
        };
        let mut tracker = Tracker::default();
        tracker.decide(custom, unresolved, Mode::Overwrite, false, now);
        assert_eq!(tracker.own.performance, Some(Destination::Custom(levels)));
        for reading in [
            O::Custom {
                manual: true,
                levels: None,
            },
            O::FixedMaximum,
        ] {
            let mut tracker = Tracker::default();
            tracker.decide(reading, unresolved, Mode::Overwrite, false, now);
            assert_eq!(tracker.own.performance, None, "{reading:?}");
        }
        // A comparison with prior state adopts nothing: the reading is no
        // evidence there.
        let mut tracker = baselined(O::PerformanceAutomatic);
        tracker.decide(
            O::PerformanceAutomatic,
            unresolved,
            Mode::WithPrior,
            false,
            now,
        );
        assert_eq!(tracker.own.performance, None);
        // An overwrite with a destination owns only what shows it.
        let mut tracker = baselined(O::PerformanceAutomatic);
        tracker.decide(
            O::PerformanceAutomatic,
            battery,
            Mode::Overwrite,
            false,
            now,
        );
        assert_eq!(tracker.own.performance, None);
    }

    #[test]
    fn a_resume_hold_clears_when_the_destination_is_read_within_the_window() {
        let now = Instant::now();
        let next = desired(Preset::BatterySaver, Fans::Automatic);
        let held = || {
            let mut tracker = owned(O::PerformanceAutomatic, Preset::Performance);
            assert_eq!(
                tracker.decide(
                    O::BatteryBalancedAutomatic,
                    next,
                    Mode::WithPrior,
                    true,
                    now
                ),
                Some(Exclusion::External)
            );
            assert!(tracker.hold.is_some());
            tracker
        };
        let mut tracker = held();
        tracker.settle(O::BatteryBalancedAutomatic, now);
        assert_eq!(tracker.external(), [PERFORMANCE]);
        assert!(tracker.hold.is_some());
        tracker.settle(O::BatterySaverAutomatic, now + RESUME_WINDOW / 2);
        assert!(tracker.external().is_empty());
        assert!(tracker.hold.is_none());
        assert_eq!(tracker.own.performance, next.performance);
        // Past the window the hold is dropped and the latch stays.
        let mut tracker = held();
        tracker.settle(O::BatterySaverAutomatic, now + RESUME_WINDOW);
        assert!(tracker.hold.is_none());
        assert_eq!(tracker.external(), [PERFORMANCE]);
        // A new intent drops it at its reconciliation.
        let mut tracker = held();
        let other = desired(Preset::BatteryBalanced, Fans::Automatic);
        tracker.decide(O::BatterySaverAutomatic, other, Mode::WithPrior, false, now);
        assert!(tracker.hold.is_none());
        tracker.settle(O::BatterySaverAutomatic, now);
        assert_eq!(tracker.external(), [PERFORMANCE]);
        // The same intent keeps it.
        let mut tracker = held();
        tracker.decide(
            O::BatteryBalancedAutomatic,
            next,
            Mode::WithPrior,
            false,
            now,
        );
        assert!(tracker.hold.is_some());
        // OpenBlade's own performance write and `drop_hold` end it.
        let mut tracker = held();
        tracker.own_performance(Destination::Preset(Preset::Balanced));
        assert!(tracker.hold.is_none());
        let mut tracker = held();
        tracker.drop_hold();
        assert!(tracker.hold.is_none());
        tracker.settle(O::BatterySaverAutomatic, now);
        assert_eq!(tracker.external(), [PERFORMANCE]);
        // A performance change no longer recorded drops it.
        let mut tracker = held();
        tracker.performance = false;
        tracker.settle(O::BatteryBalancedAutomatic, now);
        assert!(tracker.hold.is_none());
        // Without the resume, with drift already recorded, or with nothing of
        // OpenBlade's own confirmed, nothing is held.
        let mut tracker = owned(O::PerformanceAutomatic, Preset::Performance);
        tracker.decide(
            O::BatteryBalancedAutomatic,
            next,
            Mode::WithPrior,
            false,
            now,
        );
        assert!(tracker.hold.is_none());
        let mut tracker = owned(O::PerformanceAutomatic, Preset::Performance);
        tracker.observe(O::SilentAutomatic);
        tracker.decide(
            O::BatteryBalancedAutomatic,
            next,
            Mode::WithPrior,
            true,
            now,
        );
        assert!(tracker.hold.is_none());
        let mut tracker = baselined(O::PerformanceAutomatic);
        tracker.decide(
            O::BatteryBalancedAutomatic,
            next,
            Mode::WithPrior,
            true,
            now,
        );
        assert!(tracker.hold.is_none());
        assert_eq!(tracker.external(), [PERFORMANCE]);
    }

    #[test]
    fn a_transition_decides_once_for_both_steps_on_its_own_clock() {
        let tracker = Arc::new(Mutex::new(owned(
            O::PerformanceAutomatic,
            Preset::Performance,
        )));
        let start = Instant::now() - std::time::Duration::from_secs(5);
        let transition = Transition::new(tracker.clone(), Mode::WithPrior, true, start);
        let next = desired(Preset::Balanced, Fans::Automatic);
        assert_eq!(transition.exclusion(), None);
        assert!(!transition.compared());
        assert_eq!(
            transition.decide(O::SilentAutomatic, next).unwrap(),
            Some(Exclusion::External)
        );
        // The second step's reading follows the first step's own write.
        assert_eq!(
            transition.decide(O::BalancedAutomatic, next).unwrap(),
            Some(Exclusion::External)
        );
        assert_eq!(transition.exclusion(), Some(Exclusion::External));
        assert!(transition.compared());
        // An observed HyperBoost compares nothing; any other decision does.
        for (reading, compared) in [(O::HyperBoostObserved, false), (O::BalancedAutomatic, true)] {
            let decision = Transition::new(
                Arc::new(Mutex::new(Tracker::default())),
                Mode::Overwrite,
                false,
                start,
            );
            decision.decide(reading, next).unwrap();
            assert_eq!(decision.compared(), compared, "{reading:?}");
        }
        // The hold is stamped from the reconciliation's start.
        let mut tracker = tracker.lock().unwrap();
        assert_eq!(tracker.hold.unwrap().expires, start + RESUME_WINDOW);
        tracker.settle(O::BalancedAutomatic, start + RESUME_WINDOW);
        assert_eq!(tracker.external(), [PERFORMANCE]);
    }

    #[test]
    fn only_confirmed_own_manual_fans_with_no_change_since_are_owned() {
        let mut tracker = Tracker::default();
        assert!(!tracker.owns_manual_fans());
        // A reading alone is not OpenBlade's own.
        tracker.observe(O::PerformanceManual);
        assert!(!tracker.owns_manual_fans());
        tracker.own_fans_read(O::PerformanceManual);
        assert!(tracker.owns_manual_fans());
        // A performance change keeps the fans; a fan change ends them.
        tracker.observe(O::SilentManual);
        assert!(tracker.owns_manual_fans());
        tracker.observe(O::SilentAutomatic);
        assert!(!tracker.owns_manual_fans());
        // Back to manual outside OpenBlade is still not its own.
        tracker.observe(O::SilentManual);
        assert!(!tracker.owns_manual_fans());
        // Own Automatic fans are not manual ownership.
        tracker.own_fans(false);
        assert!(!tracker.owns_manual_fans());
        tracker.own_fans(true);
        assert!(tracker.owns_manual_fans());
    }
}
