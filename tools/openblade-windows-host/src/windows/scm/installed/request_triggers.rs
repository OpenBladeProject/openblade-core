//! Request-driven reconciliation of saved intent (`service-profile-triggers-v1`):
//! managed `ReportApplicationContextAsync` and `UpdateProfilesAsync` apply
//! the saved intent through `ApplyFullTransitionAsync`.
//!
//! A pair owes a reconciliation from service start and after each change of
//! its active application or saved thermal intent (`Debt`). A report
//! reconciles while the pair owes one, and a save that changes the active
//! application's thermal intent reconciles at the changed classes. Every
//! reconciliation that reads firmware settles the debt, as managed attempts a
//! changed pair once, except a refusal that may clear by itself and
//! submitted nothing (`RETRIES`). Refusals before any step (unknown power,
//! no admission, a fenced store) read no firmware and leave the debt as it
//! was; a step refused by saved intent alone reads none either
//! (`refused_by_saved_intent`).
//!
//! Each step runs under its own journal id (`step_request`), with every
//! per-write admission, `destination_holds` re-check, readback and
//! stop-on-ambiguity path of an explicit request. Nothing is stored and no
//! step is replayed: a fenced store stops the reconciliation. A step still
//! waiting on a transfer `RECONCILE_MARGIN` past the deadline is dropped as
//! an explicit request is at its own deadline, and the trigger answers.
//!
//! The low-power gate defers a reconciliation as managed
//! `ApplyFullTransitionAsync` does. Each write's own admission
//! (`WindowsGuard::authorize_for`, the gate's `epoch_for`) refuses a write
//! while the gate is set. The gate (`Runtime::gate`) is read before each
//! step, which stops that step itself, and after a trigger's or a step's
//! refusal, to give managed's reply and keep the debt owed without spending
//! a retry. A refused step is a deferral when the gate generation moved
//! during it, even if the gate cleared again. The lifecycle tick after the
//! boundary that clears the gate (`lifecycle`), or else the next report,
//! reconciles the debt.
use super::fan::FanRequest;
use super::profiles::Updated;
use super::*;
use crate::installed::readiness::thermal_storage::ProfileSave;
use openblade_domain::profile_set::{ProfileSet, update::STORED_MESSAGE};
use tokio::time::timeout_at;

#[cfg(feature = "service-thermal-convergence-v1")]
#[path = "thermal_convergence_loop.rs"]
pub(super) mod convergence_loop;
#[path = "lifecycle_triggers.rs"]
pub(super) mod lifecycle;
#[cfg(feature = "service-thermal-convergence-v1")]
#[path = "thermal_reapply.rs"]
pub(super) mod reapply;

/// Managed `ReportApplicationContextAsync` (Blocked) for a reporter that no
/// longer controls the active local session.
pub(in super::super) const APPLICATION_SESSION_REQUIRED: &str =
    "Application context requires the active local Windows session.";
/// A trigger whose deadline passed while it waited for the mutation gate:
/// nothing was recorded, stored or submitted, so it may be sent again.
const DEADLINE_PASSED: &str = "The request deadline passed before it was admitted; nothing was recorded, stored or submitted.";
const RECORDED: &str = "The active application profile was recorded.";
const RENEWED: &str = "The active application profile lease was renewed.";
const INACTIVE: &str = "No application profile is active.";
/// A reconciliation answered at its bound while a step had not finished.
const UNFINISHED: &str = "The reconciliation did not finish within its budget.";
/// The same, when that step had journaled nothing that could be submitted.
const UNFINISHED_UNSUBMITTED: &str =
    "The reconciliation did not finish within its budget; no thermal setter was submitted.";
/// A pair whose reconciliation was refused for a cause that may clear by
/// itself, before any submission (`Stepped::transient`), stays owed and is
/// retried on later reports, at most this many times: one change reads
/// firmware for at most three reconciliations. A renewal arrives every 5 s,
/// so an unbounded debt would read firmware, and draw transaction pairs, on
/// each one.
const RETRIES: u8 = 2;

/// A trigger's admission before any firmware read or transaction pair
/// (`admit_trigger`).
enum TriggerAdmission {
    /// The store is fenced by an earlier submission or a changed document.
    Fenced,
    /// Unclassified Windows power, nothing owed at a class it admits, or a
    /// refused fresh authorization: the owed classes it admits.
    Refused(Classes),
    /// The owed classes to reconcile.
    Admitted(Classes),
}

/// Managed applies only on classified power with a verified device and
/// writes not blocked (`ActivePowerSource`, `DeviceVerified`,
/// `WritesBlocked`). Every write still passes its own admission, and each
/// step refuses an adapter its own reading finds unclassified. A refusal
/// here reads no firmware and leaves the debt as it was.
fn admit_trigger(
    storage: &Storage,
    fresh: &dyn Fresh,
    owed: Classes,
) -> io::Result<TriggerAdmission> {
    let classes = owed.intersection(Classes::admitted(fresh));
    if storage.pending() {
        return Ok(TriggerAdmission::Fenced);
    }
    fresh.verify_health()?;
    let admitted = !classes.is_empty() && fresh.authorize().is_some();
    fresh.verify_health()?;
    Ok(if admitted {
        TriggerAdmission::Admitted(classes)
    } else {
        TriggerAdmission::Refused(classes)
    })
}
/// Managed `DeferLightingIntentForLowPower` (`ServiceControlRuntime.cs:19471`):
/// the message `ApplyFullTransitionAsync` is interrupted with, before any
/// device access, while the low-power gate is set (`:16360`).
pub(in super::super) const LOW_POWER_DEFERRED: &str =
    "Lighting intent was stored and will be applied after standby.";
/// Managed `ScopedThermalWriteBlockReason`'s low-power clause (`:15421`): a
/// step whose write admission refused it while the gate was set.
pub(in super::super) const LOW_POWER_STEP: &str =
    "The laptop entered a low-power transition before the thermal command was submitted.";
/// Managed `ScopedPerformanceWriteBlockReason`'s low-power clause (`:15375`):
/// the hardware result of an explicit performance request whose intent was
/// stored while the gate was set.
pub(in super::super) const LOW_POWER_PERFORMANCE: &str =
    "The laptop entered a low-power transition before the performance command was submitted.";
