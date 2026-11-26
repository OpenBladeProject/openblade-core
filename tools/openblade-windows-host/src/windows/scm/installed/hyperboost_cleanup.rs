//! The Service's own exit of owned HyperBoost at the coordinated Stop or
//! shutdown and at a confirmed suspend (managed
//! `RestoreOwnedHyperBoostForLifecycleCoreAsync`, called from
//! `PrepareForShutdownAsync` and `PrepareForLowPowerCoreAsync`), under the
//! Service's cleanup admission (`Cleanup`).
//!
//! Neither boundary admits an ordinary write. SCM STOP and SHUTDOWN revoke
//! the loaded origin before the drain (`scm_host::installed_notice`), and
//! the low-power gate fences the ordinary epoch, so no console capture
//! authorizes there. The exit therefore holds the admission the keyboard
//! release and the fan handback hold, and like them it fixes what it may
//! write (`ExitPermit`):
//!
//! - Only managed's unowned exit, the `hyperboost-restore-balanced` pair
//!   (`RESTORE_BASE`), from a fresh reading of HyperBoost that the durable
//!   journal says native owns. No cleanup resolves or applies saved intent
//!   (the fan handback applies no saved destination either), and AGENTS.md
//!   has a shutdown or low-power cleanup reach the firmware handoff directly
//!   and leave saved settings for the next active reconciliation. Managed
//!   restores the saved intent there (`restoreUserIntent: true`); native
//!   leaves Balanced as OpenBlade's own and the session that entered owes
//!   its reconciliation, which the tick after the resume runs. After a Stop
//!   the next start's activation applies the saved intent.
//! - Every other admission of an explicit exit stays: the cleanup admission
//!   fresh before every row and exchange (origin and package trust, the
//!   scope's lifecycle fence, known Windows power, competing-controller
//!   absence, the exact original custody), managed's ownership journal before
//!   the request and before each setter, no outstanding owned fan change, no
//!   unsettled journal row, the GPU thermal fence, a classified power class
//!   that agrees with Windows on each setter's own reading, the durable row
//!   before each setter, the exact acknowledgement and the independent
//!   readback. The cooling pad is observed only for an entry, as in an
//!   explicit exit.
//! - The recipe is paced before its first row to the cleanup's deadline
//!   (`Pace::covers_before_rows`), so one that cannot fit writes nothing.
//! - Nothing is replayed. A setter that may have been submitted leaves its
//!   row unsettled, which fences every later write, this exit included.
//!
//! The rows are journaled for the session that entered, which the owning
//! entry row names (`Storage::hyperboost_owner`): the Service has no caller
//! of its own, and the history admits no row without a user session. The
//! store is the latest capture's (`Runtime::captured`). A process that
//! captured none reads no ownership and exits nothing. An entry another user
//! than that store's made is refused, and a Stop reports it.
use super::*;
use crate::installed::readiness::thermal_storage::Row;

/// The journal step name of a cleanup exit (`triggers::step_id`).
const CLEANUP_STEP: &str = "hyperboost-cleanup";

/// How one cleanup exit of owned HyperBoost ended.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(in super::super::super) enum CleanupExit {
    /// Nothing to exit: no HyperBoost probes, no store captured in this
    /// process, its journal does not leave HyperBoost owned, or a reading
    /// since the entry showed firmware out of HyperBoost and the journal
    /// could not record that here. Nothing was read from firmware or
    /// submitted.
    NotOwned,
    /// HyperBoost is off by the exit's own readback of Balanced with
    /// Automatic fans.
    Exited,
    /// Firmware had already left HyperBoost, by this exit's reading or an
    /// earlier one: nothing was written to it and ownership settled in the
    /// journal (`Storage::settle_hyperboost`).
    Settled,
    /// The time ran out before ownership could be read. Nothing was sent.
    Exhausted,
    /// Native owns HyperBoost and no setter was submitted: the cleanup
    /// admission, managed's journal, an owned fan change, the GPU fence, the
    /// power class, the journal's capacity or the recipe's pace refused it,
    /// or the entry is another user's. Ownership stays in the journal.
    Refused,
    /// A thermal submission is unsettled: this exit's setter may have been
    /// submitted unconfirmed, or an earlier row already fenced the store.
    /// Nothing more is written and nothing is replayed.
    Unresolved,
}

