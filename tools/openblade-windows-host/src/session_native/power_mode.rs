//! Selected installed Windows power-mode automation: managed
//! `ServicePowerModeAutomationLoop` with `WindowsPowerModeAutomationCoordinator`.
//! The Agent owns this role because the Windows 11 power mode is a
//! current-user setting (AGENTS.md architecture rule 2). It issues no Service
//! command and touches no laptop hardware.
//!
//! The status watcher hands every verified, fresh status to
//! `Automation::observe` (managed `Observe`) and every reconnect to
//! `Automation::unavailable` (managed `MarkUnavailable`), synchronously and in
//! order. A confirmed performance preset on a confirmed power source selects
//! that preset's association in `profiles.windowsPowerModes`; an unchanged
//! target schedules nothing, a cleared association leaves Windows unchanged,
//! and anything else fills one slot and wakes `serve`. The runner applies one
//! target at a time through the injected `Supplier`, retrying a failed apply
//! once after `RETRY_DELAY`. A newer target, an unavailable status or stop
//! cancels the running apply, whose result is then never published.
//!
//! Like managed, nothing is restored: the user-configured mode Windows keeps is
//! the last one applied, on Agent exit, on a profile change and on an unknown
//! status alike. An unknown status only forgets the observed target, so the
//! next confirmed status applies its association again.
use super::feature_lifetime::stopped;
use std::{
    io,
    sync::{
        Arc, Mutex, MutexGuard, PoisonError,
        atomic::{AtomicBool, Ordering::SeqCst},
    },
    time::Duration,
};

use openblade_platform_windows::agent_diagnostics::{Event, PowerModeNotice, record};
use tokio::sync::{Notify, watch};

use super::service_client::StatusUpdate;
use crate::service_status::Snapshot;

mod status;
#[cfg(test)]
mod tests;
#[cfg(test)]
mod vectors;
mod windows;

/// Managed `MaximumApplyAttempts`.
const MAXIMUM_ATTEMPTS: u32 = 2;
/// Managed `RetryDelay`.
const RETRY_DELAY: Duration = Duration::from_secs(1);

/// Managed `WindowsPowerMode` wire values.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) enum Mode {
    BestPowerEfficiency = 0,
    Balanced = 1,
    BestPerformance = 2,
}

impl Mode {
    fn from_wire(value: i32) -> Option<Self> {
        Some(match value {
            0 => Self::BestPowerEfficiency,
            1 => Self::Balanced,
            2 => Self::BestPerformance,
            _ => return None,
        })
    }
}

/// The Windows power supply a `PowerSource` selects: UsbC and PluggedIn are AC.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) enum Supply {
    Ac,
    Dc,
}

/// Managed `Target`: the deduplication key of one observation.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) struct Target {
    supply: Supply,
    performance: u8,
    mode: Option<Mode>,
}

/// A confirmed observation: its target and the managed `PowerSource` wire
/// value (0..=2) the apply and its result name.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) struct Observation {
    target: Target,
    source: u8,
}

/// Managed `WindowsPowerModeAutomationObservation`.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) enum Classification {
    IgnoredUnavailable,
    LeftUnchanged,
    AlreadyObserved,
    ApplyScheduled,
}

/// One apply's failure, as managed `WindowsPowerModeController` reports it.
pub(super) type Failure = PowerModeNotice;

/// Managed `WindowsPowerModeApplyResult` without its message.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) enum Applied {
    /// The configured mode is the requested one; `changed` when this apply set it.
    Succeeded {
        changed: bool,
    },
    Failed(Failure),
    /// Cancellation was observed before the read or before the set.
    Canceled,
}

/// The Windows user-configured power mode, behind a port. Production is
/// `windows::UserPowerModes`; tests script it and never reach Windows.
pub(super) trait Supplier {
    /// Managed `ApplyAsync`: read the configured mode for `supply`, set it when
    /// it differs, and confirm it. Blocking. `canceled` is checked before the
    /// read and before the set.
    fn apply(&mut self, supply: Supply, mode: Mode, canceled: &AtomicBool) -> Applied;
}

/// Managed `WindowsPowerModeAutomationResult`.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) struct Outcome {
    generation: u64,
    source: u8,
    mode: Mode,
    applied: Applied,
}

/// One apply's cancellation, observable by the blocking supplier and the
/// retry delay.
#[derive(Default)]
pub(super) struct Cancel {
    flag: AtomicBool,
    wake: Notify,
}

impl Cancel {
    fn cancel(&self) {
        self.flag.store(true, SeqCst);
        // One waiter at most, the runner; a stored permit covers a cancel
        // that lands before it waits.
        self.wake.notify_one();
    }

    fn is_canceled(&self) -> bool {
        self.flag.load(SeqCst)
    }

    async fn canceled(&self) {
        if !self.is_canceled() {
            self.wake.notified().await;
        }
    }
}

/// One scheduled apply.
pub(super) struct Apply {
    generation: u64,
    source: u8,
    supply: Supply,
    mode: Mode,
    cancel: Arc<Cancel>,
}

