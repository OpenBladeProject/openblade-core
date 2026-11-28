//! Managed `ThermalConvergenceStatus` (ServiceMessages v91): the status
//! member that reports the saved thermal control policy and whether saved
//! thermal intent is being kept.
//!
//! Every enum travels as its managed numeric value. The record is read
//! strictly, as managed's codec binds it: every member is required, an
//! unknown member is refused, and `IpcMessageCodec.ValidateStatus` checks
//! follow (each enum defined, at most two affected components, each defined,
//! sorted and unique).
use std::io;

use serde::{Deserialize, Serialize};
use serde_json::Value;

use crate::service_frame::invalid;

/// Managed `ThermalControlPolicy.MaintainDesiredState`.
pub(crate) const MAINTAIN: i32 = 0;
/// Managed `ThermalControlPolicy.Strict`.
pub(crate) const STRICT: i32 = 1;
/// Managed `ThermalPolicyUpgradeDisclosureState.NotRequired`.
#[cfg_attr(
    not(feature = "service-profile-triggers-v1"),
    allow(
        dead_code,
        reason = "only the triggers lane stores and derives the disclosure"
    )
)]
pub(crate) const DISCLOSURE_NOT_REQUIRED: i32 = 0;
/// Managed `ThermalPolicyUpgradeDisclosureState.UpgradeDisclosurePending`.
#[cfg_attr(
    not(feature = "service-profile-triggers-v1"),
    allow(
        dead_code,
        reason = "only the triggers lane stores and derives the disclosure"
    )
)]
pub(crate) const DISCLOSURE_PENDING: i32 = 1;
/// Managed `ThermalPolicyUpgradeDisclosureState.Acknowledged`.
#[cfg_attr(
    not(feature = "service-profile-triggers-v1"),
    allow(
        dead_code,
        reason = "only the triggers lane stores and derives the disclosure"
    )
)]
pub(crate) const DISCLOSURE_ACKNOWLEDGED: i32 = 2;

const POLICIES: i32 = 2;
const DISCLOSURES: i32 = 3;
const STATES: i32 = 8;
const REASONS: i32 = 22;
const COMPONENTS: i32 = 2;

/// Managed `ThermalConvergenceState`.
#[allow(
    dead_code,
    reason = "every managed state is named; native publishes only those it can derive"
)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[repr(i32)]
pub(crate) enum State {
    Matched = 0,
    AwaitingObservation = 1,
    Converging = 2,
    Paused = 3,
    Suppressed = 4,
    External = 5,
    RestorePending = 6,
    ReconciliationUnavailable = 7,
}

/// Managed `ThermalConvergenceReason`.
#[allow(
    dead_code,
    reason = "every managed reason is named; native publishes only those it can derive"
)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[repr(i32)]
pub(crate) enum Reason {
    None = 0,
    StrictPolicy = 1,
    UpgradeDisclosurePending = 2,
    ConfigurationUnavailable = 3,
    ConfigurationQuarantined = 4,
    InactiveSession = 5,
    ProtocolCompatibilityClient = 6,
    AwaitingFreshObservation = 7,
    DeviceUnavailable = 8,
    DeviceIdentityBlocked = 9,
    PowerUnsettled = 10,
    LowPower = 11,
    CompetingControllerPresent = 12,
    CompetingControllerUncertain = 13,
    HyperBoostActive = 14,
    HyperBoostPending = 15,
    ManualFanReadbackAmbiguous = 16,
    PendingReconciliation = 17,
    BudgetExhausted = 18,
    Tombstoned = 19,
    Shutdown = 20,
    InternalStateUnavailable = 21,
}

/// Managed `ThermalComponent`.
#[cfg_attr(not(feature = "service-profile-triggers-v1"), allow(dead_code))]
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
#[repr(i32)]
pub(crate) enum Component {
    Performance = 0,
    Fans = 1,
}

/// Managed `ThermalConvergenceStatus`, member for member.
#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub(crate) struct ThermalConvergence {
    pub(crate) policy: i32,
    pub(crate) disclosure: i32,
    pub(crate) state: i32,
    pub(crate) affected_components: Vec<i32>,
    pub(crate) reason: i32,
    pub(crate) explicit_reapply_required: bool,
}

impl ThermalConvergence {
    /// Managed `PublishThermalConvergenceStatus`, which orders the affected
    /// components.
    #[cfg_attr(not(feature = "service-profile-triggers-v1"), allow(dead_code))]
    pub(crate) fn new(
        policy: i32,
        disclosure: i32,
        state: State,
        components: &[Component],
        reason: Reason,
        explicit_reapply_required: bool,
    ) -> Self {
        let mut components = components.to_vec();
        components.sort();
        Self {
            policy,
            disclosure,
            state: state as i32,
            affected_components: components.into_iter().map(|c| c as i32).collect(),
            reason: reason as i32,
            explicit_reapply_required,
        }
    }

