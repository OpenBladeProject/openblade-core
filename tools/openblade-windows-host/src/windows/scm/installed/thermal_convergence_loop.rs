//! Managed's thermal convergence loop over native's reconciliation steps
//! (`service-thermal-convergence-v1`): `EvaluateThermalConvergenceObservationAsync`
//! nominates a contradiction between confirmed thermal state and saved
//! intent, and `ExecuteThermalConvergenceAsync` reserves one claim in the
//! user's shared ledger (`thermal_convergence_ledger`), applies only the
//! contradicting subsystems through the same typed steps a lifecycle
//! reconciliation runs (`Runtime::reconcile_locked`), and retires the
//! reservation with a tombstone.
//!
//! An evaluation runs after the lifecycle tick, under the mutation gate,
//! once per new reading (`thermal_drift::Tracker::readings`), when the D7
//! retry is due and every `OBSERVE_INTERVAL`. It needs what managed's
//! observer needs: an authoritative ledger, an unlocked console session
//! with a settled pair and no lifecycle cause owed, no low power, settled
//! power, no possible competing controller, no unsettled journal row, an
//! admitted Maintain policy whose upgrade disclosure is not pending, and a
//! HyperBoost journal in a safe terminal state.
//!
//! The reservation is persisted before any setter, and every setter's
//! journaled intent is admitted afresh by its `Guard`: the ledger still
//! holds exactly that reservation, the HyperBoost journal is unchanged and
//! safe, the policy is still Maintain and the Service is not stopping.
//! A reservation this runtime did not make, or one left by a stop, is
//! reconciled read-only from the next confirmed reading.
//!
//! Declared differences from managed:
//! - D7: a reservation whose steps were all refused before any submission
//!   (a KnownNotApplied refusal) is kept and retried once, at least
//!   `RETRY_DELAY` later, under fresh admission and reclassification with
//!   the claim it already spent. If anything changed, or the retry is
//!   refused again, the claim is tombstoned Blocked. Managed attempts once.
//! - D9: a contradiction native has no recipe for is refused before
//!   reserving, with no claim spent: no saved destination, an incomplete
//!   GPU clock policy or an owned fan change still to hand back for
//!   performance, and no saved fan intent. A return to Automatic carries
//!   the saved preset in its 0D/02 pair, as managed's fan setter carries a
//!   performance selector, so a fans-only attempt may write that preset.
//! - A submission that may have landed but did not settle by readback is
//!   ReconciliationUnavailable while its journal row still fences the
//!   store, and External once that row settled without a match. Native's
//!   steps cannot tell those apart any further.
//! - A reservation whose persistence fails closes the ledger, so the status
//!   reports `ConfigurationQuarantined` where managed reports
//!   `InternalStateUnavailable`.
//! - An absent HyperBoost journal is safe, as managed treats it; a journal
//!   that cannot be read, even one that is absent but whose location cannot
//!   be verified, blocks.
use super::super::convergence::{self as status, Kind, Nomination, Nominator};
use super::*;
use crate::installed::readiness::thermal_storage::SavedPolicy;
use crate::service_status::thermal_convergence::{DISCLOSURE_PENDING, MAINTAIN, Reason};
use openblade_domain::{
    profile_set::FanMode,
    thermal_convergence as ledger,
    thermal_mutation::{CUSTOM, Destination, Preset},
};
use serde_json::Value;
use sha2::{Digest, Sha256};
use std::{
    cell::{Cell, RefCell},
    sync::atomic::{AtomicBool, AtomicU64, Ordering},
    time::Duration,
};

/// Reads managed's HyperBoost ownership journal: its bytes, `None` when the
/// file is absent.
pub(in super::super) type Journal = Arc<dyn Fn() -> io::Result<Option<Vec<u8>>> + Send + Sync>;

/// D5: how often the loop reads the device itself while Maintain is admitted.
pub(in super::super) const OBSERVE_INTERVAL: Duration = Duration::from_secs(15);
/// D5: the deadline of that read.
pub(in super::super::super) const OBSERVE_BUDGET: Duration = Duration::from_secs(5);
/// D7: the earliest retry after a refusal before any submission.
const RETRY_DELAY: Duration = Duration::from_secs(5);
/// Managed `HyperBoostOwnershipJournalState.Relinquished`.
const RELINQUISHED: i64 = 3;

