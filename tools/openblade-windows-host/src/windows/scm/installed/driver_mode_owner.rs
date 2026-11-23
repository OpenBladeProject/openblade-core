//! Keyboard Driver-mode ownership on exact RZ09-0581
//! (`service-keyboard-driver-mode-v1`): the managed maintenance
//! (`ServiceControlRuntime` 15 s step, `ProductionLightingPort.MaintainDriverModeAsync`)
//! and the shutdown release (`ReleaseDriverModeCoreAsync("shutdown")`).
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
//!   the shutdown release still restores Normal after a fresh read of Driver.
//! - Each step captures the console user's ordinary write admission, the
//!   candidate the lifecycle tick uses: fresh exact-device,
//!   competing-controller, AC line, session, SID and lifecycle-epoch checks,
//!   which refuse in low power. With nobody signed in, or a locked session,
//!   the step is refused and retried at the next one, and the last confirmed
//!   status stays. Managed maintains without a user; native adds no write
//!   authority, and the function row has no user to serve then. The tick
//!   also re-captures, with no job, whenever the lifecycle write epoch moves,
//!   so the release holds the latest ordinary capture.
//! - After the startup call, a step starts no job when a client's
//!   ObserveState read job confirmed Driver under the current read admission
//!   within the cadence. Otherwise each due step runs a mutation job that
//!   reads 00/84 first. A step that failed gets
//!   one recovery per failure streak: a fresh capture and read-before-write.
//!   A step refused before any exchange records nothing and spends no
//!   recovery. Success or a lost device resets the latch.
//! - Ownership lives in memory, as managed's does. The shutdown release
//!   follows managed: a fresh 00/84 read of Driver under the owned-Stop
//!   admission writes Normal once, whoever set it; a read of Normal ends an
//!   owned claim as a foreign change, within the keyboard's bounded share
//!   of the drain. The low-power boundary release is a later slice, and so
//!   is a release admission without a console user: Windows logs users off
//!   before a system shutdown reaches services, and Fast Startup hibernates
//!   instead, so the shutdown release is refused or never runs then.
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
/// work (`ReadSource::drain_active`), from the release's start. A read job
/// is joined within it; a write job still running exhausts it.
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
/// What the share keeps after the job for the lane to retire it.
const RETIRE_SLACK: Duration = Duration::from_millis(50);
/// The keyboard's whole share of the shutdown drain from the release's start:
/// the entry, the synchronous admissions, the job and its retirement. Native
/// accepts SERVICE_CONTROL_SHUTDOWN, not PRESHUTDOWN, so at system shutdown
/// the whole Stop must end within WaitToKillServiceTimeout, about 5 s, as
/// managed's 5 s shutdown cleanup budget does. The listener's 2 s, this
/// share and the fans' unchanged 2 s (`readiness::SOURCE_DRAIN_TIMEOUT`)
/// are 4.9 s, so the keyboard has what the fans leave. The figures are
/// estimates; RZ09-0581 needs an attended timing of a real release.
pub(in super::super) const SHUTDOWN_BUDGET: Duration = Duration::from_millis(900);
const _: () = assert!(
    SHUTDOWN_BUDGET.as_nanos()
        == ENTRY_BUDGET.as_nanos() + RELEASE_JOB_BUDGET.as_nanos() + RETIRE_SLACK.as_nanos()
);

