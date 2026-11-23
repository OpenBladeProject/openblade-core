//! The automatic half of managed `DisplayAutomationCoordinator`
//! (src/OpenBlade.Session/DisplayAutomationCoordinator.cs): `Observe`,
//! `MarkUnavailable`, the transition bookkeeping of `RunTransitionAsync` and
//! `ScheduleTransition`, on the same state as the manual half in `recovery`.
//! Every method runs under the recovery lock, as managed runs under `sync`.
//!
//! Managed updates `DisplayPowerContextState` itself; native power admission
//! is updated by the status watcher before each call here. `context` mirrors
//! what managed would have recorded, so a context that was lost and then
//! recovered with the same confirmed source is detected the managed way, and
//! each managed `powerContext.Update` is also a new admission generation
//! (`PowerAdmission::advance`). A transition is scheduled only against a
//! confirmed admission of its own source; otherwise it waits, pending, for
//! the next confirmed observation of that source (managed instead forces the
//! context to the remembered source).
//!
//! The transition itself, the target merge and the retry loop are
//! `automation::run_transition`; it calls the bookkeeping here between
//! applier calls, never while one runs.
use super::{CheckpointFile, Fields, Recovery, Status};
use crate::session_native::{
    display_query::{observation::Snapshot, profile_apply::Settings},
    power_admission::{PowerAdmission, PowerContext},
};
use serde_json::{Value, json};
use std::{
    borrow::Cow,
    sync::{
        Arc, Condvar, Mutex, PoisonError,
        atomic::{AtomicBool, Ordering},
    },
    time::{Duration, Instant},
};
use tokio::sync::Notify;

/// Managed `DurabilityBlocked`.
pub(in super::super) const DURABILITY: &str =
    "Display automation is paused because its recovery checkpoint could not be saved.";

/// Managed `DisplayAutomationObservation`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(in super::super) enum Observation {
    IgnoredUnconfirmed,
    BaselineEstablished,
    SameSource,
    TransitionScheduled,
}

/// Managed `DisplayAutomationTarget`: the confirmed source, the active
/// application profile (a lowercase GUID), the power profile's display
/// settings and the application override's, if any.
#[derive(Clone, Debug, PartialEq, Eq)]
pub(in super::super) struct Target {
    pub(in super::super) source: u8,
    pub(in super::super) application: Option<String>,
    pub(in super::super) base: Settings,
    pub(in super::super) application_settings: Option<Settings>,
}

/// One transition's cancellation (managed `transitionCancellation`): the
/// flag every writer fence and query observes, and a wake for the retry delay.
#[derive(Default)]
pub(in super::super) struct Cancel {
    flag: AtomicBool,
    lock: Mutex<()>,
    wake: Condvar,
}

impl Cancel {
    pub(in super::super) fn cancel(&self) {
        self.flag.store(true, Ordering::Release);
        let _held = self.lock.lock().unwrap_or_else(PoisonError::into_inner);
        self.wake.notify_all();
    }

    pub(in super::super) fn flag(&self) -> &AtomicBool {
        &self.flag
    }

    pub(in super::super) fn canceled(&self) -> bool {
        self.flag.load(Ordering::Acquire)
    }

    /// Blocking: waits `delay` unless canceled first. True when the whole
    /// delay elapsed (managed `Task.Delay(delay, token)` without throwing).
    pub(in super::super) fn sleep(&self, delay: Duration) -> bool {
        let deadline = Instant::now() + delay;
        let mut held = self.lock.lock().unwrap_or_else(PoisonError::into_inner);
        loop {
            if self.canceled() {
                return false;
            }
            let Some(left) = deadline.checked_duration_since(Instant::now()) else {
                return true;
            };
            if left.is_zero() {
                return true;
            }
            held = self
                .wake
                .wait_timeout(held, left)
                .unwrap_or_else(PoisonError::into_inner)
                .0;
        }
    }
}

