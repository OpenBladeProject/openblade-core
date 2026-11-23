//! Confirmed thermal drift, held in memory (managed
//! `ConfirmedStateDriftTracker` and the automatic-transition resolver,
//! `ServiceControlRuntime.ApplyDesiredTransitionWithRetryAsync`).
//!
//! The tracker compares each fresh exact-device thermal reading with the
//! last one: a known value that changed is an external change of its
//! subsystem, and a reading that leaves a value unknown is no evidence. The
//! first reading after the Service starts only sets the baseline, so a
//! restart forgets every latch. OpenBlade's own write readbacks are
//! intentional: a readback that matches the write's destination clears the
//! subsystem, one that does not marks it (`confirm`).
//!
//! An automatic reconciliation (`Transition`) compares its own fresh reading
//! with the saved intent it applies before any write. A subsystem that matches
//! neither the intent OpenBlade last resolved nor the new one stays an
//! external change, and either thermal subsystem changed outside OpenBlade
//! excludes both from that reconciliation (managed
//! `PendingWriteConfirmationTracker.GetWriteExclusions`), as an observed
//! HyperBoost does. Exclusion only removes writes; every write that remains
//! keeps its own admission, readback and stop-on-ambiguity path.
//!
//! Native reads the selector pair and the manual-fan bit only, so fans
//! compare as manual or Automatic (managed compares the exact mode), and GPU
//! clock offsets, which native does not read, are never evidence.
use openblade_domain::{
    thermal_mutation::{CUSTOM, Destination},
    thermal_observation::{CustomPerformanceLevels, ObservedPerformance},
};
use std::sync::{Arc, Mutex};
use tokio::time::Instant;

/// Managed `DeviceSubsystem.Performance` and `DeviceSubsystem.Fans`.
const PERFORMANCE: i32 = 0;
const FANS: i32 = 1;
/// Managed `ResumeStateValidationTracker.ObservationWindow`.
const RESUME_WINDOW: std::time::Duration = std::time::Duration::from_secs(60);

/// The thermal intent one reconciliation applies, or the one OpenBlade last
/// resolved: managed `DesiredDeviceState.Performance` and `Fans`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) struct Desired {
    pub(super) performance: Option<Destination>,
    pub(super) manual_fans: bool,
}

/// How a reconciliation treats confirmed drift.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub(super) enum Mode {
    /// Managed `preserveObservedExternalChanges: true` after a power change,
    /// a request trigger or a deferred debt replayed at a gate boundary: a
    /// reading matching the intent OpenBlade last resolved or the new one is
    /// OpenBlade's own.
    WithPrior,
    /// The lease expiry. Managed publishes the power profile's desired state
    /// before its transition reads the prior one, so only the new intent is
    /// OpenBlade's own (`ExpireApplicationContextAsync`).
    NextOnly,
    /// Managed `preserveObservedExternalChanges: false`: the startup
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

#[derive(Debug, Default)]
pub(super) struct Tracker {
    performance: bool,
    fans: bool,
    preset: Option<i32>,
    custom: Option<CustomPerformanceLevels>,
    manual: Option<bool>,
    desired: Option<Desired>,
    hold: Option<Hold>,
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
fn matches(reading: ObservedPerformance, desired: Option<Destination>) -> bool {
    desired.is_some_and(|desired| {
        preset(reading) == Some(desired.wire())
            && desired
                .custom_levels()
                .is_none_or(|levels| reading.custom_levels() == Some(levels))
    })
}

impl Tracker {
    /// Managed `ConfirmedStateDriftTracker.Observe`.
    pub(super) fn observe(&mut self, reading: ObservedPerformance) {
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
    }

    /// Managed `ConfirmIntentional` for OpenBlade's own write of `fans` (else
    /// performance): its readback is observed first, then a match clears the
    /// subsystem and a mismatch marks it. A performance write ends any resume
    /// hold (managed clears it once a transition attempts Performance).
    pub(super) fn confirm(&mut self, readback: ObservedPerformance, fans: bool, matched: bool) {
        self.observe(readback);
        if fans {
            self.fans = !matched;
        } else {
            self.performance = !matched;
            self.hold = None;
        }
    }

    /// OpenBlade resolved `desired` for an explicit request, which becomes
    /// the prior intent of the next reconciliation.
    pub(super) fn resolved(&mut self, desired: Desired) {
        self.desired = Some(desired);
    }