/// How a shutdown release ended. Only a fatal admission error fails the
/// Stop. The Service has no diagnostics sink yet, so the outcome is kept for
/// tests, and the process exit drops it, as an
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
    /// No owned-Stop admission: no capture, a competing controller, a
    /// session, logon or lifecycle change, low power, or a write lease that
    /// never activated. Nothing was sent.
    Unadmitted,
    /// Nothing was sent: the share ran out first, or the job's measured
    /// pace could not fit the setter, its acknowledgement and the readback.
    Exhausted,
    /// The job ran without confirming Normal; a setter may have been
    /// submitted.
    Unconfirmed,
    /// The release job already ran; it is never repeated.
    Attempted,
    /// Origin loss cannot authorize owned cleanup.
    OriginLost,
}
/// A write capture: its per-user store and fresh admission.
#[derive(Clone)]
struct Captured {
    storage: Arc<Storage>,
    fresh: Arc<dyn Fresh>,
}

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
    /// The recovery of the current failure streak has run.
    recovered: Cell<bool>,
    /// The startup call's job has run.
    started: Cell<bool>,
    /// When the next step is due; `None` until the first eligible tick.
    due: Cell<Option<Instant>>,
    /// The latest write capture, for the shutdown release.
    capture: RefCell<Option<Captured>>,
    /// The lifecycle write epoch the latest capture attempt read.
    capture_epoch: Cell<Option<u64>>,
    /// The shutdown release job started; it is never repeated.
    released: Cell<bool>,
    /// The last shutdown release's outcome.
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

