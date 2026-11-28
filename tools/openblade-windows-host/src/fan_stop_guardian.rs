//! Fixture-only replay of the Windows service's Fan Stop guardian ownership.
//! The effects are typed observations and one fixed Auto signal. There is no
//! process launcher, wire codec, persistent store, or native access here.

use std::num::NonZeroU128;

use crate::fan_stop_entry::{Power, Refusal, ThermalPolicy};

pub type OperationId = NonZeroU128;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ProcessIdentity {
    pub pid: i32,
    pub start_utc_ticks: i64,
}

impl ProcessIdentity {
    fn valid(self) -> bool {
        self.pid > 0 && self.start_utc_ticks > 0
    }
}

/// Exact settings captured in the reservation; never modified during ownership.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Settings {
    pub custom_end: bool,
    pub cpu_limit: i32,
    pub gpu_limit: i32,
    pub cpu_cutoff: i32,
    pub gpu_cutoff: i32,
    pub seconds_above_limit: i32,
    pub automatic_enabled: bool,
    pub automatic_cpu_below: i32,
    pub automatic_gpu_below: i32,
    pub seconds_below_limits: i32,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            custom_end: false,
            cpu_limit: 55,
            gpu_limit: 55,
            cpu_cutoff: 65,
            gpu_cutoff: 65,
            seconds_above_limit: 5,
            automatic_enabled: false,
            automatic_cpu_below: 50,
            automatic_gpu_below: 50,
            seconds_below_limits: 30,
        }
    }
}

impl Settings {
    fn valid(self) -> bool {
        [
            self.cpu_limit,
            self.gpu_limit,
            self.cpu_cutoff,
            self.gpu_cutoff,
            self.automatic_cpu_below,
            self.automatic_gpu_below,
        ]
        .into_iter()
        .all(|value| (10..=100).contains(&value))
            && self.cpu_cutoff >= self.cpu_limit
            && self.gpu_cutoff >= self.gpu_limit
            && (1..=300).contains(&self.seconds_above_limit)
            && (1..=3600).contains(&self.seconds_below_limits)
            && (!self.automatic_enabled
                || if self.custom_end {
                    self.automatic_cpu_below < self.cpu_limit
                        && self.automatic_gpu_below < self.gpu_limit
                } else {
                    self.automatic_cpu_below < 55
                })
    }

