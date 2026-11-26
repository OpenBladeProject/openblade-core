//! Managed `SetLightingOverrideAsync` on exact RZ09-0581, slice L1: the
//! keyboard Off and the logo modes through the reviewed brightness and logo
//! commands (`LightingJob`), under the operation gate.
//!
//! The order is managed `ExecuteScopedMutationAsync`'s (ServiceControlRuntime.cs
//! 14868-15060) for the Lighting subsystem:
//!
//! | Managed | Native reply |
//! |---|---|
//! | 14903-14919 low-power gate set: store, then `DeferLightingIntentForLowPower` (20209) | Stored, `LOW_POWER` |
//! | 14921-14924 `PendingWriteBlockedResult` (19255) | Blocked, `PENDING`, after a read-only resolution |
//! | 14926-14943 power resolution and unclassified adapter | Blocked, `UNCLASSIFIED` |
//! | 14965-14981 store (`SetLightingOverrideCoreAsync`, 14820) | Failed, `The requested intent was rejected: ...` |
//! | 15034-15053 apply, prefixed with the committed message unless Applied | see `reply` |
//!
//! Interim divergences that slice L2 removes, each named by a test:
//!
//! - Capabilities admit the keyboard Off only, with every logo mode; a lit
//!   effect is stored and then refused as managed refuses an effect its
//!   capability table lacks (`lit_effects_are_refused_until_l2_interim_divergence`).
//! - Clearing the override stores the clear and applies no profile lighting:
//!   managed applies the profile's lighting there (ControlCoordinator.cs
//!   `ApplyLightingOutcomeAsync`), which is profile reconciliation.
//! - Power-source transitions (`ApplyFullTransitionAsync`, 14948-14956) and
//!   profile reconciliation run no lighting step.
//!
//! Lighting reads no adapter, so it cannot tell a classified adapter from an
//! unclassified one. Every AC request therefore meets managed's unclassified
//! admission (ServiceControlRuntime.UnclassifiedExternalPower.cs 60-84):
//! fresh Windows AC presence and HyperBoost disengaged in status. Managed
//! also refuses there while its status blocks writes; native status blocks
//! writes for reasons of its own (`writes_blocked`), so that member is not
//! consulted, and fresh admission before each exchange stands in for it.
//!
//! An uncertain submission latches this process: the next request runs a
//! read-only resolution instead of any write and replies managed's pending
//! refusal, and a complete read clears the latch. Nothing is replayed.
use super::{
    Caller, Fresh, Logon, MailboxSource, Outcome, Runtime, is_fatal_admission, remember_failure,
    status_query_power, verify_retained_failure,
};
use crate::{
    discovery::performance::{
        LightingJob, LightingOutcome, LightingReading, LightingTarget, ObservedPerformance,
        ReadAdmission, lighting_approved,
        mutation::{Permit, Settlement},
    },
    service_status::{LightingOverride, Snapshot},
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

#[cfg(feature = "service-profile-triggers-v1")]
const STORED: u8 = 0;
const APPLIED: u8 = 1;
const FAILED: u8 = 3;

const COMMITTED: &str = "Lighting override intent was stored.";
const CLEARED: &str = "Lighting override was cleared.";
const ALREADY: &str = "Lighting is already confirmed.";
const CONFIRMED: &str = "Lighting setting applied and confirmed by readback.";
const MISMATCH: &str = "Lighting write is indeterminate because readback did not match.";
const UNSUPPORTED: &str = "Lighting value is not supported by the verified capability table.";
const PENDING: &str =
    "Lighting was not written because OpenBlade must confirm the current laptop state first.";
#[cfg(feature = "service-profile-triggers-v1")]
const LOW_POWER: &str = "Lighting intent was stored and will be applied after standby.";
const ROLLBACK: &str =
    "The lighting change failed and the previous lighting state could not be fully restored.";
const PARTIAL: &str = "The lighting transition stopped after partial submission.";
const UNCLASSIFIED: &str = "Hardware application was not confirmed. The external power adapter could not be classified; overrides and control writes are blocked.";
const ACTIVE_USER: &str = "The active interactive Windows user is required.";
const CUSTODY: &str = "Authenticated thermal custody is unavailable.";
const ADMISSION: &str = "Current lighting write admission is unavailable.";
const NOT_SUBMITTED: &str =
    "Current lighting write admission is unavailable; no lighting setter was submitted.";
const UNPACED: &str = "The remaining request time cannot cover this recipe at the measured device pace; no lighting setter was submitted.";

/// What a lighting request leaves for status: whether intent or a reading
/// changed, and the reading to publish.
pub(in super::super) struct Lit {
    pub(in super::super) outcome: Outcome,
    pub(in super::super) publish: bool,
    pub(in super::super) reading: Option<LightingReading>,
}
impl Lit {
    fn refused(outcome: Outcome) -> Self {
        Self {
            outcome,
            publish: false,
            reading: None,
        }
    }
}

/// One lighting job's permit: its request's fresh admission before every
/// exchange. It keeps no journal row.
struct LightingPermit {
    fresh: Arc<dyn Fresh>,
    job: LightingJob,
    fatal: Arc<Mutex<Option<&'static str>>>,
}
impl Permit for LightingPermit {
    fn lighting(&self) -> Option<&LightingJob> {
        Some(&self.job)
    }
    fn authorize(&self) -> Option<ReadAdmission> {
        self.fresh.authorize()
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
/// `ProductionLightingPort.SetLightingAsync` results for a finished job, as
/// managed words them, and whether a setter may be in doubt. Managed's
/// transport descriptions (`NativeFailureDescription.Describe`) carry
/// diagnostic detail native cannot read, so native names the stop instead.
fn reply(job: &LightingJob, target: LightingTarget) -> (Outcome, bool) {
    let failed = |message: String| Outcome {
        status: FAILED,
        message,
    };
    let indeterminate = |reason: &str| failed(format!("Lighting write is indeterminate: {reason}"));
    match job.outcome() {
        LightingOutcome::Observed => (
            Outcome {
                status: APPLIED,
                message: ALREADY.into(),
            },
            false,
        ),
        LightingOutcome::Written => {
            let confirmed = job.final_reading().is_some_and(|reading| {
                reading.brightness == Some(0)
                    && target.logo.is_none_or(|mode| reading.logo == Some(mode))
            });
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
        LightingOutcome::Unpaced => (
            failed(format!("Lighting write was not applied: {UNPACED}")),
            false,
        ),
        LightingOutcome::Refused | LightingOutcome::Pending => (
            failed(format!("Lighting write was not applied: {NOT_SUBMITTED}")),
            false,
        ),
    }
}

/// The request as `set_lighting` reads it.
pub(in super::super) struct LightingRequest {
    pub(in super::super) caller: Caller,
    pub(in super::super) sid: String,
    pub(in super::super) value: Option<LightingOverride>,
    pub(in super::super) deadline: Instant,
    pub(in super::super) status: Snapshot,
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
            status,
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
            Err(error) => {
                return Ok(Lit::refused(Outcome::blocked(format!(
                    "Saved configuration or fresh admission is unavailable: {error}"
                ))));
            }
        };
        fresh.verify_health()?;
        reader.retain_policy(self.thermal_policy(sid.clone(), caller.session, reader.custody()));
        let power = status.thermal_power_source();
        let store = |power: i32| -> io::Result<Result<(), Lit>> {
            match storage.save_lighting(value.as_ref(), power) {
                Ok(()) => Ok(Ok(())),
                Err(error) if is_fatal_admission(&error) => Err(error),
                Err(error) => Ok(Err(Lit::refused(Outcome {
                    status: FAILED,
                    message: format!("The requested intent was rejected: {error}"),
                }))),
            }
        };
        // Managed stores lighting intent at low power before any other gate
        // and applies it after standby; no read or write is admitted here.
        #[cfg(feature = "service-profile-triggers-v1")]
        if self.in_low_power() {
            if !fresh.authorize_storage() {
                return Ok(Lit::refused(Outcome::blocked(ADMISSION)));
            }
            if let Err(refused) = store(power)? {
                return Ok(refused);
            }
            return Ok(Lit {
                outcome: Outcome {
                    status: STORED,
                    message: LOW_POWER.into(),
                },
                publish: true,
                reading: None,
            });
        }
        if self.lighting_doubt.get() {
            return self.resolve_lighting_doubt(&reader, fresh, deadline).await;
        }
        // Managed classifies the adapter first and admits lighting on an
        // unclassified one only under fresh external presence with HyperBoost
        // disengaged (`CanAdmitUnclassifiedExternalControl`). Lighting reads no
        // adapter here, so every AC request needs both: fresh Windows power
        // that agrees with status (`status_query_power`), and HyperBoost
        // disengaged in status. A failed or disagreeing read is no class.
        let query = status_query_power(power, fresh.as_ref());
        if query == -1 || query == UNCLASSIFIED_EXTERNAL && status.hyperboost_engaged() {
            return Ok(Lit::refused(Outcome::blocked(UNCLASSIFIED)));
        }
        let proof = fresh.authorize();
        fresh.verify_health()?;
        if proof.is_none() {
            return Ok(Lit::refused(Outcome::blocked(ADMISSION)));
        }
        if let Err(refused) = store(query)? {
            return Ok(refused);
        }
        let committed = if value.is_some() { COMMITTED } else { CLEARED };
        let prefixed = |outcome: Outcome| Outcome {
            status: outcome.status,
            message: format!("{committed} Hardware result: {}", outcome.message),
        };
        let Some(target) = value.as_ref().and_then(|value| target(&value.settings)) else {
            return Ok(Lit {
                outcome: prefixed(Outcome::blocked(UNSUPPORTED)),
                publish: true,
                reading: None,
            });
        };
        // Doubt holds from before the job can submit until its outcome is
        // read, so a request dropped meanwhile leaves the latch set.
        self.lighting_doubt.set(true);
        let permit = Arc::new(LightingPermit {
            fresh,
            job: LightingJob::new(Some(target), false),
            fatal: self.fatal.clone(),
        });
        let result = reader.mutate(permit.clone(), deadline).await;
        verify_retained_failure(&self.fatal)?;
        if let Err(error) = result
            && is_fatal_admission(&error)
        {
            return Err(error);
        }
        let (outcome, in_doubt) = reply(&permit.job, target);
        self.lighting_doubt.set(in_doubt);
        let outcome = if outcome.status == APPLIED {
            outcome
        } else {
            prefixed(outcome)
        };
        Ok(Lit {
            outcome,
            publish: true,
            reading: permit.job.final_reading(),
        })
    }

    /// Managed's pending refusal after a possible submission: a read-only
    /// job under this request's admission, which writes nothing, and whose
    /// complete read clears the latch. Its reading is published either way.
    async fn resolve_lighting_doubt(
        &self,
        reader: &super::super::mailbox_read::ReadSource,
        fresh: Arc<dyn Fresh>,
        deadline: Instant,
    ) -> io::Result<Lit> {
        let permit = Arc::new(LightingPermit {
            fresh,
            job: LightingJob::new(None, false),
            fatal: self.fatal.clone(),
        });
        let result = reader.mutate(permit.clone(), deadline).await;
        verify_retained_failure(&self.fatal)?;
        if let Err(error) = result
            && is_fatal_admission(&error)
        {
            return Err(error);
        }
        let read = permit.job.outcome() == LightingOutcome::Observed;
        if read {
            self.lighting_doubt.set(false);
        }
        Ok(Lit {
            outcome: Outcome::blocked(PENDING),
            publish: read,
            reading: permit.job.final_reading().filter(|_| read),
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

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
        let target = LightingTarget {
            brightness: 0,
            logo: Some(LogoMode::Off),
        };
        let cases: [(LightingOutcome, u8, &str, bool); 10] = [
            (LightingOutcome::Observed, APPLIED, ALREADY, false),
            (LightingOutcome::Written, FAILED, MISMATCH, false),
            (
                LightingOutcome::Mismatched {
                    failure: "Lighting brightness did not match independent readback.",
                    restored: Some(true),
                },
                FAILED,
                "Lighting write is indeterminate: Lighting brightness did not match independent readback.",
                false,
            ),
            (
                LightingOutcome::Mismatched {
                    failure: "x",
                    restored: Some(false),
                },
                FAILED,
                "Lighting write is indeterminate: The lighting change failed and the previous lighting state could not be fully restored.",
                false,
            ),
            (
                LightingOutcome::Mismatched {
                    failure: "x",
                    restored: None,
                },
                FAILED,
                "Lighting write is indeterminate: The lighting change failed and the previous lighting state could not be fully restored.",
                true,
            ),
            (
                LightingOutcome::InDoubt(
                    "A logo command may have been submitted and was not confirmed.",
                ),
                FAILED,
                "Lighting write is indeterminate: A logo command may have been submitted and was not confirmed.",
                true,
            ),
            (
                LightingOutcome::Interrupted,
                FAILED,
                "Lighting write is indeterminate: The lighting transition stopped after partial submission.",
                false,
            ),
            (
                LightingOutcome::Unread("Logo lighting could not be read."),
                FAILED,
                "Lighting could not be read before writing: Logo lighting could not be read.",
                false,
            ),
            (
                LightingOutcome::Unpaced,
                FAILED,
                "Lighting write was not applied: The remaining request time cannot cover this recipe at the measured device pace; no lighting setter was submitted.",
                false,
            ),
            (
                LightingOutcome::Refused,
                FAILED,
                "Lighting write was not applied: Current lighting write admission is unavailable; no lighting setter was submitted.",
                false,
            ),
        ];
        for (outcome, status, message, doubt) in cases {
            let job = LightingJob::recorded(outcome, None);
            let (reply, latched) = reply(&job, target);
            assert_eq!(
                (reply.status, reply.message.as_str(), latched),
                (status, message, doubt)
            );
        }
        let confirmed = LightingJob::recorded(
            LightingOutcome::Written,
            Some(LightingReading {
                brightness: Some(0),
                logo: Some(LogoMode::Off),
            }),
        );
        assert_eq!(reply(&confirmed, target).0.message, CONFIRMED);
        let lit_logo = LightingJob::recorded(
            LightingOutcome::Written,
            Some(LightingReading {
                brightness: Some(0),
                logo: Some(LogoMode::Static),
            }),
        );
        assert_eq!(reply(&lit_logo, target).0.message, MISMATCH);
    }
}