/// One Driver-mode job's permit: the capture's admission and the job. It
/// keeps no journal row.
struct DriverModePermit {
    fresh: Arc<dyn Fresh>,
    job: DeviceModeJob,
    /// The shutdown release, under owned-Stop admission.
    cleanup: bool,
    fatal: Arc<Mutex<Option<&'static str>>>,
}
impl Permit for DriverModePermit {
    fn authorize(&self) -> Option<ReadAdmission> {
        if self.cleanup {
            self.fresh.authorize_owned_stop()
        } else {
            self.fresh.authorize()
        }
    }
    fn device_mode(&self) -> Option<&DeviceModeJob> {
        Some(&self.job)
    }
    fn budget(&self) -> Option<Duration> {
        Some(JOB_BUDGET)
    }
    fn cleanup(&self) -> bool {
        self.cleanup
    }
    fn resolve(&self, _: i32, _: ObservedPerformance) -> io::Result<Option<Destination>> {
        Ok(None)
    }
    fn failed(&self, error: &io::Error) {
        remember_failure(&self.fatal, error)
    }
    fn retired(&self) -> io::Result<Settlement> {
        verify_retained_failure(&self.fatal)?;
        self.fresh.verify_health()?;
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
    /// through the read cadence (`ReadSource::record_driver_mode`).
    ///
    /// The shutdown release uses the latest capture, whose admission pins the
    /// lifecycle write epoch. So whenever that epoch moves, a tick re-captures
    /// with no job: between steps, while the device is unverified and after
    /// an Indeterminate step. Only an epoch move captures for an Indeterminate
    /// owner. A refused capture is retried only at the next epoch move or due
    /// step on a verified device. Only fatal admission evidence is an error.
    pub(in super::super) async fn maintain_driver_mode(
        &self,
        mailbox: &MailboxSource,
    ) -> io::Result<()> {
        let keyboard = &self.keyboard;
        let now = Instant::now();
        if self.driver_mode_low_power() {
            return Ok(());
        }
        let epoch = self.driver_mode_epoch();
        let moved = epoch.is_some() && keyboard.capture_epoch.get() != epoch;
        let due = keyboard.due.get().is_none_or(|due| now >= due);
        if !due && !moved {
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
        if keyboard.indeterminate.get() || !due {
            if moved {
                self.capture_console(reader)?;
            }
            return Ok(());
        }
        if !reader.driver_mode_verified() {
            keyboard.recovered.set(false);
            if moved {
                self.capture_console(reader)?;
            }
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
    fn driver_mode_low_power(&self) -> bool {
        match self.low_power.get().map(|read| read()) {
            None => false,
            Some(Some(transition)) => transition.suspending(),
            Some(None) => true,
        }
    }

    /// The lifecycle's ordinary write epoch (`Lifecycle::active`) that every
    /// capture pins. Test composition that does not wire it reads unknown,
    /// and only due steps capture.
    fn driver_mode_epoch(&self) -> Option<u64> {
        self.admission_epoch.get().and_then(|read| read())
    }

    /// A fresh capture of the console user's ordinary write admission, kept
    /// for the shutdown release. `None` when nobody is signed in or the
    /// capture is refused; only fatal admission evidence is an error. The
    /// epoch it read is recorded either way, so a refusal is retried when the
    /// epoch moves again or at the next due step, not on every tick.
    fn capture_console(&self, reader: &ReadSource) -> io::Result<Option<Captured>> {
        self.keyboard.capture_epoch.set(self.driver_mode_epoch());
        let Some((sid, session)) = self.console.get().and_then(|read| read()) else {
            return Ok(None);
        };
        reader.custody().verify_integrity()?;
        let (storage, fresh) = match self.capture(&sid, session, reader.custody()) {
            Ok(captured) => captured,
            Err(error) if is_fatal_admission(&error) => return Err(error),
            Err(_) => return Ok(None),
        };
        let captured = Captured { storage, fresh };
        *self.keyboard.capture.borrow_mut() = Some(captured.clone());
        Ok(Some(captured))
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
        let Some(captured) = self.capture_console(reader)? else {
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
        if self.fan_unsettled() || captured.storage.pending() {
            return Ok(None);
        }
        let ownership = keyboard.ownership.clone();
        let permit = Arc::new(DriverModePermit {
            fresh: captured.fresh.clone(),
            job: DeviceModeJob::new(DeviceMode::Driver, Some(DeviceMode::Normal), move || {
                ownership.owns.store(true, Ordering::Release);
                #[cfg(test)]
                ownership.claims.fetch_add(1, Ordering::AcqRel);
            }),
            cleanup: false,
            fatal: self.fatal.clone(),
        });
        let admission = captured.fresh.authorize();
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
    /// drain from its start. The share ends the release wherever it is, the
    /// lane's join of a job past its deadline included: a worker that has not
    /// retired stays registered, and the fan handback joins it within its own
    /// drain time (`ReadSource::drain_active`). The outcome is a diagnostic;
    /// only fatal admission evidence is an error, so a refusal never fails
    /// the Stop.
    pub(in super::super) async fn release_driver_mode(
        &self,
        mailbox: &MailboxSource,
    ) -> io::Result<()> {
        let start = Instant::now();
        let job = RefCell::new(None);
        let release = match tokio::time::timeout_at(
            start + SHUTDOWN_BUDGET,
            self.release_once(mailbox, start, &job),
        )
        .await
        {
            Ok(release) => release?,
            Err(_) => match job.borrow_mut().take() {
                None => Release::Exhausted,
                Some((permit, _)) => self.released(&permit, true)?,
            },
        };
        self.keyboard.release.set(Some(release));
        Ok(())
    }

    /// The shutdown release after origin loss, which cannot authorize it.
    pub(in super::super) fn refuse_release_driver_mode(&self) {
        self.keyboard.release.set(Some(Release::OriginLost));
    }

    /// The release from `start`. The job it starts is left in `job`, with its
    /// deadline, so an exhausted share still reads how far it got.
    async fn release_once(
        &self,
        mailbox: &MailboxSource,
        start: Instant,
        job: &RefCell<Option<(Arc<DriverModePermit>, Instant)>>,
    ) -> io::Result<Release> {
        let entry = start + ENTRY_BUDGET;
        let Ok(_serial) = tokio::time::timeout_at(entry, self.gate.lock()).await else {
            return Ok(Release::Exhausted);
        };
        let keyboard = &self.keyboard;
        if keyboard.released.get() {
            return Ok(Release::Attempted);
        }
        let MailboxSource::Reader(reader) = mailbox else {
            return Ok(Release::Unadmitted);
        };
        // Join active work first, as the fan handback does.
        match tokio::time::timeout_at(entry, reader.drain_active()).await {
            Err(_) => return Ok(Release::Exhausted),
            Ok(Err(error)) if is_fatal_admission(&error) => return Err(error),
            Ok(Err(_)) => return Ok(Release::Unsettled),
            Ok(Ok(())) => {}
        }
        // Managed order: the fan guardian, a pending reconciliation, then
        // controller presence, each leaving the mode unchanged.
        if self.fan_unsettled() {
            return Ok(Release::FanBlocked);
        }
        let Some(captured) = keyboard.capture.borrow().clone() else {
            return Ok(Release::Unadmitted);
        };
        if captured.storage.pending() {
            return Ok(Release::Unsettled);
        }
        let Some(fresh) = self.owned_stop_admission(&captured)? else {
            return Ok(Release::Unadmitted);
        };
        // The admissions are synchronous, so the share is checked after them.
        // The job ends early enough for the lane to retire it in the share.
        let end = start + SHUTDOWN_BUDGET - RETIRE_SLACK;
        let now = Instant::now();
        if now >= end {
            return Ok(Release::Exhausted);
        }
        let deadline = (now + RELEASE_JOB_BUDGET).min(end);
        keyboard.released.set(true);
        let permit = Arc::new(DriverModePermit {
            fresh,
            job: DeviceModeJob::new(DeviceMode::Normal, Some(DeviceMode::Driver), || {}),
            cleanup: true,
            fatal: self.fatal.clone(),
        });
        *job.borrow_mut() = Some((permit.clone(), deadline));
        let result = reader.mutate_cleanup(permit.clone(), deadline).await;
        match result {
            Err(error) if is_fatal_admission(&error) => Err(error),
            // The lane refused before registering the job.
            Err(_) if permit.job.step() == DeviceModeStep::Pending => {
                keyboard.released.set(false);
                self.refused(&*permit.fresh)?;
                Ok(Release::Unadmitted)
            }
            _ => self.released(&permit, Instant::now() >= deadline),
        }
    }

    /// The release outcome from how far its job got. `spent` says the job's
    /// time ran out. A fatal error its worker reported fails the release.
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
            // passed or its pace refused the setter while owned-Stop
            // admission still held; otherwise admission was lost.
            DeviceModeStep::Pending => {
                if spent || permit.fresh.authorize_owned_stop().is_some() {
                    Release::Exhausted
                } else {
                    Release::Unadmitted
                }
            }
            DeviceModeStep::Unread
            | DeviceModeStep::Claimed
            | DeviceModeStep::Submitted
            | DeviceModeStep::Acknowledged => Release::Unconfirmed,
        })
    }

    /// The owned-Stop admission of the latest ordinary capture, as the fan
    /// handback's owner path uses it (`Runtime::stop_fans`): origin and
    /// custody trust, the exact device, the same unlocked console session,
    /// the lifecycle epoch it pins and fresh competing-controller absence.
    /// The tick re-captures whenever that epoch moves
    /// (`Runtime::maintain_driver_mode`); a settle capture authorizes
    /// owned-Stop reads only (`Settle`), never this setter.
    fn owned_stop_admission(&self, captured: &Captured) -> io::Result<Option<Arc<dyn Fresh>>> {
        verify_retained_failure(&self.fatal)?;
        match captured.fresh.verify_health() {
            Err(error) if is_fatal_admission(&error) => return Err(error),
            Err(_) => return Ok(None),
            Ok(()) => {}
        }
        if captured.fresh.authorize_owned_stop().is_none() {
            self.refused(&*captured.fresh)?;
            return Ok(None);
        }
        Ok(Some(captured.fresh.clone()))
    }

    /// Fails the release on fatal admission evidence behind a refusal:
    /// retained, or found by a fresh health check. An authorization refuses
    /// as `None` even when its cause, such as lost custody integrity, is
    /// fatal, so a refusal is checked again rather than reported as
    /// `Unadmitted`.
    fn refused(&self, fresh: &dyn Fresh) -> io::Result<()> {
        verify_retained_failure(&self.fatal)?;
        match fresh.verify_health() {
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
    /// The last shutdown release's outcome.
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
