//! Keyboard Driver-mode ownership on exact RZ09-0581
//! (`service-keyboard-driver-mode-v1`): the managed maintenance
//! (`ServiceControlRuntime` 15 s step, `ProductionLightingPort.MaintainDriverModeAsync`),
//! the low-power release (`PrepareForLowPowerCoreAsync`'s
//! `ReleaseDriverModeAsync("low power", generation)`) and the shutdown release
//! (`ReleaseDriverModeCoreAsync("shutdown")`).
//!
//! - Acquire and maintain read 00/84 first. A keyboard already in Driver mode
//!   publishes true with no write and no claim. From Normal, ownership is
//!   claimed before the 00/04:0300 setter can be submitted; a refusal before
//!   submission (managed `KnownNotApplied`) clears the claim and any later
//!   failure keeps it. The setter's exact acknowledgement and an independent
//!   00/84 readback must both confirm.
//! - An acquire setter that may have been submitted without that confirmation
//!   is Indeterminate: the step publishes null and the owner sends no further
//!   acquire or maintain exchange in this process; nothing is ever replayed.
//!   The read job's own 00/84 cadence keeps publishing the confirmed mode, and
//!   a release still restores Normal after a fresh read of Driver.
//! - Each step captures the console user's ordinary write admission, the
//!   candidate the lifecycle tick uses: fresh exact-device,
//!   competing-controller, AC line, session, SID and lifecycle-epoch checks,
//!   which refuse in low power. With nobody signed in, or a locked session,
//!   the step is refused and retried at the next one, and the last confirmed
//!   status stays. Managed maintains without a user; native adds no acquire
//!   authority, and the function row has no user to serve then.
//! - After the startup call, a step starts no job when a client's
//!   ObserveState read job confirmed Driver under the current read admission
//!   within the cadence. Otherwise each due step runs a mutation job that
//!   reads 00/84 first. A step that failed gets
//!   one recovery per failure streak: a fresh capture and read-before-write.
//!   A step refused before any exchange records nothing and spends no
//!   recovery. Success or a lost device resets the latch.
//! - Ownership lives in memory, as managed's does. Each release follows
//!   managed: a fresh 00/84 read of Driver writes Normal once, whoever set
//!   it; a read of Normal ends an owned claim as a foreign change. A release
//!   holds the Service's own cleanup admission (`Cleanup`), captured fresh
//!   for its boundary, never a console capture: Windows logs users off before
//!   a system shutdown reaches services, and a suspend may start from the
//!   lock screen. Each boundary releases at most once: the shutdown, and each
//!   suspend generation. A release that may have submitted Normal without
//!   confirming it ends every later release and acquire write in this
//!   process. In low power the step reads and writes nothing until Windows
//!   reports the system active again, so the 15 s cadence reacquires then.
use super::super::mailbox_read::ReadSource;
use super::*;
use crate::discovery::performance::{DeviceModeJob, DeviceModeRead, DeviceModeStep};
use openblade_protocol::device_mode::DeviceMode;
use std::{cell::Cell, time::Duration};

