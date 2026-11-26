//! Managed `SetLightingOverrideAsync` on exact RZ09-0581, slice L1: the
//! keyboard Off and the logo modes through the reviewed brightness and logo
//! commands (`LightingJob`), under the operation gate.
//!
//! The order is managed `ExecuteScopedMutationAsync`'s (ServiceControlRuntime.cs
//! 14868-15060) for the Lighting subsystem:
//!
//! | Managed | Native reply |
//! |---|---|
//! | 14903-14919 low-power gate set: store, then `DeferLightingIntentForLowPower` (20209) | Stored, the committed message; a temporary override off Windows battery is Blocked, `LOW_POWER_TEMPORARY` (see below) |
//! | 14921-14924 `PendingWriteBlockedResult` (19255) | Blocked, `PENDING`, after a read-only resolution |
//! | 14926-14943 power resolution and unclassified adapter | Blocked, `UNCLASSIFIED_BLOCKED`, in the job (`LightingPermit::store_lighting`) |
//! | 14965-14981 store (`SetLightingOverrideCoreAsync`, 14820) | Failed, `The requested intent was rejected: ...`, in the job |
//! | 15005-15013 a clear on an unclassified adapter | Stored, `CLEARED_UNCLASSIFIED` |
//! | 15034-15053 apply, prefixed with the committed message unless Applied | see `reply` |
//! | 15213-15216 `IsKeyboardLightingDelegated`, in the apply after the store | Blocked, `DELEGATED`, before the store (stricter) |
//!
//! Stricter than managed, which stores the intent in each case and then
//! refuses only the apply:
//!
//! - A keyboard delegated to Windows Dynamic Lighting is refused before the
//!   store; managed stores and replies with the committed prefix
//!   (`a_keyboard_delegated_to_dynamic_lighting_blocks_before_any_store`).
//! - A competing controller is refused by the fresh admission before the job
//!   (`capture_intent`) and at each of its checkpoints, so nothing is stored.
//!   Managed's refresh after power resolution
//!   (`RefreshControllerPresenceAndRelinquishLightingAsync`) only observes the
//!   controller and relinquishes lighting; it stores, and the apply is refused
//!   (`refused_admission_stores_and_sends_nothing`).
//! - An unclassified adapter also needs a fresh firmware observation that
//!   HyperBoost is not engaged, which managed's Lighting arm does not ask for
//!   (`admits_unclassified`).
//! - A temporary override in low power needs Windows battery; managed stores
//!   it with no power class
//!   (`a_low_power_temporary_override_needs_windows_battery_divergence`).
//!
//! Interim divergences that slice L2 removes, each named by a test:
//!
//! - Capabilities admit the keyboard Off only, with every logo mode; a lit
//!   effect is stored and then refused as managed refuses an effect its
//!   capability table lacks (`lit_effects_are_refused_until_l2_interim_divergence`).
//! - Clearing the override on a classified adapter stores the clear, applies
//!   no profile lighting and replies Stored, `CLEARED_UNAPPLIED`: managed
//!   applies the profile's lighting there (ControlCoordinator.cs
//!   `ApplyLightingOutcomeAsync`), which is profile reconciliation
//!   (`a_clear_is_stored_and_says_what_waits`).
//! - Power-source transitions (`ApplyFullTransitionAsync`, 14948-14956) and
//!   profile reconciliation run no lighting step, so a request stored while
//!   the low-power gate is set is not applied after standby. Its reply is
//!   managed's committed message, Stored, instead of managed's promise to
//!   apply it (`the_low_power_store_promises_no_resume_apply_interim_divergence`).
//!
//! Power is classified inside the job, under the gate and the job's own
//! admission, from Windows and the admitted 07/8C reading
//! (`Reader::lighting_power`). A classified adapter, and Windows battery,
//! admit lighting. An unclassified adapter needs managed's
//! `CanAdmitUnclassifiedExternalControl` (UnclassifiedExternalPower.cs 60-77)
//! for Lighting, from native's own fresh evidence, and the stricter
//! HyperBoost observation (`admits_unclassified`).
//!
//! An uncertain submission latches this process: the next request runs a
//! read-only resolution instead of any write and replies managed's pending
//! refusal, and a complete read clears the latch. Nothing is replayed.
use super::{
    Caller, Fresh, Logon, MailboxSource, Outcome, Runtime, Storage, UNCLASSIFIED_BLOCKED,
    is_fatal_admission, remember_failure, verify_retained_failure,
};
use crate::{
    discovery::performance::{
        LightingConfirmation, LightingJob, LightingOutcome, LightingTarget, ObservedPerformance,
        ReadAdmission, lighting_approved,
        mutation::{Permit, Settlement},
    },
    service_status::LightingOverride,
};
use openblade_domain::{
    automation::UNCLASSIFIED_EXTERNAL,
    profile_set::{LightingEffect, LightingSettings},
    thermal_mutation::{Destination, Progress},
};
use openblade_protocol::lighting::LogoMode;
use std::{
    io,
    sync::{Arc, Mutex, atomic::AtomicBool},
};
use tokio::time::Instant;