/// The exit's permit. It holds the cleanup admission and can journal one row
/// chain only: the Balanced restore of the request it was captured for, after
/// `resolve` accepted a fresh HyperBoost reading the journal says native
/// owns. It stores nothing and resolves no saved intent.
struct ExitPermit {
    cleanup: Arc<dyn Cleanup>,
    proof: ReadAdmission,
    storage: Arc<Storage>,
    probes: Probes,
    request: String,
    owner: String,
    intent: String,
    caller_pid: u32,
    session: u32,
    // Built at the cleanup, as the fan handback's is (`Runtime::gpu_fence`).
    gpu_fence: GpuFence,
    drift: Arc<Mutex<thermal_drift::Tracker>>,
    fatal: Arc<Mutex<Option<&'static str>>>,
    accepted: AtomicBool,
    /// A fresh reading found firmware already out of HyperBoost.
    exited: AtomicBool,
}
impl ExitPermit {
    fn row(&self) -> Row<'_> {
        Row {
            request: &self.request,
            owner: &self.owner,
            destination: RESTORE_BASE,
            intent: &self.intent,
            requested: &None,
            caller_pid: self.caller_pid,
            session: self.session,
        }
    }
    fn append(&self, progress: Progress) -> io::Result<()> {
        self.storage
            .record_cleanup(self.cleanup.scope(), self.row(), progress)
            .inspect_err(|error| remember_failure(&self.fatal, error))
    }
}
impl Permit for ExitPermit {
    fn authorize(&self) -> Option<ReadAdmission> {
        verify_retained_failure(&self.fatal).ok()?;
        self.cleanup
            .verify_health()
            .inspect_err(|error| remember_failure(&self.fatal, error))
            .ok()?;
        (self.cleanup.authorize() == Some(self.proof)).then_some(self.proof)
    }
    fn cleanup(&self) -> bool {
        true
    }
    fn windows_on_battery(&self) -> Option<bool> {
        self.cleanup.on_battery()
    }
    fn thermal_fenced(&self, budget: std::time::Duration) -> bool {
        self.gpu_fence.as_ref().is_some_and(|fenced| fenced(budget))
    }
    // Managed's unowned exit: each setter is admitted on a classified class
    // that agrees with Windows, battery included, and a class change keeps
    // it, since nothing was resolved from saved intent.
    fn restores_only(&self) -> bool {
        true
    }
    fn destination_holds(&self, _power: i32) -> bool {
        true
    }
    // Managed's journal again before each setter, as for an explicit exit
    // (`RequestPermit::hyperboost_admits_setter`).
    fn admits_setter(&self) -> bool {
        self.probes.journal_safe()
    }
    fn resolve(
        &self,
        power: i32,
        baseline: ObservedPerformance,
    ) -> io::Result<Option<Destination>> {
        verify_retained_failure(&self.fatal)?;
        if self.accepted.load(Ordering::Acquire) {
            return Err(io::ErrorKind::PermissionDenied.into());
        }
        // Managed owns the recovery of its own journal.
        if !self.probes.journal_safe() {
            return Ok(None);
        }
        // Ownership is read again under the job: only HyperBoost the journal
        // still says native owns is exited, and no row follows an unsettled
        // one.
        let left = self
            .drift
            .lock()
            .map_or(true, |drift| drift.left_hyperboost());
        if left || self.storage.pending() || !self.storage.owns_hyperboost() {
            return Ok(None);
        }
        // Firmware already out of HyperBoost ends the ownership on this
        // reading alone, before any power check: the settlement sends no
        // setter, so the power class has no part in it, and managed clears
        // its ownership at any observation with HyperBoost inactive
        // (18394-18405), whatever the source. Refusing here on an adapter
        // 07/8C cannot classify would leave the journal naming an entry
        // firmware has left, and a HyperBoost entered outside OpenBlade
        // before the next start would read as owned.
        if baseline != ObservedPerformance::HyperBoostObserved {
            self.exited.store(true, Ordering::Release);
            return Ok(None);
        }
        // The exit itself needs a classified source that Windows agrees
        // with, as managed's does (`RequestPermit::resolve`,
        // `battery_reading_contradicts_windows`); each setter is admitted
        // again on its own reading.
        if power == UNCLASSIFIED_EXTERNAL || (power == 1 && self.cleanup.on_battery() != Some(true))
        {
            return Ok(None);
        }
        // The history admits no performance row while an owned fan change is
        // outstanding (`RequestPermit::resolve_hyperboost`).
        if self.storage.outstanding_fan() {
            return Ok(None);
        }
        self.accepted.store(true, Ordering::Release);
        Ok(Some(RESTORE_BASE))
    }
    fn record(&self, progress: Progress) -> io::Result<()> {
        verify_retained_failure(&self.fatal)?;
        if !self.accepted.load(Ordering::Acquire) {
            return Err(io::ErrorKind::PermissionDenied.into());
        }
        self.authorize().ok_or(io::ErrorKind::PermissionDenied)?;
        self.append(progress)?;
        // A journaled intent precedes its setter: until a reading confirms
        // it, the Balanced it may leave is OpenBlade's own write
        // (`RequestPermit::record`).
        if progress.is_intent() {
            self.drift
                .lock()
                .map_err(|_| io::ErrorKind::Other)?
                .submitted_from(RESTORE_BASE, Some(ObservedPerformance::HyperBoostObserved));
        }
        Ok(())
    }
    fn failed(&self, error: &io::Error) {
        remember_failure(&self.fatal, error);
    }
    fn retired(&self) -> io::Result<Settlement> {
        verify_retained_failure(&self.fatal)?;
        // A Prepared row no setter followed settles as known not submitted,
        // which custody admits without a live admission (`Storage::record`).
        self.cleanup.verify_health()?;
        let prepared_only = self.accepted.load(Ordering::Acquire)
            && self.storage.last_request().as_deref() == Some(&self.request)
            && self.storage.progress() == Some(Progress::Prepared as u8);
        let result = if prepared_only {
            self.append(Progress::KnownNotSubmitted)
        } else {
            Ok(())
        };
        self.cleanup.verify_health()?;
        match result {
            Ok(()) => Ok(Settlement::Complete),
            Err(error) if is_fatal_admission(&error) => Err(error),
            Err(error) => {
                self.storage.quarantine_settlement()?;
                Ok(Settlement::Unresolved(error))
            }
        }
    }
}