/// The loop's state on one runtime.
pub(in super::super) struct Loop {
    journal: Journal,
    /// Set at Stop: no evaluation starts and no write of an attempt is
    /// admitted after it (`Guard`).
    stop: Arc<AtomicBool>,
    /// The last reading an evaluation started from; 0 before any.
    evaluated: Cell<u64>,
    /// When the last periodic evaluation started (D5).
    observed: Cell<Option<Instant>>,
    /// D7: this runtime's reservation whose steps were refused before any
    /// submission, and when it may be retried.
    retry: RefCell<Option<(ledger::Guid, Instant)>>,
}

impl Loop {
    pub(in super::super) fn new(journal: Journal) -> Self {
        Self {
            journal,
            stop: Arc::default(),
            evaluated: Cell::new(0),
            observed: Cell::new(None),
            retry: RefCell::new(None),
        }
    }
    /// The journal's bytes when it admits convergence now.
    fn safe_journal(&self) -> Option<Option<Vec<u8>>> {
        (self.journal)()
            .ok()
            .filter(|bytes| journal_safe(bytes.as_deref()))
    }
}

/// Managed `IsSafeMaintainHyperBoostJournalBaseline` over the journal's
/// bytes: absent, or a document of a schema managed recognizes, with the
/// members that schema requires, whose state is Relinquished. Members are
/// read as managed writes them (camelCase, numeric enums).
fn journal_safe(bytes: Option<&[u8]>) -> bool {
    let Some(bytes) = bytes else {
        return true;
    };
    let Ok(Value::Object(document)) = serde_json::from_slice::<Value>(bytes) else {
        return false;
    };
    let int = |name: &str| document.get(name).and_then(Value::as_i64);
    let present = |name: &str| document.get(name).is_some_and(|value| !value.is_null());
    let recognized = match int("schemaVersion") {
        Some(1) => !present("requiresCoolingPad"),
        Some(2) => present("requiresCoolingPad"),
        Some(3..=8) => {
            present("requiresCoolingPad")
                && present("restorePerformance")
                && present("restoreFanControl")
        }
        _ => false,
    };
    recognized
        && int("state") == Some(RELINQUISHED)
        && int("baseMode").is_some()
        && document.get("updatedAtUtc").is_some_and(Value::is_string)
}

/// The saved policy admits convergence: Maintain, with no pending upgrade
/// disclosure.
fn maintains(storage: &Storage) -> bool {
    matches!(
        storage.thermal_policy(),
        Ok(SavedPolicy::Admitted { policy: MAINTAIN, disclosure }) if disclosure != DISCLOSURE_PENDING
    )
}

/// A new transition attempt ID (managed `Guid.NewGuid`), a version 4 GUID
/// from a digest of inputs no two attempts share.
fn new_guid(sid: &str) -> ledger::Guid {
    static NEXT: AtomicU64 = AtomicU64::new(0);
    let at = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |elapsed| elapsed.as_nanos());
    let digest = Sha256::digest(format!(
        "OpenBlade.ThermalConvergenceAttempt\n{sid}\n{}\n{at}\n{}",
        std::process::id(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    ));
    let mut bytes = [0; 16];
    bytes.copy_from_slice(&digest[..16]);
    bytes[6] = (bytes[6] & 0x0f) | 0x40;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;
    ledger::Guid(bytes)
}

/// Managed `ThermalConvergenceContradiction` for the classifier's flags.
fn contradiction(performance: bool, fans: bool) -> ledger::Contradiction {
    let mut components = Vec::with_capacity(2);
    if performance {
        components.push(ledger::Component::PERFORMANCE);
    }
    if fans {
        components.push(ledger::Component::FANS);
    }
    ledger::Contradiction::of(&components)
}

/// The journal id of a convergence attempt's steps.
fn convergence_request() -> String {
    static NEXT: AtomicU64 = AtomicU64::new(0);
    let at = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |elapsed| elapsed.as_nanos());
    format!(
        "OpenBlade.ThermalConvergence\n{}\n{at}\n{}",
        std::process::id(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    )
}