const STORED: u8 = 0;
const APPLIED: u8 = 1;
const FAILED: u8 = 3;

const COMMITTED: &str = "Lighting override intent was stored.";
/// A clear stored while the low-power gate is set (`store_deferred`).
#[cfg(feature = "service-profile-triggers-v1")]
const CLEARED: &str = "Lighting override was cleared.";
/// A clear on a classified adapter, which applies no profile lighting until
/// slice L2.
const CLEARED_UNAPPLIED: &str =
    "Lighting override was cleared. The profile's lighting was not applied.";
/// Managed's clear on an unclassified adapter (ServiceControlRuntime.cs 15012).
const CLEARED_UNCLASSIFIED: &str =
    "Override cleared. Power-profile application waits for adapter classification.";
/// A temporary override while the low-power gate is set, off Windows battery.
#[cfg(feature = "service-profile-triggers-v1")]
const LOW_POWER_TEMPORARY: &str = "A temporary lighting override was not stored: OpenBlade does not read the power source while the laptop is entering or leaving a low-power state.";
const ALREADY: &str = "Lighting is already confirmed.";
const CONFIRMED: &str = "Lighting setting applied and confirmed by readback.";
const MISMATCH: &str = "Lighting write is indeterminate because readback did not match.";
const UNSUPPORTED: &str = "Lighting value is not supported by the verified capability table.";
const PENDING: &str =
    "Lighting was not written because OpenBlade must confirm the current laptop state first.";
/// Managed `WindowsDynamicLightingOwnsKeyboard`.
#[cfg(feature = "service-profiles-v1")]
const DELEGATED: &str = "Keyboard lighting is delegated to Windows Dynamic Lighting. OpenBlade did not write the keyboard.";
const ROLLBACK: &str =
    "The lighting change failed and the previous lighting state could not be fully restored.";
const PARTIAL: &str = "The lighting transition stopped after partial submission.";
const ACTIVE_USER: &str = "The active interactive Windows user is required.";
const CUSTODY: &str = "Authenticated thermal custody is unavailable.";
const ADMISSION: &str = "Current lighting write admission is unavailable.";
const NOT_SUBMITTED: &str =
    "Current lighting write admission is unavailable; no lighting setter was submitted.";
const UNPACED: &str = "The remaining request time cannot cover this recipe at the measured device pace; no lighting setter was submitted.";

/// What a lighting request leaves for status: whether intent or a reading
/// changed, and what the job leaves the confirmed lighting members.
pub(in super::super) struct Lit {
    pub(in super::super) outcome: Outcome,
    pub(in super::super) publish: bool,
    pub(in super::super) confirmation: LightingConfirmation,
}
impl Lit {
    fn refused(outcome: Outcome) -> Self {
        Self {
            outcome,
            publish: false,
            confirmation: LightingConfirmation::default(),
        }
    }
}

/// What the job's power admission and store decided (`store_lighting`).
enum Admission {
    /// The intent was stored at this power class.
    Stored(i32),
    /// Nothing was stored, with this reply.
    Refused(Outcome),
}