impl Runtime {
    /// One exit of owned HyperBoost under the Service's cleanup admission of
    /// `scope`, whose job ends by `deadline`: the end of the Stop drain, or
    /// of the suspend preparation. Only fatal admission evidence is an error.
    pub(in super::super) async fn exit_owned_hyperboost_for(
        &self,
        mailbox: &MailboxSource,
        scope: CleanupScope,
        deadline: Instant,
    ) -> io::Result<CleanupExit> {
        let Some(probes) = self.hyperboost.get().cloned() else {
            return Ok(CleanupExit::NotOwned);
        };
        let MailboxSource::Reader(reader) = mailbox else {
            return Ok(CleanupExit::NotOwned);
        };
        let Ok(_serial) = tokio::time::timeout_at(deadline, self.gate.lock()).await else {
            return Ok(CleanupExit::Exhausted);
        };
        // Join active work before reading ownership, as the fan handback
        // does: a job an earlier caller dropped may still journal rows.
        match tokio::time::timeout_at(deadline, reader.drain_active()).await {
            Err(_) => return Ok(CleanupExit::Exhausted),
            Ok(Err(error)) if is_fatal_admission(&error) => return Err(error),
            Ok(Err(_)) => return Ok(CleanupExit::Unresolved),
            Ok(Ok(())) => {}
        }
        // Nothing is verified before there is an exit to make: a Stop with
        // nothing owned reports no failure of its own, as the fan handback
        // returns before its checks when no fan is owned.
        let Some(storage) = self.captured.borrow().clone() else {
            return Ok(CleanupExit::NotOwned);
        };
        if !storage.owns_hyperboost() {
            return Ok(CleanupExit::NotOwned);
        }
        // A reading since the entry showed firmware out of HyperBoost
        // (`Tracker::left_hyperboost`), so nothing is owned whatever
        // firmware shows now: nothing is read or exited, and the outcome is
        // the unowned one. Only the journal still names the entry; it is
        // settled when this admission allows the rows.
        let contradicted = self.left_hyperboost();
        let exit = self
            .exit_recorded_hyperboost(reader, scope, deadline, (storage, probes), contradicted)
            .await?;
        Ok(if contradicted && exit != CleanupExit::Settled {
            CleanupExit::NotOwned
        } else {
            exit
        })
    }