/// The effective intent the ledger keeps for class `class`: the status's
/// desired state with the saved thermal intent there, as a status
/// publishes it. `None` without a saved destination or fan intent there.
fn effective_intent(
    base: &Snapshot,
    storage: &Storage,
    class: i32,
    profile: Option<&Resolution<'_>>,
) -> io::Result<Option<ledger::Intent>> {
    let Some((destination, _)) = storage.desired(class, profile)? else {
        return Ok(None);
    };
    let Some(settings) = storage.desired_fan(class, profile)? else {
        return Ok(None);
    };
    let projected = base
        .clone()
        .thermal_intent(
            storage.current_override(class)?.as_ref(),
            Some(destination),
            "",
            None,
        )
        .fan_intent(
            storage.current_fan_override(class)?.as_ref(),
            Some(settings),
            class,
            "",
        );
    let mut members = projected.effective_intent_members();
    let parse = |members: serde_json::Map<String, Value>| {
        serde_json::from_value::<ledger::Intent>(Value::Object(members)).ok()
    };
    // A saved fan control managed would not bind is the legacy one for its
    // mode (`Intent::normalized`).
    Ok(parse(members.clone()).or_else(|| {
        members.remove("fanControl");
        parse(members)
    }))
}

/// The native classifier's intent for a reserved target.
fn target_intent(target: &ledger::Intent) -> status::Intent {
    let performance = if target.performance.value == CUSTOM {
        target.custom_performance.and_then(|custom| {
            Destination::custom(
                u64::try_from(custom.cpu).ok()?,
                u64::try_from(custom.gpu).ok()?,
                custom.maximum_fans,
            )
        })
    } else {
        Preset::from_wire(target.performance.value).map(Destination::Preset)
    };
    status::Intent {
        performance,
        fans: FanMode(target.fans.value),
    }
}

/// What managed's observer decides for a contradiction past every
/// admission check (`Runtime::assess`).
enum Assessment {
    Converge(Box<Plan>),
    Suppressed(Reason),
    /// D9: refused before reserving, with no claim spent.
    Refused,
}

/// A nominated contradiction: the ledger refreshed to current intent, the
/// candidate and the components it declares.
struct Plan {
    refreshed: ledger::State,
    candidate: ledger::Candidate,
    declared: Vec<ledger::Component>,
}

/// Everything one evaluation reads for the user at class `class`.
struct Evaluation<'a> {
    base: &'a Snapshot,
    storage: &'a Storage,
    sid: &'a str,
    session: u32,
    class: i32,
    profile: Option<&'a Resolution<'a>>,
}

impl Runtime {
    /// The saved intent a contradiction converges to, with its revisions,
    /// or `None` for D9's refusals: no saved destination, an incomplete GPU
    /// clock policy or an owned fan change still to hand back for
    /// performance, and no saved fan intent.
    fn convergence_intent(
        &self,
        evaluation: &Evaluation<'_>,
        observation: &status::Observation,
        performance: bool,
    ) -> io::Result<Option<(ledger::Intent, ledger::Revisions, i32, i32)>> {
        let Evaluation {
            base,
            storage,
            class,
            profile,
            ..
        } = *evaluation;
        if performance
            && (observation.desired.performance.is_none()
                || storage.gpu_incomplete(class, profile)?
                || storage.outstanding_fan())
        {
            return Ok(None);
        }
        let Some(intent) = effective_intent(base, storage, class, profile)? else {
            return Ok(None);
        };
        let SavedPolicy::Admitted { policy, disclosure } = storage.thermal_policy()? else {
            return Ok(None);
        };
        let revisions = ledger::Revisions::of(&intent.clone().normalized(), policy, disclosure);
        Ok(Some((intent, revisions, policy, disclosure)))
    }