    /// Ends a resume hold: a new gate boundary, a power change or low power
    /// (managed clears it there, or drops it at its next observation).
    pub(super) fn drop_hold(&mut self) {
        self.hold = None;
    }

    /// Managed `ReconcileResumePerformanceDrift` at a fresh status reading:
    /// within the window, a reading that matches the held destination clears
    /// the performance change. A competing controller, a changed intent or a
    /// performance change no longer recorded drops the hold.
    pub(super) fn settle(&mut self, reading: ObservedPerformance, controllers: bool, now: Instant) {
        let Some(hold) = self.hold else {
            return;
        };
        if now >= hold.expires
            || !self.performance
            || controllers
            || self.desired.map(|desired| desired.performance) != Some(hold.desired)
        {
            self.hold = None;
            return;
        }
        if matches(reading, hold.desired) {
            self.performance = false;
            self.hold = None;
        }
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
    /// write: managed `ResolveAutomaticWriteExclusions`. `next` becomes the
    /// prior intent only once the reconciliation commits it
    /// (`Transition::commit`).
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
        let prior = self.desired;
        // Managed excludes both while HyperBoost is active or unknown,
        // whichever mode the transition runs in.
        if reading == ObservedPerformance::HyperBoostObserved {
            return Some(Exclusion::HyperBoost);
        }
        // Nothing resolved since the Service started: this is its startup
        // reconciliation, which managed applies over drift
        // (`ReconcileStartupProfileAsync`).
        if mode == Mode::Overwrite || prior.is_none() {
            return None;
        }
        let prior = prior.filter(|_| mode == Mode::WithPrior);
        if preset(reading).is_some() {
            self.performance = !(matches(reading, next.performance)
                || prior.is_some_and(|prior| matches(reading, prior.performance)));
        }
        if let Some(manual) = manual(reading) {
            self.fans = !(manual == next.manual_fans
                || prior.is_some_and(|prior| prior.manual_fans == manual));
        }
        // Firmware can settle into the destination after the first read on
        // resume: keep writes paused until a fresh reading within the window
        // proves it (managed `pendingResumePerformanceDrift`).
        if resume && !was && self.performance && prior.is_some() {
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

/// One reconciliation's drift decision, shared by its steps: the first step
/// to read firmware decides for both subsystems, and a later step reuses it.
/// Its intent becomes the prior intent of the next reconciliation only when
/// it commits (`commit`): managed carries a deferred transition's own prior
/// intent into its retry (`PendingTransitionContinuation.PriorDesired`), so a
/// reconciliation that was deferred or refused before any submission does
/// not make its unapplied intent OpenBlade's own.
pub(super) struct Transition {
    tracker: Arc<Mutex<Tracker>>,
    mode: Mode,
    resume: bool,
    decided: Mutex<Option<(Option<Exclusion>, Desired)>>,
}
impl Transition {
    pub(super) fn new(tracker: Arc<Mutex<Tracker>>, mode: Mode, resume: bool) -> Self {
        Self {
            tracker,
            mode,
            resume,
            decided: Mutex::new(None),
        }
    }
    /// The exclusion for this reconciliation, decided once from the first
    /// step's fresh `reading` and its saved intent `next`.
    pub(super) fn decide(
        &self,
        reading: ObservedPerformance,
        next: Desired,
    ) -> std::io::Result<Option<Exclusion>> {
        let mut decided = self.decided.lock().map_err(|_| std::io::ErrorKind::Other)?;
        if let Some((decision, _)) = *decided {
            return Ok(decision);
        }
        let decision = self
            .tracker
            .lock()
            .map_err(|_| std::io::ErrorKind::Other)?
            .decide(reading, next, self.mode, self.resume, Instant::now());
        *decided = Some((decision, next));
        Ok(decision)
    }
    /// The exclusion a step decided, `None` before any step decided.
    pub(super) fn exclusion(&self) -> Option<Exclusion> {
        self.decided
            .lock()
            .ok()
            .and_then(|decided| decided.and_then(|(decision, _)| decision))
    }
    /// The reconciliation finished without being deferred or owed for a
    /// retry: its decided intent is the prior intent of the next one.
    pub(super) fn commit(&self) {
        let next = self
            .decided
            .lock()
            .ok()
            .and_then(|decided| decided.map(|(_, next)| next));
        if let (Some(next), Ok(mut tracker)) = (next, self.tracker.lock()) {
            tracker.resolved(next);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ObservedPerformance as O;
    use openblade_domain::thermal_mutation::Preset;

    fn desired(preset: Preset, manual_fans: bool) -> Desired {
        Desired {
            performance: Some(Destination::Preset(preset)),
            manual_fans,
        }
    }
    fn baselined(reading: ObservedPerformance) -> Tracker {
        let mut tracker = Tracker::default();
        tracker.observe(reading);
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
        use openblade_domain::thermal_observation::CustomPerformanceLevel::{Low, Medium};
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
        let medium = Some(Destination::Custom(CustomPerformanceLevels {
            cpu: Low,
            gpu: Medium,
        }));
        assert!(matches(custom(Medium), medium));
        assert!(!matches(custom(Low), medium));
        assert!(!matches(O::BalancedAutomatic, medium));
    }

    #[test]
    fn an_own_readback_clears_on_a_match_and_marks_on_a_mismatch() {
        let mut tracker = baselined(O::BalancedAutomatic);
        tracker.observe(O::SilentAutomatic);
        tracker.confirm(O::PerformanceAutomatic, false, true);
        assert!(tracker.external().is_empty());
        tracker.confirm(O::PerformanceManual, true, false);
        assert_eq!(tracker.external(), [FANS]);
        tracker.confirm(O::PerformanceAutomatic, true, true);
        assert!(tracker.external().is_empty());
        // A carried preset the readback does not confirm is marked.
        tracker.confirm(O::PerformanceAutomatic, false, false);
        assert_eq!(tracker.external(), [PERFORMANCE]);
    }

    #[test]
    fn a_reconciliation_excludes_both_subsystems_unless_a_reading_matches_prior_or_next() {
        let now = Instant::now();
        let mut tracker = baselined(O::PerformanceAutomatic);
        tracker.resolved(desired(Preset::Performance, false));
        // OpenBlade's own prior preset is not drift.
        let next = desired(Preset::BatterySaver, false);
        assert_eq!(
            tracker.decide(O::PerformanceAutomatic, next, Mode::WithPrior, false, now),
            None
        );
        // The lease expiry reads only the new intent.
        tracker.resolved(desired(Preset::Performance, false));
        assert_eq!(
            tracker.decide(O::PerformanceAutomatic, next, Mode::NextOnly, false, now),
            Some(Exclusion::External)
        );
        assert_eq!(tracker.external(), [PERFORMANCE]);
        // Matching the new intent clears it again.
        assert_eq!(
            tracker.decide(O::BatterySaverAutomatic, next, Mode::NextOnly, false, now),
            None
        );
        // Fans alone exclude both.
        let mut tracker = baselined(O::PerformanceAutomatic);
        tracker.resolved(desired(Preset::Performance, false));
        assert_eq!(
            tracker.decide(O::PerformanceManual, next, Mode::WithPrior, false, now),
            Some(Exclusion::External)
        );
        assert_eq!(tracker.external(), [FANS]);
    }

    #[test]
    fn hyperboost_excludes_both_and_activation_overwrites_drift() {
        let now = Instant::now();
        let next = desired(Preset::Balanced, false);
        let mut tracker = baselined(O::PerformanceAutomatic);
        for mode in [Mode::WithPrior, Mode::NextOnly, Mode::Overwrite] {
            assert_eq!(
                tracker.decide(O::HyperBoostObserved, next, mode, false, now),
                Some(Exclusion::HyperBoost)
            );
        }
        let mut tracker = baselined(O::PerformanceAutomatic);
        tracker.observe(O::SilentAutomatic);
        assert_eq!(
            tracker.decide(O::SilentAutomatic, next, Mode::Overwrite, false, now),
            None
        );
        // The overwrite's own readback clears the latch.
        tracker.confirm(O::BalancedAutomatic, false, true);
        assert!(tracker.external().is_empty());
        // Only a committed reconciliation makes its intent the prior one.
        assert_eq!(tracker.desired, None);
    }

    #[test]
    fn a_resume_hold_clears_when_the_destination_is_read_within_the_window() {
        let now = Instant::now();
        let next = desired(Preset::BatterySaver, false);
        let held = || {
            let mut tracker = baselined(O::PerformanceAutomatic);
            tracker.resolved(desired(Preset::Performance, false));
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
            // The excluded reconciliation commits its intent.
            tracker.resolved(next);
            tracker
        };
        let mut tracker = held();
        tracker.settle(O::BatteryBalancedAutomatic, false, now);
        assert_eq!(tracker.external(), [PERFORMANCE]);
        tracker.settle(O::BatterySaverAutomatic, false, now);
        assert!(tracker.external().is_empty());
        assert!(tracker.hold.is_none());
        // Past the window, with a controller, or after another intent, the
        // hold is dropped and the latch stays.
        let mut tracker = held();
        tracker.settle(O::BatterySaverAutomatic, false, now + RESUME_WINDOW);
        assert!(tracker.hold.is_none());
        let mut tracker = held();
        tracker.settle(O::BatterySaverAutomatic, true, now);
        assert!(tracker.hold.is_none());
        let mut tracker = held();
        tracker.resolved(desired(Preset::Balanced, false));
        tracker.settle(O::BatterySaverAutomatic, false, now);
        assert!(tracker.hold.is_none());
        assert_eq!(tracker.external(), [PERFORMANCE]);
        // OpenBlade's own performance write ends it too.
        let mut tracker = held();
        tracker.confirm(O::BatteryBalancedAutomatic, false, false);
        assert!(tracker.hold.is_none());
        tracker.drop_hold();
        // Without the resume, or with drift already recorded, nothing is held.
        let mut tracker = baselined(O::PerformanceAutomatic);
        tracker.resolved(desired(Preset::Performance, false));
        tracker.decide(
            O::BatteryBalancedAutomatic,
            next,
            Mode::WithPrior,
            false,
            now,
        );
        assert!(tracker.hold.is_none());
        let mut tracker = baselined(O::PerformanceAutomatic);
        tracker.observe(O::SilentAutomatic);
        tracker.resolved(desired(Preset::Performance, false));
        tracker.decide(
            O::BatteryBalancedAutomatic,
            next,
            Mode::WithPrior,
            true,
            now,
        );
        assert!(tracker.hold.is_none());
    }

    #[test]
    fn a_transition_decides_once_for_both_steps() {
        let tracker = Arc::new(Mutex::new(baselined(O::PerformanceAutomatic)));
        tracker
            .lock()
            .unwrap()
            .resolved(desired(Preset::Performance, false));
        let transition = Transition::new(tracker.clone(), Mode::WithPrior, false);
        let next = desired(Preset::Balanced, false);
        assert_eq!(
            transition.decide(O::PerformanceAutomatic, next).unwrap(),
            None
        );
        // The second step's reading follows the first step's own write.
        assert_eq!(transition.decide(O::SilentManual, next).unwrap(), None);
        assert!(tracker.lock().unwrap().external().is_empty());
    }

    #[test]
    fn only_a_committed_transition_becomes_the_prior_intent() {
        let tracker = Arc::new(Mutex::new(baselined(O::PerformanceAutomatic)));
        let next = desired(Preset::BatterySaver, false);
        // Nothing resolved since the start: the startup reconciliation
        // applies over any drift.
        let startup = Transition::new(tracker.clone(), Mode::WithPrior, false);
        tracker.lock().unwrap().observe(O::SilentAutomatic);
        assert_eq!(startup.decide(O::SilentAutomatic, next).unwrap(), None);
        // Deferred before any write: the retry is still the startup one.
        let retry = Transition::new(tracker.clone(), Mode::WithPrior, false);
        assert_eq!(retry.decide(O::SilentAutomatic, next).unwrap(), None);
        retry.commit();
        assert_eq!(tracker.lock().unwrap().desired, Some(next));
        // A later reconciliation that reads neither intent excludes both.
        let later = Transition::new(tracker.clone(), Mode::WithPrior, false);
        assert_eq!(
            later
                .decide(O::BalancedAutomatic, desired(Preset::Performance, false))
                .unwrap(),
            Some(Exclusion::External)
        );
        // A transition that never decided commits nothing.
        let undecided = Transition::new(tracker.clone(), Mode::WithPrior, false);
        undecided.commit();
        assert_eq!(tracker.lock().unwrap().desired, Some(next));
    }
}