    /// `exit_owned_hyperboost_for` once the history shows an owned entry,
    /// under the held gate. `contradicted` only settles the journal.
    async fn exit_recorded_hyperboost(
        &self,
        reader: &super::super::super::mailbox_read::ReadSource,
        scope: CleanupScope,
        deadline: Instant,
        (storage, probes): (Arc<Storage>, Probes),
        contradicted: bool,
    ) -> io::Result<CleanupExit> {
        if storage.pending() {
            return Ok(CleanupExit::Unresolved);
        }
        let Some(entered) = storage.hyperboost_owner() else {
            return Ok(CleanupExit::Refused);
        };
        let Some(cleanup) = self.capture_cleanup(reader, scope)? else {
            return Ok(CleanupExit::Refused);
        };
        cleanup.verify_health()?;
        let Some(proof) = cleanup.authorize() else {
            return Ok(CleanupExit::Refused);
        };
        let permit = Arc::new(ExitPermit {
            cleanup,
            proof,
            storage: storage.clone(),
            probes,
            request: triggers::step_id(&triggers::lifecycle::lifecycle_request(), CLEANUP_STEP),
            owner: format!("{:x}", Sha256::digest(proof.generation)),
            intent: Requested::new(None)?.intent,
            caller_pid: entered.caller_pid,
            session: entered.session,
            gpu_fence: self.gpu_fence(),
            drift: self.drift.clone(),
            fatal: self.fatal.clone(),
            accepted: AtomicBool::new(false),
            exited: AtomicBool::new(false),
        });
        if contradicted {
            return self.settle_exited_cleanup(&permit, deadline);
        }
        let result = reader
            .mutate_cleanup_in(permit.clone(), scope, deadline)
            .await;
        verify_retained_failure(&self.fatal)?;
        let readback = match result {
            Err(error) if is_fatal_admission(&error) => return Err(error),
            result => result.ok().flatten(),
        };
        // The exit's own readback of Balanced with Automatic fans, which its
        // Confirmed row recorded: OpenBlade's own (`Runtime::apply_locked`).
        if let Some(readback) = readback.filter(|readback| confirms(*readback, RESTORE_BASE))
            && storage.last_request().as_deref() == Some(&permit.request)
            && storage.progress() == Some(Progress::Confirmed as u8)
        {
            let mut drift = self.drift.lock().map_err(|_| io::ErrorKind::Other)?;
            drift.observe(readback);
            drift.own_performance(RESTORE_BASE);
            drop(drift);
            // The saved intent was not applied, so the session that entered
            // owes its reconciliation, as after intent stored in low power
            // (`owe_stored_intent`): the tick after the boundary that clears
            // the gate runs it, and nothing runs before. After a Stop the
            // next start's own activation owes it instead.
            self.owe_stored_intent((&entered.sid, entered.session));
            return Ok(CleanupExit::Exited);
        }
        if permit.exited.load(Ordering::Acquire) {
            // The reading ends the ownership for this process whether or
            // not the journal can record it now.
            self.drift
                .lock()
                .map_err(|_| io::ErrorKind::Other)?
                .leave_hyperboost();
            return self.settle_exited_cleanup(&permit, deadline);
        }
        Ok(
            if triggers::submission_possible(&storage, &permit.request) {
                CleanupExit::Unresolved
            } else {
                CleanupExit::Refused
            },
        )
    }

