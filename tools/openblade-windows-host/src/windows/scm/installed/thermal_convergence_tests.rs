//! `thermal_convergence` derivation tests, and the replay of the managed
//! status vectors (`tests/data/thermal-convergence-vectors.json`).
use super::*;
use crate::service_status::thermal_convergence::{DISCLOSURE_ACKNOWLEDGED, MAINTAIN};
use openblade_domain::thermal_mutation::Preset;
use serde_json::Value;

const VECTORS: &str = include_str!("../../../../tests/data/thermal-convergence-vectors.json");

fn admitted(policy: i32, disclosure: i32) -> Admission {
    Admission {
        policy: SavedPolicy::Admitted { policy, disclosure },
        low_power: false,
        power: 0,
        controller_present: false,
        pending: false,
        #[cfg(feature = "service-thermal-convergence-v1")]
        ledger: Ledger::Open,
    }
}

fn balanced() -> Option<Destination> {
    Some(Destination::Preset(Preset::Balanced))
}

/// Saved Balanced intent with `fans`, read back as `confirmed`.
fn observation(confirmed: ConfirmedThermal, fans: FanMode) -> Observation {
    Observation {
        confirmed,
        desired: Intent {
            performance: balanced(),
            fans,
        },
        owns_manual_fans: false,
    }
}

fn reading(performance: Option<i32>, fans: Option<i32>) -> ConfirmedThermal {
    ConfirmedThermal {
        performance,
        fans,
        custom: None,
        hyper_boost: Some(false),
    }
}

fn matched() -> Observation {
    observation(reading(Some(1), Some(0)), FanMode::AUTOMATIC)
}

fn status(
    (policy, disclosure): (i32, i32),
    state: State,
    components: &[Component],
    reason: Reason,
    explicit: bool,
) -> ThermalConvergence {
    ThermalConvergence::new(policy, disclosure, state, components, reason, explicit)
}

fn paused(policy: (i32, i32), reason: Reason) -> ThermalConvergence {
    status(policy, State::Paused, &[], reason, false)
}

/// `derive` without the convergence loop.
fn derive_unlooped(
    admission: &Admission,
    observe: impl FnOnce() -> io::Result<Observation>,
) -> io::Result<ThermalConvergence> {
    #[cfg(feature = "service-thermal-convergence-v1")]
    return derive(admission, observe, None);
    #[cfg(not(feature = "service-thermal-convergence-v1"))]
    derive(admission, observe)
}
fn derived(admission: &Admission, observed: Observation) -> ThermalConvergence {
    derive_unlooped(admission, || Ok(observed)).unwrap()
}

/// A derivation that must not read the observation.
fn refused(admission: &Admission) -> ThermalConvergence {
    derive_unlooped(admission, || {
        panic!("an admission refusal reads no observation")
    })
    .unwrap()
}

#[test]
fn each_admission_refusal_wins_over_every_later_one_without_reading() {
    // Each row also sets every later refusal, so the earlier check must win.
    let mut admission = admitted(MAINTAIN, DISCLOSURE_NOT_REQUIRED);
    admission.policy = SavedPolicy::Quarantined;
    admission.low_power = true;
    admission.power = 3;
    admission.controller_present = true;
    admission.pending = true;
    assert_eq!(
        refused(&admission),
        paused(
            (STRICT, DISCLOSURE_NOT_REQUIRED),
            Reason::ConfigurationQuarantined
        )
    );
    admission.policy = SavedPolicy::Admitted {
        policy: STRICT,
        disclosure: DISCLOSURE_PENDING,
    };
    assert_eq!(
        refused(&admission),
        paused((STRICT, DISCLOSURE_PENDING), Reason::StrictPolicy)
    );
    admission.policy = SavedPolicy::Admitted {
        policy: MAINTAIN,
        disclosure: DISCLOSURE_PENDING,
    };
    assert_eq!(
        refused(&admission),
        paused(
            (MAINTAIN, DISCLOSURE_PENDING),
            Reason::UpgradeDisclosurePending
        )
    );
    let maintain = (MAINTAIN, DISCLOSURE_ACKNOWLEDGED);
    admission.policy = SavedPolicy::Admitted {
        policy: maintain.0,
        disclosure: maintain.1,
    };
    assert_eq!(refused(&admission), paused(maintain, Reason::LowPower));
    admission.low_power = false;
    assert_eq!(
        refused(&admission),
        paused(maintain, Reason::PowerUnsettled)
    );
    admission.power = 2;
    assert_eq!(
        refused(&admission),
        paused(maintain, Reason::CompetingControllerPresent)
    );
    admission.controller_present = false;
    assert_eq!(
        refused(&admission),
        paused(maintain, Reason::PendingReconciliation)
    );
    admission.pending = false;
    assert_eq!(
        derived(&admission, matched()),
        status(maintain, State::Matched, &[], Reason::None, false)
    );
}