/// One lighting job's permit: its request's fresh admission before every
/// exchange, and the power admission and store the job asks for once it has
/// classified power. It keeps no journal row.
struct LightingPermit {
    fresh: Arc<dyn Fresh>,
    storage: Arc<Storage>,
    value: Option<LightingOverride>,
    #[cfg(feature = "service-hyperboost-v1")]
    hyperboost: Option<super::Probes>,
    job: LightingJob,
    admission: Mutex<Option<Admission>>,
    fatal: Arc<Mutex<Option<&'static str>>>,
}
impl LightingPermit {
    /// Managed `CanAdmitUnclassifiedExternalControl(Lighting)`
    /// (UnclassifiedExternalPower.cs 60-77) from native's own fresh evidence,
    /// plus one stricter clause. Fresh external presence was required by the
    /// job (`Reader::lighting_power`). `Storage::pending` is the store's
    /// quarantine or an unsettled last row of the one native journal, which
    /// holds the thermal, fan and battery rows; native keeps no other
    /// battery or HyperBoost doubt, and every native HyperBoost write is a
    /// journaled thermal step, so it stands for managed's pending
    /// indeterminate HyperBoost intents. It is not managed's `WritesBlocked`
    /// status flag; each exchange's fresh admission is native's gate. With
    /// owned HyperBoost, managed's journal safety block and pending restore
    /// are its journal (`Probes`), and native's own session is its history
    /// (`Storage::owns_hyperboost`). Stricter than managed, whose Lighting arm
    /// has no `HyperBoostActive` clause: a fresh firmware observation must
    /// confirm HyperBoost inactive, and one that failed admits nothing.
    fn admits_unclassified(&self, performance: Option<ObservedPerformance>) -> bool {
        performance.is_some_and(|observed| observed != ObservedPerformance::HyperBoostObserved)
            && !self.storage.pending()
            && self.hyperboost_admits()
    }
    #[cfg(feature = "service-hyperboost-v1")]
    fn hyperboost_admits(&self) -> bool {
        #[cfg(feature = "service-profile-triggers-v1")]
        if self.storage.owns_hyperboost() {
            return false;
        }
        self.hyperboost
            .as_ref()
            .is_some_and(super::Probes::admits_unclassified_control)
    }
    #[cfg(not(feature = "service-hyperboost-v1"))]
    fn hyperboost_admits(&self) -> bool {
        true
    }
    fn store(&self, power: i32, performance: Option<ObservedPerformance>) -> Admission {
        if power == UNCLASSIFIED_EXTERNAL && !self.admits_unclassified(performance) {
            return Admission::Refused(Outcome::blocked(UNCLASSIFIED_BLOCKED));
        }
        // The class becomes current before the store, as a thermal step's
        // does, so the temporary carries this Windows power generation.
        let observed = match self.fresh.power_generation() {
            Some(generation) if self.fresh.authorize().is_some() => {
                self.storage.observe_power(power, generation)
            }
            _ => Err(io::ErrorKind::PermissionDenied.into()),
        };
        if let Err(error) = observed {
            remember_failure(&self.fatal, &error);
            return Admission::Refused(Outcome::blocked(ADMISSION));
        }
        match self.storage.save_lighting(self.value.as_ref(), power) {
            Ok(()) => Admission::Stored(power),
            Err(error) => {
                remember_failure(&self.fatal, &error);
                Admission::Refused(rejected(&error))
            }
        }
    }
    /// The reply, and whether a setter may be in doubt.
    fn finish(&self) -> (Lit, bool) {
        let admission = self.admission.lock().ok().and_then(|mut a| a.take());
        let power = match admission {
            Some(Admission::Stored(power)) => power,
            Some(Admission::Refused(outcome)) => return (Lit::refused(outcome), false),
            None => {
                let refusal = match self.job.outcome() {
                    LightingOutcome::Unclassified => UNCLASSIFIED_BLOCKED,
                    _ => ADMISSION,
                };
                return (Lit::refused(Outcome::blocked(refusal)), false);
            }
        };
        if self.value.is_none() {
            // A clear is a store-only job, which wrote nothing.
            return (stored_clear(power), false);
        }
        let (outcome, in_doubt) = reply(&self.job);
        let outcome = if outcome.status == APPLIED {
            outcome
        } else {
            Outcome {
                status: outcome.status,
                message: format!("{COMMITTED} Hardware result: {}", outcome.message),
            }
        };
        let lit = Lit {
            outcome,
            publish: true,
            confirmation: self.job.confirmation(),
        };
        (lit, in_doubt)
    }
}
impl Permit for LightingPermit {
    fn lighting(&self) -> Option<&LightingJob> {
        Some(&self.job)
    }
    fn authorize(&self) -> Option<ReadAdmission> {
        self.fresh.authorize()
    }
    fn windows_on_battery(&self) -> Option<bool> {
        self.fresh.on_battery()
    }
    fn store_lighting(&self, power: i32, performance: Option<ObservedPerformance>) -> bool {
        let admission = self.store(power, performance);
        let stored = matches!(admission, Admission::Stored(_));
        match self.admission.lock() {
            Ok(mut slot) => *slot = Some(admission),
            Err(_) => return false,
        }
        stored
    }
    fn resolve(
        &self,
        _: i32,
        _: ObservedPerformance,
        _: &AtomicBool,
    ) -> io::Result<Option<Destination>> {
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
            "A keyboard lighting write keeps no thermal journal row.",
        ))
    }
}