/// Managed `ServicePeriodicObservationLoop.ObservationInterval`, the read
/// job's own 00/84 cadence.
pub(in super::super) use super::super::mailbox_read::DRIVER_MODE_INTERVAL as INTERVAL;
/// One acquire job from its start: three exchanges with their settles and
/// checkpoints.
const JOB_BUDGET: Duration = Duration::from_secs(2);
/// The shutdown release's bound for the gate wait and the join of active
/// work (`ReadSource::drain_active`), from the release's start. A read job is
/// joined within it; a write job still running exhausts it. A low-power
/// release instead waits for the rest of its share, as managed's
/// `ReleaseDriverModeAsync` waits on its operation gate for the whole
/// preparation window, so a request in flight at the boundary does not cost
/// it the release: the request is refused at its next authorization.
const ENTRY_BUDGET: Duration = Duration::from_millis(150);
/// The release job from its start: its baseline read, setter and readback.
/// By count, a healthy one takes about 0.28 s besides its three exchanges:
/// about 22 fresh admissions at 8-9 ms each and three 30 ms settles, an
/// estimate pending an attended timing. The allowance of twice
/// `FRAME_EXCHANGE` per exchange plus those admissions and settles is about
/// 1.09 s, which does not fit the share below; the job's measured pace still
/// refuses the setter when the acknowledgement and readback cannot finish in
/// time (`Pace::covers`).
const RELEASE_JOB_BUDGET: Duration = Duration::from_millis(700);
/// What a share keeps after the job for the lane to retire it.
const RETIRE_SLACK: Duration = Duration::from_millis(50);
/// The keyboard's whole share of the shutdown drain from the release's start:
/// the entry, the synchronous admissions, the job and its retirement. Native
/// accepts SERVICE_CONTROL_SHUTDOWN, not PRESHUTDOWN, so at system shutdown
/// the whole Stop must end within WaitToKillServiceTimeout, about 5 s, as
/// managed's 5 s shutdown cleanup budget does. The listener's 2 s, this
/// share and the fans' unchanged 2 s (`readiness::SOURCE_DRAIN_TIMEOUT`)
/// are 4.9 s, so the keyboard has what the fans leave. The low-power release
/// has at most this share too (`low_power::KEYBOARD_SHARE`). The figures are
/// estimates; RZ09-0581 needs an attended timing of a real release.
pub(in super::super) const SHUTDOWN_BUDGET: Duration = Duration::from_millis(900);
const _: () = assert!(
    SHUTDOWN_BUDGET.as_nanos()
        == ENTRY_BUDGET.as_nanos() + RELEASE_JOB_BUDGET.as_nanos() + RETIRE_SLACK.as_nanos()
);

/// How a release ended. Only a fatal admission error fails the Stop or the
/// low-power preparation. The Service has no diagnostics sink yet, so the
/// outcome is kept for tests, and the process exit drops it, as an
/// unregistration failure is (docs/windows-native-thermal-mutation.md).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(in super::super) enum Release {
    /// Normal confirmed by the exact acknowledgement and readback.
    Restored,
    /// A fresh read found Normal and nothing was claimed.
    NotDriver,
    /// A fresh read found Normal although OpenBlade had claimed Driver mode.
    ForeignChange,
    /// A fan row was not settled (managed
    /// `FanStopController.BlocksOrdinaryHardware`). Nothing was sent. Managed
    /// retries once after the fan handback when that cleared the block; here
    /// every fan job has joined by then (`ReadSource::drain_active`), and
    /// the handback never settles an unsettled fan row, so nothing could
    /// clear it and there is no retry.
    FanBlocked,
    /// A laptop-control journal row or earlier write is not settled (managed
    /// `RequiresReconciliation`). Nothing was sent.
    Unsettled,
    /// No cleanup admission: none composed, a competing controller, unknown
    /// power, origin or custody trust lost, a lifecycle change since the
    /// boundary, or a write lease that never activated. Nothing was sent.
    Unadmitted,
    /// Nothing was sent: the share ran out first, or the job's measured
    /// pace could not fit the setter, its acknowledgement and the readback.
    Exhausted,
    /// The job ran without confirming Normal; a setter may have been
    /// submitted.
    Unconfirmed,
    /// An earlier release may have submitted Normal unconfirmed, so this one
    /// sent nothing: the uncertain write is never replayed.
    Uncertain,
    /// This boundary's release job already ran; it is never repeated.
    Attempted,
    /// Origin loss cannot authorize owned cleanup.
    OriginLost,
}

/// Which boundary a release serves. Each releases at most once.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(in super::super) enum Boundary {
    /// One suspend generation of the low-power gate.
    LowPower(u64),
    /// The coordinated Stop or system shutdown.
    Shutdown,
}
impl Boundary {
    fn scope(self) -> CleanupScope {
        match self {
            Self::LowPower(generation) => CleanupScope::LowPower { generation },
            Self::Shutdown => CleanupScope::OwnedStop,
        }
    }
}

/// A console write capture and the store it was captured with.
type ConsoleCapture = (Arc<Storage>, Arc<dyn Fresh>);