#[derive(Default)]
struct State {
    /// Managed `lastObservedTarget`.
    last: Option<Target>,
    generation: u64,
    /// Scheduled and not yet taken by the runner.
    slot: Option<Apply>,
    /// The cancellation of the apply the runner took last.
    running: Option<Arc<Cancel>>,
    /// Managed `LastResult`.
    result: Option<Outcome>,
    /// Set when the runner ends; nothing is scheduled after it.
    retired: bool,
}

impl State {
    /// Managed `CancelActiveApply`: the scheduled apply never runs and the
    /// running one is canceled.
    fn cancel_active(&mut self) {
        if let Some(apply) = self.slot.take() {
            apply.cancel.cancel();
        }
        if let Some(cancel) = self.running.take() {
            cancel.cancel();
        }
    }
}

/// Power-mode automation shared by the status watcher and the runner.
#[derive(Clone, Default)]
pub(crate) struct Automation {
    state: Arc<Mutex<State>>,
    wake: Arc<Notify>,
    associations: Arc<Mutex<status::Associations>>,
}

impl Automation {
    pub(crate) fn new() -> Self {
        Self::default()
    }

    fn lock(&self) -> io::Result<MutexGuard<'_, State>> {
        self.state
            .lock()
            .map_err(|_| io::Error::other("power-mode automation state poisoned"))
    }

    /// Managed `Observe` over verified Service status.
    pub(crate) fn observe(&self, snapshot: &Snapshot) -> io::Result<Classification> {
        let observation = {
            // The cache holds only a decoded value; a panic cannot leave it
            // half written.
            let mut associations = self
                .associations
                .lock()
                .unwrap_or_else(PoisonError::into_inner);
            status::decode(snapshot.power_mode_automation(), &mut associations)
        };
        self.observe_decoded(observation)
    }

    fn observe_decoded(&self, observation: Option<Observation>) -> io::Result<Classification> {
        let mut state = self.lock()?;
        let Some(Observation { target, source }) = observation else {
            state.last = None;
            state.cancel_active();
            return Ok(Classification::IgnoredUnavailable);
        };
        if state.last == Some(target) {
            return Ok(Classification::AlreadyObserved);
        }
        state.last = Some(target);
        state.cancel_active();
        let Some(mode) = target.mode else {
            return Ok(Classification::LeftUnchanged);
        };
        if state.retired {
            // Stop already drained the runner; managed refuses a disposed
            // coordinator, and an apply scheduled now would never run.
            return Ok(Classification::IgnoredUnavailable);
        }
        state.generation += 1;
        state.slot = Some(Apply {
            generation: state.generation,
            source,
            supply: target.supply,
            mode,
            cancel: Arc::default(),
        });
        drop(state);
        self.wake.notify_one();
        Ok(Classification::ApplyScheduled)
    }

    /// Managed `MarkUnavailable`: the subscription is reconnecting, or its
    /// status could not be verified.
    pub(crate) fn unavailable(&self) -> io::Result<()> {
        self.observe_decoded(None).map(drop)
    }

    /// The next scheduled apply, now the running one.
    fn take(&self) -> io::Result<Option<Apply>> {
        let mut state = self.lock()?;
        let apply = state.slot.take();
        if let Some(apply) = &apply {
            state.running = Some(apply.cancel.clone());
        }
        Ok(apply)
    }

    /// Record one apply's end. Managed publishes the result only for the
    /// current generation and only when it was not canceled.
    fn complete(&self, apply: &Apply, applied: Option<Applied>) -> io::Result<Option<Outcome>> {
        let mut state = self.lock()?;
        if state
            .running
            .as_ref()
            .is_some_and(|running| Arc::ptr_eq(running, &apply.cancel))
        {
            state.running = None;
        }
        let Some(applied) = applied else {
            return Ok(None);
        };
        if state.generation != apply.generation || apply.cancel.is_canceled() {
            return Ok(None);
        }
        let outcome = Outcome {
            generation: apply.generation,
            source: apply.source,
            mode: apply.mode,
            applied,
        };
        state.result = Some(outcome);
        Ok(Some(outcome))
    }

    /// Managed `DisposeAsync`: cancel whatever is scheduled or running and
    /// schedule nothing after.
    fn retire(&self) {
        let mut state = self.state.lock().unwrap_or_else(|error| error.into_inner());
        state.retired = true;
        state.cancel_active();
    }

    #[cfg(test)]
    fn last_result(&self) -> Option<Outcome> {
        self.lock().unwrap().result
    }
}

/// Hand a verified, fresh status to the automation, as managed
/// `ServicePowerModeAutomationLoop` hands every connected status to `Observe`
/// and every reconnect to `MarkUnavailable`. A status that fails verification
/// marks the automation unavailable before its error ends the watcher.
pub(crate) fn publish_status(
    automation: &Automation,
    update: &StatusUpdate,
    verify: impl Fn() -> io::Result<()>,
) -> io::Result<()> {
    let observed = match update {
        StatusUpdate::Opened(initial) => verify()
            .and_then(|()| initial.require_fresh())
            .and_then(|()| automation.observe(initial.snapshot())),
        StatusUpdate::Event(observation) => verify()
            .and_then(|()| observation.require_fresh())
            .and_then(|()| automation.observe(&observation.snapshot)),
        StatusUpdate::Reconnecting => return automation.unavailable(),
    };
    if let Err(error) = observed {
        let _ = automation.unavailable();
        return Err(error);
    }
    Ok(())
}

