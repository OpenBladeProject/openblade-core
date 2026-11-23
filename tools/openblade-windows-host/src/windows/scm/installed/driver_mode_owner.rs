//! Keyboard Driver-mode ownership on exact RZ09-0581
//! (`service-keyboard-driver-mode-v1`): the managed maintenance
//! (`ServiceControlRuntime` 15 s step, `ProductionLightingPort.MaintainDriverModeAsync`)
//! and the shutdown release (`ReleaseDriverModeCoreAsync("shutdown")`,
//! `TryRestoreOwnedDeviceModeCoreAsync`).
//!
//! - Acquire and maintain read 00/84 first. A keyboard already in Driver mode
//!   publishes true with no write and no claim. From Normal, ownership is
//!   claimed before the 00/04:0300 setter can be submitted; a refusal before
//!   submission (managed `KnownNotApplied`) clears the claim and any later
//!   failure keeps it, so cleanup can still restore Normal. The setter's exact
//!   acknowledgement and an independent 00/84 readback must both confirm.
//! - A setter that may have been submitted without that confirmation is
//!   Indeterminate: the step publishes null and the owner stops, sending nothing
//!   more in this process; nothing is ever replayed. The read job's own 00/84
//!   cadence keeps publishing the confirmed mode.
//! - Each write captures the console user's ordinary write admission, as the
//!   lifecycle tick does (`Runtime::console_pair`): fresh exact-device,
//!   competing-controller, AC line, session, SID and lifecycle-epoch checks,
//!   which refuse in low power. With nobody signed in, or a locked session,
//!   the step is refused and retried at the next one, and the last confirmed
//!   status stays. Managed maintains without a user; native adds no write
//!   authority, and the function row has no user to serve then.
//! - A step that failed gets one recovery per failure streak: a fresh capture
//!   and read-before-write. Success or a lost device resets the latch.
//! - Ownership lives in memory, as managed's does: a restart reads 00/84 again
//!   and never claims a keyboard already in Driver mode. A durable record and
//!   crash recovery are a later slice.
//! - Shutdown restores Normal only when OpenBlade owns Driver mode, under the
//!   owned-Stop admission of the last write capture; a fresh 00/84 read that
//!   is not Driver is a foreign change and is never overwritten, and a
//!   release that may have been submitted unconfirmed blocks any later
//!   restore write. The low-power boundary release is a later slice.
use super::super::mailbox_read::ReadSource;
use super::*;
use crate::discovery::performance::{DeviceModeJob, DeviceModeRead, DeviceModeStep};
use openblade_protocol::device_mode::DeviceMode;
use std::cell::Cell;

/// Managed `ServicePeriodicObservationLoop.ObservationInterval`.
pub(in super::super) const INTERVAL: std::time::Duration = std::time::Duration::from_secs(15);
/// One Driver-mode job: three exchanges with their settles and checkpoints.
const BUDGET: std::time::Duration = std::time::Duration::from_secs(2);

/// Why a shutdown release did not restore Normal. Each leaves the mode as it
/// was.
const RELEASE_UNRESOLVED: &str =
    "Keyboard Driver-mode release is unresolved; no restore write is submitted.";
const RELEASE_BLOCKED: &str =
    "Keyboard Driver mode was left unchanged: a fan or laptop-control write is not settled.";
const RELEASE_UNADMITTED: &str = "Keyboard Driver mode was left unchanged: fresh owned-Stop admission is unavailable (competing controller, session or lifecycle change).";
const RELEASE_UNCONFIRMED: &str =
    "Keyboard Driver mode was not confirmed Normal; evidence is retained.";
const RELEASE_ORIGIN_LOST: &str = "Unexpected origin loss cannot authorize owned keyboard cleanup.";

/// A write capture: its per-user store and its fresh admission.
type Captured = (Arc<Storage>, Arc<dyn Fresh>);

/// What OpenBlade holds of the keyboard's Driver mode. The worker thread
/// claims through it, so it is shared with each job.
#[derive(Default)]
struct Ownership {
    owns: AtomicBool,
    release_uncertain: AtomicBool,
    // Acquire claims made, so a test can tell a cleared claim from none.
    #[cfg(test)]
    claims: std::sync::atomic::AtomicUsize,
}

/// The owner's state between steps.
#[derive(Default)]
pub(super) struct Keyboard {
    ownership: Arc<Ownership>,
    /// An acquire setter may have been submitted unconfirmed: no further
    /// acquire write in this process.
    indeterminate: Cell<bool>,
    /// The recovery of the current failure streak has run.
    recovered: Cell<bool>,
    /// When the next step is due; `None` until the first eligible tick.
    due: Cell<Option<Instant>>,
    /// The latest write capture, for the shutdown release's owned-Stop
    /// admission.
    capture: RefCell<Option<Captured>>,
    /// The shutdown release was attempted; it is never repeated.
    released: Cell<bool>,
}