/// What OpenBlade has claimed of the keyboard's Driver mode. The worker
/// thread claims through it, so it is shared with each job.
#[derive(Default)]
struct Ownership {
    owns: AtomicBool,
    // Acquire claims made, so a test can tell a cleared claim from none.
    #[cfg(test)]
    claims: std::sync::atomic::AtomicUsize,
}

/// The owner's state between steps.
#[derive(Default)]
pub(super) struct Keyboard {
    ownership: Arc<Ownership>,
    /// An acquire setter may have been submitted unconfirmed: no further
    /// acquire or maintain exchange in this process.
    indeterminate: Cell<bool>,
    /// A release setter may have been submitted unconfirmed: no further
    /// release, acquire or maintain exchange in this process.
    uncertain: Cell<bool>,
    /// The recovery of the current failure streak has run.
    recovered: Cell<bool>,
    /// The startup call's job has run.
    started: Cell<bool>,
    /// When the next step is due; `None` until the first eligible tick.
    due: Cell<Option<Instant>>,
    /// The store of the latest console capture, whose unsettled journal row
    /// blocks a release (managed `RequiresReconciliation`).
    storage: RefCell<Option<Arc<Storage>>>,
    /// The shutdown release job started.
    shut_down: Cell<bool>,
    /// The suspend generation whose release job last started.
    suspended: Cell<Option<u64>>,
    /// A release job its caller dropped before reading how far it got (a
    /// Stop during the low-power release). The next release reads it after
    /// joining active work.
    inflight: RefCell<Option<Arc<DriverModePermit>>>,
    /// The last release's outcome.
    release: Cell<Option<Release>>,
}

/// How one acquire step ended.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Acquired {
    /// Driver mode confirmed: read, or written and read back.
    Driver,
    /// The read job holds Driver for this admission; no job ran.
    Current,
    /// Nothing confirmed and nothing possibly submitted unconfirmed.
    Failed,
    /// The setter may have been submitted unconfirmed.
    Indeterminate,
}

/// The admission one Driver-mode job holds. Its type fixes what the job may
/// write: an acquire writes Driver from Normal under a console capture, and a
/// release writes Normal from Driver under the Service's cleanup admission.
enum Authority {
    Acquire(Arc<dyn Fresh>),
    Release(Arc<dyn Cleanup>),
}

/// One Driver-mode job's permit: its admission and the job. It keeps no
/// journal row.
struct DriverModePermit {
    authority: Authority,
    job: DeviceModeJob,
    fatal: Arc<Mutex<Option<&'static str>>>,
}
impl DriverModePermit {
    fn acquire(
        fresh: Arc<dyn Fresh>,
        ownership: Arc<Ownership>,
        fatal: &Arc<Mutex<Option<&'static str>>>,
    ) -> Self {
        Self {
            authority: Authority::Acquire(fresh),
            job: DeviceModeJob::new(DeviceMode::Driver, Some(DeviceMode::Normal), move || {
                ownership.owns.store(true, Ordering::Release);
                #[cfg(test)]
                ownership.claims.fetch_add(1, Ordering::AcqRel);
            }),
            fatal: fatal.clone(),
        }
    }
    /// The only constructor a cleanup admission reaches: Normal, written only
    /// after a fresh read of Driver, with no claim.
    fn release(cleanup: Arc<dyn Cleanup>, fatal: &Arc<Mutex<Option<&'static str>>>) -> Self {
        Self {
            authority: Authority::Release(cleanup),
            job: DeviceModeJob::new(DeviceMode::Normal, Some(DeviceMode::Driver), || {}),
            fatal: fatal.clone(),
        }
    }
    fn verify_health(&self) -> io::Result<()> {
        match &self.authority {
            Authority::Acquire(fresh) => fresh.verify_health(),
            Authority::Release(cleanup) => cleanup.verify_health(),
        }
    }
}
impl Permit for DriverModePermit {
    fn authorize(&self) -> Option<ReadAdmission> {
        match &self.authority {
            Authority::Acquire(fresh) => fresh.authorize(),
            Authority::Release(cleanup) => cleanup.authorize(),
        }
    }
    fn device_mode(&self) -> Option<&DeviceModeJob> {
        Some(&self.job)
    }
    fn budget(&self) -> Option<Duration> {
        Some(JOB_BUDGET)
    }
    fn cleanup(&self) -> bool {
        matches!(self.authority, Authority::Release(_))
    }
    fn resolve(&self, _: i32, _: ObservedPerformance) -> io::Result<Option<Destination>> {
        Ok(None)
    }
    fn failed(&self, error: &io::Error) {
        remember_failure(&self.fatal, error)
    }
    fn retired(&self) -> io::Result<Settlement> {
        verify_retained_failure(&self.fatal)?;
        self.verify_health()?;
        Ok(Settlement::Complete)
    }
    fn record(&self, _: Progress) -> io::Result<()> {
        Err(io::Error::other(
            "A keyboard Driver-mode write keeps no thermal journal row.",
        ))
    }
}