#[test]
fn only_pluggedin_battery_and_usbc_are_settled_power() {
    for power in [0, 1, 2] {
        let mut admission = admitted(MAINTAIN, DISCLOSURE_NOT_REQUIRED);
        admission.power = power;
        assert_eq!(derived(&admission, matched()).state, State::Matched as i32);
    }
    for power in [-1, 3, 4, i32::MAX] {
        let mut admission = admitted(MAINTAIN, DISCLOSURE_NOT_REQUIRED);
        admission.power = power;
        assert_eq!(
            refused(&admission),
            paused((MAINTAIN, DISCLOSURE_NOT_REQUIRED), Reason::PowerUnsettled)
        );
    }
}

#[test]
fn strict_is_advisory_under_any_reading_and_acknowledged_maintain_classifies() {
    for disclosure in [
        DISCLOSURE_NOT_REQUIRED,
        DISCLOSURE_PENDING,
        DISCLOSURE_ACKNOWLEDGED,
    ] {
        assert_eq!(
            refused(&admitted(STRICT, disclosure)),
            paused((STRICT, disclosure), Reason::StrictPolicy)
        );
    }
    for disclosure in [DISCLOSURE_NOT_REQUIRED, DISCLOSURE_ACKNOWLEDGED] {
        assert_eq!(
            derived(&admitted(MAINTAIN, disclosure), matched()),
            status(
                (MAINTAIN, disclosure),
                State::Matched,
                &[],
                Reason::None,
                false
            )
        );
    }
}

#[test]
fn an_admitted_observation_failure_propagates() {
    let error = derive_unlooped(&admitted(MAINTAIN, DISCLOSURE_NOT_REQUIRED), || {
        Err(io::ErrorKind::PermissionDenied.into())
    })
    .unwrap_err();
    assert_eq!(error.kind(), io::ErrorKind::PermissionDenied);
}

#[test]
fn each_observation_kind_publishes_managed_state() {
    let maintain = (MAINTAIN, DISCLOSURE_NOT_REQUIRED);
    let admission = admitted(maintain.0, maintain.1);
    let awaiting = observation(reading(None, Some(0)), FanMode::AUTOMATIC);
    assert_eq!(
        derived(&admission, awaiting),
        status(
            maintain,
            State::AwaitingObservation,
            &[],
            Reason::AwaitingFreshObservation,
            false
        )
    );
    let topology = observation(reading(Some(1), Some(3)), FanMode::FIXED);
    assert_eq!(
        derived(&admission, topology),
        status(
            maintain,
            State::Paused,
            &[Component::Fans],
            Reason::ManualFanReadbackAmbiguous,
            true
        )
    );
    let mut boosted = reading(None, Some(0));
    boosted.hyper_boost = Some(true);
    assert_eq!(
        derived(&admission, observation(boosted, FanMode::AUTOMATIC)),
        status(
            maintain,
            State::External,
            &[Component::Performance, Component::Fans],
            Reason::HyperBoostActive,
            false
        )
    );
    // The S5 gap: a candidate is never Converging.
    let drift = observation(reading(Some(2), Some(3)), FanMode::AUTOMATIC);
    assert_eq!(
        classify(&drift),
        Kind::Candidate {
            performance: true,
            fans: true
        }
    );
    assert_eq!(
        derived(&admission, drift),
        paused(maintain, Reason::InternalStateUnavailable)
    );
}