    /// Managed's observer from its classification on: D9's refusals, the
    /// ledger refreshed to current intent, its tombstone and its budget.
    fn assess(
        &self,
        evaluation: &Evaluation<'_>,
        held: &ledger::State,
        observation: &status::Observation,
        (performance, fans): (bool, bool),
    ) -> io::Result<Assessment> {
        let Some((intent, revisions, policy, disclosure)) =
            self.convergence_intent(evaluation, observation, performance)?
        else {
            return Ok(Assessment::Refused);
        };
        let Evaluation { base, class, .. } = *evaluation;
        let model = base
            .verified_model_number()
            .filter(|model| !model.trim().is_empty())
            .unwrap_or("verified-device");
        let destination = ledger::Power(class);
        let refreshed = held.refresh_intent(
            ledger::Refresh {
                desired: intent,
                policy,
                disclosure,
                destination: Some(destination),
                fingerprint: &format!("OpenBlade:{model}"),
                // Native does not track a reconnect of the device; a
                // changed model is still a new device authority.
                device_invalidated: false,
                policy_changed: held
                    .policy_revision
                    .as_ref()
                    .is_some_and(|revision| *revision != revisions.policy),
            },
            || new_guid(evaluation.sid),
        );
        let contradiction = contradiction(performance, fans);
        let candidate = ledger::Candidate {
            destination,
            contradiction,
            performance_revision: revisions.performance,
            fan_revision: revisions.fans,
        };
        Ok(if refreshed.tombstoned(&candidate) {
            Assessment::Suppressed(Reason::Tombstoned)
        } else if refreshed.budget_exhausted(&candidate) {
            Assessment::Suppressed(Reason::BudgetExhausted)
        } else {
            Assessment::Converge(Box::new(Plan {
                refreshed,
                declared: contradiction.components(),
                candidate,
            }))
        })
    }

    /// The status's view of the loop for the user `sid` at class `query`,
    /// `None` when no loop runs (`Runtime::convergence_loop`).
    #[allow(clippy::too_many_arguments)]
    pub(in super::super) fn nominate<'a>(
        &'a self,
        base: &'a Snapshot,
        storage: &'a Storage,
        sid: &'a str,
        session: u32,
        query: i32,
        profile: Option<&'a Resolution<'a>>,
        held: Option<ledger::State>,
    ) -> Option<Nominate<'a>> {
        Some(Nominate {
            runtime: self,
            loop_: self.convergence_loop.get()?,
            evaluation: Evaluation {
                base,
                storage,
                sid,
                session,
                class: query,
                profile,
            },
            held,
        })
    }
}

/// The loop as one status publication sees it (`Runtime::nominate`).
pub(in super::super) struct Nominate<'a> {
    runtime: &'a Runtime,
    loop_: &'a Loop,
    evaluation: Evaluation<'a>,
    held: Option<ledger::State>,
}

impl Nominator for Nominate<'_> {
    fn journal_blocks(&self) -> bool {
        self.loop_.safe_journal().is_none()
    }
    fn nominate(&self, performance: bool, fans: bool) -> io::Result<Nomination> {
        let Some(held) = &self.held else {
            return Ok(Nomination::Unavailable);
        };
        let e = &self.evaluation;
        let observation = self
            .runtime
            .convergence_observation(e.base, e.storage, e.sid, e.session, e.class, e.profile)?;
        Ok(
            match self
                .runtime
                .assess(e, held, &observation, (performance, fans))?
            {
                Assessment::Converge(_) => Nomination::Converging,
                Assessment::Suppressed(reason) => Nomination::Suppressed(reason),
                Assessment::Refused => Nomination::Unavailable,
            },
        )
    }
}

/// One attempt's write admission, shared by its steps' permits
/// (`thermal_drift::Scope`).
struct Guard {
    storage: Arc<Storage>,
    /// The reservation this attempt persisted.
    reservation: ledger::State,
    journal: Journal,
    /// The journal's bytes when the attempt reserved.
    baseline: Option<Vec<u8>>,
    stop: Arc<AtomicBool>,
    performance: bool,
    fans: bool,
    /// A setter was refused: no later write of this attempt is admitted.
    refused: AtomicBool,
}

impl Guard {
    /// Managed `RevalidateThermalConvergenceReservationAsync` and the
    /// HyperBoost journal recheck before a submission.
    fn holds(&self) -> bool {
        self.storage.convergence_ledger_holds()
            && self
                .storage
                .convergence_ledger()
                .is_some_and(|state| state.is_expected_reservation(&self.reservation))
            && (self.journal)()
                .is_ok_and(|bytes| bytes == self.baseline && journal_safe(bytes.as_deref()))
            && maintains(&self.storage)
    }
    fn refused(&self) -> bool {
        self.refused.load(Ordering::Acquire)
    }
}

impl thermal_drift::Scope for Guard {
    fn declares(&self, fans: bool) -> bool {
        if fans { self.fans } else { self.performance }
    }
    fn open(&self) -> bool {
        !self.stop.load(Ordering::Acquire) && !self.refused()
    }
    fn admits(&self, fans: bool) -> bool {
        let admitted = self.open() && self.declares(fans) && self.holds();
        if !admitted {
            self.refused.store(true, Ordering::Release);
        }
        admitted
    }
}