impl Runtime {
    /// One maintenance step on the origin tick, after the power check. It is
    /// due at the first eligible tick and then `INTERVAL` after the last,
    /// managed's 15 s. In low power it reads, writes and publishes nothing: no
    /// acquire before Windows reports the system active again. A recovery-only
    /// lease never activates a write, so the step never runs there. A tick
    /// that finds a request holding the mutation gate retries at the next
    /// tick. Without a fresh exact-device read
    /// (`ReadSource::driver_mode_verified`) the step writes nothing, resets
    /// the recovery latch (managed `TryGetVerifiedDevice` false) and stays
    /// due; the status projects null for that read. The step publishes
    /// through the read cadence (`ReadSource::record_driver_mode`). Only
    /// fatal admission evidence is an error.
    pub(in super::super) async fn maintain_driver_mode(
        &self,
        mailbox: &MailboxSource,
    ) -> io::Result<()> {
        let keyboard = &self.keyboard;
        let now = Instant::now();
        if self.driver_mode_low_power() {
            return Ok(());
        }
        if keyboard.due.get().is_some_and(|due| now < due) {
            return Ok(());
        }
        let MailboxSource::Reader(reader) = mailbox else {
            return Ok(());
        };
        if reader.recovery_only() {
            return Ok(());
        }
        let Ok(_serial) = self.gate.try_lock() else {
            return Ok(());
        };
        verify_retained_failure(&self.fatal)?;
        if keyboard.indeterminate.get() || keyboard.uncertain.get() {
            return Ok(());
        }
        if !reader.driver_mode_verified() {
            keyboard.recovered.set(false);
            return Ok(());
        }
        keyboard.due.set(Some(now + INTERVAL));
        let Some(first) = self.acquire_once(reader, now).await? else {
            return Ok(());
        };
        // One recovery per failure streak: a fresh capture and
        // read-before-write, never a replay. An Indeterminate step has none.
        let result = if first == Acquired::Failed && !keyboard.recovered.replace(true) {
            self.acquire_once(reader, now)
                .await?
                .unwrap_or(Acquired::Failed)
        } else {
            first
        };
        if matches!(result, Acquired::Driver | Acquired::Current) {
            keyboard.recovered.set(false);
        }
        Ok(())
    }

    /// Whether the low-power gate reads set, unreadable or exhausted, as the
    /// lifecycle tick's gate does (`Runtime::gate`). Test composition that
    /// does not wire it reads clear.
    pub(super) fn driver_mode_low_power(&self) -> bool {
        match self.low_power.get().map(|read| read()) {
            None => false,
            Some(Some(transition)) => transition.suspending(),
            Some(None) => true,
        }
    }

    /// A fresh capture of the console user's ordinary write admission for one
    /// acquire. `None` when nobody is signed in or the capture is refused;
    /// only fatal admission evidence is an error. Its store is kept for the
    /// release's journal check.
    fn capture_console(&self, reader: &ReadSource) -> io::Result<Option<ConsoleCapture>> {
        let Some((sid, session)) = self.console.get().and_then(|read| read()) else {
            return Ok(None);
        };
        reader.custody().verify_integrity()?;
        let (storage, fresh) = match self.capture(&sid, session, reader.custody()) {
            Ok(captured) => captured,
            Err(error) if is_fatal_admission(&error) => return Err(error),
            Err(_) => return Ok(None),
        };
        *self.keyboard.storage.borrow_mut() = Some(storage.clone());
        Ok(Some((storage, fresh)))
    }