#[test]
fn classification_follows_the_managed_classifier() {
    let candidate = |performance, fans| Kind::Candidate { performance, fans };
    let kind = |confirmed, fans| classify(&observation(confirmed, fans));
    // HyperBoost wins over every contradiction.
    let mut boosted = reading(Some(0), Some(3));
    boosted.hyper_boost = Some(true);
    assert_eq!(kind(boosted, FanMode::AUTOMATIC), Kind::ExternalHyperBoost);
    // An unknown HyperBoost bit is no evidence.
    let mut unknown = reading(Some(1), Some(0));
    unknown.hyper_boost = None;
    assert_eq!(kind(unknown, FanMode::AUTOMATIC), Kind::Matched);
    // Fan topology, for every manual mode against an Automatic reading and
    // back; managed's projection of unplanned intent is a drift rule only.
    for mode in [
        FanMode::FIXED,
        FanMode::MANUAL_CURVE,
        FanMode::MAXIMUM,
        FanMode::STOP,
    ] {
        assert_eq!(
            kind(reading(Some(1), Some(0)), mode),
            candidate(false, true)
        );
    }
    assert_eq!(
        kind(reading(Some(1), Some(3)), FanMode::AUTOMATIC),
        candidate(false, true)
    );
    // An undefined saved mode is not manual.
    assert_eq!(kind(reading(Some(1), Some(0)), FanMode(-1)), Kind::Matched);
    assert_eq!(kind(reading(Some(1), Some(0)), FanMode(5)), Kind::Matched);
    // Both manual hides the selector, so a different preset is no evidence.
    for mode in [FanMode::FIXED, FanMode::MANUAL_CURVE, FanMode::MAXIMUM] {
        assert_eq!(
            kind(reading(Some(2), Some(3)), mode),
            Kind::ManualTopologyOnly
        );
        assert_eq!(kind(reading(None, Some(3)), mode), Kind::ManualTopologyOnly);
        assert_eq!(
            kind(reading(Some(1), Some(1)), mode),
            Kind::ManualTopologyOnly
        );
    }
    // Stop is its own topology: native reads manual fans as Maximum, so a
    // saved Stop is contradicted, and the selector is compared.
    assert_eq!(
        kind(reading(Some(1), Some(3)), FanMode::STOP),
        candidate(false, true)
    );
    assert_eq!(
        kind(reading(Some(2), Some(3)), FanMode::STOP),
        candidate(true, true)
    );
    assert_eq!(
        kind(reading(Some(1), Some(4)), FanMode::STOP),
        Kind::ManualTopologyOnly
    );
    assert_eq!(
        kind(reading(None, Some(4)), FanMode::STOP),
        Kind::ManualTopologyOnly
    );
    assert_eq!(
        kind(reading(Some(2), Some(4)), FanMode::STOP),
        candidate(true, false)
    );
    assert_eq!(
        kind(reading(Some(2), Some(4)), FanMode::FIXED),
        candidate(true, true)
    );
    // Performance against the selector, and unknown values.
    assert_eq!(
        kind(reading(Some(0), Some(0)), FanMode::AUTOMATIC),
        candidate(true, false)
    );
    assert_eq!(
        kind(reading(None, Some(0)), FanMode::AUTOMATIC),
        Kind::AwaitingObservation
    );
    assert_eq!(
        kind(reading(Some(1), None), FanMode::AUTOMATIC),
        Kind::AwaitingObservation
    );
    assert_eq!(
        kind(reading(Some(2), None), FanMode::FIXED),
        candidate(true, false)
    );
    let mut unresolved = observation(reading(Some(1), Some(0)), FanMode::AUTOMATIC);
    unresolved.desired.performance = None;
    assert_eq!(classify(&unresolved), Kind::AwaitingObservation);
    // A contradiction wins over an unknown value.
    assert_eq!(
        kind(reading(None, Some(3)), FanMode::AUTOMATIC),
        candidate(false, true)
    );
}