/// The battery class, the one class Windows alone establishes.
pub(super) const BATTERY: i32 = 1;

/// Why an explicit request stores no intent while the low-power gate is set
/// (`deferred_store_refusal`).
pub(super) enum DeferredRefusal {
    /// The storage admission refused: nothing is accepted.
    Admission,
    /// Windows reports AC or an unknown source. No admission reads the
    /// adapter while the gate is set, so the external class is unknown, and
    /// managed admits no unclassified external control in low power
    /// (`FreshUnclassifiedExternalControlIsAdmitted`): nothing is stored.
    Unclassified,
}
/// Whether an explicit request may store its intent while the low-power gate
/// is set, at the battery class: its fresh storage admission
/// (`Fresh::authorize_storage`) passes and Windows reports battery. Managed
/// `ResolveCurrentPowerSourceAsync` returns Battery from Windows alone,
/// without reading the adapter.
pub(super) fn deferred_store_refusal(fresh: &dyn Fresh) -> Option<DeferredRefusal> {
    if !fresh.authorize_storage() {
        Some(DeferredRefusal::Admission)
    } else if fresh.on_battery() != Some(true) {
        Some(DeferredRefusal::Unclassified)
    } else {
        None
    }
}

/// What an explicit request's low-power store reads and records: its
/// capture's storage and storage admission, the foreground it resolves the
/// profiles for, and the `(sid, session)` pair that then owes a
/// reconciliation (`Runtime::owe_stored_intent`).
pub(super) struct DeferredStore<'a> {
    pub(super) storage: &'a Storage,
    pub(super) fresh: &'a dyn Fresh,
    pub(super) application: &'a Foreground,
    pub(super) pair: (&'a str, u32),
}

/// Managed `AnyWritesAttempted` or `SubmissionPossible` for the step or
/// request `id`: a journal row of it that is not known unsubmitted, or a
/// fence that may hide a submission. A fence that provably reached none,
/// such as a changed override document, is not.
fn submission_possible(storage: &Storage, id: &str) -> bool {
    storage.submission_possible()
        || (storage.last_request().as_deref() == Some(id)
            && storage.progress() != Some(Progress::KnownNotSubmitted as u8))
}

/// The low-power gate as an explicit request found it once it held its own
/// write authorization, after its capture (`Runtime::overtaken_by_gate`).
#[derive(Clone, Copy)]
pub(super) struct GateMark(Gate);

/// One reading of the low-power gate (`Runtime::gate`).
#[derive(Clone, Copy, PartialEq, Eq)]
enum Gate {
    /// Clear at this gate generation. Every accepted boundary advances the
    /// generation, so a later reading that differs proves only that a
    /// boundary arrived in between: usually a suspend, possibly cleared
    /// again, but a new resume (`NEW_RESUME_TICKS` after the last boundary)
    /// moves it by two without setting the gate. Treating a step refused
    /// across that resume as a deferral is benign: nothing of it was
    /// submitted, and the debt stays owed for the next report.
    Clear(u64),
    /// Set, or the lifecycle is unreadable or exhausted.
    Low,
}

/// The classified classes fresh Windows power admits before any adapter
/// reading: battery, or on AC the two classified external classes, which
/// only the step's own adapter reading tells apart. Unknown power admits
/// none, as managed applies nothing on an unknown source. The status base
/// cannot tell them apart either: it reports 3 for any power but battery.
fn reconcile_classes(fresh: &dyn Fresh) -> &'static [i32] {
    match fresh.on_battery() {
        Some(true) => &[1],
        Some(false) => &[0, 2],
        None => &[],
    }
}
impl Classes {
    const ALL: Self = Self(0b111);
    /// The classes fresh Windows power admits (`reconcile_classes`).
    pub(super) fn admitted(fresh: &dyn Fresh) -> Self {
        Self::of(reconcile_classes(fresh).iter().copied())
    }
    fn of(classes: impl IntoIterator<Item = i32>) -> Self {
        Self(
            classes
                .into_iter()
                .filter(|class| (0..=2).contains(class))
                .fold(0, |mask, class| mask | (1 << class)),
        )
    }
    fn is_empty(self) -> bool {
        self.0 == 0
    }
    fn union(self, other: Self) -> Self {
        Self(self.0 | other.0)
    }
    fn intersection(self, other: Self) -> Self {
        Self(self.0 & other.0)
    }
    pub(super) fn iter(self) -> impl Iterator<Item = i32> {
        (0..=2).filter(move |&class| self.contains(class))
    }
}

/// What one `(sid, session)` pair owes (`Runtime::debts`). A pair without
/// an entry owes its startup reconciliation at every class.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum Debt {
    Settled,
    /// The saved intent at `classes` needs a reconciliation; `attempts`
    /// counts the transient refusals already spent on it (`RETRIES`).
    Owed {
        classes: Classes,
        attempts: u8,
    },
}
impl Debt {
    /// A new change at `classes`, with no attempt spent.
    fn owed(classes: Classes) -> Self {
        Self::Owed {
            classes,
            attempts: 0,
        }
    }
}

/// Where a user's startup reconciliation stands (`Runtime::startup`), which
/// applies over confirmed drift as managed `ReconcileStartupProfileAsync`
/// does (`thermal_drift::Mode::Overwrite`). It is keyed on the SID, as
/// managed keys activation: the same user in a new session is not starting
/// again. Managed ends startup after one transition on classified power and
/// keeps it pending on unclassified power (`ReconcileStartupProfileCoreAsync`).
/// Native ends it once a starting reconciliation has compared a fresh reading
/// and a debt of that user then settles, or once a competing controller
/// drops a cause (`Runtime::drop_for_controller`). A debt that settles with
/// nothing compared, such as refusals on an adapter not classified yet or
/// saved intent that reads no firmware, leaves the user starting: its next
/// reconciliation still overwrites, instead of comparing an intent that
/// native never confirmed and pausing until an explicit request.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum Startup {
    /// A starting reconciliation compared a fresh reading
    /// (`thermal_drift::Transition::compared`).
    Compared,
    Ended,
}

