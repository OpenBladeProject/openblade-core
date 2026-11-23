//! The native capability contract: a status carries the observation record
//! only beside a confirmed active HyperBoost, and every projection of one
//! confirmed state, read, retained, unavailable or rebased after a mutation,
//! carries the same record.
use super::*;
use serde_json::json;

fn state(hyper_boost: Value) -> FeatureRecord {
    Map::from_iter([
        ("performance".into(), Value::Null),
        ("fans".into(), json!(0)),
        ("battery".into(), Value::Null),
        ("lighting".into(), Value::Null),
        ("hyperBoostActive".into(), hyper_boost),
    ])
}

fn record() -> FeatureRecord {
    serde_json::from_str(include_str!("observed_capabilities.json")).unwrap()
}

#[test]
fn only_a_confirmed_active_hyper_boost_needs_a_record() {
    assert_eq!(observed_capabilities(None), None);
    for unknown in [Value::Null, json!(false)] {
        assert_eq!(observed_capabilities(Some(&state(unknown))), None);
    }
    assert_eq!(
        observed_capabilities(Some(&state(json!(true)))),
        Some(record())
    );
}

/// The record admits no control: HyperBoost is observed only and every
/// control list is empty. Its one true member is the exact RZ09-0581
/// temporary full charge, which the model's battery evidence establishes;
/// false there would tell the tray the model has none.
#[test]
fn the_record_admits_no_control_and_keeps_the_exact_model_temporary_full_charge() {
    let record = Value::Object(record());
    assert_eq!(record["hyperBoostAccess"], 0);
    for list in [
        "performancePresets",
        "fanModes",
        "lightingEffects",
        "chargeLimits",
        "logoLightingModes",
        "oneShotLaptopControls",
        "hostManagedLaptopControls",
    ] {
        assert_eq!(record[list], json!([]), "{list}");
    }
    let evidence: Vec<Value> = serde_json::from_str(include_str!(
        "../../../../src/OpenBlade.Protocol/Fixtures/Rz09_0581_02e0_3_01_BatteryProduction.json"
    ))
    .unwrap();
    let captured = |action: &str| {
        evidence.iter().any(|entry| {
            entry["action"] == action
                && entry["device"]["productId"] == 0x02e0
                && ["captured", "decoded", "applied", "readBack", "restored"]
                    .iter()
                    .all(|key| entry[*key] == true)
        })
    };
    let temporary = captured("charge-to-100-enable") && captured("charge-to-100-cancel");
    assert!(temporary);
    assert_eq!(record["supportsTemporaryFullCharge"], temporary);
}

#[cfg(all(windows, feature = "service-mailbox-observation-v1"))]
mod projections {
    use super::*;
    use openblade_domain::thermal_observation::ObservedPerformance;
    use std::time::Duration;

    fn base() -> Snapshot {
        Snapshot::observed_read_only(0, 0, false, false, vec![], None, "Awaiting.".into())
    }

    fn read(performance: ObservedPerformance, fresh: bool) -> Snapshot {
        let deadline = tokio::time::Instant::now() + Duration::from_secs(60);
        base().with_mailbox_read(performance, ReadConfirmation::new(deadline, move || fresh))
    }

    fn capabilities(status: &Snapshot) -> Option<&FeatureRecord> {
        status.capabilities.as_ref()
    }

    #[test]
    fn a_read_and_its_retained_and_unavailable_projections_agree() {
        let hyper_boost = read(ObservedPerformance::HyperBoostObserved, true);
        assert_eq!(capabilities(&hyper_boost), Some(&record()));
        assert_eq!(
            capabilities(&base().retained_mailbox(&hyper_boost, 2)),
            Some(&record())
        );
        assert_eq!(
            capabilities(&hyper_boost.unavailable_mailbox(3, " Gone.")),
            None
        );
        let balanced = read(ObservedPerformance::BalancedAutomatic, true);
        assert_eq!(capabilities(&balanced), None);
        // A Balanced read replaces a HyperBoost record.
        assert_eq!(
            capabilities(&hyper_boost.retained_mailbox(&balanced, 4)),
            None
        );
    }

    /// A mutation's own fresh read publishes its confirmed state with the
    /// record it needs, whatever the previous publication carried; without
    /// a fresh read the previous publication's state keeps its own record.
    #[cfg(feature = "service-thermal-mutation-v1")]
    #[test]
    fn a_rebased_mutation_carries_the_record_of_the_state_it_publishes() {
        let previous_hyper_boost = read(ObservedPerformance::HyperBoostObserved, true);
        let previous_balanced = read(ObservedPerformance::BalancedAutomatic, true);
        for (delta, latest, expect) in [
            (
                ObservedPerformance::HyperBoostObserved,
                &previous_balanced,
                true,
            ),
            (
                ObservedPerformance::BalancedAutomatic,
                &previous_hyper_boost,
                false,
            ),
        ] {
            let rebased = read(delta, true).rebase_thermal_mutation(latest);
            assert_eq!(capabilities(&rebased).is_some(), expect, "{delta:?}");
            assert_eq!(
                rebased.confirmed_state.as_ref().unwrap()["hyperBoostActive"],
                expect
            );
        }
        let stale = read(ObservedPerformance::BalancedAutomatic, false);
        let rebased = stale.rebase_thermal_mutation(&previous_hyper_boost);
        assert_eq!(capabilities(&rebased), Some(&record()));
        assert_eq!(
            rebased.confirmed_state.as_ref().unwrap()["hyperBoostActive"],
            true
        );
    }
}