#[test]
fn custom_levels_are_compared_only_under_a_matching_custom_selector() {
    let levels = Destination::custom(0, 1, false);
    let custom = |custom: Option<(u8, u8)>| {
        let mut observed = observation(
            ConfirmedThermal {
                performance: Some(5),
                fans: Some(0),
                custom,
                hyper_boost: Some(false),
            },
            FanMode::AUTOMATIC,
        );
        observed.desired.performance = levels;
        classify(&observed)
    };
    assert_eq!(custom(Some((0, 1))), Kind::Matched);
    for drifted in [(1, 0), (0, 0), (1, 1)] {
        assert_eq!(
            custom(Some(drifted)),
            Kind::Candidate {
                performance: true,
                fans: false
            }
        );
    }
    assert_eq!(custom(None), Kind::AwaitingObservation);
    // A preset intent ignores reported levels.
    let mut preset = matched();
    preset.confirmed.custom = Some((2, 2));
    assert_eq!(classify(&preset), Kind::Matched);
}

#[test]
fn only_owned_exact_manual_fans_match_under_both_manual() {
    let mut owned = observation(reading(Some(1), Some(3)), FanMode::FIXED);
    assert_eq!(classify(&owned), Kind::ManualTopologyOnly);
    owned.owns_manual_fans = true;
    assert_eq!(classify(&owned), Kind::Matched);
    // Ownership never hides a contradiction or an unknown value.
    owned.confirmed.fans = Some(0);
    assert_eq!(
        classify(&owned),
        Kind::Candidate {
            performance: false,
            fans: true
        }
    );
    let mut unknown = matched();
    unknown.owns_manual_fans = true;
    unknown.confirmed.performance = None;
    assert_eq!(classify(&unknown), Kind::AwaitingObservation);
}

fn int(row: &Value, name: &str) -> Option<i32> {
    row.get(name)
        .and_then(Value::as_i64)
        .map(|value| i32::try_from(value).unwrap())
}

fn flag(row: &Value, name: &str) -> bool {
    row.get(name).and_then(Value::as_bool).unwrap_or(false)
}

fn levels(value: &Value) -> Option<(u64, u64)> {
    let pair = value.as_array()?;
    Some((pair[0].as_u64().unwrap(), pair[1].as_u64().unwrap()))
}

/// The admission and observation one managed status row was recorded from.
fn replayed(row: &Value) -> (Admission, Observation) {
    let policy = if flag(row, "quarantined") {
        SavedPolicy::Quarantined
    } else {
        SavedPolicy::Admitted {
            policy: int(row, "policy").unwrap(),
            disclosure: int(row, "disclosure").unwrap(),
        }
    };
    let mut admission = admitted(MAINTAIN, DISCLOSURE_NOT_REQUIRED);
    admission.policy = policy;
    admission.low_power = flag(row, "lowPower");
    admission.power = if flag(row, "unsettled") {
        3
    } else {
        int(row, "power").unwrap()
    };
    admission.controller_present = flag(row, "controller");
    let desired = &row["desired"];
    let performance = int(desired, "performance").unwrap();
    let destination = match levels(&desired["custom"]) {
        Some((cpu, gpu)) if performance == openblade_domain::thermal_mutation::CUSTOM => {
            Destination::custom(cpu, gpu, false)
        }
        _ => Preset::from_wire(performance).map(Destination::Preset),
    };
    assert!(destination.is_some(), "{row}");
    let confirmed = &row["confirmed"];
    let observation = Observation {
        confirmed: ConfirmedThermal {
            performance: int(confirmed, "performance"),
            fans: int(confirmed, "fans"),
            custom: levels(&confirmed["custom"])
                .map(|(cpu, gpu)| (u8::try_from(cpu).unwrap(), u8::try_from(gpu).unwrap())),
            hyper_boost: confirmed["hyperBoost"].as_bool(),
        },
        desired: Intent {
            performance: destination,
            fans: FanMode(int(desired, "fans").unwrap()),
        },
        owns_manual_fans: flag(row, "owned"),
    };
    (admission, observation)
}

