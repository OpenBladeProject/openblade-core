//! Automatic display profile reconciliation: managed
//! `ServiceDisplayAutomationLoop` and the transition of
//! `DisplayAutomationCoordinator.RunTransitionAsync`.
//!
//! The status watcher hands every verified status to `Automation::observe`
//! (managed `Observe`) and every reconnect or unreadable status to
//! `Automation::unavailable` (managed `MarkUnavailable`), synchronously and in
//! order, after power admission took the same status. Scheduling fills one
//! slot and wakes `serve`, which runs one transition at a time on a blocking
//! thread and waits for nothing else: there is no timer, poll or queue. Stop
//! cancels the running transition and joins it, so its certainty is recorded
//! before the feature drains.
//!
//! A transition writes only through the reviewed `applyProfile` composition
//! (`profile_apply::transition`) with each writer's read-before-write,
//! fence and readback, under the display operation gate that also
//! serializes every manual query and write (managed `operationGate`). It
//! stops at the first refused or ambiguous step and never replays: only a
//! Failed or Blocked attempt, which submitted nothing, is retried once after
//! `RETRY_DELAY`, and an Indeterminate result pauses automation until an
//! explicit read confirms the display.
use super::{
    observation::{SharedBrightness, Snapshot},
    profile_apply::{self, Applied, ApplyProfile, Settings},
    recovery::{CheckpointFile, Fields, Recovery, Status, Target, Transition, baseline},
    write::{Gate, WindowsHost, lock, refreshed},
};
use crate::{service_status, session_native::power_admission::PowerAdmission};
use serde_json::Value;
use std::{
    borrow::Cow,
    io,
    sync::{Arc, Mutex, atomic::AtomicBool},
    time::Duration,
};
use tokio::sync::{Notify, watch};

mod target;

/// Managed `MaximumTransitionAttempts`.
const MAXIMUM_ATTEMPTS: u32 = 2;
/// Managed `TransitionRetryDelay`.
pub(super) const RETRY_DELAY: Duration = Duration::from_secs(1);
/// Managed `RunTransitionAsync`: an application owned a refresh rate that its
/// baseline cannot restore.
pub(super) const REFRESH_UNRESTORABLE: &str = "The application's previous refresh rate could not be restored because its baseline is unknown.";

/// The display reads and writes one transition uses. Production is
/// `ProfileApplier`; tests script it.
pub(super) trait Applier {
    /// Managed `QueryAsync`: an interrupted read is an unavailable snapshot.
    fn query(&mut self, canceled: &AtomicBool) -> io::Result<Snapshot>;
    /// Managed `ApplyProfileForTransitionAsync`. An error means nothing ran.
    fn apply(
        &mut self,
        request: &ApplyProfile,
        admitted: crate::session_native::power_admission::PowerContext,
        canceled: &AtomicBool,
    ) -> io::Result<Applied>;
}

/// The production applier: the Writer's host, brightness owner and gate.
pub(super) struct ProfileApplier {
    pub(super) host: WindowsHost,
    pub(super) brightness: SharedBrightness,
    pub(super) gate: Gate,
}

impl Applier for ProfileApplier {
    fn query(&mut self, canceled: &AtomicBool) -> io::Result<Snapshot> {
        let _turn = super::write::turn(&self.gate)?;
        refreshed(self.host.query(&self.brightness, canceled))
    }

    fn apply(
        &mut self,
        request: &ApplyProfile,
        admitted: crate::session_native::power_admission::PowerContext,
        canceled: &AtomicBool,
    ) -> io::Result<Applied> {
        let _turn = super::write::turn(&self.gate)?;
        Ok(profile_apply::transition(
            &mut self.host,
            &self.brightness,
            request,
            admitted,
            canceled,
        ))
    }
}

/// Display automation shared by the status watcher, the Display endpoint and
/// the runner: the one recovery state, the trusted power admission and the
/// runner's wake.
pub(in crate::session_native) struct Automation<F = super::recovery::WindowsCheckpointFile> {
    recovery: Arc<Mutex<Recovery<F>>>,
    power: PowerAdmission,
    wake: Arc<Notify>,
}

impl<F> Clone for Automation<F> {
    fn clone(&self) -> Self {
        Self {
            recovery: self.recovery.clone(),
            power: self.power.clone(),
            wake: self.wake.clone(),
        }
    }
}