    /// One acquire attempt under a fresh capture. `None` when it was refused
    /// before any exchange: managed returns without publishing while a fan or
    /// laptop-control write is unsettled, and a refused capture or job keeps
    /// the last confirmed status. `step` dates what it records, so the next
    /// due step finds it stale and reads again.
    async fn acquire_once(
        &self,
        reader: &ReadSource,
        step: Instant,
    ) -> io::Result<Option<Acquired>> {
        let Some((storage, fresh)) = self.capture_console(reader)? else {
            return Ok(None);
        };
        let keyboard = &self.keyboard;
        // The startup call always reads. After it, a Driver result that a
        // client's ObserveState read job recorded under this admission within
        // the cadence needs no job (managed reads 00/84 and finds Driver).
        // Subscription refreshes do not run the read job, so otherwise each
        // due step on a verified device runs one.
        if keyboard.started.get() && reader.driver_mode_confirmed() == Some(true) {
            return Ok(Some(Acquired::Current));
        }
        if self.fan_unsettled() || storage.pending() {
            return Ok(None);
        }
        let admission = fresh.authorize();
        let permit = Arc::new(DriverModePermit::acquire(
            fresh,
            keyboard.ownership.clone(),
            &self.fatal,
        ));
        // The job's own budget starts after the lane wait (`Permit::budget`);
        // the interval bounds the wait.
        let result = reader
            .mutate(permit.clone(), Instant::now() + INTERVAL)
            .await;
        if let Err(error) = result
            && is_fatal_admission(&error)
        {
            return Err(error);
        }
        let acquired = match permit.job.step() {
            // Refused before any exchange: nothing to record or recover.
            DeviceModeStep::Pending => return Ok(None),
            DeviceModeStep::Unread => Acquired::Failed,
            // The baseline was not Normal, so it was Driver.
            DeviceModeStep::Observed(_) | DeviceModeStep::Confirmed => Acquired::Driver,
            DeviceModeStep::Claimed => {
                // Known not applied: the claim is cleared.
                keyboard.ownership.owns.store(false, Ordering::Release);
                Acquired::Failed
            }
            DeviceModeStep::Submitted | DeviceModeStep::Acknowledged => {
                keyboard.indeterminate.set(true);
                Acquired::Indeterminate
            }
        };
        keyboard.started.set(true);
        // A read job under the same admission publishes this result for the
        // interval instead of querying again; a failure publishes null, as
        // managed does after a failed step.
        if let Some(admission) = admission {
            let read = if acquired == Acquired::Driver {
                DeviceModeRead::Confirmed(DeviceMode::Driver)
            } else {
                DeviceModeRead::Unconfirmed
            };
            reader.record_driver_mode(admission, read, step);
        }
        Ok(Some(acquired))
    }

    /// The shutdown release before the fan handback (managed
    /// `ReleaseDriverModeCoreAsync("shutdown")`), within its share of the
    /// drain from its start. The outcome is a diagnostic; only fatal
    /// admission evidence is an error, so a refusal never fails the Stop.
    pub(in super::super) async fn release_driver_mode(
        &self,
        mailbox: &MailboxSource,
    ) -> io::Result<()> {
        self.release_driver_mode_for(mailbox, Boundary::Shutdown, SHUTDOWN_BUDGET)
            .await
            .map(drop)
    }