/// Managed `ServiceTransitionApplicationOutcome.ToOperationResult`: Applied
/// (1) when fully applied, else Stored (0) when the trigger stored intent,
/// else Failed (3) when a setter may have been submitted, else Blocked (2).
pub(in super::super) fn operation_status(
    fully_applied: bool,
    intent_stored: bool,
    submitted: bool,
) -> u8 {
    if fully_applied {
        1
    } else if intent_stored {
        0
    } else if submitted {
        3
    } else {
        2
    }
}
/// Managed `DeviceSubsystemOrder.ForTransition`: fans go first only when the
/// confirmed fans are manual and the destination returns them to Automatic.
pub(in super::super) fn fans_first(confirmed_manual: bool, next_manual: bool) -> bool {
    confirmed_manual && !next_manual
}

/// One subsystem step of a reconciliation.
#[derive(Clone, Copy, Eq, PartialEq)]
enum Step {
    Performance,
    Fans,
}
/// The journal id of one step of the trigger `request`. The journal keeps
/// one row chain per id, so the two steps of one trigger need distinct ids.
/// A step id is the first 16 bytes of SHA-256 over a fixed domain, the
/// lowercased trigger id and the step name, written as a GUID with version
/// 8 and the RFC 9562 variant: deterministic for its trigger, and distinct
/// from the version 4 ids managed clients send (`Guid.NewGuid`).
fn step_request(request: &str, step: Step) -> String {
    let name = match step {
        Step::Performance => "performance",
        Step::Fans => "fans",
    };
    let digest = Sha256::digest(format!(
        "OpenBlade.ReconcileStep.v1\n{}\n{name}",
        request.to_ascii_lowercase()
    ));
    let mut id = [0u8; 16];
    id.copy_from_slice(&digest[..16]);
    id[6] = (id[6] & 0x0f) | 0x80;
    id[8] = (id[8] & 0x3f) | 0x80;
    let hex: String = id.iter().map(|byte| format!("{byte:02x}")).collect();
    format!(
        "{}-{}-{}-{}-{}",
        &hex[..8],
        &hex[8..12],
        &hex[12..16],
        &hex[16..20],
        &hex[20..]
    )
}
/// Whether `id` is a reconciliation step's journal id (`step_request`).
#[cfg(test)]
pub(in super::super) fn is_step_request(id: &str) -> bool {
    crate::service_frame::is_guid(id) && id.as_bytes()[14] == b'8' && id == id.to_ascii_lowercase()
}

/// One reconciliation of saved intent (`Runtime::reconcile_locked`): the
/// trigger's request, the user's store, the classes it applies at, and the
/// status the trigger was read against.
struct Reconcile<'a> {
    caller: Caller,
    sid: String,
    request: String,
    storage: &'a Storage,
    classes: Classes,
    deadline: Instant,
    status: Snapshot,
    // How the reconciliation treats confirmed drift, and whether it applies
    // a power change seen at a resume, whose firmware may still settle
    // (`thermal_drift`).
    mode: thermal_drift::Mode,
    resume: bool,
    // A convergence attempt's scope: only its declared subsystems run, and
    // each write is admitted by its reservation (`convergence_loop`).
    #[cfg(feature = "service-thermal-convergence-v1")]
    scope: Option<Arc<dyn thermal_drift::Scope>>,
}
/// What one reconciliation did, managed `ServiceTransitionApplicationOutcome`.
struct Reconciled {
    // Every step ran and each one that needed a change was confirmed
    // (`FullyApplied`).
    applied: bool,
    // A setter may have been submitted (`SubmissionPossible`,
    // `AnyConfirmedWrites`, `AnyWritesAttempted`).
    submitted: bool,
    // Every step found its class unchanged and wrote nothing.
    unchanged: bool,
    // Not applied, but each step that needed a change was confirmed or was
    // refused transiently (`Stepped::transient`) with nothing of it
    // journaled as possibly submitted, and the store is not fenced: running
    // the steps again replays no submission.
    retry: bool,
    // The low-power gate stopped a step before it started, or set during a
    // step that was then refused or not finished with nothing of it possibly
    // submitted, and the store is not fenced; no later step ran. The
    // deferred step runs once the gate clears, so the debt stays owed
    // whatever the earlier steps did, and a deferral spends no retry. A
    // relabelled step's own refusal is not reported for that reconciliation.
    deferred: bool,
    // The classes the readings of the steps that needed a change found.
    read: Classes,
    // A step had not finished at the bound (`RECONCILE_MARGIN`) and was not
    // a deferral: its worker may still submit.
    unfinished: bool,
    // The last step that needed a change was refused with nothing of it
    // possibly submitted, as managed's last attempt is KnownNotApplied
    // (`ThermalTransitionExecutor.Disposition`): the convergence loop's
    // Blocked.
    #[cfg(feature = "service-thermal-convergence-v1")]
    refused_last: bool,
    // Each step that ran or was deferred, with its reply, in step order
    // (`reapply`).
    #[cfg(feature = "service-thermal-convergence-v1")]
    outcomes: Vec<(Step, Outcome)>,
    message: String,
    snapshot: Option<Snapshot>,
}
impl Reconciled {
    /// The debt a report's reconciliation of `classes` leaves after
    /// `attempts` earlier transient refusals: settled, unless it may be
    /// retried and `RETRIES` allows another. A low-power deferral stays owed
    /// with the count kept, even after an earlier step that may not be
    /// retried, so renewals while the laptop sleeps never exhaust it and the
    /// deferred step is not dropped.
    fn after(&self, classes: Classes, attempts: u8) -> Debt {
        if self.deferred {
            Debt::Owed { classes, attempts }
        } else if self.retry && attempts < RETRIES {
            Debt::Owed {
                classes,
                attempts: attempts + 1,
            }
        } else {
            Debt::Settled
        }
    }
    /// The attempts a save's retryable reconciliation spent: the save itself,
    /// unless the low-power gate deferred it.
    fn spent(&self) -> u8 {
        u8::from(!self.deferred)
    }
    /// Managed `ToOperationResult(intentStored)`, with the trigger's prefix.
    /// Managed waits for every step; native answers at its bound while a step
    /// may still be submitting, which is Failed even after a save, so the
    /// caller does not take the stored profiles for a settled hardware result.
    fn into_outcome(self, intent_stored: bool, prefix: &str) -> (Outcome, Option<Snapshot>) {
        let message = if self.message.is_empty() {
            prefix.to_owned()
        } else {
            format!("{prefix} {}", self.message)
        };
        let status = if self.unfinished && self.submitted {
            3
        } else {
            operation_status(self.applied, intent_stored, self.submitted)
        };
        (Outcome { status, message }, self.snapshot)
    }
}
/// One admitted profile save (`Runtime::save_profiles_locked`).
pub(super) struct Saving<'a> {
    pub(super) mailbox: &'a MailboxSource,
    pub(super) caller: Caller,
    pub(super) sid: String,
    pub(super) request: String,
    pub(super) storage: &'a Storage,
    pub(super) fresh: Arc<dyn Fresh>,
    pub(super) profiles: &'a ProfileSet,
    pub(super) deadline: Instant,
    pub(super) status: Snapshot,
}

