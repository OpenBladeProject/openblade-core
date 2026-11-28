//! Managed `ThermalConvergenceStatus` from what native knows at a status:
//! the saved policy, the admission facts managed's evaluation reads, and
//! the confirmed state compared with saved intent
//! (`ServiceControlRuntime.EvaluateThermalConvergenceObservationAsync` and
//! `ThermalConvergenceClassifier`).
//!
//! Without `service-thermal-convergence-v1` native has no convergence loop:
//! no budget, tombstone or arbiter is acted on, and the policy changes no
//! write: under either policy confirmed drift only excludes automatic
//! writes (`thermal_drift`). A contradiction that managed would nominate
//! for convergence under an admitted Maintain policy is published as
//! managed publishes a reconciliation that cannot hold its intent: Paused
//! with `InternalStateUnavailable`. Nothing here writes.
//!
//! With `service-thermal-convergence-v1` the status also reads the user's
//! ledger, which native shares with managed (`thermal_convergence_ledger`),
//! and asks the loop (`thermal_convergence_loop`, through `Nominator`) what
//! it would do with a contradiction: Converging, or RestorePending with
//! `HyperBoostPending` while managed's HyperBoost journal blocks it. A
//! tombstone or a spent budget publishes Suppressed with its reason and an
//! explicit reapply required, which `reapplyChangedSettings` rearms
//! (`request_triggers::reapply`).
//!
//! A ledger the store cannot admit closes convergence as managed closes it:
//! Strict is published, with the saved disclosure, and Paused with
//! `ConfigurationQuarantined`. A reservation in the ledger is reconciled
//! under either policy, as managed's is
//! (`ThermalConvergenceReconciliationAdmitted`), so while it is admitted it
//! publishes AwaitingObservation for its declared components until a
//! read-only reconciliation retires it.
//!
//! Native derives the status from current facts at each publication.
//! Managed publishes AwaitingObservation right after a policy change until
//! its next observation; native publishes what that observation would give
//! from the confirmed state the status already carries.
use super::{Runtime, drift_intent};
use crate::installed::readiness::thermal_drift::Fans;
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
#[cfg(feature = "service-thermal-convergence-v1")]
use openblade_domain::thermal_convergence as ledger;
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
    /// What the user's shared convergence ledger holds.
    #[cfg(feature = "service-thermal-convergence-v1")]
    pub(super) ledger: Ledger,
}

/// The user's convergence ledger as a status reads it
/// (`Storage::convergence_ledger`).
#[cfg(feature = "service-thermal-convergence-v1")]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) enum Ledger {
    /// Not authoritative: managed `thermalConvergenceLedgerAuthoritative`
    /// is false.
    Closed,
    /// Authoritative, with no reservation.
    Open,
    /// Authoritative and holding a reservation for these components, which
    /// only a read-only reconciliation retires.
    Reserved { performance: bool, fans: bool },
}

#[cfg(feature = "service-thermal-convergence-v1")]
impl Ledger {
    pub(super) fn of(state: Option<&ledger::State>) -> Self {
        let Some(state) = state else {
            return Self::Closed;
        };
        match &state.in_flight {
            None => Self::Open,
            Some(reservation) => {
                let declares = |component| reservation.declared_components.contains(&component);
                Self::Reserved {
                    performance: declares(ledger::Component::PERFORMANCE),
                    fans: declares(ledger::Component::FANS),
                }
            }
        }
    }
}

/// Managed's settled destinations: PluggedIn, Battery and UsbC.
fn settled(power: i32) -> bool {
    (0..=2).contains(&power)
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

/// What the convergence loop says of the status's evaluation
/// (`triggers::convergence_loop::Nominate`).
#[cfg(feature = "service-thermal-convergence-v1")]
pub(super) trait Nominator {
    /// Managed `hyperBoostJournalSafetyBlock`.
    fn journal_blocks(&self) -> bool;
    /// What managed's observer does with a contradiction past every
    /// admission check: nominate it, suppress it, or refuse it.
    fn nominate(&self, performance: bool, fans: bool) -> io::Result<Nomination>;
}

/// Managed's observer outcome for a contradiction (`Nominator::nominate`).
#[cfg(feature = "service-thermal-convergence-v1")]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) enum Nomination {
    /// Nominated: managed publishes Converging.
    Converging,
    /// A tombstone or a spent budget suppresses it (`Reason::Tombstoned`,
    /// `Reason::BudgetExhausted`).
    Suppressed(Reason),
    /// No convergence intent can be held for it (D9).
    Unavailable,
    /// The loop does not evaluate yet: the user is starting, a lifecycle
    /// cause is owed or pending, and its reconciliation runs first.
    Waiting,
}