    /// The read-only settlement of a cleanup exit whose fresh reading, or an
    /// earlier one, found firmware out of HyperBoost: the journal records
    /// the exit's HyperBoost row Prepared and KnownNotSubmitted, under the
    /// same cleanup admission, authorized again, and only before the
    /// deadline. Without either the journal keeps the entry for a later
    /// exit to settle. Nothing is owned in either case, since the reading
    /// ended the ownership for this process, so an unrecorded end is
    /// `NotOwned` and never a HyperBoost the Stop left on.
    ///
    /// Unlike the battery boundary's settlement (`Runtime::settle_exited`),
    /// the reading does not become OpenBlade's own. No transition follows it
    /// here, and managed only retires its ownership journal at a lifecycle
    /// boundary that finds HyperBoost inactive
    /// (`RetireInactiveHyperBoostJournalIfPresentAsync`), so what firmware
    /// left stays an external change for the next reconciliation to compare.
    fn settle_exited_cleanup(
        &self,
        permit: &ExitPermit,
        deadline: Instant,
    ) -> io::Result<CleanupExit> {
        let storage = &permit.storage;
        if Instant::now() >= deadline || storage.pending() || storage.outstanding_fan() {
            return Ok(CleanupExit::NotOwned);
        }
        permit.cleanup.verify_health()?;
        if permit.authorize().is_none() {
            return Ok(CleanupExit::NotOwned);
        }
        match storage.settle_hyperboost(
            &permit.request,
            &permit.owner,
            permit.caller_pid,
            permit.session,
            Some(permit.cleanup.scope()),
        ) {
            Ok(()) => Ok(CleanupExit::Settled),
            Err(error) if is_fatal_admission(&error) => Err(error),
            Err(_) => Ok(CleanupExit::NotOwned),
        }
    }

    /// Owned Stop's exit of owned HyperBoost, paced to `deadline`, what is
    /// left of the source drain. An exit that did not leave HyperBoost fails
    /// the Stop as an unconfirmed fan handback does, so it is reported; the
    /// ownership stays in the journal for the next start.
    pub(in super::super::super) async fn stop_hyperboost(
        &self,
        mailbox: &MailboxSource,
        deadline: Instant,
    ) -> io::Result<()> {
        match self
            .exit_owned_hyperboost_for(mailbox, CleanupScope::OwnedStop, deadline)
            .await?
        {
            CleanupExit::NotOwned | CleanupExit::Exited | CleanupExit::Settled => Ok(()),
            CleanupExit::Unresolved => Err(io::Error::other(
                "Owned HyperBoost was not exited at Stop: a thermal submission remains unresolved; evidence is retained.",
            )),
            CleanupExit::Exhausted | CleanupExit::Refused => Err(io::Error::other(
                "Owned HyperBoost was not exited at Stop; its ownership is retained.",
            )),
        }
    }

    /// Owned Stop after origin loss, which cannot authorize the exit.
    pub(in super::super::super) fn refuse_stop_hyperboost(&self) -> io::Result<()> {
        if self.hyperboost.get().is_some()
            && self
                .captured
                .borrow()
                .as_ref()
                .is_some_and(|storage| self.owns_hyperboost(storage))
        {
            Err(io::Error::other(
                "Unexpected origin loss cannot authorize the owned HyperBoost exit.",
            ))
        } else {
            Ok(())
        }
    }
}