impl Runtime {
    /// Managed `ReportApplicationContextAsync` for one admitted report, under
    /// the mutation gate. The report selects the active application pair once
    /// (`Storage::select_application`); a changed pair owes a reconciliation,
    /// and a pair that owes one reconciles (`reconcile_locked`).
    // Every caller, including the shared mutation tests, passes the admitted request fields individually.
    #[allow(clippy::too_many_arguments)]
    pub(in super::super) async fn application(
        &self,
        mailbox: MailboxSource,
        caller: Caller,
        sid: String,
        request: String,
        instance: String,
        sequence: i64,
        path: Option<String>,
        deadline: Instant,
        status: Snapshot,
    ) -> io::Result<(Outcome, Option<Snapshot>)> {
        let stored = |message: &str| {
            Ok((
                Outcome {
                    status: 0,
                    message: message.into(),
                },
                None,
            ))
        };
        if caller.logon != Logon::Interactive || caller.control_session != Some(caller.session) {
            return Ok((Outcome::blocked(APPLICATION_SESSION_REQUIRED), None));
        }
        let Some(pid) = caller.pid else {
            return Ok((Outcome::blocked("Application context was rejected."), None));
        };
        if sequence <= 0
            || !crate::service_frame::is_guid(&instance)
            || path
                .as_ref()
                .is_some_and(|p| !super::super::thermal_storage::valid_application_path(p))
        {
            return Ok((Outcome::blocked("Application context was rejected."), None));
        }
        #[cfg(test)]
        self.entered.set(self.entered.get() + 1);
        // One mutation at a time, as managed's operation gate allows.
        let _serial = self.gate.lock().await;
        verify_retained_failure(&self.fatal)?;
        if let Some(old) = self.application.borrow().as_ref().filter(|old| {
            old.sid == sid
                && old.session == caller.session
                && old.instance.eq_ignore_ascii_case(&instance)
        }) {
            if old.pid != pid {
                return Ok((
                    Outcome::blocked("Application context belongs to another reporter."),
                    None,
                ));
            }
            if sequence <= old.sequence {
                return stored("A stale application context was ignored.");
            }
        }
        // The reconciliation after the record is bounded by its steps' own
        // pace (`readiness::reconciles`); the record itself is not late.
        if Instant::now() >= deadline {
            return Ok((Outcome::blocked(DEADLINE_PASSED), None));
        }
        // The lease bounds the pair: an expired one is no longer active.
        let prior = self.foreground(&sid, caller.session);
        // The sequence is accepted before the profiles are read, as managed
        // accepts it before `SetApplicationContext`; a read that fails keeps
        // the pair already selected.
        let record = |foreground: Foreground| {
            *self.application.borrow_mut() = Some(Application {
                sid: sid.clone(),
                session: caller.session,
                pid,
                instance: instance.clone(),
                sequence,
                foreground,
                expires: Instant::now() + super::APPLICATION_LEASE,
            });
        };
        let MailboxSource::Reader(reader) = &mailbox else {
            record(prior);
            return Ok((
                Outcome::blocked("Authenticated thermal custody is unavailable."),
                None,
            ));
        };
        reader.custody().verify_integrity()?;
        // In low power a storage capture records the pair; its refused write
        // admission below then takes the deferral.
        let captured = self.capture_intent(&sid, caller.session, reader.custody());
        let selected = captured.and_then(|(storage, fresh)| {
            let (next, changed) = storage.select_application(&prior, path.as_deref())?;
            Ok((storage, fresh, next, changed))
        });
        let (storage, fresh, next, changed) = match selected {
            Ok(value) => value,
            Err(error) if is_fatal_admission(&error) => return Err(error),
            Err(error) => {
                record(prior);
                if is_competing_controller(&error) {
                    self.drop_for_controller(&sid, caller.session);
                }
                return Ok((
                    Outcome::blocked(format!(
                        "Saved configuration or the current session is unavailable: {error}"
                    )),
                    None,
                ));
            }
        };
        let active = next.active_id().is_some();
        record(next);
        let key = (sid.clone(), caller.session);
        let idle = if active { RENEWED } else { INACTIVE };
        // Managed publishes the new application's desired state before its
        // transition reads the prior one, so its own preset under the old
        // application reads as drift and pauses the change. Native compares
        // with OpenBlade's own confirmed state as well, as managed's resolver
        // intends (`thermal_drift::Mode`).
        let mode = thermal_drift::Mode::WithPrior;
        // A changed pair owes a new reconciliation at every class.
        let debt = if changed {
            Debt::owed(Classes::ALL)
        } else {
            self.debt(&key)
        };
        self.owe(key.clone(), debt);
        let Debt::Owed {
            classes: owed,
            attempts,
        } = debt
        else {
            return stored(idle);
        };
        let unapplied = if changed { RECORDED } else { idle };
        let classes = match admit_trigger(&storage, fresh.as_ref(), owed)? {
            // A fenced store, such as an override document changed outside
            // the Service, names its storage reconciliation before any
            // submission.
            TriggerAdmission::Fenced => {
                return Ok((
                    Outcome::blocked(format!("{unapplied} {}", storage.pending_reason()?)),
                    None,
                ));
            }
            TriggerAdmission::Refused(classes) => {
                // Managed checks the gate inside `ApplyFullTransitionAsync`,
                // which only a changed pair on classified power reaches; it
                // defers before any device access and replies Blocked. The
                // debt is kept as it was for the next report after the gate
                // clears.
                if changed && !classes.is_empty() && self.gate() == Gate::Low {
                    return Ok((
                        Outcome::blocked(format!("{RECORDED} {LOW_POWER_DEFERRED}")),
                        None,
                    ));
                }
                return stored(unapplied);
            }
            TriggerAdmission::Admitted(classes) => classes,
        };
        let reconciled = self
            .reconcile_locked(
                &mailbox,
                Reconcile {
                    caller,
                    sid,
                    request,
                    storage: &storage,
                    classes,
                    deadline,
                    status,
                    mode,
                    resume: false,
                    #[cfg(feature = "service-thermal-convergence-v1")]
                    scope: None,
                },
            )
            .await?;
        self.owe(key, reconciled.after(owed, attempts));
        if reconciled.unchanged {
            return stored(unapplied);
        }
        Ok(reconciled.into_outcome(false, RECORDED))
    }