/// A scheduled transition (managed `RunTransitionAsync` arguments).
pub(in super::super) struct Transition {
    generation: u64,
    pub(in super::super) target: Target,
    /// The confirmed admission of `target.source` this transition may write on.
    pub(in super::super) admitted: PowerContext,
    pub(in super::super) cancel: Arc<Cancel>,
}

/// Managed `DisplayTransitionResult`, without the snapshot. Managed keeps it
/// in memory only and nothing in the installed Agent reads it; tests do.
#[derive(Clone, Debug, PartialEq, Eq)]
pub(in super::super) struct Outcome {
    pub(in super::super) generation: u64,
    pub(in super::super) source: u8,
    pub(in super::super) status: Status,
    pub(in super::super) error: Option<Cow<'static, str>>,
}

/// The managed `DisplayPowerContextState` this coordinator last recorded:
/// a confirmed source, or ExternalUnknown with its manual-external flag.
#[derive(Clone, Copy, Default, PartialEq, Eq)]
struct Context {
    source: Option<u8>,
    manual: bool,
}

#[derive(Default)]
pub(super) struct Automatic {
    /// Managed `automaticWriteInFlight`.
    in_flight: bool,
    /// Managed `automaticCancellationHold`.
    cancellation_hold: bool,
    /// Managed `manualOperationPending`.
    manual_pending: bool,
    /// Managed `pendingTransitionPowerSource`.
    pending: Option<u8>,
    /// Managed `lastObservedTarget`.
    observed: Option<Target>,
    context: Context,
    /// Managed `generation`.
    generation: u64,
    /// Managed `transitionCancellation`.
    active: Option<Arc<Cancel>>,
    /// Native unresolved-field gate: the latest target was refused at
    /// admission because it touches a field whose submission may be
    /// unresolved. An explicit read that resolves a field schedules it again.
    gated: bool,
    /// The one transition waiting for the runner. Scheduling replaces it, as
    /// managed `ScheduleTransition` cancels the one before.
    scheduled: Option<Transition>,
    #[cfg_attr(not(test), allow(dead_code))]
    last_result: Option<Outcome>,
    power: Option<PowerAdmission>,
    wake: Option<Arc<Notify>>,
}

/// Managed `TryRecordRecoveryState` baseline: the members of the snapshot the
/// application overwrote, in the durable record's form. Managed keeps the
/// whole snapshot in memory and records HDR only where it is supported; the
/// merge reads the recorded form, so an unsupported panel's HDR state is
/// `false` (managed reads `HdrEnabled` as reported, which only a panel that
/// reports HDR unsupported yet enabled can make differ).
pub(in super::super) fn baseline(state: &Snapshot) -> Value {
    let state = state.observed();
    json!({
        "RefreshRateKey": state.refresh_key,
        "HdrEnabled": state.hdr_supported.then_some(state.hdr_enabled),
        "ColorProfileKey": state.color_profile,
        "BrightnessPercent": state.brightness,
    })
}

impl<F: CheckpointFile> Recovery<F> {
    /// Binds the trusted power admission this coordinator advances and the
    /// runner's wake.
    pub(in super::super) fn attach(&mut self, power: PowerAdmission, wake: Arc<Notify>) {
        self.automatic.power = Some(power);
        self.automatic.wake = Some(wake);
    }

    pub(super) fn in_flight(&self) -> bool {
        self.automatic.in_flight
    }

    /// `BeginManualOperation`'s automatic part: a manual operation is pending
    /// and the active transition is canceled.
    pub(super) fn begin_manual(&mut self) {
        self.automatic.manual_pending = true;
        self.cancel_active();
    }

    /// `CompleteManualOperation` clears the pending manual operation; a
    /// confirmed one, or one that restores an unpaused state, also clears the
    /// cancellation hold.
    pub(super) fn complete_manual(&mut self, clears_hold: bool) {
        self.automatic.manual_pending = false;
        if clears_hold {
            self.automatic.cancellation_hold = false;
        }
    }

    /// `ResumeAfterSuccessfulExplicitRead` after its checkpoint was saved:
    /// schedule the latest observed target.
    pub(super) fn resume_latest(&mut self) {
        if let Some(target) = self.automatic.observed.clone() {
            let admitted = self.advance_to(target.source);
            self.schedule(target, admitted);
        }
    }

