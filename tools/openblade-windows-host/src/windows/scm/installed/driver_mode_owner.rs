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
//!   is Indeterminate: the step publishes null and nothing is ever replayed.
//!   Each later due step resolves it read-only (`resolve_indeterminate`): one
//!   job under a fresh capture reads 00/84 and writes nothing. Driver means
//!   the acquire took effect and keeps the claim it made, if a release has
//!   not ended it; a read never makes one. Normal means it did not, or was
//!   undone: the claim ends, and the next tick acquires as a new write
//!   through the ordinary claim-before-write job, never in the resolving
//!   tick. An unreadable, fenced or refused read stays Indeterminate.
//!   Managed has no Indeterminate state: its failed step publishes null and
//!   its one recovery on a fresh port reads and may write again in the same
//!   step. A release still restores Normal after a fresh read of Driver.
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
//! - Ownership is durable (`ledger`, `owner\keyboard-driver-mode.bin`),
//!   where managed keeps it in memory. Each job that may write claims
//!   before its setter can be sent: an acquire's `AcquireIntent` under the
//!   custody's ordinary admission, a release's `ReleaseIntent` under its
//!   boundary's cleanup scope, each naming the job's four IDs. A claim that
//!   cannot be written sends no setter. After the job the owner settles it:
//!   confirmed, an acquire becomes `Owned` and a release `Clear`; known not
//!   submitted, it reverts to its prior; otherwise it stays in doubt. The
//!   job charges a fixed estimate per record write before it claims and
//!   checks its pace again with the claim's real time spent, so a slow
//!   write costs only the job it slowed. A release settles only while its
//!   share has room for the write, charged at the last measured one. The
//!   owner's flags are derived from the record, never ahead of it, and a
//!   failed write, or a record that cannot be read or is refused, closes
//!   the keyboard owner for the process: no acquire, maintain or release
//!   write runs again, and the status projects confirmed reads only. A
//!   later process that loads an intent resolves it read-only, as an
//!   Indeterminate acquire. Each release follows
//!   managed: a fresh 00/84 read of Driver writes Normal once, whoever set
//!   it; a read of Normal ends an owned claim as a foreign change. A release
//!   holds the Service's own cleanup admission (`Cleanup`), captured fresh
//!   for its boundary, never a console capture: Windows logs users off before
//!   a system shutdown reaches services, and a suspend may start from the
//!   lock screen. Each boundary releases at most once: the shutdown, and each
//!   suspend generation. A release that may have submitted Normal without
//!   confirming it leaves its `ReleaseIntent`, which ends every later
//!   release and acquire write until a later read resolves it. In low power
//!   the step reads and writes nothing until Windows reports the system
//!   active again, so the 15 s cadence reacquires then.
use super::super::mailbox_read::ReadSource;
use super::*;
use crate::discovery::performance::{DeviceModeJob, DeviceModeRead, DeviceModeStep};
use ledger::{Ledger, Standing, Writer};
use openblade_domain::driver_mode_history::{Intent, Settled};
use openblade_protocol::device_mode::DeviceMode;
use std::{cell::Cell, time::Duration};

#[path = "driver_mode_ledger.rs"]
mod ledger;

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
/// 20 cleanup authorizations, counted in the test rig, at an unmeasured
/// 8-9 ms each, and three 30 ms settles, an estimate pending an attended
/// timing. The allowance of twice
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
    /// A fresh read found Normal and no confirmed claim: nothing was
    /// claimed, or an acquire intent was in doubt, whose setter the read
    /// shows never took effect. That intent settles `Clear`.
    NotDriver,
    /// A fresh read found Normal although OpenBlade owned Driver mode
    /// (`Owned`). The outcome names what the read found: the record ends the
    /// claim (`Owned -> Clear`) only while the share holds that write, and
    /// otherwise keeps `Owned`, from which the next step acquires again.
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
    /// pace could not fit the setter, its acknowledgement and the readback,
    /// before its claim or once the claim was written. A written claim then
    /// reverts only while the share holds the write and the cleanup
    /// admission admits it; otherwise its `ReleaseIntent` stays for a later
    /// read to resolve.
    Exhausted,
    /// The job ran without confirming Normal; a setter may have been
    /// submitted.
    Unconfirmed,
    /// An earlier release, in this process or a crashed earlier one, may
    /// have submitted Normal unconfirmed (`ReleaseIntent`), so this one sent
    /// nothing: the uncertain write is never replayed.
    Uncertain,
    /// The Driver-mode record is closed for this process, or this release's
    /// claim could not be written (which closes it), so no setter was sent.
    Unrecorded,
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