    /// The `thermalConvergence` member of a status: `None` for an absent or
    /// null member, otherwise a record that passes managed's checks.
    pub(crate) fn decode(member: Option<&Value>) -> io::Result<Option<Self>> {
        let Some(value) = member.filter(|value| !value.is_null()) else {
            return Ok(None);
        };
        let record: Self = serde_json::from_value(value.clone())
            .map_err(|_| invalid("thermal-convergence status is malformed"))?;
        record.verify()?;
        Ok(Some(record))
    }

    /// Managed `IpcMessageCodec.ValidateStatus`, in its order.
    pub(crate) fn verify(&self) -> io::Result<()> {
        let defined = |value: i32, count: i32| (0..count).contains(&value);
        if !defined(self.policy, POLICIES) {
            return Err(invalid("thermal-control policy is unknown"));
        }
        if !defined(self.disclosure, DISCLOSURES) {
            return Err(invalid("thermal-policy disclosure state is unknown"));
        }
        if !defined(self.state, STATES) {
            return Err(invalid("thermal-convergence state is unknown"));
        }
        if !defined(self.reason, REASONS) {
            return Err(invalid("thermal-convergence reason is unknown"));
        }
        let components = &self.affected_components;
        if components.len() > 2 {
            return Err(invalid(
                "thermal-convergence affected-component collection exceeds two entries",
            ));
        }
        if !components.iter().all(|c| defined(*c, COMPONENTS)) {
            return Err(invalid(
                "thermal-convergence affected-component collection contains an unknown component",
            ));
        }
        // Managed `ValidateSortedUnique` reports a duplicate before an
        // unsorted pair.
        if components.windows(2).any(|pair| pair[0] == pair[1]) {
            return Err(invalid(
                "thermal-convergence affected-component collection contains duplicates",
            ));
        }
        if components.windows(2).any(|pair| pair[0] > pair[1]) {
            return Err(invalid(
                "thermal-convergence affected-component collection is not sorted",
            ));
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn record(components: Value) -> Value {
        json!({
            "policy": 0,
            "disclosure": 0,
            "state": 0,
            "affectedComponents": components,
            "reason": 0,
            "explicitReapplyRequired": false,
        })
    }

    #[test]
    fn absent_and_null_members_carry_no_record() {
        assert_eq!(ThermalConvergence::decode(None).unwrap(), None);
        assert_eq!(
            ThermalConvergence::decode(Some(&Value::Null)).unwrap(),
            None
        );
    }

    #[test]
    fn a_well_formed_record_decodes_and_round_trips() {
        let value = record(json!([0, 1]));
        let decoded = ThermalConvergence::decode(Some(&value)).unwrap().unwrap();
        assert_eq!(serde_json::to_value(&decoded).unwrap(), value);
        assert_eq!(
            ThermalConvergence::new(
                STRICT,
                DISCLOSURE_ACKNOWLEDGED,
                State::ReconciliationUnavailable,
                &[Component::Fans, Component::Performance],
                Reason::InternalStateUnavailable,
                true,
            ),
            ThermalConvergence {
                policy: 1,
                disclosure: 2,
                state: 7,
                affected_components: vec![0, 1],
                reason: 21,
                explicit_reapply_required: true,
            }
        );
    }

    #[test]
    fn each_managed_codec_check_refuses_its_record() {
        let mut cases = vec![
            ("policy", json!(2), "thermal-control policy is unknown"),
            ("policy", json!(-1), "thermal-control policy is unknown"),
            (
                "disclosure",
                json!(3),
                "thermal-policy disclosure state is unknown",
            ),
            ("state", json!(8), "thermal-convergence state is unknown"),
            ("reason", json!(22), "thermal-convergence reason is unknown"),
            (
                "affectedComponents",
                json!([0, 1, 1]),
                "thermal-convergence affected-component collection exceeds two entries",
            ),
            (
                "affectedComponents",
                json!([2]),
                "thermal-convergence affected-component collection contains an unknown component",
            ),
            (
                "affectedComponents",
                json!([1, 1]),
                "thermal-convergence affected-component collection contains duplicates",
            ),
            (
                "affectedComponents",
                json!([1, 0]),
                "thermal-convergence affected-component collection is not sorted",
            ),
        ];
        // Each enum's largest defined value is admitted.
        for (member, largest) in [
            ("policy", 1),
            ("disclosure", 2),
            ("state", 7),
            ("reason", 21),
        ] {
            let mut value = record(json!([]));
            value[member] = json!(largest);
            assert!(ThermalConvergence::decode(Some(&value)).is_ok(), "{member}");
        }
        for (member, bad, message) in cases.drain(..) {
            let mut value = record(json!([]));
            value[member] = bad;
            let error = ThermalConvergence::decode(Some(&value)).unwrap_err();
            assert_eq!(error.to_string(), message, "{member}");
        }
    }

    #[test]
    fn the_strict_reader_refuses_missing_unknown_and_mistyped_members() {
        let mut missing = record(json!([]));
        missing
            .as_object_mut()
            .unwrap()
            .remove("explicitReapplyRequired");
        let mut unknown = record(json!([]));
        unknown["budget"] = json!(1);
        let mut fractional = record(json!([]));
        fractional["state"] = json!(1.0);
        let mut text = record(json!([]));
        text["policy"] = json!("Strict");
        let mut nulled = record(json!([]));
        nulled["affectedComponents"] = Value::Null;
        for value in [
            missing,
            unknown,
            fractional,
            text,
            nulled,
            json!([]),
            json!(1),
        ] {
            assert_eq!(
                ThermalConvergence::decode(Some(&value))
                    .unwrap_err()
                    .to_string(),
                "thermal-convergence status is malformed",
                "{value}"
            );
        }
    }
}

/// The member on a status: only the control-session owner's projection
/// carries it, and every path that drops private policy drops it.
#[cfg(all(test, feature = "service-profile-triggers-v1"))]
mod snapshot_tests {
    use super::*;
    use crate::service_status::Snapshot;
    use serde_json::json;

    const OWNER: &str = "S-1-5-21-111-222-333-1001";

    fn status(access: i32) -> Snapshot {
        Snapshot::observed_read_only(1, access, false, false, vec![], None, String::new())
    }

    fn strict() -> ThermalConvergence {
        ThermalConvergence::new(
            STRICT,
            DISCLOSURE_NOT_REQUIRED,
            State::Paused,
            &[],
            Reason::StrictPolicy,
            false,
        )
    }

    #[test]
    fn only_the_owner_projection_carries_the_member() {
        let owned = status(0).with_thermal_convergence(Some(strict()));
        assert_eq!(owned.thermal_convergence(), Some(strict()));
        owned.verify(0).unwrap();
        for access in [1, 2] {
            let other = status(access).with_thermal_convergence(Some(strict()));
            assert!(!other.feature_fields.contains_key("thermalConvergence"));
        }
        // None removes an earlier member.
        let cleared = owned.clone().with_thermal_convergence(None);
        assert!(!cleared.feature_fields.contains_key("thermalConvergence"));
        // Dropping private policy, or another caller's view, drops it.
        let bound = owned.bind_private_user(OWNER);
        assert!(
            !bound
                .without_private_policy()
                .feature_fields
                .contains_key("thermalConvergence")
        );
        assert!(
            !bound
                .project_private(Some("S-1-5-21-111-222-333-1002"))
                .feature_fields
                .contains_key("thermalConvergence")
        );
        assert_eq!(
            bound.project_private(Some(OWNER)).thermal_convergence(),
            Some(strict())
        );
        // A mutation rebased on newer public facts keeps it.
        let rebased = bound.rebase_thermal_mutation(&status(0));
        assert_eq!(rebased.thermal_convergence(), Some(strict()));
    }

    #[test]
    fn a_status_with_a_malformed_member_is_refused() {
        let mut value = status(0);
        value
            .feature_fields
            .insert("thermalConvergence".into(), json!({"policy": 2}));
        assert!(value.verify(0).is_err());
        value
            .feature_fields
            .insert("thermalConvergence".into(), Value::Null);
        value.verify(0).unwrap();
    }

    #[test]
    fn the_classifier_reads_the_confirmed_members_and_nothing_mistyped() {
        let mut value = status(0);
        assert_eq!(value.confirmed_thermal(), Default::default());
        value.confirmed_state = Some(
            json!({"performance": 5, "fans": 3, "customPerformance": {"cpu": 0, "gpu": 2},
                "hyperBoostActive": false})
            .as_object()
            .unwrap()
            .clone()
            .into(),
        );
        assert_eq!(
            value.confirmed_thermal(),
            crate::service_status::ConfirmedThermal {
                performance: Some(5),
                fans: Some(3),
                custom: Some((0, 2)),
                hyper_boost: Some(false),
            }
        );
        value.confirmed_state = Some(
            json!({"performance": "5", "fans": null, "customPerformance": {"cpu": 0},
                "hyperBoostActive": 1})
            .as_object()
            .unwrap()
            .clone()
            .into(),
        );
        assert_eq!(value.confirmed_thermal(), Default::default());
        assert!(!value.names_potential_controllers());
        value.potential_controllers = vec!["RazerAppEngine".into()];
        assert!(value.names_potential_controllers());
    }
}