/// A stored clear's reply. On an unclassified adapter it is managed's:
/// profile application waits for adapter classification. On a classified
/// one managed applies the profile's lighting, which slice L1 does not.
fn stored_clear(power: i32) -> Lit {
    let message = if power == UNCLASSIFIED_EXTERNAL {
        CLEARED_UNCLASSIFIED
    } else {
        CLEARED_UNAPPLIED
    };
    Lit {
        outcome: Outcome {
            status: STORED,
            message: message.into(),
        },
        publish: true,
        confirmation: LightingConfirmation::default(),
    }
}

/// Managed's rejected intent (`RecordRejectedIntent`).
fn rejected(error: &io::Error) -> Outcome {
    Outcome {
        status: FAILED,
        message: format!("The requested intent was rejected: {error}"),
    }
}

/// The L1 target of `settings`, `None` when the verified capability table
/// does not admit it: the keyboard Off (well-formedness makes it black at
/// zero brightness) with an optional logo mode, under the pinned semantic
/// admission (`lighting_approved`).
fn target(settings: &LightingSettings) -> Option<LightingTarget> {
    if !lighting_approved()
        || settings.effect != LightingEffect::OFF
        || settings.independent_controls.is_some()
        || settings.controls_keyboard_effect
    {
        return None;
    }
    let logo = match settings.logo_mode.map(|mode| mode.0) {
        None => None,
        Some(0) => Some(LogoMode::Static),
        Some(1) => Some(LogoMode::Breathing),
        Some(2) => Some(LogoMode::Off),
        Some(_) => return None,
    };
    Some(LightingTarget {
        brightness: 0,
        logo,
    })
}

/// Managed `LogoLightingMode`'s wire value.
pub(in super::super) fn logo_wire(mode: LogoMode) -> i32 {
    match mode {
        LogoMode::Static => 0,
        LogoMode::Breathing => 1,
        LogoMode::Off => 2,
    }
}

/// Managed `ControlCoordinator.ApplyScopedAsync` and
/// `ProductionLightingPort.SetLightingAsync` results for a job that stored
/// its intent, as managed words them, and whether a setter may be in doubt.
/// Managed's transport descriptions (`NativeFailureDescription.Describe`)
/// carry diagnostic detail native cannot read, so native names the stop.
/// A final read that could not confirm the write is managed's indeterminate
/// write with confirmation unavailable, which managed holds as a pending
/// confirmation; native latches it the same way.
fn reply(job: &LightingJob) -> (Outcome, bool) {
    let failed = |message: String| Outcome {
        status: FAILED,
        message,
    };
    let indeterminate = |reason: &str| failed(format!("Lighting write is indeterminate: {reason}"));
    let not_applied = |reason: &str| failed(format!("Lighting write was not applied: {reason}"));
    match job.outcome() {
        LightingOutcome::Observed => (
            Outcome {
                status: APPLIED,
                message: ALREADY.into(),
            },
            false,
        ),
        LightingOutcome::Written => {
            let confirmed = job
                .target()
                .zip(job.final_reading())
                .is_some_and(|(target, reading)| target.matches(reading));
            if confirmed {
                (
                    Outcome {
                        status: APPLIED,
                        message: CONFIRMED.into(),
                    },
                    false,
                )
            } else {
                (failed(MISMATCH.into()), false)
            }
        }
        LightingOutcome::Unconfirmed(reason) => (indeterminate(reason), true),
        LightingOutcome::Mismatched {
            failure,
            restored: Some(true),
        } => (indeterminate(failure), false),
        LightingOutcome::Mismatched {
            restored: Some(false),
            ..
        } => (indeterminate(ROLLBACK), false),
        // A restore setter that may have been submitted unconfirmed.
        LightingOutcome::Mismatched { restored: None, .. } => (indeterminate(ROLLBACK), true),
        LightingOutcome::InDoubt(reason) => (indeterminate(reason), true),
        LightingOutcome::Interrupted => (indeterminate(PARTIAL), false),
        LightingOutcome::Unread(reason) => (
            failed(format!(
                "Lighting could not be read before writing: {reason}"
            )),
            false,
        ),
        LightingOutcome::Malformed => (
            failed(
                "Lighting could not be read before writing: Logo lighting response was malformed or unsupported."
                    .into(),
            ),
            false,
        ),
        LightingOutcome::Unpaced => (not_applied(UNPACED), false),
        // A store-only job for a value outside the capability table; a
        // clear gets its own reply (`stored_clear`).
        LightingOutcome::Stored => (Outcome::blocked(UNSUPPORTED), false),
        LightingOutcome::Refused
        | LightingOutcome::Pending
        | LightingOutcome::Unclassified
        | LightingOutcome::NotAdmitted => (not_applied(NOT_SUBMITTED), false),
    }
}