    /// One boundary's release within `share` from its start. The share ends
    /// the release wherever it is, the lane's join of a job past its deadline
    /// included: a worker that has not retired stays registered, and the fan
    /// handback joins it within its own time (`ReadSource::drain_active`).
    /// A caller dropped while the job runs leaves it for the next release to
    /// read (`Keyboard::inflight`).
    pub(in super::super) async fn release_driver_mode_for(
        &self,
        mailbox: &MailboxSource,
        boundary: Boundary,
        share: Duration,
    ) -> io::Result<Release> {
        let start = Instant::now();
        let share = share.min(SHUTDOWN_BUDGET);
        let release = match tokio::time::timeout_at(
            start + share,
            self.release_once(mailbox, boundary, start, share),
        )
        .await
        {
            Ok(release) => release?,
            Err(_) => match self.keyboard.inflight.borrow_mut().take() {
                None => Release::Exhausted,
                Some(permit) => self.released(&permit, true)?,
            },
        };
        self.keyboard.release.set(Some(release));
        Ok(release)
    }

    /// The shutdown release after origin loss, which cannot authorize it.
    pub(in super::super) fn refuse_release_driver_mode(&self) {
        self.keyboard.release.set(Some(Release::OriginLost));
    }

    /// The release from `start`. The job it starts stays in
    /// `Keyboard::inflight` until it is read, so an exhausted share or a
    /// dropped caller still reads how far it got.
    async fn release_once(
        &self,
        mailbox: &MailboxSource,
        boundary: Boundary,
        start: Instant,
        share: Duration,
    ) -> io::Result<Release> {
        let end = start + share.saturating_sub(RETIRE_SLACK);
        let entry = match boundary {
            Boundary::Shutdown => start + ENTRY_BUDGET.min(share),
            Boundary::LowPower(_) => end,
        };
        let Ok(_serial) = tokio::time::timeout_at(entry, self.gate.lock()).await else {
            return Ok(Release::Exhausted);
        };
        let keyboard = &self.keyboard;
        let MailboxSource::Reader(reader) = mailbox else {
            return Ok(Release::Unadmitted);
        };
        // Join active work first, as the fan handback does: a release job an
        // earlier caller dropped has then retired, and is read before
        // anything else.
        match tokio::time::timeout_at(entry, reader.drain_active()).await {
            Err(_) => return Ok(Release::Exhausted),
            Ok(Err(error)) if is_fatal_admission(&error) => return Err(error),
            Ok(Err(_)) => return Ok(Release::Unsettled),
            Ok(Ok(())) => {}
        }
        if let Some(earlier) = keyboard.inflight.borrow_mut().take() {
            self.released(&earlier, false)?;
        }
        if keyboard.uncertain.get() {
            return Ok(Release::Uncertain);
        }
        let attempted = match boundary {
            Boundary::Shutdown => keyboard.shut_down.get(),
            Boundary::LowPower(generation) => keyboard.suspended.get() == Some(generation),
        };
        if attempted {
            return Ok(Release::Attempted);
        }
        // Managed order: the fan guardian, a pending reconciliation, then
        // controller presence, each leaving the mode unchanged.
        if self.fan_unsettled() {
            return Ok(Release::FanBlocked);
        }
        if keyboard
            .storage
            .borrow()
            .as_ref()
            .is_some_and(|storage| storage.pending())
        {
            return Ok(Release::Unsettled);
        }
        let scope = boundary.scope();
        let Some(cleanup) = self.capture_cleanup(reader, scope)? else {
            return Ok(Release::Unadmitted);
        };
        // The admissions are synchronous, so the share is checked after them.
        // The job ends early enough for the lane to retire it in the share.
        let now = Instant::now();
        if now >= end {
            return Ok(Release::Exhausted);
        }
        let deadline = (now + RELEASE_JOB_BUDGET).min(end);
        let permit = Arc::new(DriverModePermit::release(cleanup, &self.fatal));
        // The boundary's one attempt, returned only when the lane refuses
        // before registering the job.
        let mark = |started: bool| match boundary {
            Boundary::Shutdown => keyboard.shut_down.set(started),
            Boundary::LowPower(generation) => {
                keyboard.suspended.set(started.then_some(generation));
            }
        };
        mark(true);
        *keyboard.inflight.borrow_mut() = Some(permit.clone());
        let result = reader
            .mutate_cleanup_in(permit.clone(), scope, deadline)
            .await;
        keyboard.inflight.borrow_mut().take();
        match result {
            Err(error) if is_fatal_admission(&error) => Err(error),
            // The lane refused before registering the job.
            Err(_) if permit.job.step() == DeviceModeStep::Pending => {
                mark(false);
                self.refused(&permit)?;
                Ok(Release::Unadmitted)
            }
            _ => self.released(&permit, Instant::now() >= deadline),
        }
    }