/// How one acquire job ended.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Acquired {
    /// The confirmed mode: true for Driver.
    Mode(bool),
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
    fn cleanup(&self) -> bool {
        self.cleanup
    }
    fn resolve(&self, _: i32, _: ObservedPerformance) -> io::Result<Option<Destination>> {
        Ok(None)
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
    /// it reads and writes nothing and publishes nothing: no acquire before
    /// Windows reports the system active again. A tick that finds a request
    /// holding the mutation gate retries at the next tick. Without a fresh
    /// exact-device read (`ReadSource::driver_mode_verified`) the step
    /// publishes null, writes nothing, resets the recovery latch (managed
    /// `TryGetVerifiedDevice` false) and stays due, so the first verified
    /// tick is the startup call. After an Indeterminate step the owner stops:
    /// it sends nothing more in this process, and the read job's own 00/84
    /// cadence keeps publishing the confirmed mode. The step publishes through
    /// that cadence (`ReadSource::record_driver_mode`). Returns the mode it
    /// confirmed, inner `None` when unknown, and `None` when it read nothing.
    pub(in super::super) async fn maintain_driver_mode(
        &self,
        mailbox: &MailboxSource,
    ) -> io::Result<Option<Option<bool>>> {
        let keyboard = &self.keyboard;
        let now = Instant::now();
        if keyboard.indeterminate.get()
            || self.in_low_power()
            || keyboard.due.get().is_some_and(|due| now < due)
        {
            return Ok(None);
        }
        let MailboxSource::Reader(reader) = mailbox else {
            return Ok(None);
        };
        let Ok(_serial) = self.gate.try_lock() else {
            return Ok(None);
        };
        verify_retained_failure(&self.fatal)?;
        if !reader.driver_mode_verified() {
            keyboard.recovered.set(false);
            return Ok(Some(None));
        }
        keyboard.due.set(Some(now + INTERVAL));
        let Some(first) = self.acquire_once(reader).await? else {
            return Ok(None);
        };
        // One recovery per failure streak: a fresh capture and
        // read-before-write, never a replay. An Indeterminate step has none.
        let result = if first == Acquired::Failed && !keyboard.recovered.replace(true) {
            self.acquire_once(reader).await?.unwrap_or(Acquired::Failed)
        } else {
            first
        };
        Ok(Some(match result {
            Acquired::Mode(active) => {
                keyboard.recovered.set(false);
                Some(active)
            }
            Acquired::Failed | Acquired::Indeterminate => None,
        }))
    }

    /// One acquire job under a fresh console-user capture. `None` when it was
    /// refused before any firmware access: managed returns without
    /// publishing while a fan or laptop-control write is unsettled, and a
    /// refused capture keeps the last confirmed status.
    async fn acquire_once(&self, reader: &ReadSource) -> io::Result<Option<Acquired>> {
        if self.fan_unsettled() {
            return Ok(None);
        }
        let Some((sid, session)) = self.console_pair() else {
            return Ok(None);
        };
        reader.custody().verify_integrity()?;
        let (storage, fresh) = match self.capture(&sid, session, reader.custody()) {
            Ok(captured) => captured,
            Err(error) if is_fatal_admission(&error) => return Err(error),
            Err(_) => return Ok(None),
        };
        // Managed `RequiresReconciliation`: an unsettled journal row.
        if storage.pending() {
            return Ok(None);
        }
        let keyboard = &self.keyboard;
        *keyboard.capture.borrow_mut() = Some((storage, fresh.clone()));
        let ownership = keyboard.ownership.clone();
        let permit = Arc::new(DriverModePermit {
            fresh: fresh.clone(),
            job: DeviceModeJob::new(DeviceMode::Driver, Some(DeviceMode::Normal), move || {
                ownership.owns.store(true, Ordering::Release);
                #[cfg(test)]
                ownership.claims.fetch_add(1, Ordering::AcqRel);
            }),
            cleanup: false,
            fatal: self.fatal.clone(),
        });
        let admission = fresh.authorize();
        let result = reader.mutate(permit.clone(), Instant::now() + BUDGET).await;
        if let Err(error) = result
            && is_fatal_admission(&error)
        {
            return Err(error);
        }
        let acquired = match permit.job.step() {
            DeviceModeStep::Pending | DeviceModeStep::Unread => Acquired::Failed,
            DeviceModeStep::Observed(mode) => Acquired::Mode(mode == DeviceMode::Driver),
            DeviceModeStep::Claimed => {
                // Known not applied: the claim is cleared.
                keyboard.ownership.owns.store(false, Ordering::Release);
                Acquired::Failed
            }
            DeviceModeStep::Submitted | DeviceModeStep::Acknowledged => {
                keyboard.indeterminate.set(true);
                Acquired::Indeterminate
            }
            DeviceModeStep::Confirmed => Acquired::Mode(true),
        };
        // A read job under the same admission publishes this result for the
        // interval instead of querying again.
        if let Some(admission) = admission {
            let read = match acquired {
                Acquired::Mode(true) => DeviceModeRead::Confirmed(DeviceMode::Driver),
                Acquired::Mode(false) => DeviceModeRead::Confirmed(DeviceMode::Normal),
                _ => DeviceModeRead::Unconfirmed,
            };
            reader.record_driver_mode(admission, read, Instant::now());
        } else if !matches!(acquired, Acquired::Mode(_)) {
            // Managed publishes null after a failed step.
            reader.forget_driver_mode();
        }
        Ok(Some(acquired))
    }

    /// The shutdown release before the fan handback (managed
    /// `ReleaseDriverModeCoreAsync("shutdown")`). A keyboard OpenBlade does
    /// not own is left alone. Its error leaves the mode unchanged or
    /// unresolved; the caller still runs the fan step.
    pub(in super::super) async fn release_driver_mode(
        &self,
        mailbox: &MailboxSource,
    ) -> io::Result<()> {
        let _serial = self.gate.lock().await;
        let keyboard = &self.keyboard;
        let ownership = keyboard.ownership.clone();
        if !ownership.owns.load(Ordering::Acquire) {
            return Ok(());
        }
        if ownership.release_uncertain.load(Ordering::Acquire) || keyboard.released.replace(true) {
            return Err(io::Error::other(RELEASE_UNRESOLVED));
        }
        let MailboxSource::Reader(reader) = mailbox else {
            return Err(io::Error::other(RELEASE_UNADMITTED));
        };
        // Managed order: the fan guardian, a pending reconciliation, then
        // controller presence, each leaving the mode unchanged.
        let Some((storage, fresh)) = keyboard.capture.borrow().clone() else {
            return Err(io::Error::other(RELEASE_UNADMITTED));
        };
        if self.fan_unsettled() || storage.pending() {
            return Err(io::Error::other(RELEASE_BLOCKED));
        }
        // The owned-Stop admission of the latest write capture: its lifecycle
        // epoch, session, SID, AC line and fresh competing-controller absence.
        if fresh.authorize_owned_stop().is_none() {
            verify_retained_failure(&self.fatal)?;
            return Err(io::Error::other(RELEASE_UNADMITTED));
        }
        let claim = ownership.clone();
        let permit = Arc::new(DriverModePermit {
            fresh,
            job: DeviceModeJob::new(DeviceMode::Normal, Some(DeviceMode::Driver), move || {
                claim.release_uncertain.store(true, Ordering::Release);
            }),
            cleanup: true,
            fatal: self.fatal.clone(),
        });
        let result = reader
            .mutate_cleanup(permit.clone(), Instant::now() + BUDGET)
            .await;
        let released = match permit.job.step() {
            DeviceModeStep::Confirmed => {
                ownership.release_uncertain.store(false, Ordering::Release);
                ownership.owns.store(false, Ordering::Release);
                Ok(())
            }
            // Not Driver now: a foreign change, never overwritten.
            DeviceModeStep::Observed(_) => {
                ownership.owns.store(false, Ordering::Release);
                Ok(())
            }
            // Known not applied: only this clears the uncertain release.
            DeviceModeStep::Claimed => {
                ownership.release_uncertain.store(false, Ordering::Release);
                Err(io::Error::other(RELEASE_UNCONFIRMED))
            }
            DeviceModeStep::Pending
            | DeviceModeStep::Unread
            | DeviceModeStep::Submitted
            | DeviceModeStep::Acknowledged => Err(io::Error::other(RELEASE_UNCONFIRMED)),
        };
        match result {
            Err(error) if is_fatal_admission(&error) => Err(error),
            _ => released,
        }
    }

    /// The shutdown release after origin loss: an owned Driver mode cannot be
    /// restored then.
    pub(in super::super) fn refuse_release_driver_mode(&self) -> io::Result<()> {
        if self.keyboard.ownership.owns.load(Ordering::Acquire) {
            Err(io::Error::other(RELEASE_ORIGIN_LOST))
        } else {
            Ok(())
        }
    }
}

#[cfg(test)]
impl Runtime {
    /// (owns, release uncertain, indeterminate, recovery latch).
    pub(in super::super) fn driver_mode_state_for_tests(&self) -> (bool, bool, bool, bool) {
        let keyboard = &self.keyboard;
        (
            keyboard.ownership.owns.load(Ordering::Acquire),
            keyboard.ownership.release_uncertain.load(Ordering::Acquire),
            keyboard.indeterminate.get(),
            keyboard.recovered.get(),
        )
    }
    /// Makes the next tick's step due now.
    /// Acquire claims made so far.
    pub(in super::super) fn driver_mode_claims_for_tests(&self) -> usize {
        self.keyboard.ownership.claims.load(Ordering::Acquire)
    }
    pub(in super::super) fn driver_mode_due_for_tests(&self) {
        self.keyboard.due.set(None);
    }
    /// Moves the next step's due time `by` earlier, as if that much time had
    /// passed.
    pub(in super::super) fn driver_mode_age_for_tests(&self, by: std::time::Duration) {
        let due = self.keyboard.due.get();
        self.keyboard.due.set(due.map(|due| due - by));
    }
}