/// The request as `set_lighting` reads it.
pub(in super::super) struct LightingRequest {
    pub(in super::super) caller: Caller,
    pub(in super::super) sid: String,
    pub(in super::super) value: Option<LightingOverride>,
    pub(in super::super) deadline: Instant,
}

impl Runtime {
    /// Managed `SetLightingOverrideAsync` (ServiceControlRuntime.cs 3269),
    /// in the order the module documents.
    pub(in super::super) async fn set_lighting(
        &self,
        mailbox: MailboxSource,
        request: LightingRequest,
    ) -> io::Result<Lit> {
        #[cfg(test)]
        self.entered.set(self.entered.get() + 1);
        let LightingRequest {
            caller,
            sid,
            value,
            deadline,
        } = request;
        let _serial = self.gate.lock().await;
        verify_retained_failure(&self.fatal)?;
        if caller.logon != Logon::Interactive || caller.control_session != Some(caller.session) {
            return Ok(Lit::refused(Outcome::blocked(ACTIVE_USER)));
        }
        let MailboxSource::Reader(reader) = mailbox else {
            return Ok(Lit::refused(Outcome::blocked(CUSTODY)));
        };
        reader.custody().verify_integrity()?;
        let (storage, fresh) = match self.capture_intent(&sid, caller.session, reader.custody()) {
            Ok(captured) => captured,
            Err(error) if is_fatal_admission(&error) => return Err(error),
            Err(error) => return Ok(Lit::refused(unavailable(&error))),
        };
        fresh.verify_health()?;
        reader.retain_policy(self.thermal_policy(sid.clone(), caller.session, reader.custody()));
        #[cfg(feature = "service-profile-triggers-v1")]
        if self.in_low_power() {
            return store_deferred(&storage, fresh.as_ref(), value.as_ref());
        }
        if self.lighting_doubt.get() {
            return self
                .resolve_lighting_doubt(&reader, storage, fresh, deadline)
                .await;
        }
        // Stricter than managed, which stores the intent and refuses only the
        // apply with the committed prefix (15213): a delegated keyboard is
        // refused before the store. Without a profile store there are no
        // stored preferences, and managed's defaults do not delegate.
        #[cfg(feature = "service-profiles-v1")]
        match storage.lighting_delegated() {
            Ok(false) => {}
            Ok(true) => return Ok(Lit::refused(Outcome::blocked(DELEGATED))),
            Err(error) if is_fatal_admission(&error) => return Err(error),
            Err(error) => return Ok(Lit::refused(unavailable(&error))),
        }
        let target = value.as_ref().and_then(|value| target(&value.settings));
        let job = match target {
            Some(target) => LightingJob::write(target),
            None => LightingJob::store(),
        };
        // Doubt holds from before a write job can submit until its outcome
        // is read, so a request dropped meanwhile leaves the latch set.
        if target.is_some() {
            self.lighting_doubt.set(true);
        }
        let permit = Arc::new(LightingPermit {
            fresh,
            storage,
            value,
            #[cfg(feature = "service-hyperboost-v1")]
            hyperboost: self.hyperboost.get().cloned(),
            job,
            admission: Mutex::new(None),
            fatal: self.fatal.clone(),
        });
        let result = reader.mutate(permit.clone(), deadline).await;
        verify_retained_failure(&self.fatal)?;
        if let Err(error) = result
            && is_fatal_admission(&error)
        {
            return Err(error);
        }
        let (lit, in_doubt) = permit.finish();
        if target.is_some() {
            self.lighting_doubt.set(in_doubt);
        }
        Ok(lit)
    }