#[test]
fn managed_status_vectors_replay_through_the_strict_codec() {
    let vectors: Value = serde_json::from_str(VECTORS).unwrap();
    let rows = vectors["statuses"].as_array().unwrap();
    let mut published = std::collections::BTreeSet::new();
    let mut converging = 0;
    for row in rows {
        let name = row["name"].as_str().unwrap();
        let managed = ThermalConvergence::decode(Some(&row["status"]))
            .unwrap_or_else(|error| panic!("{name}: {error}"))
            .unwrap_or_else(|| panic!("{name}: managed published no status"));
        let (admission, observation) = replayed(row);
        let native = derive_unlooped(&admission, || Ok(observation)).unwrap();
        native.verify().unwrap();
        if managed.state == State::Converging as i32 {
            // Managed nominates the contradiction; native classifies the
            // same components and publishes the S5 gap.
            converging += 1;
            assert_eq!(managed.reason, Reason::None as i32, "{name}");
            assert!(!managed.explicit_reapply_required, "{name}");
            let Kind::Candidate { performance, fans } = classify(&observation) else {
                panic!("{name}: native found no contradiction");
            };
            let components: Vec<i32> = [(performance, 0), (fans, 1)]
                .into_iter()
                .filter_map(|(set, component)| set.then_some(component))
                .collect();
            assert_eq!(components, managed.affected_components, "{name}");
            assert_eq!(
                native,
                paused(
                    (managed.policy, managed.disclosure),
                    Reason::InternalStateUnavailable
                ),
                "{name}"
            );
        } else {
            assert_eq!(native, managed, "{name}");
        }
        published.insert((native.state, native.reason));
    }
    assert!(converging >= 5, "the vectors name each contradiction");
    // Every state and reason native publishes from a managed row.
    for (state, reason) in [
        (State::Matched, Reason::None),
        (State::AwaitingObservation, Reason::AwaitingFreshObservation),
        (State::Paused, Reason::ManualFanReadbackAmbiguous),
        (State::External, Reason::HyperBoostActive),
        (State::Paused, Reason::StrictPolicy),
        (State::Paused, Reason::UpgradeDisclosurePending),
        (State::Paused, Reason::ConfigurationQuarantined),
        (State::Paused, Reason::LowPower),
        (State::Paused, Reason::PowerUnsettled),
        (State::Paused, Reason::CompetingControllerPresent),
        (State::Paused, Reason::InternalStateUnavailable),
    ] {
        assert!(
            published.contains(&(state as i32, reason as i32)),
            "{state:?} {reason:?}"
        );
    }
}

#[cfg(feature = "service-thermal-convergence-v1")]
mod ledger_status {
    use super::*;

    fn reserved(performance: bool, fans: bool) -> Admission {
        Admission {
            ledger: Ledger::Reserved { performance, fans },
            ..admitted(MAINTAIN, DISCLOSURE_ACKNOWLEDGED)
        }
    }

    #[test]
    fn a_closed_ledger_publishes_strict_and_quarantined_before_every_later_check() {
        for (policy, disclosure) in [
            (MAINTAIN, DISCLOSURE_ACKNOWLEDGED),
            (MAINTAIN, DISCLOSURE_PENDING),
            (STRICT, DISCLOSURE_NOT_REQUIRED),
        ] {
            let admission = Admission {
                ledger: Ledger::Closed,
                low_power: true,
                power: 3,
                controller_present: true,
                pending: true,
                ..admitted(policy, disclosure)
            };
            assert_eq!(
                refused(&admission),
                paused((STRICT, disclosure), Reason::ConfigurationQuarantined)
            );
        }
        // A quarantined configuration has no disclosure to keep.
        let admission = Admission {
            policy: SavedPolicy::Quarantined,
            ledger: Ledger::Closed,
            ..admitted(MAINTAIN, DISCLOSURE_PENDING)
        };
        assert_eq!(
            refused(&admission),
            paused(
                (STRICT, DISCLOSURE_NOT_REQUIRED),
                Reason::ConfigurationQuarantined
            )
        );
    }