/// The status managed would publish for `admission`, classifying the
/// observation `observe` reads only once every admission check passed.
pub(super) fn derive(
    admission: &Admission,
    observe: impl FnOnce() -> io::Result<Observation>,
    #[cfg(feature = "service-thermal-convergence-v1")] nominator: Option<&dyn Nominator>,
) -> io::Result<ThermalConvergence> {
    // Managed `BatteryConfigurationStore` quarantine preferences, and
    // `PublishThermalConvergenceStatus`, which reports Strict while the
    // configuration is not authoritative.
    let (policy, disclosure) = match admission.policy {
        SavedPolicy::Quarantined => (STRICT, DISCLOSURE_NOT_REQUIRED),
        SavedPolicy::Admitted { policy, disclosure } => (policy, disclosure),
    };
    // Managed `PublishThermalConvergenceStatus` reports Strict while the
    // ledger is not authoritative, and keeps the saved disclosure.
    #[cfg(feature = "service-thermal-convergence-v1")]
    let ledger_closed = admission.ledger == Ledger::Closed;
    #[cfg(not(feature = "service-thermal-convergence-v1"))]
    let ledger_closed = false;
    let policy = if ledger_closed { STRICT } else { policy };
    let publish = |state, components: &[Component], reason, explicit| {
        Ok(ThermalConvergence::new(
            policy, disclosure, state, components, reason, explicit,
        ))
    };
    let paused = |reason| publish(State::Paused, &[], reason, false);
    // Managed `ResolveThermalConvergenceClosureReason` order.
    if admission.policy == SavedPolicy::Quarantined || ledger_closed {
        return paused(Reason::ConfigurationQuarantined);
    }
    // Managed reconciles a reservation whatever the policy and disclosure,
    // once power is settled. Until a read-only reconciliation retires it,
    // it awaits that observation.
    #[cfg(feature = "service-thermal-convergence-v1")]
    if let Ledger::Reserved { performance, fans } = admission.ledger
        && !admission.low_power
        && settled(admission.power)
    {
        let declared = [
            (performance, Component::Performance),
            (fans, Component::Fans),
        ];
        let components: Vec<_> = declared
            .into_iter()
            .filter_map(|(declares, component)| declares.then_some(component))
            .collect();
        return publish(
            State::AwaitingObservation,
            &components,
            Reason::AwaitingFreshObservation,
            false,
        );
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
    if !settled(admission.power) {
        return paused(Reason::PowerUnsettled);
    }
    if admission.controller_present {
        return paused(Reason::CompetingControllerPresent);
    }
    // Managed `hyperBoostJournalSafetyBlock`: a HyperBoost journal that is
    // not in a safe terminal state closes convergence for both components.
    #[cfg(feature = "service-thermal-convergence-v1")]
    if nominator.is_some_and(|loop_| loop_.journal_blocks()) {
        return publish(
            State::RestorePending,
            &[Component::Performance, Component::Fans],
            Reason::HyperBoostPending,
            false,
        );
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
        // Without the loop native cannot hold a convergence intent, as
        // managed cannot when its ledger has none.
        #[cfg(not(feature = "service-thermal-convergence-v1"))]
        Kind::Candidate { .. } => paused(Reason::InternalStateUnavailable),
        #[cfg(feature = "service-thermal-convergence-v1")]
        Kind::Candidate { performance, fans } => {
            let Some(loop_) = nominator else {
                return paused(Reason::InternalStateUnavailable);
            };
            let components: Vec<_> = [
                (performance, Component::Performance),
                (fans, Component::Fans),
            ]
            .into_iter()
            .filter_map(|(contradicts, component)| contradicts.then_some(component))
            .collect();
            match loop_.nominate(performance, fans)? {
                Nomination::Converging => {
                    publish(State::Converging, &components, Reason::None, false)
                }
                Nomination::Waiting => publish(
                    State::AwaitingObservation,
                    &components,
                    Reason::AwaitingFreshObservation,
                    false,
                ),
                // Only an explicit reapply rearms a suppression.
                Nomination::Suppressed(reason) => {
                    publish(State::Suppressed, &components, reason, true)
                }
                Nomination::Unavailable => paused(Reason::InternalStateUnavailable),
            }
        }
    }
}

/// Managed `IsManual`: Fixed, ManualCurve, Maximum and Stop.
pub(super) fn manual(mode: i32) -> bool {
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
        #[cfg(feature = "service-thermal-convergence-v1")]
        let held = storage.convergence_ledger();
        let admission = Admission {
            policy: storage.thermal_policy()?,
            low_power: self.status_low_power(),
            power: query,
            controller_present: base.names_potential_controllers(),
            pending: storage.pending(),
            #[cfg(feature = "service-thermal-convergence-v1")]
            ledger: Ledger::of(held.as_ref()),
        };
        let observe = || self.convergence_observation(base, storage, sid, session, query, profile);
        #[cfg(not(feature = "service-thermal-convergence-v1"))]
        let status = derive(&admission, observe);
        #[cfg(feature = "service-thermal-convergence-v1")]
        let status = {
            let nominate = self.nominate(base, storage, sid, session, query, profile, held);
            derive(
                &admission,
                observe,
                nominate.as_ref().map(|n| n as &dyn Nominator),
            )
        };
        status.map(Some)
    }
    /// What the classifier compares for the user `sid` at class `query`:
    /// the status's confirmed state, saved intent there and whether this
    /// runtime owns the exact manual fan intent. Fan intent native has no
    /// recipe for is Automatic over a confirmed Automatic reading, as
    /// managed `ResolveExperimentalFanHardwareDesired` projects unsupported
    /// fan intent (`thermal_drift::Fans::Unplanned`); over a manual reading
    /// it is kept, and still contradicts.
    pub(super) fn convergence_observation(
        &self,
        base: &Snapshot,
        storage: &Storage,
        sid: &str,
        session: u32,
        query: i32,
        profile: Option<&Resolution<'_>>,
    ) -> io::Result<Observation> {
        let confirmed = base.confirmed_thermal();
        let intent = drift_intent(storage, query, profile)?;
        let projected =
            intent.fans == Fans::Unplanned && confirmed.fans == Some(FanMode::AUTOMATIC.0);
        let desired = Intent {
            performance: intent.performance,
            fans: if projected {
                FanMode::AUTOMATIC
            } else {
                storage.desired_fan_mode(query, profile)?
            },
        };
        let owns_manual_fans = manual(desired.fans.0)
            && self.owns_exact_fans(
                sid,
                session,
                query,
                storage.desired_fan(query, profile)?.as_ref(),
            );
        Ok(Observation {
            confirmed,
            desired,
            owns_manual_fans,
        })
    }
}

#[cfg(test)]
#[path = "thermal_convergence_tests.rs"]
mod tests;