    pub fn entry_policy(self) -> ThermalPolicy {
        if self.custom_end {
            ThermalPolicy::Custom {
                cpu_cutoff: self.cpu_cutoff,
                gpu_cutoff: self.gpu_cutoff,
            }
        } else {
            ThermalPolicy::Auto
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Reservation {
    pub operation: OperationId,
    pub service: ProcessIdentity,
    pub settings: Settings,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Phase {
    Idle,
    Starting,
    Stopped,
    ReturningToAutomatic,
    Completed,
    RecoveryRequired,
    Refused,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ObservedState {
    Unknown,
    Ordinary,
    Stopped,
    Automatic,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum EndReason {
    None,
    ThermalThreshold,
    SafetyCondition,
    PowerChanged,
    SessionChanged,
    CallerRequest,
}

/// Typed owner observation after structural decoding. The owner cannot attest
/// its own exit. Only the service may add that fact after independent checks.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Snapshot {
    pub operation: OperationId,
    pub revision: i64,
    pub phase: Phase,
    pub confirmed_state: ObservedState,
    pub stop_confirmed: bool,
    pub auto_confirmed: bool,
    pub cleanup_confirmed: bool,
    pub journal_complete: bool,
    pub native_released: bool,
    pub owner_exited: bool,
    pub command_pending: bool,
    pub unresolved: bool,
    pub reason: Option<String>,
    pub failure: Option<String>,
    pub observed_power: Option<Power>,
    pub end_reason: EndReason,
    pub known_not_submitted: bool,
}

impl Snapshot {
    fn terminal_candidate(&self) -> bool {
        matches!(self.phase, Phase::Completed | Phase::Refused)
    }

    fn valid_owner_shape(&self) -> bool {
        self.phase != Phase::Idle
            && !self.owner_exited
            && [&self.reason, &self.failure].into_iter().all(|text| {
                text.as_ref()
                    .is_none_or(|s| s.encode_utf16().take(1025).count() <= 1024)
            })
    }

    fn handback(&self) -> Option<Handback> {
        if !self.native_released
            || !self.owner_exited
            || self.command_pending
            || self.unresolved
            || self.failure.is_some()
        {
            return None;
        }
        match self.phase {
            Phase::Completed
                if self.auto_confirmed
                    && self.cleanup_confirmed
                    && self.journal_complete
                    && self.confirmed_state == ObservedState::Automatic =>
            {
                Some(Handback::Automatic)
            }
            Phase::Refused
                if self.known_not_submitted
                    && !self.stop_confirmed
                    && self.confirmed_state == ObservedState::Ordinary =>
            {
                Some(Handback::NeverSubmitted)
            }
            _ => None,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Handback {
    Automatic,
    NeverSubmitted,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum StartupDisposition {
    Clear,
    Unresolved,
    Unknown,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum EvidenceError {
    Lost,
    Malformed,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Reserve {
    Durable,
    ProvenAbsent,
    Uncertain,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Launch {
    Dispatched,
    ProvenNotLaunched,
    Uncertain,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Exit {
    Pending,
    Observed { owner: ProcessIdentity, code: i32 },
    Lost,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum AutoSignal {
    Latched,
    /// Managed Auto can race autonomous completion. Reconcile only this one
    /// observation, and still require the same independent handback proof.
    NotSubmitted(Option<Snapshot>),
    Uncertain,
}

/// Scripted seams corresponding to the managed backend and store. Success
/// means the fixture observed the effect on the supplied exact identity.
pub trait Effects {
    fn acquire_delegation(&mut self, operation: OperationId) -> bool;
    fn release_delegation(&mut self, operation: OperationId);
    fn reserve(&mut self, reservation: Reservation) -> Reserve;
    fn dispatch(&mut self, operation: OperationId) -> Launch;
    fn archive_unlaunched(&mut self, reservation: Reservation) -> bool;
    fn signal_auto(&mut self, operation: OperationId) -> AutoSignal;
    fn observe_exit(&mut self, owner: ProcessIdentity) -> Exit;
    fn read_owner_result(&mut self, operation: OperationId) -> Result<Snapshot, EvidenceError>;
    fn archive_completed(&mut self, reservation: Reservation) -> bool;
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RestartReason {
    Startup(StartupDisposition),
    Reservation,
    Launch,
    OwnerIdentity,
    LostEvidence,
    InvalidEvidence,
    OwnerUnresolved,
    AutoSubmission,
    Exit,
    DurableResult,
    IncompleteHandback,
    Archive,
    ConfirmationExpired,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Disposition {
    Available,
    Delegated,
    RestartRequired(RestartReason),
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Start {
    Dispatched,
    NotSubmitted,
    Canceled,
    EntryRefused(Refusal),
    Fenced,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Auto {
    NoOwner,
    Waiting,
    Canceled,
    Finished(Handback),
    Fenced,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Continuation {
    pub operation: OperationId,
    pub can_stop: bool,
    pub observed_power: Option<Power>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ContinuationAction {
    Inactive,
    AwaitingOwner,
    Heartbeat(Option<Power>),
    Automatic,
}

struct Run {
    reservation: Reservation,
    owner: Option<ProcessIdentity>,
    latest: Option<Snapshot>,
    auto_latched: bool,
    continuation_lost: bool,
}

/// The service's lease and its recovery disposition are separate. A bounded
/// Stop confirmation can expire while the exact owner's transport still runs.
pub struct Replay {
    active: Option<Run>,
    terminal: Option<(Handback, Snapshot)>,
    fence: Option<RestartReason>,
    stop_confirmation_expired: bool,
}

impl Replay {
    pub fn new(startup: StartupDisposition) -> Self {
        Self {
            active: None,
            terminal: None,
            fence: (startup != StartupDisposition::Clear)
                .then_some(RestartReason::Startup(startup)),
            stop_confirmation_expired: false,
        }
    }

    pub fn disposition(&self) -> Disposition {
        if let Some(reason) = self.fence {
            Disposition::RestartRequired(reason)
        } else if self.stop_confirmation_expired {
            Disposition::RestartRequired(RestartReason::ConfirmationExpired)
        } else if self.active.is_some() {
            Disposition::Delegated
        } else {
            Disposition::Available
        }
    }

    pub fn holds_delegation(&self) -> bool {
        self.active.is_some()
    }

    pub fn accepted(&self) -> Option<&Snapshot> {
        match &self.active {
            Some(run) => run.latest.as_ref(),
            None => self.terminal.as_ref().map(|(_, snapshot)| snapshot),
        }
    }

    /// Entry evidence is evaluated separately by Phase 5p. A successful verdict
    /// permits this scripted service dispatch; it does not confirm Stop.
    pub fn start(
        &mut self,
        effects: &mut impl Effects,
        reservation: Reservation,
        entry: Result<(), Refusal>,
        guardian_available: bool,
        canceled_before_dispatch: bool,
    ) -> Start {
        if self.disposition() != Disposition::Available {
            return Start::Fenced;
        }
        if canceled_before_dispatch {
            return Start::Canceled;
        }
        if let Err(reason) = entry {
            return Start::EntryRefused(reason);
        }
        if !guardian_available
            || !reservation.service.valid()
            || !reservation.settings.valid()
            || self
                .terminal
                .as_ref()
                .is_some_and(|(_, prior)| prior.operation == reservation.operation)
            || !effects.acquire_delegation(reservation.operation)
        {
            return Start::NotSubmitted;
        }

        self.active = Some(Run {
            reservation,
            owner: None,
            latest: None,
            auto_latched: false,
            continuation_lost: false,
        });
        match effects.reserve(reservation) {
            Reserve::Durable => (),
            Reserve::ProvenAbsent => return self.release_unlaunched(effects),
            Reserve::Uncertain => {
                self.fence = Some(RestartReason::Reservation);
                return Start::Fenced;
            }
        }
        match effects.dispatch(reservation.operation) {
            Launch::Dispatched => Start::Dispatched,
            Launch::ProvenNotLaunched if effects.archive_unlaunched(reservation) => {
                self.release_unlaunched(effects)
            }
            Launch::ProvenNotLaunched => {
                self.fence = Some(RestartReason::Archive);
                Start::Fenced
            }
            Launch::Uncertain => {
                self.fence = Some(RestartReason::Launch);
                Start::Fenced
            }
        }
    }

    fn release_unlaunched(&mut self, effects: &mut impl Effects) -> Start {
        let run = self.active.take().expect("delegation acquired");
        effects.release_delegation(run.reservation.operation);
        Start::NotSubmitted
    }

    /// The fixture supplies the already authenticated process/pipe peer.
    pub fn authenticate_owner(&mut self, operation: OperationId, owner: ProcessIdentity) {
        if self.fence.is_some() {
            return;
        }
        match self.active.as_mut() {
            Some(run)
                if run.reservation.operation == operation
                    && owner.valid()
                    && owner.pid != run.reservation.service.pid
                    && run.owner.is_none() =>
            {
                run.owner = Some(owner)
            }
            _ => self.fence = Some(RestartReason::OwnerIdentity),
        }
    }

    /// Consume a live owner observation, not a notification or cached reread.
    /// The native transport requires strictly increasing owner revisions;
    /// repeated reads of the accepted cache do not advance this sequence.
    pub fn observe(&mut self, snapshot: Result<Snapshot, EvidenceError>) {
        if self.fence.is_some() {
            return;
        }
        let next = match snapshot {
            Ok(next) => next,
            Err(error) => {
                self.fence = Some(match error {
                    EvidenceError::Lost => RestartReason::LostEvidence,
                    EvidenceError::Malformed => RestartReason::InvalidEvidence,
                });
                return;
            }
        };
        let Some(run) = self.active.as_mut() else {
            self.fence = Some(RestartReason::InvalidEvidence);
            return;
        };
        if run.owner.is_none()
            || next.operation != run.reservation.operation
            || next.revision <= 0
            || !next.valid_owner_shape()
            || run.latest.as_ref().is_some_and(|previous| {
                next.revision <= previous.revision
                    || previous.terminal_candidate()
                    || previous.stop_confirmed && !next.stop_confirmed
            })
        {
            self.fence = Some(RestartReason::InvalidEvidence);
            return;
        }
        if next.unresolved || next.phase == Phase::RecoveryRequired || next.failure.is_some() {
            self.fence = Some(RestartReason::OwnerUnresolved);
        }
        run.latest = Some(next);
    }

    pub fn poll_handback(&mut self, effects: &mut impl Effects) -> Option<Handback> {
        if self.fence.is_some() {
            return None;
        }
        let Some(run) = &self.active else {
            return self.terminal.as_ref().map(|(outcome, _)| *outcome);
        };
        let owner = run.owner?;
        let live = run.latest.as_ref().filter(|s| s.terminal_candidate())?;
        let failure = match effects.observe_exit(owner) {
            Exit::Pending => return None,
            Exit::Observed {
                owner: exited,
                code: 0,
            } if exited == owner => match effects.read_owner_result(run.reservation.operation) {
                Ok(durable) if &durable == live => {
                    let verified = Snapshot {
                        owner_exited: true,
                        ..durable
                    };
                    if let Some(outcome) = verified.handback() {
                        if effects.archive_completed(run.reservation) {
                            effects.release_delegation(run.reservation.operation);
                            self.active = None;
                            self.terminal = Some((outcome, verified));
                            return Some(outcome);
                        }
                        RestartReason::Archive
                    } else {
                        RestartReason::IncompleteHandback
                    }
                }
                _ => RestartReason::DurableResult,
            },
            _ => RestartReason::Exit,
        };
        self.fence = Some(failure);
        None
    }

    /// Request the one logical Auto signal. The managed pump's timing and
    /// repeated delivery of its latched state are outside this replay.
    pub fn request_auto(&mut self, effects: &mut impl Effects, caller_canceled: bool) -> Auto {
        if caller_canceled {
            return Auto::Canceled;
        }
        if matches!(self.disposition(), Disposition::RestartRequired(_)) {
            return Auto::Fenced;
        }
        if self.active.is_none() {
            return Auto::NoOwner;
        }
        if let Some(outcome) = self.poll_handback(effects) {
            return Auto::Finished(outcome);
        }
        if self.fence.is_some() {
            return Auto::Fenced;
        }
        let run = self.active.as_mut().expect("owned operation");
        if run.auto_latched
            || run
                .latest
                .as_ref()
                .is_some_and(Snapshot::terminal_candidate)
        {
            return Auto::Waiting;
        }
        run.auto_latched = true;
        match effects.signal_auto(run.reservation.operation) {
            AutoSignal::Latched => Auto::Waiting,
            AutoSignal::NotSubmitted(Some(observation)) => {
                self.observe(Ok(observation));
                if let Some(outcome) = self.poll_handback(effects) {
                    return Auto::Finished(outcome);
                }
                self.fence.get_or_insert(RestartReason::AutoSubmission);
                Auto::Fenced
            }
            AutoSignal::NotSubmitted(None) | AutoSignal::Uncertain => {
                self.fence = Some(RestartReason::AutoSubmission);
                Auto::Fenced
            }
        }
    }

    /// Loss of the entry caller after dispatch requests cleanup with a fresh
    /// budget. Cancellation of an Auto waiter itself has no owner-side effect.
    pub fn cancel_entry_wait(&mut self, effects: &mut impl Effects) -> Auto {
        self.request_auto(effects, false)
    }

    /// Use only cached, run-bound continuation. The pump selects a typed
    /// heartbeat or its fixed Automatic control; it does not call entry
    /// admission or make another service-level Auto request.
    pub fn continue_owned(&mut self, evidence: Option<Continuation>) -> ContinuationAction {
        let Some(run) = self.active.as_mut().filter(|_| self.fence.is_none()) else {
            return ContinuationAction::Inactive;
        };
        if run.owner.is_none() {
            return ContinuationAction::AwaitingOwner;
        }
        if run
            .latest
            .as_ref()
            .is_some_and(Snapshot::terminal_candidate)
        {
            return ContinuationAction::Inactive;
        }
        if !self.stop_confirmation_expired
            && !run.auto_latched
            && !run.continuation_lost
            && let Some(evidence) = evidence
            && evidence.operation == run.reservation.operation
            && evidence.can_stop
        {
            return ContinuationAction::Heartbeat(evidence.observed_power);
        }
        // Once the pump selects Auto the independent owner can latch it.
        // Later healthy observations cannot continue this Stop.
        run.continuation_lost = true;
        ContinuationAction::Automatic
    }

    /// The service deadline blocks new control, without changing the owner's
    /// transport or inventing its exit. A later receipt can release the lease
    /// but cannot clear the service recovery disposition.
    pub fn expire_stop_confirmation(&mut self) {
        if self.active.is_some() {
            self.stop_confirmation_expired = true;
        }
    }
}

#[cfg(test)]
mod tests;