/// What one evaluation leaves for its caller: a reconciliation's status to
/// publish, and whether to read the device (D5).
#[derive(Default)]
pub(in super::super::super) struct Converged {
    pub(in super::super::super) snapshot: Option<Snapshot>,
    pub(in super::super::super) observe: bool,
}

impl Runtime {
    /// Stops the loop: no evaluation starts and no write of an attempt in
    /// progress is admitted after this. A reservation it leaves is
    /// reconciled read-only at the next start.
    pub(in super::super::super) fn stop_convergence(&self) {
        if let Some(loop_) = self.convergence_loop.get() {
            loop_.stop.store(true, Ordering::Release);
        }
    }

    /// Runs the loop with `journal` as managed's HyperBoost journal.
    #[cfg(test)]
    pub(in super::super::super) fn run_convergence(&self, journal: Journal) {
        assert!(self.convergence_loop.set(Loop::new(journal)).is_ok());
    }
    /// The loop's stop flag, which a test sets from a device hook as Stop
    /// sets it.
    #[cfg(test)]
    pub(in super::super::super) fn convergence_stop(&self) -> Arc<AtomicBool> {
        self.convergence_loop
            .get()
            .expect("the loop runs")
            .stop
            .clone()
    }
    /// Moves the D7 retry and the last D5 evaluation `by` into the past.
    #[cfg(test)]
    pub(in super::super::super) fn age_convergence(&self, by: Duration) {
        let loop_ = self.convergence_loop.get().expect("the loop runs");
        if let Some((_, due)) = loop_.retry.borrow_mut().as_mut() {
            *due -= by;
        }
        loop_.observed.set(loop_.observed.get().map(|at| at - by));
    }

    /// One evaluation after the lifecycle tick, from the latest published
    /// status `status`. Only fatal admission evidence fails it.
    pub(in super::super::super) async fn converge(
        &self,
        mailbox: &MailboxSource,
        status: impl FnOnce() -> Snapshot,
    ) -> io::Result<Converged> {
        let Some(loop_) = self.convergence_loop.get() else {
            return Ok(Converged::default());
        };
        if loop_.stop.load(Ordering::Acquire) {
            return Ok(Converged::default());
        }
        let Ok(_serial) = self.gate.try_lock() else {
            return Ok(Converged::default());
        };
        verify_retained_failure(&self.fatal)?;
        // The lifecycle's own reconciliation runs first, and a user still
        // starting is applied by it.
        if self.gate() == Gate::Low || self.lifecycle_pending() {
            return Ok(Converged::default());
        }
        let Some(pair) = self.lifecycle_pair() else {
            return Ok(Converged::default());
        };
        let key = (pair.sid.clone(), pair.session);
        if self.starting(&pair.sid) || self.debt(&key) != Debt::Settled {
            return Ok(Converged::default());
        }
        let now = Instant::now();
        let reading = self
            .drift
            .lock()
            .map_err(|_| io::ErrorKind::Other)?
            .readings();
        let retry_due = loop_.retry.borrow().is_some_and(|(_, due)| now >= due);
        let observe_due = loop_
            .observed
            .get()
            .is_none_or(|at| now.saturating_duration_since(at) >= OBSERVE_INTERVAL);
        // Managed evaluates an observation: only a reading newer than the
        // last evaluated one is evidence. A due D5 read or D7 retry asks
        // for one, which the next tick evaluates.
        let new_reading = loop_.evaluated.get() != reading;
        if !new_reading && !retry_due && !observe_due {
            return Ok(Converged::default());
        }
        let MailboxSource::Reader(reader) = mailbox else {
            return Ok(Converged::default());
        };
        loop_.evaluated.set(reading);
        if observe_due {
            loop_.observed.set(Some(now));
        }
        let (storage, fresh) = match self.capture(&pair.sid, pair.session, reader.custody()) {
            Ok(captured) => captured,
            Err(error) if is_fatal_admission(&error) => return Err(error),
            Err(_) => return Ok(Converged::default()),
        };
        // D5: the loop's own read, and the one a due D7 retry waits for,
        // only while Maintain is admitted; it asks for no optional reading.
        let observe = (observe_due || retry_due && !new_reading) && maintains(&storage);
        if !new_reading {
            return Ok(Converged {
                snapshot: None,
                observe,
            });
        }
        let snapshot = match self
            .evaluate(mailbox, loop_, &pair, &storage, fresh.as_ref(), status())
            .await
        {
            Ok(snapshot) => snapshot,
            Err(error) if is_fatal_admission(&error) => return Err(error),
            // A store read that failed is the store's own state, which the
            // next evaluation reads again.
            Err(_) => None,
        };
        Ok(Converged { snapshot, observe })
    }

