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
use super::fan::FanRequest;
use super::profiles::Updated;
use super::*;
use crate::installed::readiness::thermal_storage::ProfileSave;
use openblade_domain::profile_set::{ProfileSet, update::STORED_MESSAGE};
use tokio::time::timeout_at;

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
#[derive(Clone, Copy)]
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
    // The classes the readings of the steps that needed a change found.
    read: Classes,
    // A step had not finished at the bound (`RECONCILE_MARGIN`).
    unfinished: bool,
    message: String,
    snapshot: Option<Snapshot>,
}
impl Reconciled {
    /// The debt a report's reconciliation of `classes` leaves after
    /// `attempts` earlier transient refusals: settled, unless it may be
    /// retried and `RETRIES` allows another.
    fn after(&self, classes: Classes, attempts: u8) -> Debt {
        if self.retry && attempts < RETRIES {
            Debt::Owed {
                classes,
                attempts: attempts + 1,
            }
        } else {
            Debt::Settled
        }
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
                expires: Instant::now() + std::time::Duration::from_secs(15),
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
        let captured = self.capture(&sid, caller.session, reader.custody());
        let selected = captured.and_then(|(storage, fresh)| {
            let (next, changed) = storage.select_application(&prior, path.as_deref())?;
            Ok((storage, fresh, next, changed))
        });
        let (storage, fresh, next, changed) = match selected {
            Ok(value) => value,
            Err(error) if is_fatal_admission(&error) => return Err(error),
            Err(error) => {
                record(prior);
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
        // A changed pair owes a new reconciliation at every class.
        let debt = if changed {
            Debt::owed(Classes::ALL)
        } else {
            self.debt(&key)
        };
        self.debts.borrow_mut().insert(key.clone(), debt);
        let Debt::Owed {
            classes: owed,
            attempts,
        } = debt
        else {
            return stored(idle);
        };
        let unapplied = if changed { RECORDED } else { idle };
        // Managed applies only on classified power with a verified device and
        // writes not blocked (`ActivePowerSource`, `DeviceVerified`,
        // `WritesBlocked`); every write still passes its own admission, and
        // each step refuses an adapter its own reading finds unclassified.
        // A refusal here reads no firmware and leaves the debt as it was.
        let classes = owed.intersection(Classes::admitted(fresh.as_ref()));
        // A fenced store, such as an override document changed outside the
        // Service, names its storage reconciliation before any submission.
        if storage.pending() {
            return Ok((
                Outcome::blocked(format!("{unapplied} {}", storage.pending_reason()?)),
                None,
            ));
        }
        fresh.verify_health()?;
        let admitted = !classes.is_empty() && fresh.authorize().is_some();
        fresh.verify_health()?;
        if !admitted {
            return stored(unapplied);
        }
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
                },
            )
            .await?;
        self.debts
            .borrow_mut()
            .insert(key, reconciled.after(owed, attempts));
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
            ProfileSave::Stored => {}
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
        let prior = self.debt(&key);
        // The save's change, with what the pair owed before it.
        let combined = match prior {
            Debt::Owed { classes, .. } => classes.union(changed),
            Debt::Settled => changed,
        };
        self.debts
            .borrow_mut()
            .insert(key.clone(), Debt::owed(combined));
        // Managed also needs a verified device with writes not blocked; every
        // write still passes its own admission. A refusal here reads no
        // firmware and leaves the change owed to the next report.
        fresh.verify_health()?;
        let admitted = !storage.pending() && fresh.authorize().is_some();
        fresh.verify_health()?;
        if !admitted {
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
                },
            )
            .await?;
        let debt = if reconciled.unchanged {
            // The adapter reports a class the save did not change: managed
            // compares there and applies nothing, and what the pair owed
            // before the save is left as it was.
            prior
        } else if reconciled.retry {
            // The save was the first attempt; later reports retry.
            Debt::Owed {
                classes: combined,
                attempts: 1,
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
        self.debts.borrow_mut().insert(key, debt);
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
        } = step;
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
        // The trigger answers by this bound even when a step's transfer has
        // not retired (`RECONCILE_MARGIN`).
        let bound = deadline + crate::service_frame::RECONCILE_MARGIN;
        // Each step, and whether it may have submitted.
        let mut steps: Vec<(Stepped, bool)> = Vec::with_capacity(order.len());
        let mut unfinished = false;
        for kind in order {
            if storage.pending() {
                break;
            }
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
            let possible = storage.submission_possible()
                || (storage.last_request().as_deref() == Some(id.as_str())
                    && storage.progress() != Some(Progress::KnownNotSubmitted as u8));
            match stepped {
                Ok(stepped) => steps.push((stepped?, possible)),
                Err(_) => {
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
        let submitted = steps.iter().any(|(_, possible)| *possible);
        let ran = !unfinished && steps.len() == order.len();
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
        Ok(Reconciled {
            applied,
            submitted,
            unchanged,
            retry,
            read,
            unfinished,
            message,
            snapshot: steps.into_iter().rev().find_map(|(s, _)| s.snapshot),
        })
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