    /// Managed `UpdateProfilesAsync` from its store, under the held gate:
    /// the save is stored, and when an application profile was active before
    /// it and the save changes that application's thermal intent at a class
    /// fresh Windows power admits (`SaveIntent::thermal_changed`), the saved
    /// intent is applied at those classes (`reconcile_locked`).
    pub(super) async fn save_profiles_locked(&self, saving: Saving<'_>) -> io::Result<Updated> {
        let Saving {
            mailbox,
            caller,
            sid,
            request,
            storage,
            fresh,
            profiles,
            deadline,
            status,
        } = saving;
        if Instant::now() >= deadline {
            return Ok((Outcome::blocked(DEADLINE_PASSED), false, None));
        }
        // The terms managed compares at its active power source. Native learns
        // the external class only from the step's own adapter reading, so
        // each class fresh Windows power admits is compared: a step whose
        // reading finds a class the save did not change writes nothing.
        // Managed applies only on classified power, so unknown power compares
        // nothing. A store that cannot be read now applies nothing after the
        // save.
        let classes = reconcile_classes(fresh.as_ref());
        let foreground = self.foreground(&sid, caller.session);
        let intents = |foreground: &Foreground| {
            classes
                .iter()
                .map(|&class| storage.save_intent(foreground, class))
                .collect::<io::Result<Vec<_>>>()
        };
        let prior = if !classes.is_empty() && foreground.active_id().is_some() {
            match intents(&foreground) {
                Ok(prior) => Some(prior),
                Err(error) if is_fatal_admission(&error) => return Err(error),
                Err(_) => None,
            }
        } else {
            None
        };
        match storage.save_profiles(profiles)? {
            ProfileSave::Stored => self.record_stored_profiles(),
            // Managed `Failed`.
            ProfileSave::Rejected(message) => {
                return Ok((Outcome { status: 3, message }, false, None));
            }
            ProfileSave::Blocked(message) => return Ok((Outcome::blocked(message), false, None)),
        }
        let stored = || {
            Ok((
                Outcome {
                    status: 0,
                    message: STORED_MESSAGE.into(),
                },
                true,
                None,
            ))
        };
        // Managed `ProfileAutomationState.UpdateProfiles`: the active pair
        // survives only while its rule still holds its path. The save
        // committed, so a read that fails now leaves the stored reply.
        let next = match storage.retain_application(&foreground) {
            Ok(next) => next,
            Err(error) if is_fatal_admission(&error) => return Err(error),
            Err(_) => return stored(),
        };
        if let Some(application) = self
            .application
            .borrow_mut()
            .as_mut()
            .filter(|a| a.sid == sid && a.session == caller.session)
        {
            application.foreground = next.clone();
        }
        let Some(prior) = prior else {
            return stored();
        };
        let changed = match intents(&next) {
            Ok(next) => Classes::of(
                classes
                    .iter()
                    .zip(prior.iter().zip(&next))
                    .filter(|(_, (prior, next))| prior.thermal_changed(next))
                    .map(|(&class, _)| class),
            ),
            Err(error) if is_fatal_admission(&error) => return Err(error),
            Err(_) => Classes::of([]),
        };
        if changed.is_empty() {
            return stored();
        }
        let key = (sid.clone(), caller.session);
        // As a report's: managed `UpdateProfilesAsync` also publishes the
        // saved set before its transition reads the prior desired state.
        let mode = thermal_drift::Mode::WithPrior;
        let prior = self.debt(&key);
        // The save's change, with what the pair owed before it.
        let combined = match prior {
            Debt::Owed { classes, .. } => classes.union(changed),
            Debt::Settled => changed,
        };
        self.owe(key.clone(), Debt::owed(combined));
        // Managed also needs a verified device with writes not blocked; every
        // write still passes its own admission. A refusal here reads no
        // firmware and leaves the change owed to the next report.
        fresh.verify_health()?;
        let pending = storage.pending();
        let admitted = !pending && fresh.authorize().is_some();
        fresh.verify_health()?;
        if !admitted {
            // Managed `ApplyFullTransitionAsync` defers in low power after its
            // pending-reconciliation check; the save is Stored either way.
            if !pending && self.gate() == Gate::Low {
                return Ok((
                    Outcome {
                        status: 0,
                        message: format!("Profiles were stored. {LOW_POWER_DEFERRED}"),
                    },
                    true,
                    None,
                ));
            }
            return stored();
        }
        let reconciled = self
            .reconcile_locked(
                mailbox,
                Reconcile {
                    caller,
                    sid,
                    request,
                    storage,
                    classes: changed,
                    deadline,
                    status,
                    mode,
                    resume: false,
                    #[cfg(feature = "service-thermal-convergence-v1")]
                    scope: None,
                },
            )
            .await?;
        let debt = if reconciled.unchanged {
            // The adapter reports a class the save did not change: managed
            // compares there and applies nothing, and what the pair owed
            // before the save is left as it was.
            prior
        } else if reconciled.retry || reconciled.deferred {
            // The save was the first attempt, unless the gate deferred it;
            // later reports retry. A deferral stays owed even after an
            // earlier step that may not be retried (`Reconciled::after`).
            Debt::Owed {
                classes: combined,
                attempts: reconciled.spent(),
            }
        } else {
            match prior {
                // A debt owed before the save is settled only by a
                // reconciliation whose reading found one of its classes:
                // the steps there apply the whole saved intent of that class.
                Debt::Owed { classes, .. } if reconciled.read.intersection(classes).is_empty() => {
                    prior
                }
                _ => Debt::Settled,
            }
        };
        self.owe(key, debt);
        if reconciled.unchanged {
            return stored();
        }
        let (outcome, snapshot) = reconciled.into_outcome(true, "Profiles were stored.");
        Ok((outcome, true, snapshot))
    }