    async fn evaluate(
        &self,
        mailbox: &MailboxSource,
        loop_: &Loop,
        pair: &lifecycle::Pair,
        storage: &Arc<Storage>,
        fresh: &dyn Fresh,
        base: Snapshot,
    ) -> io::Result<Option<Snapshot>> {
        let Some(held) = storage.convergence_ledger() else {
            return Ok(None);
        };
        let query = status_query_power(base.thermal_power_source(), fresh);
        let class = convergence_power(query, fresh, storage)?;
        if !(0..=2).contains(&class) {
            return Ok(None);
        }
        let layer = storage.profile_layer(&self.foreground(&pair.sid, pair.session))?;
        let profile = layer.at(class)?;
        let evaluation = Evaluation {
            base: &base,
            storage,
            sid: &pair.sid,
            session: pair.session,
            class,
            profile: profile.as_ref(),
        };
        if let Some(reservation) = &held.in_flight {
            let ours = *loop_.retry.borrow();
            return match ours {
                Some((attempt, due)) if attempt == reservation.transition_attempt_id => {
                    if Instant::now() < due {
                        return Ok(None);
                    }
                    loop_.retry.take();
                    self.retry(mailbox, loop_, pair, &evaluation, storage, fresh, held)
                        .await
                }
                _ => {
                    self.reconcile_reserved(&evaluation, &held)?;
                    Ok(None)
                }
            };
        }
        if !maintains(storage) || storage.pending() || base.names_potential_controllers() {
            return Ok(None);
        }
        let observation = self.convergence_observation(
            &base,
            storage,
            &pair.sid,
            pair.session,
            class,
            profile.as_ref(),
        )?;
        let Kind::Candidate { performance, fans } = status::classify(&observation) else {
            return Ok(None);
        };
        let Assessment::Converge(plan) =
            self.assess(&evaluation, &held, &observation, (performance, fans))?
        else {
            return Ok(None);
        };
        let Some(journal) = loop_.safe_journal() else {
            return Ok(None);
        };
        // Managed's executor: a fresh admission and settled power before
        // the reservation, which only then spends a claim.
        if self.gate() == Gate::Low || fresh.authorize().is_none() {
            return Ok(None);
        }
        let confirmed = base.confirmed_thermal();
        let custom = confirmed
            .custom
            .map(|(cpu, gpu)| ledger::CustomPerformance {
                cpu: i32::from(cpu),
                gpu: i32::from(gpu),
                maximum_fans: false,
                gpu_clock_offsets: None,
            });
        let Some(desired) = plan.refreshed.effective_intent.clone() else {
            return Ok(None);
        };
        let reserved = plan.refreshed.reserve(
            &plan.candidate,
            new_guid(&pair.sid),
            ledger::Observation::thermal(
                confirmed.performance,
                confirmed.fans,
                custom,
                confirmed.hyper_boost,
            ),
            desired,
            &plan.declared,
        );
        if storage
            .replace_convergence_ledger(&held, &reserved)
            .is_err()
        {
            return Ok(None);
        }
        self.run_attempt(
            mailbox, loop_, pair, storage, reserved, journal, base, class, false,
        )
        .await
    }

