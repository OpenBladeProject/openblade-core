//! Managed `ThermalConvergenceStatus` from what native knows at a status:
//! the saved policy, the admission facts managed's evaluation reads, and
//! the confirmed state compared with saved intent
//! (`ServiceControlRuntime.EvaluateThermalConvergenceObservationAsync` and
//! `ThermalConvergenceClassifier`).
//!
//! Native has no convergence loop: no durable ledger, budget, tombstone or
//! arbiter. Nothing here writes, and the policy changes no write: under
//! either policy confirmed drift only excludes automatic writes
//! (`thermal_drift`). The status says so. A contradiction that managed would
//! nominate for convergence under an admitted Maintain policy is published
//! as managed publishes a reconciliation that cannot hold its intent:
//! Paused with `InternalStateUnavailable`. Managed's Converging,
//! Suppressed, RestorePending and ReconciliationUnavailable states, and
//! the budget, tombstone and HyperBoost-journal reasons, need that loop and
//! are never published.
//!
//! Native derives the status from current facts at each publication.
//! Managed publishes AwaitingObservation right after a policy change until
//! its next observation; native publishes what that observation would give
//! from the confirmed state the status already carries.
use super::{Runtime, drift_intent};
use crate::{
    installed::readiness::thermal_storage::{SavedPolicy, Storage},
    service_status::{
        ConfirmedThermal, Snapshot,
        thermal_convergence::{
            Component, DISCLOSURE_NOT_REQUIRED, DISCLOSURE_PENDING, Reason, STRICT, State,
            ThermalConvergence,
        },
    },
};
use openblade_domain::{
    profile_set::{FanMode, application::Resolution},
    thermal_mutation::Destination,
};
use std::io;

/// The admission facts managed's evaluation reads before it classifies.
#[derive(Clone, Copy, Debug)]
pub(super) struct Admission {
    pub(super) policy: SavedPolicy,
    /// Managed `lowPowerGate.IsActive`.
    pub(super) low_power: bool,
    /// The power class the member is derived at (`convergence_power`):
    /// PluggedIn, Battery or UsbC only when fresh Windows power agrees with
    /// the status or with this user's last classified step; anything else
    /// is unsettled.
    pub(super) power: i32,
    /// Managed `FanControllerAdmissionUnsafe`: the status names a possible
    /// competing controller.
    pub(super) controller_present: bool,
    /// Managed `pendingWriteConfirmations.HasPending`: a journaled thermal
    /// write that is not settled (`Storage::pending`).
    pub(super) pending: bool,
}

/// Saved thermal intent at the status's power class, as managed's ledger
/// resolves it (`ResolveHardwareDesired`): the performance destination,
/// `None` when native cannot resolve it (such as an incomplete GPU clock
/// policy), and the saved fan mode. Managed's experimental-fan projection
/// is a drift rule only; convergence compares the saved mode itself.
#[derive(Clone, Copy, Debug)]
pub(super) struct Intent {
    pub(super) performance: Option<Destination>,
    pub(super) fans: FanMode,
}

/// What the classifier compares.
#[derive(Clone, Copy, Debug)]
pub(super) struct Observation {
    pub(super) confirmed: ConfirmedThermal,
    pub(super) desired: Intent,
    /// Managed `IsExactFanIntentOwned`.
    pub(super) owns_manual_fans: bool,
}

/// Managed `ThermalConvergenceObservationKind`.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) enum Kind {
    Matched,
    AwaitingObservation,
    /// A contradiction managed would nominate for convergence, by
    /// component (managed `ThermalConvergenceContradiction`).
    Candidate {
        performance: bool,
        fans: bool,
    },
    ManualTopologyOnly,
    ExternalHyperBoost,
}

/// The status managed would publish for `admission`, classifying the
/// observation `observe` reads only once every admission check passed.
pub(super) fn derive(
    admission: &Admission,
    observe: impl FnOnce() -> io::Result<Observation>,
) -> io::Result<ThermalConvergence> {
    // Managed `BatteryConfigurationStore` quarantine preferences, and
    // `PublishThermalConvergenceStatus`, which reports Strict while the
    // configuration is not authoritative.
    let (policy, disclosure) = match admission.policy {
        SavedPolicy::Quarantined => (STRICT, DISCLOSURE_NOT_REQUIRED),
        SavedPolicy::Admitted { policy, disclosure } => (policy, disclosure),
    };
    let publish = |state, components: &[Component], reason, explicit| {
        Ok(ThermalConvergence::new(
            policy, disclosure, state, components, reason, explicit,
        ))
    };
    let paused = |reason| publish(State::Paused, &[], reason, false);
    // Managed `ResolveThermalConvergenceClosureReason` order.
    if admission.policy == SavedPolicy::Quarantined {
        return paused(Reason::ConfigurationQuarantined);
    }
    if policy == STRICT {
        return paused(Reason::StrictPolicy);
    }
    if disclosure == DISCLOSURE_PENDING {
        return paused(Reason::UpgradeDisclosurePending);
    }
    if admission.low_power {
        return paused(Reason::LowPower);
    }
    if !(0..=2).contains(&admission.power) {
        return paused(Reason::PowerUnsettled);
    }
    if admission.controller_present {
        return paused(Reason::CompetingControllerPresent);
    }
    if admission.pending {
        return paused(Reason::PendingReconciliation);
    }
    match classify(&observe()?) {
        Kind::Matched => publish(State::Matched, &[], Reason::None, false),
        Kind::AwaitingObservation => publish(
            State::AwaitingObservation,
            &[],
            Reason::AwaitingFreshObservation,
            false,
        ),
        Kind::ManualTopologyOnly => publish(
            State::Paused,
            &[Component::Fans],
            Reason::ManualFanReadbackAmbiguous,
            true,
        ),
        Kind::ExternalHyperBoost => publish(
            State::External,
            &[Component::Performance, Component::Fans],
            Reason::HyperBoostActive,
            false,
        ),
        // Native cannot hold a convergence intent, as managed cannot when
        // its ledger has none.
        Kind::Candidate { .. } => paused(Reason::InternalStateUnavailable),
    }
}