    /// Managed `ApplyFullTransitionAsync` for performance and fans, under the
    /// held gate: the saved intent of `step.sid` is applied at `step.classes`
    /// without storing any. Fans go first when the confirmed fans are manual
    /// and every class returns them to Automatic
    /// (`DeviceSubsystemOrder.ForTransition`); that return carries the saved
    /// preset in the same pair (`FanPermit`). Otherwise performance goes
    /// first. Both steps run unless the store becomes fenced, which stops the
    /// reconciliation; a failed step is recorded by its own permit and never
    /// replayed.
    async fn reconcile_locked(
        &self,
        mailbox: &MailboxSource,
        step: Reconcile<'_>,
    ) -> io::Result<Reconciled> {
        let Reconcile {
            caller,
            sid,
            request,
            storage,
            classes,
            deadline,
            status,
            mode,
            resume,
            #[cfg(feature = "service-thermal-convergence-v1")]
            scope,
        } = step;
        // One drift decision for both steps, from the first step's own
        // fresh reading (`thermal_drift::Transition`). The user's startup
        // reconciliation applies over drift, whatever its cause; any resume
        // hold is stamped on this runtime's clock, which the status reading
        // that settles it reads too.
        let starting = self.starting(&sid);
        let mode = if starting {
            thermal_drift::Mode::Overwrite
        } else {
            mode
        };
        let transition =
            thermal_drift::Transition::new(self.drift.clone(), mode, resume, Instant::now());
        #[cfg(feature = "service-thermal-convergence-v1")]
        let transition = transition.scoped(scope);
        let transition = Arc::new(transition);
        // The confirmed fans are the retained thermal observation's: the
        // published status is rebuilt from observed metadata and carries no
        // firmware fans. An owned fan change not yet handed back is manual
        // firmware fans this Service set, which the performance step would
        // refuse to write under, whatever that observation last saw.
        let manual = mailbox.confirmed_manual_fans() || storage.outstanding_fan();
        let order = if manual
            && match self.next_manual(storage, &sid, caller.session, classes) {
                Ok(next_manual) => fans_first(true, next_manual),
                Err(error) if is_fatal_admission(&error) => return Err(error),
                // The steps read the same store and report its failure.
                Err(_) => false,
            } {
            [Step::Fans, Step::Performance]
        } else {
            [Step::Performance, Step::Fans]
        };
        // A scoped attempt runs only the subsystems it declared.
        let order: Vec<Step> = order
            .into_iter()
            .filter(|kind| transition.declares(*kind == Step::Fans))
            .collect();
        // The trigger answers by this bound even when a step's transfer has
        // not retired (`RECONCILE_MARGIN`).
        let bound = deadline + crate::service_frame::RECONCILE_MARGIN;
        // Each step, and whether it may have submitted.
        let mut steps: Vec<(Stepped, bool)> = Vec::with_capacity(order.len());
        let mut unfinished = false;
        let mut deferred = false;
        for &kind in &order {
            if storage.pending() {
                break;
            }
            // Before the step's capture, firmware read or transaction pair,
            // as managed reads the gate before a transition touches the
            // device: a suspend after the previous step stops this one. This
            // check is the refusal itself; nothing of the step ran.
            let before = self.gate();
            if before == Gate::Low {
                deferred = true;
                break;
            }
            let epoch = self.admission_epoch();
            let id = step_request(&request, kind);
            let step = async {
                match kind {
                    Step::Performance => {
                        self.apply_locked(
                            mailbox.clone(),
                            ThermalStep {
                                caller,
                                sid: sid.clone(),
                                request: id.clone(),
                                value: None,
                                reconcile: Some(classes),
                                transition: Some(transition.clone()),
                                #[cfg(feature = "service-performance-shortcut-v1")]
                                shortcut: None,
                                deadline,
                                status: status.clone(),
                            },
                        )
                        .await
                    }
                    Step::Fans => {
                        self.apply_fan_locked(
                            mailbox.clone(),
                            FanRequest {
                                caller,
                                sid: sid.clone(),
                                request: id.clone(),
                                value: None,
                                settings: None,
                                deadline,
                                status: status.clone(),
                                transition: Some(transition.clone()),
                            },
                            Some(classes),
                        )
                        .await
                    }
                }
            };
            // At the bound the step is dropped exactly as `serve_mutation`
            // drops an explicit request at its deadline: the Reader keeps the
            // worker and permit it registered, so every later mutation first
            // waits for that worker's own retirement and then settles the
            // permit (`ReadSource::drain_mutation`), and a journal row past
            // its intent stays unsettled, fencing the store. No later request
            // can submit while this worker still might.
            let stepped = timeout_at(bound, step).await;
            // Managed `AnyWritesAttempted` or `SubmissionPossible`: a journal
            // row of this step that is not known unsubmitted, or a fence that
            // may hide a submission. A fence that provably reached none, such
            // as a changed override document, is Blocked.
            let possible = submission_possible(storage, &id);
            match stepped {
                Ok(stepped) => {
                    let mut stepped = stepped?;
                    // The drift decision excluded both thermal subsystems
                    // before any write: the other step does not run, and
                    // replies as managed's excluded subsystem does. Nothing
                    // is owed for later (managed attempts a transition once).
                    if stepped.excluded {
                        let class = stepped.class;
                        let rest = steps.len() + 1;
                        steps.push((stepped, possible));
                        if let Some(exclusion) = transition.exclusion() {
                            for kind in &order[rest..] {
                                let message = exclusion.message(match kind {
                                    Step::Performance => "Performance",
                                    Step::Fans => "Fans",
                                });
                                steps.push((
                                    Stepped {
                                        class,
                                        excluded: true,
                                        ..Stepped::new(Outcome::blocked(message), None)
                                    },
                                    false,
                                ));
                            }
                        }
                        break;
                    }
                    // A refusal by the step's own admission: its capture, its
                    // authorization before any firmware read or pair, or a
                    // write's authorization at submission (`epoch_for`).
                    // When the gate set at any time during the step, even if
                    // it cleared again, that refusal is the deferral. A step
                    // that may have submitted, found its class unchanged or
                    // fenced the store keeps its own result, since nothing
                    // of it is replayed.
                    if stepped.outcome.status == 2
                        && !stepped.unchanged
                        && !possible
                        && !storage.pending()
                        && self.gate() != before
                    {
                        deferred = true;
                        break;
                    }
                    // A refusal by a fence that left the gate generation in
                    // place, such as a repeated resume or a power-status
                    // change during the step, lost the step's admission with
                    // nothing submitted. It is no deferral, since the gate
                    // never set, but the cause clears by itself: the debt
                    // stays owed with a retry spent. A lifecycle attempt is
                    // retried by the tick once the write epoch has moved
                    // (`triggers::lifecycle`); otherwise the next report
                    // applies it under fresh admission.
                    if stepped.outcome.status == 2
                        && !stepped.unchanged
                        && !possible
                        && !storage.pending()
                        && epoch.is_some()
                        && self.admission_epoch() != epoch
                    {
                        stepped.transient = true;
                    }
                    steps.push((stepped, possible));
                }
                Err(_) => {
                    // A step the gate interrupted that cannot have submitted
                    // anything is a deferral, as a refused one is: it spends
                    // no retry, and it replies as the pre-step deferral does,
                    // not as an unfinished step that may still submit.
                    if !possible && !storage.pending() && self.gate() != before {
                        deferred = true;
                        break;
                    }
                    unfinished = true;
                    let stepped = if possible {
                        let reason = if storage.pending() {
                            storage.pending_reason()?
                        } else {
                            ""
                        };
                        Stepped::new(
                            Outcome {
                                status: 3,
                                message: format!("{UNFINISHED} {reason}").trim_end().to_owned(),
                            },
                            None,
                        )
                    } else {
                        Stepped::transient(Outcome::blocked(UNFINISHED_UNSUBMITTED))
                    };
                    steps.push((stepped, possible));
                    break;
                }
            }
        }
        // A starting reconciliation that compared a reading ends startup
        // once the debt it runs for settles (`Runtime::owe`).
        if starting && transition.compared() {
            self.startup
                .borrow_mut()
                .entry(sid)
                .or_insert(Startup::Compared);
        }
        // The deferred step submitted nothing and may run once the gate
        // clears; it reports managed's per-write low-power refusal. It did
        // not run, so the reconciliation did not run every step.
        if deferred {
            steps.push((Stepped::transient(Outcome::blocked(LOW_POWER_STEP)), false));
        }
        let submitted = steps.iter().any(|(_, possible)| *possible);
        let ran = !unfinished && !deferred && steps.len() == order.len();
        let unchanged = ran && steps.iter().all(|(s, _)| s.unchanged);
        // A step whose class did not change needed nothing; every other step
        // must be confirmed.
        let needed: Vec<&(Stepped, bool)> = steps.iter().filter(|(s, _)| !s.unchanged).collect();
        let applied =
            ran && !needed.is_empty() && needed.iter().all(|(s, _)| s.outcome.status == 1);
        let retry = !applied
            && !needed.is_empty()
            && !storage.pending()
            && needed
                .iter()
                .all(|(s, possible)| s.outcome.status == 1 || (s.transient && !possible));
        let read = Classes::of(needed.iter().filter_map(|(s, _)| s.class));
        #[cfg(feature = "service-thermal-convergence-v1")]
        let refused_last = needed
            .last()
            .is_some_and(|(s, possible)| s.outcome.status != 1 && !possible);
        // Both steps can refuse for one cause, which is said once.
        let mut said: Vec<&str> = Vec::with_capacity(steps.len());
        for (stepped, _) in if needed.is_empty() {
            steps.iter().collect()
        } else {
            needed
        } {
            if !said.contains(&stepped.outcome.message.as_str()) {
                said.push(&stepped.outcome.message);
            }
        }
        let message = said.join(" ");
        // Each step is pushed in `order`: a step that breaks the loop before
        // it runs pushes nothing, and the deferral pushed after the loop is
        // the step the gate stopped.
        #[cfg(feature = "service-thermal-convergence-v1")]
        let outcomes = order
            .iter()
            .copied()
            .zip(steps.iter().map(|(s, _)| s.outcome.clone()))
            .collect();
        Ok(Reconciled {
            applied,
            submitted,
            unchanged,
            retry,
            deferred,
            read,
            unfinished,
            #[cfg(feature = "service-thermal-convergence-v1")]
            refused_last,
            #[cfg(feature = "service-thermal-convergence-v1")]
            outcomes,
            message,
            snapshot: steps.into_iter().rev().find_map(|(s, _)| s.snapshot),
        })
    }

