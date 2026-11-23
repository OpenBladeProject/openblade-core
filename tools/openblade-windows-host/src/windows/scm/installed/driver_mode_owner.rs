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
//!   authority, and the function row has no user to serve then.
//! - After the startup call, a step whose read job already confirmed Driver
//!   under the current read admission starts no job. A step that failed gets
//!   one recovery per failure streak: a fresh capture and read-before-write.
//!   A step refused before any exchange records nothing and spends no
//!   recovery. Success or a lost device resets the latch.
//! - Ownership lives in memory, as managed's does. The shutdown release
//!   follows managed: a fresh 00/84 read of Driver under the owned-Stop
//!   admission writes Normal once, whoever set it; a read of Normal ends an
//!   owned claim as a foreign change. The low-power boundary release is a
//!   later slice.
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
/// The keyboard's whole share of the shutdown drain, the wait for the gate
/// and for active work included. The fan handback keeps the drain
/// time it had (`readiness::SOURCE_DRAIN_TIMEOUT`).
pub(in super::super) const SHUTDOWN_BUDGET: Duration = Duration::from_millis(500);

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
    /// The share ran out before the job started. Nothing was sent.
    Exhausted,
    /// The job ran without confirming Normal; a setter may have been
    /// submitted.
    Unconfirmed,
    /// The release job already ran; it is never repeated.
    Attempted,
    /// Origin loss cannot authorize owned cleanup.
    OriginLost,
}
/// A write capture: its identity, per-user store and fresh admission.
#[derive(Clone)]
struct Captured {
    sid: String,
    session: u32,
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
    /// due at the first eligible tick and then every `INTERVAL`. In low power
    /// it reads, writes and publishes nothing: no acquire before Windows
    /// reports the system active again. A recovery-only lease never activates
    /// a write, so the step never runs there. A tick that finds a request
    /// holding the mutation gate retries at the next tick. Without a fresh
    /// exact-device read (`ReadSource::driver_mode_verified`) the step writes
    /// nothing, resets the recovery latch (managed `TryGetVerifiedDevice`
    /// false) and stays due; the status projects null for that read. The
    /// step publishes through the read cadence (`ReadSource::record_driver_mode`).
    /// Only fatal admission evidence is an error.
    pub(in super::super) async fn maintain_driver_mode(
        &self,
        mailbox: &MailboxSource,
    ) -> io::Result<()> {
        let keyboard = &self.keyboard;
        let now = Instant::now();
        if keyboard.indeterminate.get()
            || self.driver_mode_low_power()
            || keyboard.due.get().is_some_and(|due| now < due)
        {
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
    fn driver_mode_low_power(&self) -> bool {
        match self.low_power.get().map(|read| read()) {
            None => false,
            Some(Some(transition)) => transition.suspending(),
            Some(None) => true,
        }
    }

    /// A fresh capture of the console user's ordinary write admission, kept
    /// for the shutdown release. `None` when nobody is signed in or the
    /// capture is refused; only fatal admission evidence is an error.
    fn capture_console(&self, reader: &ReadSource) -> io::Result<Option<Captured>> {
        let Some((sid, session)) = self.console.get().and_then(|read| read()) else {
            return Ok(None);
        };
        reader.custody().verify_integrity()?;
        let (storage, fresh) = match self.capture(&sid, session, reader.custody()) {
            Ok(captured) => captured,
            Err(error) if is_fatal_admission(&error) => return Err(error),
            Err(_) => return Ok(None),
        };
        let captured = Captured {
            sid,
            session,
            storage,
            fresh,
        };
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
        // The startup call always reads. After it, a read job's own Driver
        // result under this admission needs no job (managed reads 00/84 and
        // finds Driver).
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
    /// drain. Its outcome is a diagnostic; only fatal admission evidence is
    /// an error, so a refusal never fails the Stop.
    pub(in super::super) async fn release_driver_mode(
        &self,
        mailbox: &MailboxSource,
    ) -> io::Result<()> {
        let release = self
            .release_once(mailbox, Instant::now() + SHUTDOWN_BUDGET)
            .await?;
        self.keyboard.release.set(Some(release));
        Ok(())
    }

    /// The shutdown release after origin loss, which cannot authorize it.
    pub(in super::super) fn refuse_release_driver_mode(&self) {
        self.keyboard.release.set(Some(Release::OriginLost));
    }

    async fn release_once(
        &self,
        mailbox: &MailboxSource,
        deadline: Instant,
    ) -> io::Result<Release> {
        let Ok(_serial) = tokio::time::timeout_at(deadline, self.gate.lock()).await else {
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
        match tokio::time::timeout_at(deadline, reader.drain_active()).await {
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
        keyboard.released.set(true);
        let permit = Arc::new(DriverModePermit {
            fresh,
            job: DeviceModeJob::new(DeviceMode::Normal, Some(DeviceMode::Driver), || {}),
            cleanup: true,
            fatal: self.fatal.clone(),
        });
        let result = reader.mutate_cleanup(permit.clone(), deadline).await;
        if let Err(error) = result
            && is_fatal_admission(&error)
        {
            return Err(error);
        }
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
            // No exchange: the job never started.
            DeviceModeStep::Pending => {
                keyboard.released.set(false);
                verify_retained_failure(&self.fatal)?;
                Release::Unadmitted
            }
            DeviceModeStep::Unread
            | DeviceModeStep::Claimed
            | DeviceModeStep::Submitted
            | DeviceModeStep::Acknowledged => Release::Unconfirmed,
        })
    }

    /// The owned-Stop admission the fan handback uses (`Runtime::stop_fans`):
    /// the latest capture's own, or, after a power or session notice advanced
    /// the lifecycle epoch it pins, a settle capture of the same SID, session
    /// and logon at the current epoch (`Fresh::capture_settle`). Either keeps
    /// every owned-Stop check: origin and custody trust, the exact device, the
    /// same unlocked console session and fresh competing-controller absence.
    fn owned_stop_admission(&self, captured: &Captured) -> io::Result<Option<Arc<dyn Fresh>>> {
        if captured.fresh.authorize_owned_stop().is_some() {
            return Ok(Some(captured.fresh.clone()));
        }
        match captured.fresh.capture_settle(
            &captured.sid,
            captured.session,
            captured.fresh.authentication(),
        ) {
            Ok(fresh) if fresh.authorize_owned_stop().is_some() => Ok(Some(fresh)),
            Err(error) if is_fatal_admission(&error) => Err(error),
            _ => {
                verify_retained_failure(&self.fatal)?;
                Ok(None)
            }
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