    /// D7: the retry of this runtime's reservation, from fresh admission
    /// and reclassification. The same contradiction under the same intent,
    /// policy and destination runs again with the claim already spent;
    /// anything else retires the claim.
    #[allow(clippy::too_many_arguments)]
    async fn retry(
        &self,
        mailbox: &MailboxSource,
        loop_: &Loop,
        pair: &lifecycle::Pair,
        evaluation: &Evaluation<'_>,
        storage: &Arc<Storage>,
        fresh: &dyn Fresh,
        held: ledger::State,
    ) -> io::Result<Option<Snapshot>> {
        let Evaluation {
            base,
            class,
            profile,
            ..
        } = *evaluation;
        let observation =
            self.convergence_observation(base, storage, &pair.sid, pair.session, class, profile)?;
        let kind = status::classify(&observation);
        // The journal's bytes when the same contradiction still holds under
        // the same intent, policy, destination and admission.
        let same = || -> io::Result<Option<Option<Vec<u8>>>> {
            let Kind::Candidate { performance, fans } = kind else {
                return Ok(None);
            };
            if contradiction(performance, fans) != held.contradiction
                || held.settled_destination != Some(ledger::Power(class))
                || !maintains(storage)
                || storage.pending()
                || base.names_potential_controllers()
            {
                return Ok(None);
            }
            let Some((_, revisions, ..)) =
                self.convergence_intent(evaluation, &observation, performance)?
            else {
                return Ok(None);
            };
            if held.performance_desired_revision.as_ref() != Some(&revisions.performance)
                || held.fan_desired_revision.as_ref() != Some(&revisions.fans)
                || held.policy_revision.as_ref() != Some(&revisions.policy)
                || self.gate() == Gate::Low
                || fresh.authorize().is_none()
            {
                return Ok(None);
            }
            Ok(loop_.safe_journal())
        };
        let Some(journal) = same()? else {
            let disposition = if kind == Kind::Matched {
                ledger::Disposition::CONFIRMED
            } else {
                ledger::Disposition::BLOCKED
            };
            complete(storage, &held, disposition);
            return Ok(None);
        };
        self.run_attempt(
            mailbox,
            loop_,
            pair,
            storage,
            held,
            journal,
            base.clone(),
            class,
            true,
        )
        .await
    }

    /// Runs one reserved attempt and retires its reservation, unless the
    /// loop stopped or D7 keeps it for one retry.
    #[allow(clippy::too_many_arguments)]
    async fn run_attempt(
        &self,
        mailbox: &MailboxSource,
        loop_: &Loop,
        pair: &lifecycle::Pair,
        storage: &Arc<Storage>,
        reserved: ledger::State,
        journal: Option<Vec<u8>>,
        status: Snapshot,
        class: i32,
        retried: bool,
    ) -> io::Result<Option<Snapshot>> {
        let Some(reservation) = &reserved.in_flight else {
            return Ok(None);
        };
        let attempt = reservation.transition_attempt_id;
        let declares = |component| reservation.declared_components.contains(&component);
        let guard = Arc::new(Guard {
            storage: storage.clone(),
            journal: loop_.journal.clone(),
            baseline: journal,
            stop: loop_.stop.clone(),
            performance: declares(ledger::Component::PERFORMANCE),
            fans: declares(ledger::Component::FANS),
            refused: AtomicBool::new(false),
            reservation: reserved.clone(),
        });
        let reconciled = self
            .reconcile_locked(
                mailbox,
                Reconcile {
                    // As a lifecycle attempt's: an interactive user
                    // controlling the console session, re-checked by each
                    // step's capture and every write's authorization.
                    caller: Caller {
                        pid: Some(pair.pid),
                        logon: Logon::Interactive,
                        session: pair.session,
                        control_session: Some(pair.session),
                    },
                    sid: pair.sid.clone(),
                    request: convergence_request(),
                    storage,
                    classes: Classes::of([class]),
                    deadline: Instant::now() + crate::service_frame::RECONCILE_BUDGET,
                    status,
                    mode: thermal_drift::Mode::Overwrite,
                    resume: false,
                    scope: Some(guard.clone()),
                },
            )
            .await?;
        // A stop leaves the reservation for read-only reconciliation.
        if loop_.stop.load(Ordering::Acquire) {
            return Ok(reconciled.snapshot);
        }
        let disposition = if reconciled.applied {
            ledger::Disposition::CONFIRMED
        } else if reconciled.submitted {
            // A possible submission that did not settle by readback. While
            // its journal row fences the store nothing tells whether it
            // landed; once settled without a match, it is another state.
            if storage.pending() {
                ledger::Disposition::RECONCILIATION_UNAVAILABLE
            } else {
                ledger::Disposition::EXTERNAL
            }
        } else if !retried
            && !guard.refused()
            && (reconciled.retry || reconciled.deferred || reconciled.unchanged)
        {
            // D7: refused before any submission for a cause that may clear.
            *loop_.retry.borrow_mut() = Some((attempt, Instant::now() + RETRY_DELAY));
            return Ok(reconciled.snapshot);
        } else {
            ledger::Disposition::BLOCKED
        };
        complete(storage, &reserved, disposition);
        Ok(reconciled.snapshot)
    }