    /// The lifecycle's low-power gate now (`Runtime::low_power`); set,
    /// unreadable or exhausted is `Low`. It stops a step before it starts and
    /// classifies a refusal that a write's own admission already made; it
    /// never admits a write. Test composition that does not wire it reads
    /// clear.
    fn gate(&self) -> Gate {
        match self.low_power.get().map(|read| read()) {
            None => Gate::Clear(0),
            Some(Some(transition)) if !transition.suspending() => {
                Gate::Clear(transition.generation)
            }
            Some(_) => Gate::Low,
        }
    }

    /// The lifecycle a GPU clock step is admitted under
    /// (`thermal_gpu::Boundary`): the gate's clear generation, which a
    /// suspend and resume move, and the Windows power generation. Neither
    /// admits anything; the lane only compares them with what it last saw.
    #[cfg(feature = "service-gpu-clock-tuning-v1")]
    pub(super) fn gpu_boundary(&self) -> super::thermal_gpu::Boundary {
        super::thermal_gpu::Boundary {
            gate: match self.gate() {
                Gate::Clear(generation) => Some(generation),
                Gate::Low => None,
            },
            power: self.power_generation.get().and_then(|read| read()),
            // The class the step resolves (`RequestPermit::prepare_gpu`).
            source: None,
        }
    }