/// The owner's state between steps. What OpenBlade has claimed, and whether
/// an acquire or release setter may have been submitted unconfirmed
/// (`AcquireIntent`, `ReleaseIntent`: until a later read resolves it, no
/// acquire, maintain or release write runs), is the durable record the
/// ledger holds.
#[derive(Default)]
pub(super) struct Keyboard {
    /// The durable record, read at the first step or release with a reader
    /// (`Runtime::driver_mode_ledger`). The worker claims through it, so it
    /// is shared with each job.
    ledger: RefCell<Option<Arc<Ledger>>>,
    /// When the release doubt the record holds (`ReleaseIntent`) began, for
    /// the read that resolves it (`Runtime::release_doubt_since`).
    uncertain_since: Cell<Option<Instant>>,
    /// The recovery of the current failure streak has run.
    recovered: Cell<bool>,
    /// The startup call's job has run.
    started: Cell<bool>,
    /// When the next step is due; `None` until the first eligible tick.
    due: Cell<Option<Instant>>,
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
/// write: an acquire writes Driver from Normal under a console capture (its
/// resolution read writes nothing), and a release writes Normal from Driver
/// under the Service's cleanup admission.
enum Authority {
    Acquire(Arc<dyn Fresh>),
    Release(Arc<dyn Cleanup>),
}

/// One Driver-mode job's permit: its admission and the job. It keeps no
/// journal row; its claim is the durable Driver-mode record.
struct DriverModePermit {
    authority: Authority,
    job: DeviceModeJob,
    fatal: Arc<Mutex<Option<&'static str>>>,
}
impl DriverModePermit {
    /// Driver from Normal, claimed as an `AcquireIntent` under the custody's
    /// ordinary admission before the setter can be sent.
    fn acquire(
        fresh: Arc<dyn Fresh>,
        ledger: Arc<Ledger>,
        fatal: &Arc<Mutex<Option<&'static str>>>,
    ) -> Self {
        let allowance = ledger.claim_allowance(Intent::Acquire);
        Self {
            authority: Authority::Acquire(fresh),
            job: DeviceModeJob::new(
                DeviceMode::Driver,
                Some(DeviceMode::Normal),
                allowance,
                move |ids| ledger.claim(Writer::Ordinary, Intent::Acquire, *ids),
            ),
            fatal: fatal.clone(),
        }
    }
    /// A fresh 00/84 read under a console capture that writes nothing and
    /// claims nothing, whatever it reads: the resolution of an Indeterminate
    /// acquire.
    fn observe(fresh: Arc<dyn Fresh>, fatal: &Arc<Mutex<Option<&'static str>>>) -> Self {
        Self {
            authority: Authority::Acquire(fresh),
            job: DeviceModeJob::new(DeviceMode::Driver, None, Duration::ZERO, |_| {
                Err(io::ErrorKind::PermissionDenied.into())
            }),
            fatal: fatal.clone(),
        }
    }
    /// The only constructor a cleanup admission reaches: Normal, written only
    /// after a fresh read of Driver, claimed as a `ReleaseIntent` under the
    /// release's own cleanup scope before the setter can be sent.
    fn release(
        cleanup: Arc<dyn Cleanup>,
        ledger: Arc<Ledger>,
        fatal: &Arc<Mutex<Option<&'static str>>>,
    ) -> Self {
        let writer = Writer::Cleanup(cleanup.scope());
        let allowance = ledger.claim_allowance(Intent::Release);
        Self {
            authority: Authority::Release(cleanup),
            job: DeviceModeJob::new(
                DeviceMode::Normal,
                Some(DeviceMode::Driver),
                allowance,
                move |ids| ledger.claim(writer, Intent::Release, *ids),
            ),
            fatal: fatal.clone(),
        }
    }
    fn verify_health(&self) -> io::Result<()> {
        match &self.authority {
            Authority::Acquire(fresh) => fresh.verify_health(),
            Authority::Release(cleanup) => cleanup.verify_health(),
        }
    }
    /// The custody admission this job's record writes hold: its own kind of
    /// authority, so a cleanup scope never reaches an acquire.
    fn writer(&self) -> Writer {
        match &self.authority {
            Authority::Acquire(_) => Writer::Ordinary,
            Authority::Release(cleanup) => Writer::Cleanup(cleanup.scope()),
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

/// Runs one console-admitted job, an acquire or its resolution read, through
/// the mutation lane. How far it got is its step (`DeviceModeJob::step`);
/// only fatal admission evidence is an error. The job's own budget starts
/// after the lane wait (`Permit::budget`); the interval bounds the wait.
async fn run_console_job(reader: &ReadSource, permit: &Arc<DriverModePermit>) -> io::Result<()> {
    match reader
        .mutate(permit.clone(), Instant::now() + INTERVAL)
        .await
    {
        Err(error) if is_fatal_admission(&error) => Err(error),
        _ => Ok(()),
    }
}

/// Whether a record write succeeded. A failed one has closed the ledger, and
/// one refused before writing left the record as it was; only fatal
/// admission evidence is an error.
fn recorded(written: io::Result<bool>) -> io::Result<bool> {
    match written {
        Ok(written) => Ok(written),
        Err(error) if is_fatal_admission(&error) => Err(error),
        Err(_) => Ok(false),
    }
}

impl Runtime {
    /// The Driver-mode record's ledger, read through `reader`'s custody the
    /// first time a step or release needs it. A record that cannot be read
    /// or is refused closes it for the process (`Standing::Closed`); only
    /// fatal admission evidence is an error, and is read again next time.
    fn driver_mode_ledger(&self, reader: &ReadSource) -> io::Result<Arc<Ledger>> {
        if let Some(ledger) = &*self.keyboard.ledger.borrow() {
            return Ok(ledger.clone());
        }
        let ledger = Arc::new(Ledger::load(reader.custody(), &self.fatal)?);
        *self.keyboard.ledger.borrow_mut() = Some(ledger.clone());
        // A release doubt an earlier process left dates from now.
        self.release_doubt_since(&ledger);
        Ok(ledger)
    }

    /// When the release doubt the record holds began, `None` without one: no
    /// earlier than the setter it doubts, so only a read that started after
    /// that setter resolves it. A release that leaves a `ReleaseIntent`
    /// dates it when its job has retired, and a loaded one dates from the
    /// load.
    fn release_doubt_since(&self, ledger: &Ledger) -> Option<Instant> {
        let since = &self.keyboard.uncertain_since;
        if !matches!(ledger.standing(), Standing::ReleaseIntent { .. }) {
            since.set(None);
            return None;
        }
        let began = since.get().unwrap_or_else(Instant::now);
        since.set(Some(began));
        Some(began)
    }

    /// Whether the loaded record holds a release doubt.
    fn release_in_doubt(&self) -> bool {
        self.keyboard
            .ledger
            .borrow()
            .as_ref()
            .is_some_and(|ledger| matches!(ledger.standing(), Standing::ReleaseIntent { .. }))
    }

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
        let ledger = self.driver_mode_ledger(reader)?;
        // A closed record admits no write for the rest of the process.
        if ledger.standing() == Standing::Closed {
            return Ok(());
        }
        // An acquire in doubt is resolved by the step's own read-only job.
        let mut resolve = ledger.standing() == Standing::AcquireIntent;
        // A release that may have applied Normal unconfirmed (`ReleaseIntent`)
        // is resolved read-only, never replayed. After this process's own
        // release, whose job activated the write lease, a confirmed 00/84
        // read under the current read admission, by a job that started after
        // that release, says which mode the keyboard is in. Driver settles to
        // the intent's prior: the claim there was, if any, and a read never
        // makes one, since only an acquire's own setter claims. Any other
        // mode settles to `Clear`; the step then acquires through the normal
        // cadence. Until the settle is written the doubt stays. A low-power
        // release's doubt can only be resolved after the resume, whose epoch
        // every such read admission carries. An intent an earlier process
        // left is resolved as a loaded acquire intent is, by the step's own
        // read-only job (`resolve_indeterminate`): until a job activates this
        // process's write lease, the custody writes no record.
        if let Standing::ReleaseIntent { prior } = ledger.standing() {
            if !reader.mutation_activated() {
                resolve = true;
            } else {
                let since = self.release_doubt_since(&ledger).unwrap_or(now);
                let Some(driver) = reader.driver_mode_confirmed_since(since) else {
                    return Ok(());
                };
                let to = if driver { prior } else { Settled::Clear };
                if !recorded(ledger.settle(Writer::Ordinary, to))? {
                    return Ok(());
                }
                keyboard.uncertain_since.set(None);
            }
        }
        if !reader.driver_mode_verified() {
            keyboard.recovered.set(false);
            return Ok(());
        }
        keyboard.due.set(Some(now + INTERVAL));
        // A step that resolves an intent only reads.
        if resolve {
            return self.resolve_indeterminate(reader, &ledger, now).await;
        }
        let Some(first) = self.acquire_once(reader, &ledger, now).await? else {
            return Ok(());
        };
        // One recovery per failure streak: a fresh capture and
        // read-before-write, never a replay. An Indeterminate step has none,
        // and a closed record leaves nothing to recover with.
        let result = if first == Acquired::Failed
            && ledger.standing() != Standing::Closed
            && !keyboard.recovered.replace(true)
        {
            self.acquire_once(reader, &ledger, now)
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
    /// only fatal admission evidence is an error.
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
        ledger: &Arc<Ledger>,
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
            ledger.clone(),
            &self.fatal,
        ));
        run_console_job(reader, &permit).await?;
        let acquired = match permit.job.step() {
            // Refused before any exchange: nothing to record or recover.
            DeviceModeStep::Pending => return Ok(None),
            // The claim could not be recorded, which closed the record, so
            // no setter was sent.
            DeviceModeStep::Unread | DeviceModeStep::Unrecorded => Acquired::Failed,
            // The baseline was not Normal, so it was Driver.
            DeviceModeStep::Observed(_) => Acquired::Driver,
            DeviceModeStep::Confirmed => {
                // The claim settles; a failed write closes the record and
                // keeps nothing in memory ahead of it.
                recorded(ledger.settle(Writer::Ordinary, Settled::Owned))?;
                Acquired::Driver
            }
            DeviceModeStep::Claimed => {
                // Known not applied: the claim reverts to its prior.
                recorded(ledger.revert(Writer::Ordinary))?;
                Acquired::Failed
            }
            // The record keeps the intent: Indeterminate.
            DeviceModeStep::Submitted | DeviceModeStep::Acknowledged => Acquired::Indeterminate,
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

    /// Resolves an Indeterminate acquire read-only, never by replaying its
    /// setter: one job under a fresh capture, with the acquire's own skip
    /// conditions, reads 00/84 and writes nothing. Each of its checkpoints
    /// checks the capture's lifecycle epoch, so a notice during the read
    /// leaves it unconfirmed. Driver records the acquire's claim as `Owned`;
    /// the read makes no claim of its own. Normal records `Clear`, and the
    /// step is due again at the next tick, whose acquire claims and writes
    /// afresh on new IDs. Either is written before the doubt ends in memory,
    /// and a failed write closes the record instead. Anything else stays
    /// Indeterminate: a read sent without a confirmed reply publishes null,
    /// and one refused before any exchange records nothing. An intent a
    /// crashed earlier process left is resolved the same way, a release
    /// intent included: Driver records its prior, the claim there was before
    /// the release, and Normal records `Clear`.
    async fn resolve_indeterminate(
        &self,
        reader: &ReadSource,
        ledger: &Ledger,
        step: Instant,
    ) -> io::Result<()> {
        let Some((storage, fresh)) = self.capture_console(reader)? else {
            return Ok(());
        };
        if self.fan_unsettled() || storage.pending() {
            return Ok(());
        }
        let admission = fresh.authorize();
        let permit = Arc::new(DriverModePermit::observe(fresh, &self.fatal));
        run_console_job(reader, &permit).await?;
        let keyboard = &self.keyboard;
        let read = match permit.job.step() {
            DeviceModeStep::Pending => return Ok(()),
            DeviceModeStep::Observed(mode) => {
                let to = match (mode, ledger.standing()) {
                    (DeviceMode::Driver, Standing::ReleaseIntent { prior }) => prior,
                    (DeviceMode::Driver, _) => Settled::Owned,
                    _ => Settled::Clear,
                };
                if recorded(ledger.settle(Writer::Ordinary, to))? {
                    keyboard.uncertain_since.set(None);
                    if to == Settled::Clear {
                        keyboard.due.set(None);
                    }
                }
                keyboard.recovered.set(false);
                DeviceModeRead::Confirmed(mode)
            }
            // A job that writes nothing reaches no later step.
            _ => DeviceModeRead::Unconfirmed,
        };
        if let Some(admission) = admission {
            reader.record_driver_mode(admission, read, step);
        }
        Ok(())
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
            // The share is over, so nothing is recorded after the job.
            Err(_) => match self.keyboard.inflight.borrow_mut().take() {
                None => Release::Exhausted,
                Some(permit) => self.released(&permit, true, None)?,
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
        let until = start + share;
        if let Some(earlier) = keyboard.inflight.borrow_mut().take() {
            self.released(&earlier, false, Some(until))?;
        }
        if self.release_in_doubt() {
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
        // An unsettled journal row blocks the release (managed
        // `RequiresReconciliation`). The latest capture's store has seen
        // every row (`Runtime::captured`); a console capture alone would miss
        // one another console user's request wrote after the owner's last
        // step.
        if self
            .captured
            .borrow()
            .as_ref()
            .is_some_and(|storage| storage.pending())
        {
            return Ok(Release::Unsettled);
        }
        // The record is read only now, after every check that needs no
        // custody, so a release refused before it never reads it. A closed
        // record cannot hold the release's claim, so no setter is sent; one
        // an earlier process left in release doubt is resolved by a read.
        let ledger = self.driver_mode_ledger(reader)?;
        match ledger.standing() {
            Standing::Closed => return Ok(Release::Unrecorded),
            Standing::ReleaseIntent { .. } => return Ok(Release::Uncertain),
            _ => {}
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
        let permit = Arc::new(DriverModePermit::release(cleanup, ledger, &self.fatal));
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
            _ => self.released(&permit, Instant::now() >= deadline, Some(until)),
        }
    }

    /// The release outcome from how far its job got. `spent` says the job's
    /// time ran out. The job's record is settled only while the share, which
    /// ends at `until`, still holds one record write (`None`: it is over);
    /// otherwise what the record holds stays for a later read or release to
    /// resolve. A setter that may have been submitted unconfirmed leaves the
    /// `ReleaseIntent`, which ends every later release and acquire write
    /// until a read resolves it. A fatal error its worker reported fails the
    /// release.
    fn released(
        &self,
        permit: &DriverModePermit,
        spent: bool,
        until: Option<Instant>,
    ) -> io::Result<Release> {
        verify_retained_failure(&self.fatal)?;
        // A release job starts only after the ledger was read.
        let ledger = self.keyboard.ledger.borrow().clone();
        // Settles the record to `to`, or reverts its intent (`None`), under
        // the release's own cleanup admission, if the share has room.
        let settle = |to: Option<Settled>| -> io::Result<()> {
            let Some(ledger) = &ledger else {
                return Ok(());
            };
            if !until.is_some_and(|until| Instant::now() + ledger.write_allowance() <= until) {
                return Ok(());
            }
            recorded(match to {
                Some(to) => ledger.settle(permit.writer(), to),
                None => ledger.revert(permit.writer()),
            })
            .map(drop)
        };
        // Nothing was sent. The job ran out of time if its deadline passed or
        // its pace refused the setter while cleanup admission still held;
        // otherwise admission was lost. The job records how far it got, not
        // why it stopped, so the cause is inferred from the admission now: a
        // pace refusal followed by a lost admission reads as `Unadmitted`,
        // and either way nothing was sent.
        let unsent = || {
            if spent || permit.authorize().is_some() {
                Release::Exhausted
            } else {
                Release::Unadmitted
            }
        };
        let release = match permit.job.step() {
            // The claimed `ReleaseIntent` settles `Clear`. Only the intent
            // is settled: a job an earlier caller dropped may be read after a
            // later read resolved its intent and an acquire claimed again.
            DeviceModeStep::Confirmed => {
                if self.release_in_doubt() {
                    settle(Some(Settled::Clear))?;
                }
                Release::Restored
            }
            // Not Driver, so this job claimed nothing. An owned claim found
            // Normal is a foreign change and ends; an acquire intent found
            // Normal is resolved as its read-only resolution does, `Clear`,
            // since its setter never took effect (`resolve_indeterminate`).
            // A `ReleaseIntent` is another job's, left for its resolution.
            DeviceModeStep::Observed(_) => match ledger.as_ref().map(|ledger| ledger.standing()) {
                Some(Standing::Owned) => {
                    settle(Some(Settled::Clear))?;
                    Release::ForeignChange
                }
                Some(Standing::AcquireIntent) => {
                    settle(Some(Settled::Clear))?;
                    Release::NotDriver
                }
                _ => Release::NotDriver,
            },
            DeviceModeStep::Pending => unsent(),
            // The baseline may have been sent, but no claim and no setter.
            DeviceModeStep::Unread => Release::Unconfirmed,
            // The claim could not be written, which closed the record, so no
            // setter was sent.
            DeviceModeStep::Unrecorded => Release::Unrecorded,
            // Claimed, but the setter is known not to have been submitted:
            // the intent reverts to its prior, and nothing was sent.
            DeviceModeStep::Claimed => {
                if self.release_in_doubt() {
                    settle(None)?;
                }
                unsent()
            }
            // The record keeps the intent: the doubt.
            DeviceModeStep::Submitted | DeviceModeStep::Acknowledged => Release::Unconfirmed,
        };
        if let Some(ledger) = &ledger {
            self.release_doubt_since(ledger);
        }
        Ok(release)
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
    /// The held record's standing, `Clear` before it is read.
    fn driver_mode_standing(&self) -> Standing {
        self.keyboard
            .ledger
            .borrow()
            .as_ref()
            .map_or(Standing::Clear, |ledger| ledger.standing())
    }
    /// (owns, indeterminate, recovery latch), as the held record says.
    pub(in super::super) fn driver_mode_state_for_tests(&self) -> (bool, bool, bool) {
        let standing = self.driver_mode_standing();
        (
            standing.owns(),
            standing == Standing::AcquireIntent,
            self.keyboard.recovered.get(),
        )
    }
    /// Whether the record is closed for this process.
    pub(in super::super) fn driver_mode_closed_for_tests(&self) -> bool {
        self.driver_mode_standing() == Standing::Closed
    }
    /// Whether a release may have submitted Normal unconfirmed, as the held
    /// record says (`ReleaseIntent`).
    pub(in super::super) fn driver_mode_uncertain_for_tests(&self) -> bool {
        matches!(self.driver_mode_standing(), Standing::ReleaseIntent { .. })
    }
    /// The last release's outcome.
    pub(in super::super) fn driver_mode_release(&self) -> Option<Release> {
        self.keyboard.release.get()
    }
    /// Acquire claims made so far.
    pub(in super::super) fn driver_mode_claims_for_tests(&self) -> usize {
        self.keyboard
            .ledger
            .borrow()
            .as_ref()
            .map_or(0, |ledger| ledger.claims())
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