    /// Managed's pending refusal after a possible submission: a read-only
    /// job under this request's admission, which writes nothing, and whose
    /// complete read clears the latch. A correlated logo pair outside the
    /// contract is a complete read too: nothing is in doubt about what was
    /// sent, and the next write's own baseline refuses that pair. What it
    /// read is published either way.
    async fn resolve_lighting_doubt(
        &self,
        reader: &super::super::mailbox_read::ReadSource,
        storage: Arc<Storage>,
        fresh: Arc<dyn Fresh>,
        deadline: Instant,
    ) -> io::Result<Lit> {
        let permit = Arc::new(LightingPermit {
            storage,
            fresh,
            value: None,
            #[cfg(feature = "service-hyperboost-v1")]
            hyperboost: None,
            job: LightingJob::resolution(),
            admission: Mutex::new(None),
            fatal: self.fatal.clone(),
        });
        let result = reader.mutate(permit.clone(), deadline).await;
        verify_retained_failure(&self.fatal)?;
        if let Err(error) = result
            && is_fatal_admission(&error)
        {
            return Err(error);
        }
        let read = matches!(
            permit.job.outcome(),
            LightingOutcome::Observed | LightingOutcome::Malformed
        );
        if read {
            self.lighting_doubt.set(false);
        }
        let confirmation = permit.job.confirmation();
        Ok(Lit {
            outcome: Outcome::blocked(PENDING),
            publish: confirmation != LightingConfirmation::default(),
            confirmation,
        })
    }
}

/// A capture or saved configuration that cannot be read.
fn unavailable(error: &io::Error) -> Outcome {
    Outcome::blocked(format!(
        "Saved configuration or fresh admission is unavailable: {error}"
    ))
}