    /// A manual mutation is about to wait for the display operation gate.
    /// Managed `BeginManualOperation` runs before the applier's gate, so the
    /// mutation is pending from here: the active transition is canceled, and
    /// whichever of its result and the mutation's `begin` comes first, the
    /// transition records as it would under a begun manual operation and
    /// neither unpauses nor resumes. `end_manual_turn` clears what a mutation
    /// refused before `begin` leaves.
    pub(in super::super) fn preempt(&mut self) {
        self.begin_manual();
    }

    /// The manual turn ended, still holding the gate. The endpoint serves one
    /// request at a time, so no manual operation is pending after it.
    pub(in super::super) fn end_manual_turn(&mut self) {
        self.automatic.manual_pending = false;
    }

    /// Native unresolved-field gate: an explicit read resolved a field, so a
    /// target refused at admission for one is scheduled again (managed, which
    /// has no such gate, applied it then). A paused or held target waits as
    /// any other does.
    pub(super) fn resume_gated(&mut self) {
        if self.automatic.gated && !self.writes_paused() && !self.manual_hold {
            self.resume_latest();
        }
    }

    /// Managed `Observe` for a confirmed source.
    pub(in super::super) fn observe(&mut self, target: Target) -> Observation {
        let source = target.source;
        let previous = self.automatic.observed.replace(target.clone());
        let had_observed = previous.is_some();
        let changed = previous.as_ref() != Some(&target);
        let automation_changed = changed
            && (target.application.is_some()
                || previous.is_some_and(|previous| previous.application.is_some())
                || self.baseline.is_some());

        let Some(last) = self.last else {
            self.last = Some(source);
            let admitted = self.advance_to(source);
            if !self.persist() {
                self.latch();
                return Observation::BaselineEstablished;
            }
            if target.application.is_some() && !self.writes_paused() && !self.manual_hold {
                self.schedule(target, admitted);
                return Observation::TransitionScheduled;
            }
            return Observation::BaselineEstablished;
        };

        if last == source {
            if self.automatic.context.source != Some(source) {
                let admitted = self.advance_to(source);
                if self.automatic.pending == Some(source)
                    && !self.writes_paused()
                    && !self.manual_hold
                {
                    self.schedule(target, admitted);
                    return Observation::TransitionScheduled;
                }
            }
            if automation_changed && self.automatic.in_flight {
                self.hold_for_cancellation();
                self.advance_to(source);
                self.cancel_active();
                let _ = self.persist();
                return Observation::SameSource;
            }
            if automation_changed
                && !self.manual_hold
                && !self.writes_paused()
                && (had_observed || self.baseline.is_some() || target.application.is_some())
            {
                let admitted = self.advance_to(source);
                self.schedule(target, admitted);
                return Observation::TransitionScheduled;
            }
            return Observation::SameSource;
        }

        self.last = Some(source);
        let admitted = self.advance_to(source);
        self.cancel_active();
        if self.automatic.in_flight {
            self.hold_for_cancellation();
        }
        self.baseline = None;
        self.owned = Fields::default();
        self.application_profile_id = None;
        if self.writes_paused() {
            let _ = self.persist();
            return Observation::SameSource;
        }
        self.manual_hold = false;
        if !self.persist() {
            self.latch();
            return Observation::SameSource;
        }
        self.schedule(target, admitted);
        Observation::TransitionScheduled
    }

    /// Managed `MarkUnavailableCore(manual)`: ExternalUnknown, a retained
    /// source, a reconnecting subscription or an unreadable status.
    pub(in super::super) fn mark_unavailable(&mut self, manual: bool) -> Observation {
        let lost = Context {
            source: None,
            manual,
        };
        if self.automatic.context != lost {
            self.automatic.context = lost;
            self.automatic.generation += 1;
            self.cancel_active();
            if self.automatic.in_flight {
                self.hold_for_cancellation();
                if self.last.is_some() {
                    let _ = self.persist();
                }
            }
        }
        Observation::IgnoredUnconfirmed
    }