impl Automation {
    /// Blocking: reads the current user's managed checkpoint once.
    pub(in crate::session_native) fn load(power: PowerAdmission) -> Self {
        Self::new(
            Recovery::load(super::recovery::WindowsCheckpointFile::current_user()),
            power,
        )
    }
}

impl<F: CheckpointFile> Automation<F> {
    pub(super) fn new(mut recovery: Recovery<F>, power: PowerAdmission) -> Self {
        let wake = Arc::new(Notify::new());
        recovery.attach(power.clone(), wake.clone());
        Self {
            recovery: Arc::new(Mutex::new(recovery)),
            power,
            wake,
        }
    }

    pub(super) fn recovery(&self) -> Arc<Mutex<Recovery<F>>> {
        self.recovery.clone()
    }

    /// Managed `ServiceDisplayAutomationLoop` for a verified, fresh status:
    /// `Observe`, or `MarkUnavailable(manual)` for an unconfirmed or retained
    /// source. The display members are resolved before `admit` lets power
    /// admission take the status: one managed would refuse to decode is never
    /// admitted, and makes the context unavailable, as the managed watcher's
    /// failure does.
    pub(in crate::session_native) fn observe(
        &self,
        status: &service_status::Snapshot,
        admit: impl FnOnce(),
    ) -> io::Result<()> {
        let source = status.display_power_source();
        let target = match u8::try_from(source) {
            Ok(source @ 0..=2) => match target::resolve(status.display_automation(), source) {
                Some(target) => Some(target),
                None => {
                    self.power.unavailable();
                    return self.unavailable();
                }
            },
            _ => None,
        };
        admit();
        let mut recovery = lock(&self.recovery)?;
        match target {
            Some(target) => {
                recovery.observe(target);
            }
            None if source == 3 => {
                recovery.mark_unavailable(status.display_manual_external_power());
            }
            None => {
                recovery.mark_unavailable(false);
            }
        }
        Ok(())
    }

    /// Managed `MarkUnavailable()`: the subscription is reconnecting or its
    /// status could not be verified.
    pub(in crate::session_native) fn unavailable(&self) -> io::Result<()> {
        lock(&self.recovery)?.mark_unavailable(false);
        Ok(())
    }
}

/// The runner: waits for a scheduled transition or Stop, runs each
/// transition to completion on a blocking thread, and on Stop cancels the
/// running one and joins it before returning. A fatal transition error ends
/// the feature.
pub(super) async fn serve<F, A>(
    automation: &Automation<F>,
    mut applier: A,
    stop: &mut watch::Receiver<bool>,
) -> io::Result<()>
where
    F: CheckpointFile + 'static,
    A: Applier + Send + 'static,
{
    loop {
        if *stop.borrow() {
            return Ok(());
        }
        let next = lock(&automation.recovery)?.take_transition();
        let Some(transition) = next else {
            tokio::select! {
                biased;
                () = super::stopped(stop) => return Ok(()),
                () = automation.wake.notified() => continue,
            }
        };
        let recovery = automation.recovery.clone();
        let cancel = transition.cancel.clone();
        let work = tokio::task::spawn_blocking(move || {
            let result = run_transition(&recovery, &mut applier, &transition, |cancel, delay| {
                cancel.sleep(delay)
            });
            (applier, result)
        });
        tokio::pin!(work);
        let joined = tokio::select! {
            biased;
            () = super::stopped(stop) => {
                // Cancellation is a request; the transition still records
                // whatever it reached before the feature drains.
                cancel.cancel();
                (&mut work).await
            }
            joined = &mut work => joined,
        };
        let (returned, result) =
            joined.map_err(|_| io::Error::other("display automation worker failed"))?;
        applier = returned;
        result?;
    }
}