    /// The lifecycle's ordinary write epoch now (`Lifecycle::active`), `None`
    /// in low power, unreadable or unwired. It only classifies a refusal a
    /// write's own admission already made; it never admits a write.
    fn admission_epoch(&self) -> Option<u64> {
        self.admission_epoch.get().and_then(|read| read())
    }

    /// Whether the gate reads set, unreadable or exhausted now
    /// (`Runtime::gate`). A request that stores intent then takes a storage
    /// capture (`Runtime::capture_intent`); nothing is admitted to write.
    pub(super) fn in_low_power(&self) -> bool {
        self.gate() == Gate::Low
    }

    /// The gate now, for `overtaken_by_gate` after the request.
    pub(super) fn gate_mark(&self) -> GateMark {
        GateMark(self.gate())
    }

    /// Whether an explicit request that stored its intent and then lost its
    /// own write admission was overtaken by the low-power gate: the gate set
    /// at some time after `mark`, even if it cleared again, nothing of the
    /// request can have been submitted (`submission_possible`), and the store
    /// is not fenced. It is the rule by which `reconcile_locked` takes a
    /// refused step as a deferral, so the request replies as one stored while
    /// the gate was set and owes the same reconciliation
    /// (`owe_stored_intent`). A request that may have submitted keeps its own
    /// result.
    pub(super) fn overtaken_by_gate(
        &self,
        mark: GateMark,
        storage: &Storage,
        request: &str,
    ) -> bool {
        !submission_possible(storage, request) && !storage.pending() && self.gate() != mark.0
    }

    /// An explicit request stored new intent while the gate was set: the
    /// pair owes a reconciliation at every class with no attempt spent, as
    /// after a save's change. The lifecycle tick after the boundary that
    /// clears the gate runs it for the reporting pair, or else the first
    /// report after it. A persistent override or a clear changes every class.
    pub(super) fn owe_stored_intent(&self, (sid, session): (&str, u32)) {
        self.owe((sid.to_owned(), session), Debt::owed(Classes::ALL));
    }

    /// The retries the pair has spent while it owes a reconciliation, or
    /// `None` once it is settled.
    #[cfg(test)]
    pub(in super::super) fn owed_attempts(&self, sid: &str, session: u32) -> Option<u8> {
        match self.debt(&(sid.to_owned(), session)) {
            Debt::Owed { attempts, .. } => Some(attempts),
            Debt::Settled => None,
        }
    }

    /// Records what the pair `key` owes. A settled debt ends its user's
    /// startup reconciliation once a starting reconciliation compared a
    /// reading (`Startup`), however the debt settled: applied, excluded, or
    /// out of retries.
    pub(super) fn owe(&self, key: (String, u32), debt: Debt) {
        if debt == Debt::Settled
            && let Some(startup) = self.startup.borrow_mut().get_mut(&key.0)
        {
            *startup = Startup::Ended;
        }
        self.debts.borrow_mut().insert(key, debt);
    }

    /// A capture refused because fresh competing-controller absence is not
    /// confirmed: the pair's debt is dropped and its user's startup ends,
    /// whatever was compared. Strict thermal control stays advisory and
    /// keeps nothing owed for after the controller leaves, which would
    /// overwrite what the user chose there (Architecture rule 5).
    ///
    /// This is native's stricter write admission, not managed parity.
    /// Managed has no controller clause on startup or ordinary thermal
    /// transitions (`ApplyFullTransitionAsync`,
    /// `ScopedThermalWriteBlockReason`), so its startup under a controller
    /// writes the saved profile. Native refuses every write under a
    /// controller. A controller-detection failure, or an inventory slower
    /// than 1 s, returns the same denial with no controller present.
    ///
    /// With nothing of OpenBlade's own confirmed, the next power change then
    /// compares only its new intent. Unless firmware already shows it, the
    /// user sees `externalChanges` [0] although nothing changed outside
    /// OpenBlade. That heals when a later intent matches firmware; otherwise
    /// it stays paused until an explicit request, another user's activation
    /// or a restart.
    ///
    /// Only captures take this path. A step whose own authorization a
    /// controller refuses inside `reconcile_locked` spends a retry instead.
    pub(super) fn drop_for_controller(&self, sid: &str, session: u32) {
        self.startup
            .borrow_mut()
            .insert(sid.to_owned(), Startup::Ended);
        self.owe((sid.to_owned(), session), Debt::Settled);
    }

    /// Whether the user `sid` is still in its startup reconciliation
    /// (`Startup`). A report, save or lifecycle cause that replaces its debt
    /// before it settled is part of it.
    fn starting(&self, sid: &str) -> bool {
        self.startup.borrow().get(sid) != Some(&Startup::Ended)
    }

    /// What the pair `key` owes; startup is owed until it is settled.
    fn debt(&self, key: &(String, u32)) -> Debt {
        self.debts
            .borrow()
            .get(key)
            .copied()
            .unwrap_or(Debt::owed(Classes::ALL))
    }

    /// Whether the saved fan intent is manual at any of `classes`.
    fn next_manual(
        &self,
        storage: &Storage,
        sid: &str,
        session: u32,
        classes: Classes,
    ) -> io::Result<bool> {
        let layer = storage.profile_layer(&self.foreground(sid, session))?;
        for class in classes.iter() {
            if storage.desired_manual_fans(class, layer.at(class)?.as_ref())? {
                return Ok(true);
            }
        }
        Ok(false)
    }
}