    /// The transition the runner should start next, if any.
    pub(in super::super) fn take_transition(&mut self) -> Option<Transition> {
        self.automatic.scheduled.take()
    }

    /// The baseline and ownership a transition starts from.
    pub(in super::super) fn transition_start(&self) -> (Option<Value>, Fields) {
        (self.baseline.clone(), self.owned)
    }

    /// The baseline query was unavailable: managed records its result.
    pub(in super::super) fn transition_unavailable(&mut self, transition: &Transition) {
        if self.current(transition) {
            self.automatic.pending = None;
            self.record(transition, Status::Succeeded, None);
        }
    }

    /// The baseline query succeeded: managed keeps it for this application.
    pub(in super::super) fn transition_baseline(
        &mut self,
        transition: &Transition,
        baseline: Value,
    ) {
        if self.current(transition) {
            self.baseline = Some(baseline);
            self.application_profile_id = transition.target.application.clone();
        }
    }

    /// Managed admission before the first apply attempt: the merged settings
    /// become the in-flight write and its write-ahead checkpoint. `None`
    /// refuses; otherwise whether this transition owns an automatic write.
    ///
    /// Native-only gate first: a transition touching a field whose earlier
    /// submission may be unresolved is refused like a manual write.
    pub(in super::super) fn admit_transition(
        &mut self,
        transition: &Transition,
        owned: Fields,
        settings: &Settings,
    ) -> Option<bool> {
        if !self.current(transition) {
            return None;
        }
        let fields = settings.fields();
        if self.unresolved.overlaps(fields) {
            // The target stays pending until a read resolves the field.
            self.automatic.gated = true;
            self.record(
                transition,
                Status::Blocked,
                Some(Cow::Borrowed(super::super::profile_apply::PAUSED)),
            );
            return None;
        }
        self.owned = owned;
        self.application_profile_id = transition.target.application.clone();
        let owns = settings.configured();
        if owns {
            self.automatic.in_flight = true;
            self.uncertain = fields;
        }
        if !self.persist() {
            self.automatic.in_flight = false;
            self.latch();
            self.automatic.pending = None;
            self.record(transition, Status::Blocked, Some(Cow::Borrowed(DURABILITY)));
            return None;
        }
        Some(owns)
    }

    /// Managed completion after the apply attempts. `unresolved` is the
    /// native write gate's share: fields whose submission may be unresolved.
    pub(in super::super) fn complete_transition(
        &mut self,
        transition: &Transition,
        owns: bool,
        status: Status,
        error: Option<Cow<'static, str>>,
        unresolved: Fields,
    ) {
        let current = self.current(transition);
        if !current && !owns {
            return;
        }
        if owns {
            self.automatic.in_flight = false;
            self.unresolved = self.unresolved.include(unresolved);
        }
        self.automatic.pending = None;
        self.record(transition, status, error);
        if status == Status::Indeterminate {
            self.latch();
            self.cancel_active();
            let _ = self.persist();
            return;
        }
        if self.automatic.manual_pending {
            self.paused = true;
            self.automatic.cancellation_hold = false;
            let _ = self.persist();
            return;
        }
        if current && status == Status::Succeeded && transition.target.application.is_none() {
            self.baseline = None;
            self.owned = Fields::default();
            self.application_profile_id = None;
        }
        let completed = self.uncertain;
        self.paused = false;
        self.uncertain = Fields::default();
        let resume = std::mem::take(&mut self.automatic.cancellation_hold);
        if resume {
            self.manual_hold = false;
        }
        if !self.persist() {
            self.uncertain = completed;
            self.latch();
            self.record(transition, Status::Blocked, Some(Cow::Borrowed(DURABILITY)));
            self.cancel_active();
            return;
        }
        if !current
            && resume
            && let Some(latest) = self.automatic.observed.clone()
            && self.last == Some(latest.source)
        {
            let admitted = self.advance_to(latest.source);
            self.schedule(latest, admitted);
        }
    }