/// Managed's store while the low-power gate is set, before any other gate,
/// with no read or write. Only Windows battery classifies power without an
/// adapter reading, so a temporary override needs it, as an explicit thermal
/// request's deferred store does (`triggers::deferred_store_refusal`), and is
/// otherwise refused as `LOW_POWER_TEMPORARY`: stricter than managed, which
/// stores it with no power class (ServiceControlRuntime.cs 14903-14918). A
/// persistent override and a clear carry no power class. Nothing applies the
/// stored intent after standby in slice L1, so the reply is managed's
/// committed message, Stored, without managed's promise.
#[cfg(feature = "service-profile-triggers-v1")]
fn store_deferred(
    storage: &Storage,
    fresh: &dyn Fresh,
    value: Option<&LightingOverride>,
) -> io::Result<Lit> {
    use crate::service_status::LIGHTING_PERSISTENT;
    if !fresh.authorize_storage() {
        return Ok(Lit::refused(Outcome::blocked(ADMISSION)));
    }
    let temporary = value.is_some_and(|value| value.lifetime != LIGHTING_PERSISTENT);
    let power = if temporary {
        match (fresh.on_battery(), fresh.power_generation()) {
            (Some(true), Some(generation)) => {
                storage.observe_power(1, generation)?;
                1
            }
            (Some(true), None) => return Ok(Lit::refused(Outcome::blocked(ADMISSION))),
            _ => return Ok(Lit::refused(Outcome::blocked(LOW_POWER_TEMPORARY))),
        }
    } else {
        -1
    };
    if let Err(error) = storage.save_lighting(value, power) {
        if is_fatal_admission(&error) {
            return Err(error);
        }
        return Ok(Lit::refused(rejected(&error)));
    }
    Ok(Lit {
        outcome: Outcome {
            status: STORED,
            message: if value.is_some() { COMMITTED } else { CLEARED }.into(),
        },
        publish: true,
        confirmation: LightingConfirmation::default(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::discovery::performance::LightingReading;

    fn off(logo: Option<i32>) -> LightingSettings {
        let mut settings = LightingSettings::off();
        settings.logo_mode = logo.map(openblade_domain::profile_set::LogoLightingMode);
        settings
    }

    #[test]
    fn l1_admits_the_keyboard_off_with_every_logo_mode_only() {
        assert_eq!(
            target(&off(None)),
            Some(LightingTarget {
                brightness: 0,
                logo: None
            })
        );
        for (wire, mode) in [
            (0, LogoMode::Static),
            (1, LogoMode::Breathing),
            (2, LogoMode::Off),
        ] {
            assert_eq!(target(&off(Some(wire))).unwrap().logo, Some(mode));
            assert_eq!(logo_wire(mode), wire);
        }
        let mut lit = off(None);
        lit.effect = LightingEffect(1);
        assert_eq!(target(&lit), None);
        let mut keyboard = off(None);
        keyboard.controls_keyboard_effect = true;
        assert_eq!(target(&keyboard), None);
    }

    /// Every job outcome's managed reply, and which ones latch.
    #[test]
    fn each_outcome_replies_as_managed_and_only_doubt_latches() {
        let indeterminate = |reason: &str| format!("Lighting write is indeterminate: {reason}");
        let not_submitted = "Lighting write was not applied: Current lighting write admission is unavailable; no lighting setter was submitted.";
        let cases: Vec<(LightingOutcome, u8, String, bool)> = vec![
            (LightingOutcome::Observed, APPLIED, ALREADY.into(), false),
            (LightingOutcome::Written, FAILED, MISMATCH.into(), false),
            (
                LightingOutcome::Unconfirmed("Lighting brightness could not be read."),
                FAILED,
                indeterminate("Lighting brightness could not be read."),
                true,
            ),
            (
                LightingOutcome::Mismatched {
                    failure: "Lighting brightness did not match independent readback.",
                    restored: Some(true),
                },
                FAILED,
                indeterminate("Lighting brightness did not match independent readback."),
                false,
            ),
            (
                LightingOutcome::Mismatched {
                    failure: "x",
                    restored: Some(false),
                },
                FAILED,
                indeterminate(ROLLBACK),
                false,
            ),
            (
                LightingOutcome::Mismatched {
                    failure: "x",
                    restored: None,
                },
                FAILED,
                indeterminate(ROLLBACK),
                true,
            ),
            (
                LightingOutcome::InDoubt(
                    "A logo command may have been submitted and was not confirmed.",
                ),
                FAILED,
                indeterminate("A logo command may have been submitted and was not confirmed."),
                true,
            ),
            (
                LightingOutcome::Interrupted,
                FAILED,
                indeterminate(PARTIAL),
                false,
            ),
            (
                LightingOutcome::Unread("Logo lighting could not be read."),
                FAILED,
                "Lighting could not be read before writing: Logo lighting could not be read."
                    .into(),
                false,
            ),
            (
                LightingOutcome::Malformed,
                FAILED,
                "Lighting could not be read before writing: Logo lighting response was malformed or unsupported.".into(),
                false,
            ),
            (
                LightingOutcome::Unpaced,
                FAILED,
                "Lighting write was not applied: The remaining request time cannot cover this recipe at the measured device pace; no lighting setter was submitted.".into(),
                false,
            ),
            (LightingOutcome::Stored, 2, UNSUPPORTED.into(), false),
            (LightingOutcome::Refused, FAILED, not_submitted.into(), false),
            (LightingOutcome::Pending, FAILED, not_submitted.into(), false),
        ];
        for (outcome, status, message, doubt) in cases {
            let job = LightingJob::recorded(None, outcome, None);
            let (reply, latched) = reply(&job);
            assert_eq!(
                (reply.status, reply.message.as_str(), latched),
                (status, message.as_str(), doubt),
                "{outcome:?}"
            );
        }
    }

    /// A written job is confirmed only when its final read confirms every
    /// member its target sets (`LightingTarget::matches`).
    #[test]
    fn a_written_job_is_confirmed_by_a_matching_final_read_only() {
        let target = LightingTarget {
            brightness: 0,
            logo: Some(LogoMode::Off),
        };
        let written = |reading: LightingReading| {
            let job = LightingJob::recorded(Some(target), LightingOutcome::Written, Some(reading));
            reply(&job).0.message
        };
        assert_eq!(
            written(LightingReading {
                brightness: Some(0),
                logo: Some(LogoMode::Off),
            }),
            CONFIRMED
        );
        assert_eq!(
            written(LightingReading {
                brightness: Some(0),
                logo: Some(LogoMode::Static),
            }),
            MISMATCH
        );
    }
}