/// Managed `RunTransitionAsync`, blocking. `delay` is the retry delay; it
/// returns false when the transition was canceled first.
pub(super) fn run_transition<F: CheckpointFile>(
    recovery: &Mutex<Recovery<F>>,
    applier: &mut impl Applier,
    transition: &Transition,
    delay: impl Fn(&super::recovery::Cancel, Duration) -> bool,
) -> io::Result<()> {
    let target = &transition.target;
    let canceled = transition.cancel.flag();
    let (recorded, owned) = lock(recovery)?.transition_start();
    let baseline = match recorded {
        Some(recorded) => Some(recorded),
        None if target.application.is_some() => {
            let state = match applier.query(canceled) {
                Ok(state) => state,
                Err(error) => {
                    lock(recovery)?.fail_transition(transition, false, &error);
                    return Err(error);
                }
            };
            if !state.observed().available {
                lock(recovery)?.transition_unavailable(transition);
                return Ok(());
            }
            let state = baseline(&state);
            lock(recovery)?.transition_baseline(transition, state.clone());
            Some(state)
        }
        None => None,
    };
    let plan = plan(target, baseline.as_ref(), owned);
    let request = ApplyProfile {
        expected_power_source: target.source,
        settings: plan.settings,
    };
    let Some(owns) = lock(recovery)?.admit_transition(transition, plan.owned, &request.settings)
    else {
        return Ok(());
    };
    let mut attempt = 1;
    let applied = loop {
        let applied = match applier.apply(&request, transition.admitted, canceled) {
            Ok(applied) => applied,
            Err(error) => {
                lock(recovery)?.fail_transition(transition, owns, &error);
                return Err(error);
            }
        };
        let status = applied.outcome.status;
        if matches!(status, Status::Succeeded | Status::Indeterminate)
            || attempt == MAXIMUM_ATTEMPTS
            || applied.fatal.is_some()
        {
            break applied;
        }
        if !delay(&transition.cancel, RETRY_DELAY) {
            lock(recovery)?.cancel_transition(owns);
            return Ok(());
        }
        attempt += 1;
    };
    let Applied {
        outcome,
        unresolved,
        fatal,
    } = applied;
    let (status, error) = if outcome.status == Status::Succeeded && plan.refresh_unrestorable {
        (Status::Blocked, Some(Cow::Borrowed(REFRESH_UNRESTORABLE)))
    } else {
        (outcome.status, outcome.error)
    };
    lock(recovery)?.complete_transition(transition, owns, status, error, unresolved);
    fatal.map_or(Ok(()), Err)
}

/// The settings one transition writes and the ownership it records.
struct Plan {
    settings: Settings,
    owned: Fields,
    /// Managed `refreshRestoreBlocked`: the recorded ownership claims a
    /// refresh rate that the baseline cannot restore.
    refresh_unrestorable: bool,
}

/// Managed `RunTransitionAsync` before admission and `MergeDisplayTarget`:
/// an application owns only a refresh rate its baseline can restore; each
/// field comes from the application override, else from the baseline it
/// owns, else from the power profile.
fn plan(target: &Target, baseline: Option<&Value>, owned: Fields) -> Plan {
    let member = |name: &str| baseline.and_then(|baseline| baseline.get(name));
    let text = |name: &str| member(name).and_then(Value::as_str).map(str::to_owned);
    let refresh = text("RefreshRateKey").filter(|key| !key.trim().is_empty());
    let refresh_unrestorable = refresh.is_none() && owned.refresh;
    let mut application = target.application_settings.clone();
    if refresh.is_none()
        && let Some(application) = &mut application
    {
        application.refresh_rate_key = None;
    }
    let owned = owned.include(
        application
            .as_ref()
            .map(Settings::fields)
            .unwrap_or_default(),
    );
    let base = &target.base;
    let application = application.unwrap_or_default();
    let settings = Settings {
        refresh_rate_key: application.refresh_rate_key.or_else(|| {
            if owned.refresh {
                refresh.clone()
            } else {
                base.refresh_rate_key.clone()
            }
        }),
        hdr_enabled: application.hdr_enabled.or(if owned.hdr {
            // A recorded baseline is a whole managed snapshot, so its HDR
            // state is never missing; an unsupported panel's reads `false`.
            baseline.map(|_| {
                member("HdrEnabled")
                    .and_then(Value::as_bool)
                    .unwrap_or(false)
            })
        } else {
            base.hdr_enabled
        }),
        color_profile_key: application.color_profile_key.or_else(|| {
            if owned.color {
                text("ColorProfileKey")
            } else {
                base.color_profile_key.clone()
            }
        }),
        brightness_percent: application.brightness_percent.or(if owned.brightness {
            member("BrightnessPercent")
                .and_then(Value::as_u64)
                .and_then(|percent| u8::try_from(percent).ok())
        } else {
            base.brightness_percent
        }),
    };
    Plan {
        refresh_unrestorable,
        settings,
        owned,
    }
}

#[cfg(test)]
#[path = "automation_tests.rs"]
mod tests;