    /// Managed `OperationCanceledException` from the retry delay.
    pub(in super::super) fn cancel_transition(&mut self, owns: bool) {
        if owns {
            self.automatic.in_flight = false;
            self.latch();
            self.automatic.pending = None;
            if self.automatic.manual_pending {
                self.automatic.cancellation_hold = false;
            }
            let _ = self.persist();
        }
    }

    /// Managed `catch (Exception)`: a fatal failure, recorded Indeterminate.
    /// An owned write's fields stay in the native gate, as after a restart.
    pub(in super::super) fn fail_transition(
        &mut self,
        transition: &Transition,
        owns: bool,
        error: &std::io::Error,
    ) {
        if !self.current(transition) && !owns {
            return;
        }
        if owns {
            self.automatic.in_flight = false;
            self.unresolved = self.unresolved.include(self.uncertain);
        }
        self.latch();
        self.automatic.pending = None;
        self.record(
            transition,
            Status::Indeterminate,
            Some(Cow::Owned(error.to_string())),
        );
        let _ = self.persist();
    }

    #[cfg(test)]
    pub(in super::super) fn last_result(&self) -> Option<&Outcome> {
        self.automatic.last_result.as_ref()
    }

    #[cfg(test)]
    pub(in super::super) fn scheduled(&self) -> Option<&Transition> {
        self.automatic.scheduled.as_ref()
    }

    #[cfg(test)]
    pub(in super::super) fn automatic_flags(&self) -> (bool, bool, bool, Option<u8>) {
        let automatic = &self.automatic;
        (
            automatic.in_flight,
            automatic.cancellation_hold,
            automatic.manual_pending,
            automatic.pending,
        )
    }

    /// Managed `automaticWritesPaused` for scheduling. Managed
    /// `BeginManualOperation` pauses before the applier's gate; native records
    /// the pause at `begin`, under the gate, so a mutation pending since
    /// `preempt` pauses scheduling as well.
    fn writes_paused(&self) -> bool {
        self.paused || self.automatic.manual_pending
    }

    fn current(&self, transition: &Transition) -> bool {
        transition.generation == self.automatic.generation && !transition.cancel.canceled()
    }

    fn record(
        &mut self,
        transition: &Transition,
        status: Status,
        error: Option<Cow<'static, str>>,
    ) {
        self.automatic.last_result = Some(Outcome {
            generation: transition.generation,
            source: transition.target.source,
            status,
            error,
        });
    }

    /// The automatic write in flight was abandoned: pause until it is resolved
    /// and resume the latest target once it completes.
    fn hold_for_cancellation(&mut self) {
        self.latch();
        self.automatic.cancellation_hold = true;
    }

    /// Managed `powerContext.Update(source, NextGeneration())`: the new
    /// admission, when it confirms `source`.
    fn advance_to(&mut self, source: u8) -> Option<PowerContext> {
        self.automatic.generation += 1;
        self.automatic.context = Context {
            source: Some(source),
            manual: false,
        };
        self.automatic
            .power
            .as_ref()?
            .advance()
            .filter(|admitted| admitted.source == source)
    }

    /// Managed `CancelActiveTransition`.
    fn cancel_active(&mut self) {
        if let Some(active) = self.automatic.active.take() {
            active.cancel();
        }
        self.automatic.scheduled = None;
    }

    /// Managed `ScheduleTransition`. Without a confirmed admission of its
    /// source the transition stays pending, and the context counts as lost so
    /// the next confirmed observation of that source schedules it.
    fn schedule(&mut self, target: Target, admitted: Option<PowerContext>) {
        self.cancel_active();
        self.automatic.gated = false;
        self.automatic.pending = Some(target.source);
        let Some(admitted) = admitted else {
            self.automatic.context = Context::default();
            return;
        };
        let cancel = Arc::new(Cancel::default());
        self.automatic.active = Some(cancel.clone());
        self.automatic.scheduled = Some(Transition {
            generation: self.automatic.generation,
            target,
            admitted,
            cancel,
        });
        if let Some(wake) = &self.automatic.wake {
            wake.notify_one();
        }
    }
}