/// Managed `IsManual`: Fixed, ManualCurve, Maximum and Stop.
fn manual(mode: i32) -> bool {
    (FanMode::FIXED.0..=FanMode::STOP.0).contains(&mode)
}

/// Managed `ThermalConvergenceClassifier.Classify` over what native reads:
/// the selector, its Custom levels, the fan mode it reports and HyperBoost.
/// Native reports manual fans as Maximum, so Stop never reads back and a
/// saved Stop under manual fans is a contradiction, as managed's rule gives
/// for any other manual reading. GPU clock offsets are never read, so a GPU
/// clock policy native cannot resolve leaves performance unavailable.
pub(super) fn classify(observation: &Observation) -> Kind {
    let Observation {
        confirmed,
        desired,
        owns_manual_fans,
    } = *observation;
    if confirmed.hyper_boost == Some(true) {
        return Kind::ExternalHyperBoost;
    }
    let stop = FanMode::STOP.0;
    let desired_fans = desired.fans.0;
    let desired_manual = manual(desired_fans);
    let both_manual = desired_manual && confirmed.fans.is_some_and(manual);
    let mut unavailable = confirmed.fans.is_none();
    let fans = confirmed.fans.is_some_and(|observed| {
        desired_manual != manual(observed)
            || ((desired_fans == stop || observed == stop) && desired_fans != observed)
    });
    let mut performance = false;
    // Firmware hides the separate selector while both sides are in generic
    // manual fan topology.
    if !both_manual || desired_fans == stop || confirmed.fans == Some(stop) {
        match (confirmed.performance, desired.performance) {
            (None, _) | (_, None) => unavailable = true,
            (Some(observed), Some(desired)) if observed != desired.wire() => performance = true,
            (Some(_), Some(desired)) => {
                if let Some(levels) = desired.custom_levels() {
                    match confirmed.custom {
                        None => unavailable = true,
                        Some(observed) => {
                            performance = observed != (levels.cpu as u8, levels.gpu as u8);
                        }
                    }
                }
            }
        }
    }
    if performance || fans {
        Kind::Candidate { performance, fans }
    } else if both_manual && !owns_manual_fans {
        Kind::ManualTopologyOnly
    } else if unavailable {
        Kind::AwaitingObservation
    } else {
        Kind::Matched
    }
}

impl Runtime {
    /// Managed `lowPowerGate.IsActive` for a publication, read from the
    /// lifecycle's ordinary write epoch (`Lifecycle::active`): `None` exactly
    /// when the gate is set or the lifecycle is unreadable or exhausted, which
    /// is when `Runtime::in_low_power` reads low. The gate probe is not read
    /// here: it belongs to the reconciliation, whose checks are counted
    /// (`triggers::Gate`), and a publication is no step. Unwired reads clear,
    /// as an unwired gate does.
    fn status_low_power(&self) -> bool {
        self.admission_epoch
            .get()
            .is_some_and(|read| read().is_none())
    }
    /// The convergence status of the user `sid` whose store `storage` is,
    /// for the status `base`, with saved intent read at class `query` under
    /// the application profile `profile`. `None` for a view that does not
    /// carry it (`Snapshot::carries_thermal_convergence`), without reading
    /// the store.
    pub(super) fn thermal_convergence(
        &self,
        base: &Snapshot,
        storage: &Storage,
        sid: &str,
        session: u32,
        query: i32,
        profile: Option<&Resolution<'_>>,
    ) -> io::Result<Option<ThermalConvergence>> {
        if !base.carries_thermal_convergence() {
            return Ok(None);
        }
        let admission = Admission {
            policy: storage.thermal_policy()?,
            low_power: self.status_low_power(),
            power: query,
            controller_present: base.names_potential_controllers(),
            pending: storage.pending(),
        };
        derive(&admission, || {
            let desired = Intent {
                performance: drift_intent(storage, query, profile)?.performance,
                fans: storage.desired_fan_mode(query, profile)?,
            };
            let owns_manual_fans = manual(desired.fans.0)
                && self.owns_exact_fans(
                    sid,
                    session,
                    query,
                    storage.desired_fan(query, profile)?.as_ref(),
                );
            Ok(Observation {
                confirmed: base.confirmed_thermal(),
                desired,
                owns_manual_fans,
            })
        })
        .map(Some)
    }
}

#[cfg(test)]
#[path = "thermal_convergence_tests.rs"]
mod tests;