    /// Managed's read-only reconciliation of a reservation this runtime did
    /// not make: the confirmed reading against its target. An unknown
    /// HyperBoost or an incomplete reading is no evidence and keeps it.
    fn reconcile_reserved(
        &self,
        evaluation: &Evaluation<'_>,
        held: &ledger::State,
    ) -> io::Result<()> {
        let (Some(reservation), Some(destination)) = (&held.in_flight, held.settled_destination)
        else {
            return Ok(());
        };
        let confirmed = evaluation.base.confirmed_thermal();
        if confirmed.hyper_boost.is_none() {
            return Ok(());
        }
        let target = &reservation.desired_target;
        let desired = target_intent(target);
        let settings = target
            .fan_control
            .as_ref()
            .and_then(|control| serde_json::to_value(control).ok());
        let owns_manual_fans = status::manual(desired.fans.0)
            && self.owns_exact_fans(
                evaluation.sid,
                evaluation.session,
                destination.0,
                settings.as_ref(),
            );
        let disposition = match status::classify(&status::Observation {
            confirmed,
            desired,
            owns_manual_fans,
        }) {
            Kind::Matched => ledger::Disposition::CONFIRMED,
            Kind::AwaitingObservation => return Ok(()),
            _ => ledger::Disposition::EXTERNAL,
        };
        complete(evaluation.storage, held, disposition);
        Ok(())
    }
}

/// Retires `reserved`'s reservation with `disposition` (managed
/// `ThermalConvergenceLedgerTransitions.Complete`). A failed write closes
/// the ledger, which the status reports.
fn complete(storage: &Storage, reserved: &ledger::State, disposition: ledger::Disposition) {
    let Some(reservation) = &reserved.in_flight else {
        return;
    };
    let Some(destination) = reserved.settled_destination else {
        return;
    };
    let completed = reserved.complete(
        destination,
        reserved.contradiction,
        &reservation.declared_components.clone(),
        disposition,
    );
    let _ = storage.replace_convergence_ledger(reserved, &completed);
}

/// Reads managed's HyperBoost ownership journal,
/// `ProgramData\OpenBlade\hyperboost-ownership.json`, read-only through the
/// protected path checks every machine record is read with. `None` when the
/// file does not exist in the verified directory.
#[cfg(not(test))]
pub(in super::super) fn hyperboost_journal() -> io::Result<Option<Vec<u8>>> {
    use crate::windows_protected_path::{self as paths, Policy};
    use std::{ffi::OsStr, io::Read};
    use windows_sys::{
        Wdk::Storage::FileSystem::{FILE_NON_DIRECTORY_FILE, FILE_OPEN, FILE_OPEN_REPARSE_POINT},
        Win32::Storage::FileSystem::{FILE_GENERIC_READ, FILE_SHARE_READ},
    };
    let path = crate::service_install_identity::known_folder(
        &windows_sys::Win32::UI::Shell::FOLDERID_ProgramData,
    )?
    .join("OpenBlade");
    let (root, ancestors, _) = paths::pin_root(&path)?;
    paths::require_ntfs(&root)?;
    paths::verify_security(&root, Policy::ROOT)?;
    for (i, parent) in ancestors.iter().enumerate() {
        paths::verify_security(
            parent,
            if i + 1 == ancestors.len() {
                Policy::PROGRAM_DATA
            } else {
                Policy::ANCESTOR
            },
        )?;
    }
    let mut file = match paths::open_relative(
        &root,
        OsStr::new("hyperboost-ownership.json"),
        FILE_GENERIC_READ,
        FILE_SHARE_READ,
        FILE_OPEN,
        FILE_NON_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
    ) {
        Ok(file) => file,
        Err(error) if paths::missing_leaf(&error) => return Ok(None),
        Err(error) => return Err(error),
    };
    paths::check_regular_file(&file)?;
    paths::verify_security(&file, Policy::Record)?;
    if file.metadata()?.len() > 65536 {
        return Err(paths::denied("the HyperBoost journal is too large"));
    }
    let mut bytes = Vec::new();
    file.read_to_end(&mut bytes)?;
    Ok(Some(bytes))
}

#[cfg(test)]
#[path = "thermal_convergence_loop_tests.rs"]
mod tests;