/// The installed role: the runner over the Windows user-configured power mode.
pub(super) async fn run(
    origin: Arc<crate::loaded_origin::Origin>,
    mut stop: watch::Receiver<bool>,
    automation: Automation,
) -> io::Result<()> {
    super::verify_origin(&origin)?;
    let mut supplier = Some(windows::UserPowerModes);
    serve(
        &automation,
        async |supply, mode, cancel| blocking(&mut supplier, supply, mode, cancel).await,
        async |cancel| delay(cancel, RETRY_DELAY).await,
        &mut stop,
        || super::verify_origin(&origin),
        report,
    )
    .await
}

/// Run one supplier call on the blocking pool. The supplier comes back with
/// the result, so the next call reuses it.
async fn blocking<S: Supplier + Send + 'static>(
    supplier: &mut Option<S>,
    supply: Supply,
    mode: Mode,
    cancel: Arc<Cancel>,
) -> io::Result<Applied> {
    let mut held = supplier
        .take()
        .ok_or_else(|| io::Error::other("power-mode supplier lost"))?;
    let (held, applied) = tokio::task::spawn_blocking(move || {
        let applied = held.apply(supply, mode, &cancel.flag);
        (held, applied)
    })
    .await
    .map_err(|_| io::Error::other("power-mode worker failed"))?;
    *supplier = Some(held);
    Ok(applied)
}

/// The retry delay. False when the apply was canceled first.
async fn delay(cancel: &Cancel, delay: Duration) -> bool {
    tokio::select! {
        biased;
        () = cancel.canceled() => false,
        () = tokio::time::sleep(delay) => true,
    }
}

/// Managed `resultObserver`: a failed apply is traced; nothing else is.
fn report(outcome: &Outcome) {
    if let Applied::Failed(failure) = outcome.applied {
        record(Event::WindowsPowerMode(failure));
    }
}

/// The runner: waits for a scheduled apply or stop and runs each apply to its
/// end. Stop cancels the running apply and waits for its supplier call, so no
/// Windows call is in flight when the role drains. `verify` runs before each
/// supplier call; its failure ends the role.
async fn serve(
    automation: &Automation,
    mut call: impl AsyncFnMut(Supply, Mode, Arc<Cancel>) -> io::Result<Applied>,
    mut delay: impl AsyncFnMut(&Cancel) -> bool,
    stop: &mut watch::Receiver<bool>,
    verify: impl Fn() -> io::Result<()>,
    mut report: impl FnMut(&Outcome),
) -> io::Result<()> {
    let result = async {
        loop {
            if *stop.borrow() {
                return Ok(());
            }
            if !run_one(
                automation,
                &mut call,
                &mut delay,
                stop,
                &verify,
                &mut report,
            )
            .await?
            {
                tokio::select! {
                    biased;
                    () = stopped(stop) => return Ok(()),
                    () = automation.wake.notified() => (),
                }
            }
        }
    }
    .await;
    automation.retire();
    result
}

/// Managed `RunApplyAsync` for the scheduled apply, if any. False when nothing
/// was scheduled.
async fn run_one(
    automation: &Automation,
    call: &mut impl AsyncFnMut(Supply, Mode, Arc<Cancel>) -> io::Result<Applied>,
    delay: &mut impl AsyncFnMut(&Cancel) -> bool,
    stop: &mut watch::Receiver<bool>,
    verify: &impl Fn() -> io::Result<()>,
    report: &mut impl FnMut(&Outcome),
) -> io::Result<bool> {
    let Some(apply) = automation.take()? else {
        return Ok(false);
    };
    let mut attempt = 1;
    let applied = loop {
        if apply.cancel.is_canceled() {
            break None;
        }
        if let Err(error) = verify() {
            automation.complete(&apply, None)?;
            return Err(error);
        }
        let attempted = {
            let attempted = call(apply.supply, apply.mode, apply.cancel.clone());
            tokio::pin!(attempted);
            tokio::select! {
                biased;
                () = stopped(stop) => {
                    // A request; the supplier still finishes its Windows call.
                    apply.cancel.cancel();
                    attempted.await
                }
                applied = &mut attempted => applied,
            }
        };
        let applied = match attempted {
            Ok(applied) => applied,
            Err(error) => {
                automation.complete(&apply, None)?;
                return Err(error);
            }
        };
        match applied {
            Applied::Canceled => break None,
            Applied::Succeeded { .. } => break Some(applied),
            Applied::Failed(_) if attempt == MAXIMUM_ATTEMPTS => break Some(applied),
            Applied::Failed(_) => (),
        }
        let delayed = tokio::select! {
            biased;
            () = stopped(stop) => false,
            delayed = delay(&apply.cancel) => delayed,
        };
        if !delayed {
            break None;
        }
        attempt += 1;
    };
    if let Some(outcome) = automation.complete(&apply, applied)? {
        report(&outcome);
    }
    Ok(true)
}