    /// The release outcome from how far its job got. `spent` says the job's
    /// time ran out. A setter that may have been submitted unconfirmed ends
    /// every later release and acquire write in this process. A fatal error
    /// its worker reported fails the release.
    fn released(&self, permit: &DriverModePermit, spent: bool) -> io::Result<Release> {
        verify_retained_failure(&self.fatal)?;
        let keyboard = &self.keyboard;
        let ownership = &keyboard.ownership;
        Ok(match permit.job.step() {
            DeviceModeStep::Confirmed => {
                ownership.owns.store(false, Ordering::Release);
                Release::Restored
            }
            // Not Driver: a foreign change if OpenBlade had claimed it.
            DeviceModeStep::Observed(_) => {
                if ownership.owns.swap(false, Ordering::AcqRel) {
                    Release::ForeignChange
                } else {
                    Release::NotDriver
                }
            }
            // Nothing was sent. The job ran out of time if its deadline
            // passed or its pace refused the setter while cleanup admission
            // still held; otherwise admission was lost.
            DeviceModeStep::Pending => {
                if spent || permit.authorize().is_some() {
                    Release::Exhausted
                } else {
                    Release::Unadmitted
                }
            }
            // Only the baseline query, or no setter at all, may have been
            // sent: nothing is in doubt.
            DeviceModeStep::Unread | DeviceModeStep::Claimed => Release::Unconfirmed,
            DeviceModeStep::Submitted | DeviceModeStep::Acknowledged => {
                keyboard.uncertain.set(true);
                Release::Unconfirmed
            }
        })
    }

    /// Fails the release on fatal admission evidence behind a refusal:
    /// retained, or found by a fresh health check. An authorization refuses
    /// as `None` even when its cause, such as lost custody integrity, is
    /// fatal, so a refusal is checked again rather than reported as
    /// `Unadmitted`.
    fn refused(&self, permit: &DriverModePermit) -> io::Result<()> {
        verify_retained_failure(&self.fatal)?;
        match permit.verify_health() {
            Err(error) if is_fatal_admission(&error) => Err(error),
            _ => Ok(()),
        }
    }
}

#[cfg(test)]
impl Runtime {
    /// (owns, indeterminate, recovery latch).
    pub(in super::super) fn driver_mode_state_for_tests(&self) -> (bool, bool, bool) {
        let keyboard = &self.keyboard;
        (
            keyboard.ownership.owns.load(Ordering::Acquire),
            keyboard.indeterminate.get(),
            keyboard.recovered.get(),
        )
    }
    /// Whether a release may have submitted Normal unconfirmed.
    pub(in super::super) fn driver_mode_uncertain_for_tests(&self) -> bool {
        self.keyboard.uncertain.get()
    }
    /// The last release's outcome.
    pub(in super::super) fn driver_mode_release(&self) -> Option<Release> {
        self.keyboard.release.get()
    }
    /// Acquire claims made so far.
    pub(in super::super) fn driver_mode_claims_for_tests(&self) -> usize {
        self.keyboard.ownership.claims.load(Ordering::Acquire)
    }
    /// The mutation gate, for a test that holds it.
    pub(in super::super) fn driver_mode_gate_for_tests(&self) -> Rc<tokio::sync::Mutex<()>> {
        self.gate.clone()
    }
    /// Makes the next tick's step due now.
    pub(in super::super) fn driver_mode_due_for_tests(&self) {
        self.keyboard.due.set(None);
    }
    /// Moves the next step's due time `by` earlier, as if that much time had
    /// passed.
    pub(in super::super) fn driver_mode_age_for_tests(&self, by: Duration) {
        let due = self.keyboard.due.get();
        self.keyboard.due.set(due.map(|due| due - by));
    }
}
