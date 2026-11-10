use std::{
    io,
    num::NonZeroU128,
    sync::{Arc, Mutex, MutexGuard},
    time::Duration,
};

use openblade_domain::recovery::ScopedStatus;
use openblade_windows_host::{fan_stop_entry::*, fan_stop_guardian as guardian};
use serde_json::{Value, json};
use tokio::sync::{mpsc, oneshot, watch};

use crate::fan_runtime;

#[path = "../tests/fixtures/fan_stop_entry.rs"]
#[allow(dead_code)]
mod entry_fixture;

const OPERATION: guardian::OperationId = NonZeroU128::new(1).unwrap();
const SERVICE: guardian::ProcessIdentity = guardian::ProcessIdentity {
    pid: 10,
    start_utc_ticks: 1,
};
const OWNER: guardian::ProcessIdentity = guardian::ProcessIdentity {
    pid: 11,
    start_utc_ticks: 2,
};

#[derive(Clone, Copy, PartialEq, Eq)]
pub(crate) enum Case {
    Complete,
    Incomplete,
    Stale,
    Ambiguous,
    Refused,
    EntryStale,
    EntryPower,
    EntryConsole,
}

impl Case {
    pub(crate) fn parse(value: &str) -> io::Result<Self> {
        match value {
            "complete" => Ok(Self::Complete),
            "incomplete" => Ok(Self::Incomplete),
            "stale" => Ok(Self::Stale),
            "ambiguous" => Ok(Self::Ambiguous),
            "refused" => Ok(Self::Refused),
            "entry-stale" => Ok(Self::EntryStale),
            "entry-power" => Ok(Self::EntryPower),
            "entry-console" => Ok(Self::EntryConsole),
            _ => Err(io::Error::other("Unknown Fan Stop fixture case.")),
        }
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
pub(crate) enum Mode {
    Automatic,
    Stop,
}

type Outcome = (ScopedStatus, &'static str);
const BLOCKED: Outcome = (
    ScopedStatus::Blocked,
    "Fan Stop operation is fenced or already pending.",
);
const APPLIED: ScopedStatus = ScopedStatus::Applied;

pub(crate) enum Response {
    Immediate(Outcome),
    Pending(oneshot::Receiver<Outcome>),
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Step {
    Ready,
    Starting,
    Stopped,
    Returning,
    Readback,
    Terminal,
    Done,
    CanaryReady,
}

pub(crate) struct Scenario {
    state: Mutex<State>,
    publish: watch::Sender<Arc<Value>>,
}

struct State {
    case: Case,
    step: Step,
    stop_confirmed: bool,
    replay: guardian::Replay,
    effects: ScriptedEffects,
    waiter: Option<oneshot::Sender<Outcome>>,
    handback: Option<guardian::Handback>,
    ordinary: fan_runtime::Controller,
    status: Value,
}

impl Scenario {
    pub(crate) fn new(mut status: Value, case: Case) -> Self {
        status["deviceVerified"] = true.into();
        status["capabilities"]["supportsFanStop"] = true.into();
        status["capabilities"]["hasFanStopTrialEvidence"] = true.into();
        status["capabilities"]["performancePresets"] = json!([1]);
        status["capabilities"]["fanModes"] = json!([0, 1]);
        status["capabilities"]["fanSpeedRange"] =
            json!({"minimumRpm": 4000, "maximumRpm": 4000, "stepRpm": 100});
        status["activePowerSource"] = if case == Case::EntryPower { 3 } else { 2 }.into();
        status["potentialControllers"] = json!([]);
        status["confirmedState"]["performance"] = 1.into(); // Balanced readback
        let mut state = State {
            case,
            step: Step::Ready,
            stop_confirmed: false,
            replay: guardian::Replay::new(guardian::StartupDisposition::Clear),
            effects: ScriptedEffects {
                case,
                lease: false,
                reserved: false,
                dispatched: false,
                auto_signals: 0,
                exit: false,
                receipt: None,
                archived: false,
            },
            waiter: None,
            handback: None,
            ordinary: fan_runtime::Controller::standard(),
            status,
        };
        state.project("Synthetic Fan Stop fixture ready.");
        let (publish, _) = watch::channel(Arc::new(state.status.clone()));
        Self {
            state: Mutex::new(state),
            publish,
        }
    }

    fn lock(&self) -> io::Result<MutexGuard<'_, State>> {
        self.state
            .lock()
            .map_err(|_| io::Error::other("Fan Stop fixture state poisoned."))
    }

    pub(crate) fn subscribe(&self) -> watch::Receiver<Arc<Value>> {
        self.publish.subscribe()
    }

    fn publish(&self, state: &mut State, message: &'static str) {
        state.project(message);
        self.publish.send_replace(Arc::new(state.status.clone()));
    }

    pub(crate) fn begin(&self, mode: Mode) -> io::Result<Response> {
        let mut guard = self.lock()?;
        let s = &mut *guard;
        let message = match mode {
            Mode::Stop if s.step == Step::Ready => {
                let settings = guardian::Settings::default();
                let entry = s.entry_verdict();
                match s.replay.start(
                    &mut s.effects,
                    guardian::Reservation {
                        operation: OPERATION,
                        service: SERVICE,
                        settings,
                    },
                    entry,
                    true,
                    false,
                ) {
                    guardian::Start::Dispatched => {
                        s.replay.authenticate_owner(OPERATION, OWNER);
                        s.step = Step::Starting;
                        "Scripted Stop dispatch is awaiting owner confirmation."
                    }
                    guardian::Start::EntryRefused(_) => {
                        return Ok(Response::Immediate((
                            ScopedStatus::Blocked,
                            "Fan Stop entry evidence was refused before dispatch.",
                        )));
                    }
                    _ => return Ok(Response::Immediate(BLOCKED)),
                }
            }
            Mode::Automatic if s.step == Step::Stopped => {
                match s.replay.request_auto(&mut s.effects, false) {
                    guardian::Auto::Waiting => {
                        s.step = Step::Returning;
                        "Scripted Automatic is awaiting independent readback."
                    }
                    guardian::Auto::Fenced => {
                        s.step = Step::Done;
                        self.publish(
                            s,
                            "Automatic submission was uncertain; recovery is required.",
                        );
                        return Ok(Response::Immediate(BLOCKED));
                    }
                    _ => return Ok(Response::Immediate(BLOCKED)),
                }
            }
            Mode::Automatic if s.handback == Some(guardian::Handback::Automatic) => {
                return Ok(Response::Immediate((
                    ScopedStatus::Unchanged,
                    "Scripted Automatic handback is already complete.",
                )));
            }
            _ => return Ok(Response::Immediate(BLOCKED)),
        };
        let (send, receive) = oneshot::channel();
        s.waiter = Some(send);
        self.publish(s, message);
        Ok(Response::Pending(receive))
    }

    pub(crate) fn disconnected(&self, mode: Mode) -> io::Result<()> {
        let mut guard = self.lock()?;
        let s = &mut *guard;
        // An Auto waiter is never the independent owner. Only the still-pending
        // original Stop waiter can initiate a fresh cleanup on disconnect.
        if mode == Mode::Stop && s.step == Step::Starting {
            s.waiter.take();
            if s.replay.cancel_entry_wait(&mut s.effects) == guardian::Auto::Waiting {
                s.step = Step::Returning;
                self.publish(
                    s,
                    "Stop caller disconnected; scripted cleanup is still owned.",
                );
            } else {
                s.step = Step::Done;
                self.publish(s, "Cleanup signal was uncertain; recovery is required.");
            }
        }
        Ok(())
    }

    pub(crate) fn ordinary_fan(
        &self,
        value: Option<fan_runtime::Override>,
        settings: Option<fan_runtime::Settings>,
    ) -> io::Result<Outcome> {
        let mut guard = self.lock()?;
        let s = &mut *guard;
        // This single canary is only admitted after observed handback. Neither a
        // matching readback nor loss of the waiter gives the service the lease.
        if s.handback.is_none()
            || s.replay.disposition() != guardian::Disposition::Available
            || s.replay.holds_delegation()
            || s.step != Step::CanaryReady
        {
            return Ok(BLOCKED);
        }
        let result = s.ordinary.request(value, settings)?;
        if result.status == ScopedStatus::Applied {
            self.publish(s, result.message);
        }
        Ok((result.status, result.message))
    }

    pub(crate) async fn drive(&self, mut advance: mpsc::Receiver<()>) -> io::Result<()> {
        while advance.recv().await.is_some() {
            self.advance()?;
        }
        Ok(())
    }

    fn advance(&self) -> io::Result<()> {
        use guardian::{Handback, ObservedState, Phase};
        let mut guard = self.lock()?;
        let s = &mut *guard;
        let mut finish = None;
        let message = match s.step {
            Step::Starting if s.case == Case::Refused => {
                let mut observation = s.snapshot(1, Phase::Refused, ObservedState::Ordinary);
                observation.known_not_submitted = true;
                observation.native_released = true;
                s.terminal_candidate(observation);
                "Owner refused before submission; awaiting independent exit."
            }
            Step::Starting => {
                s.stop_confirmed = true;
                s.replay
                    .observe(Ok(s.snapshot(1, Phase::Stopped, ObservedState::Stopped)));
                if s.replay.disposition() == guardian::Disposition::Delegated
                    && s.stop_is_confirmed()
                {
                    s.step = Step::Stopped;
                    finish = Some(APPLIED);
                    "Scripted Stop readback confirmed; ordinary fan writes remain fenced."
                } else {
                    s.step = Step::Done;
                    finish = Some(ScopedStatus::Blocked);
                    "Stop proof was not accepted; ordinary fan writes remain fenced."
                }
            }
            Step::Stopped if s.case == Case::Stale => {
                s.replay
                    .observe(Ok(s.snapshot(1, Phase::Stopped, ObservedState::Stopped)));
                s.step = Step::Done;
                "Stale owner revision; recovery is required."
            }
            Step::Returning => {
                let mut observation =
                    s.snapshot(2, Phase::ReturningToAutomatic, ObservedState::Automatic);
                observation.auto_confirmed = true;
                s.replay.observe(Ok(observation));
                s.step = Step::Readback;
                "Automatic readback observed; cleanup is still pending."
            }
            Step::Readback => {
                let mut observation = s.snapshot(3, Phase::Completed, ObservedState::Automatic);
                observation.auto_confirmed = true;
                observation.cleanup_confirmed = s.case != Case::Incomplete;
                observation.journal_complete = true;
                observation.native_released = true;
                s.terminal_candidate(observation);
                "Owner receipt observed; awaiting independent exit."
            }
            Step::Terminal => {
                s.effects.exit = true;
                s.handback = s.replay.poll_handback(&mut s.effects);
                s.step = Step::Done;
                match s.handback {
                    Some(Handback::Automatic) => {
                        finish = Some(APPLIED);
                        "Scripted Automatic handback verified."
                    }
                    Some(Handback::NeverSubmitted) => {
                        finish = Some(ScopedStatus::Blocked);
                        "Scripted refusal verified; Stop was not submitted."
                    }
                    None => {
                        finish = Some(ScopedStatus::Blocked);
                        "Incomplete terminal evidence; recovery is required."
                    }
                }
            }
            Step::Done if s.handback.is_some() => {
                s.step = Step::CanaryReady;
                // The existing ordinary fan transport requires its own verified
                // full-adapter context. Stop never uses this power observation.
                s.status["activePowerSource"] = 0.into();
                s.status["confirmedState"]["performance"] = 2.into();
                s.status["capabilities"]["performancePresets"] = json!([1, 2]);
                "Scripted full-adapter context admitted for the ordinary fan canary."
            }
            _ => return Ok(()),
        };
        self.publish(s, message);
        if let Some(result) = finish
            && let Some(waiter) = s.waiter.take()
        {
            let _ = waiter.send((result, message));
        }
        Ok(())
    }
}

impl State {
    fn entry_verdict(&self) -> Result<(), Refusal> {
        let mut entry = entry_fixture::admitted();
        match self.case {
            Case::EntryStale => entry.apply.now += Duration::from_secs(3),
            Case::EntryPower => entry.service.power = Power::Unknown,
            Case::EntryConsole => entry.apply.console.console += 1,
            _ => {}
        }
        replay(&entry)
    }

    fn stop_is_confirmed(&self) -> bool {
        self.replay.accepted().is_some_and(|observation| {
            observation.phase == guardian::Phase::Stopped
                && observation.confirmed_state == guardian::ObservedState::Stopped
                && observation.stop_confirmed
                && !observation.command_pending
        })
    }
    fn snapshot(
        &self,
        revision: i64,
        phase: guardian::Phase,
        state: guardian::ObservedState,
    ) -> guardian::Snapshot {
        guardian::Snapshot {
            operation: OPERATION,
            revision,
            phase,
            confirmed_state: state,
            stop_confirmed: self.stop_confirmed,
            auto_confirmed: false,
            cleanup_confirmed: false,
            journal_complete: false,
            native_released: false,
            owner_exited: false,
            command_pending: false,
            unresolved: false,
            reason: None,
            failure: None,
            observed_power: Some(Power::UsbC),
            end_reason: if revision > 1 {
                guardian::EndReason::CallerRequest
            } else {
                guardian::EndReason::None
            },
            known_not_submitted: false,
        }
    }

    fn terminal_candidate(&mut self, live: guardian::Snapshot) {
        // Scripted durable receipt is read through its own effect after exit.
        self.effects.receipt = Some(live.clone());
        self.replay.observe(Ok(live));
        self.step = Step::Terminal;
        assert!(self.replay.poll_handback(&mut self.effects).is_none());
    }

    fn project(&mut self, message: &'static str) {
        let recovering = matches!(
            self.replay.disposition(),
            guardian::Disposition::RestartRequired(_)
        );
        let owned = self.replay.holds_delegation();
        let returning = matches!(self.step, Step::Returning | Step::Readback)
            || self.step == Step::Terminal && self.case != Case::Refused;
        // The numeric values are the existing managed FanStopMode/State enums.
        let state = if recovering {
            4
        } else if returning {
            6
        } else {
            match (self.step, self.handback) {
                (Step::Starting | Step::Terminal, _) => 5,
                (Step::Stopped, _) if self.stop_is_confirmed() => 2,
                (_, Some(guardian::Handback::Automatic)) => 3,
                _ => 1,
            }
        };
        let stop_intent = owned && !returning && self.effects.auto_signals == 0;
        self.status["fanStopStatus"]["requestedMode"] = i32::from(stop_intent).into();
        self.status["fanStopStatus"]["state"] = state.into();
        self.status["fanStopStatus"]["unavailableReason"] = 0.into();
        self.status["fanStopStatus"]["canStop"] =
            (self.step == Step::Ready && self.entry_verdict().is_ok()).into();
        // This script admits one waiter; while it is pending there is no
        // second request to issue. The owner retains its cleanup obligation.
        self.status["fanStopStatus"]["canReturnToAutomatic"] =
            (self.step == Step::Stopped && owned && !recovering).into();
        self.status["fanStopStatus"]["message"] = message.into();
        self.status["writesBlocked"] = (owned || recovering).into();
        self.status["unconfirmedWrites"] = json!([]);
        self.status["externalChanges"] = json!([]);
        self.ordinary.project(&mut self.status);
        if owned || recovering {
            self.status["confirmedState"]["fans"] = Value::Null;
        }
        self.status["revision"] = (self.status["revision"].as_i64().unwrap() + 1).into();
        self.status["message"] = message.into();
    }
}

struct ScriptedEffects {
    case: Case,
    lease: bool,
    reserved: bool,
    dispatched: bool,
    auto_signals: u8,
    exit: bool,
    receipt: Option<guardian::Snapshot>,
    archived: bool,
}

impl guardian::Effects for ScriptedEffects {
    fn acquire_delegation(&mut self, operation: guardian::OperationId) -> bool {
        assert_eq!(operation, OPERATION);
        assert!(!self.lease && !self.reserved);
        self.lease = true;
        true
    }

    fn release_delegation(&mut self, operation: guardian::OperationId) {
        assert_eq!(operation, OPERATION);
        assert!(self.lease && self.archived);
        self.lease = false;
    }

    fn reserve(&mut self, reservation: guardian::Reservation) -> guardian::Reserve {
        assert_eq!(
            (reservation.operation, reservation.service),
            (OPERATION, SERVICE)
        );
        assert!(self.lease && !self.reserved);
        self.reserved = true;
        guardian::Reserve::Durable
    }

    fn dispatch(&mut self, operation: guardian::OperationId) -> guardian::Launch {
        assert_eq!(operation, OPERATION);
        assert!(self.lease && self.reserved && !self.dispatched);
        self.dispatched = true;
        guardian::Launch::Dispatched
    }

    fn archive_unlaunched(&mut self, _: guardian::Reservation) -> bool {
        unreachable!("the fixed dispatch fixture always reaches the owner")
    }

    fn signal_auto(&mut self, operation: guardian::OperationId) -> guardian::AutoSignal {
        assert_eq!(operation, OPERATION);
        assert!(self.lease && self.dispatched && self.auto_signals == 0);
        self.auto_signals += 1;
        if self.case == Case::Ambiguous {
            guardian::AutoSignal::Uncertain
        } else {
            guardian::AutoSignal::Latched
        }
    }

    fn observe_exit(&mut self, owner: guardian::ProcessIdentity) -> guardian::Exit {
        assert_eq!(owner, OWNER);
        if self.exit {
            guardian::Exit::Observed { owner, code: 0 }
        } else {
            guardian::Exit::Pending
        }
    }

    fn read_owner_result(
        &mut self,
        operation: guardian::OperationId,
    ) -> Result<guardian::Snapshot, guardian::EvidenceError> {
        assert_eq!(operation, OPERATION);
        assert!(self.exit && self.lease);
        self.receipt.clone().ok_or(guardian::EvidenceError::Lost)
    }

    fn archive_completed(&mut self, reservation: guardian::Reservation) -> bool {
        assert_eq!(
            (reservation.operation, reservation.service),
            (OPERATION, SERVICE)
        );
        assert!(self.exit && self.receipt.is_some() && !self.archived);
        self.archived = true;
        true
    }
}