    #[test]
    fn a_reservation_awaits_reconciliation_under_either_policy_once_power_is_settled() {
        let awaiting = |policy, components: &[Component]| {
            status(
                policy,
                State::AwaitingObservation,
                components,
                Reason::AwaitingFreshObservation,
                false,
            )
        };
        for (policy, disclosure) in [
            (MAINTAIN, DISCLOSURE_ACKNOWLEDGED),
            (MAINTAIN, DISCLOSURE_PENDING),
            (STRICT, DISCLOSURE_NOT_REQUIRED),
        ] {
            // It is reconciled ahead of controller and pending checks.
            let admission = Admission {
                controller_present: true,
                pending: true,
                ..Admission {
                    policy: SavedPolicy::Admitted { policy, disclosure },
                    ..reserved(true, false)
                }
            };
            assert_eq!(
                refused(&admission),
                awaiting((policy, disclosure), &[Component::Performance])
            );
        }
        assert_eq!(
            refused(&reserved(false, true)),
            awaiting((MAINTAIN, DISCLOSURE_ACKNOWLEDGED), &[Component::Fans])
        );
        assert_eq!(
            refused(&reserved(true, true)),
            awaiting(
                (MAINTAIN, DISCLOSURE_ACKNOWLEDGED),
                &[Component::Performance, Component::Fans]
            )
        );
        // Low power or unsettled power closes admission, with managed's
        // closure order.
        let maintain = (MAINTAIN, DISCLOSURE_ACKNOWLEDGED);
        let low = Admission {
            low_power: true,
            ..reserved(true, false)
        };
        assert_eq!(refused(&low), paused(maintain, Reason::LowPower));
        let unsettled = Admission {
            power: -1,
            ..reserved(true, false)
        };
        assert_eq!(
            refused(&unsettled),
            paused(maintain, Reason::PowerUnsettled)
        );
        let strict = Admission {
            policy: SavedPolicy::Admitted {
                policy: STRICT,
                disclosure: DISCLOSURE_NOT_REQUIRED,
            },
            ..unsettled
        };
        assert_eq!(
            refused(&strict),
            paused((STRICT, DISCLOSURE_NOT_REQUIRED), Reason::StrictPolicy)
        );
    }

    #[test]
    fn the_status_view_follows_authority_and_the_declared_components() {
        use openblade_domain::thermal_convergence as ledger;
        const SID: &str = "S-1-5-21-111-222-333-1001";
        assert_eq!(Ledger::of(None), Ledger::Closed);
        let mut state = ledger::State::empty(SID);
        assert_eq!(Ledger::of(Some(&state)), Ledger::Open);
        let target: ledger::Intent = serde_json::from_value(serde_json::json!({
            "performance": {"value": 1, "source": 1},
            "fans": {"value": 0, "source": 1},
            "lighting": null,
            "battery": null,
        }))
        .unwrap();
        for (declared, performance, fans) in [
            (vec![ledger::Component::PERFORMANCE], true, false),
            (vec![ledger::Component::FANS], false, true),
            (
                vec![ledger::Component::PERFORMANCE, ledger::Component::FANS],
                true,
                true,
            ),
        ] {
            state.in_flight = Some(ledger::InFlight {
                transition_attempt_id: ledger::Guid::EMPTY,
                declared_components: declared,
                pre_write_observation: ledger::Observation::thermal(None, None, None, None),
                desired_target: target.clone(),
                // Managed reconciles any reservation, required or not.
                reconciliation_required: false,
            });
            assert_eq!(
                Ledger::of(Some(&state)),
                Ledger::Reserved { performance, fans }
            );
        }
    }
}
