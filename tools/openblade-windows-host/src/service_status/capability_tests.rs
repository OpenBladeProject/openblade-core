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

/// The reviewed record, with the Fan Stop evidence managed admits on
/// exact RZ09-0581 when the Fan Stop requests are served.
fn record() -> FeatureRecord {
    #[cfg_attr(not(feature = "service-fan-stop-v1"), allow(unused_mut))]
    let mut record: FeatureRecord =
        serde_json::from_str(include_str!("observed_capabilities.json")).unwrap();
    #[cfg(feature = "service-fan-stop-v1")]
    for evidence in ["hasFanStopTrialEvidence", "hasFanStopSourceEvidence"] {
        record.insert(evidence.into(), json!(true));
    }
    record
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

    /// The source files outside tests that publish an observation through
    /// `with_mailbox_read`, relative to `src`.
    fn observation_publishers() -> Vec<String> {
        fn walk(dir: &std::path::Path, root: &std::path::Path, found: &mut Vec<String>) {
            for entry in std::fs::read_dir(dir).unwrap() {
                let path = entry.unwrap().path();
                let relative = path.strip_prefix(root).unwrap().to_string_lossy();
                let relative = relative.replace('\\', "/");
                if relative.contains("test") {
                    continue;
                }
                if path.is_dir() {
                    walk(&path, root, found);
                } else if std::fs::read_to_string(&path)
                    .is_ok_and(|text| text.contains(".with_mailbox_read("))
                {
                    found.push(relative);
                }
            }
        }
        let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("src");
        let mut found = Vec::new();
        walk(&root, &root, &mut found);
        found.sort();
        found
    }

    /// The record is exact RZ09-0581 data and a confirmed state names no
    /// model, so the record is bound to its producers: the RZ09-0581 Reader
    /// and its battery evidence share its identity, and only the reviewed
    /// read and mutation paths over that Reader publish an observation. A
    /// new publisher fails here until it carries an identity of its own.
    #[test]
    fn the_record_is_bound_to_the_one_observation_producer() {
        assert_eq!(
            crate::discovery::performance::OBSERVED_IDENTITY,
            OBSERVED_CAPABILITIES_MODEL
        );
        let (model, _, product) = OBSERVED_CAPABILITIES_MODEL;
        let evidence: Vec<Value> = serde_json::from_str(include_str!(
            "../../../../src/OpenBlade.Protocol/Fixtures/Rz09_0581_02e0_3_01_BatteryProduction.json"
        ))
        .unwrap();
        assert!(
            evidence
                .iter()
                .all(|entry| entry["device"]["model"] == model
                    && entry["device"]["productId"] == product)
        );
        assert_eq!(
            observation_publishers(),
            [
                "windows/scm/installed/fan_mutation.rs",
                "windows/scm/installed/mailbox_read.rs",
                "windows/scm/installed/thermal_mutation.rs",
            ]
        );
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

/// Managed's recorded observation record for an exact RZ09-0581 host, whose
/// production factory admits both Fan Stop evidence flags, and for any other
/// host, which has neither. A composition that serves the Fan Stop requests
/// publishes the first; every other keeps the second.
#[test]
fn the_record_is_managed_with_the_fan_stop_evidence_of_this_composition() {
    let vectors: Value =
        serde_json::from_str(include_str!("../../tests/data/fan-stop-vectors.json")).unwrap();
    let rows = vectors["capabilities"].as_array().unwrap();
    let exact = &rows[0];
    assert_eq!(exact["productName"], "Blade 16 - RZ09-0581");
    assert_eq!(exact["evidence"], json!({"trial": true, "source": true}));
    assert_eq!(
        rows[1]["evidence"],
        json!({"trial": false, "source": false})
    );
    let row = if cfg!(feature = "service-fan-stop-v1") {
        exact
    } else {
        &rows[1]
    };
    assert_eq!(
        observed_capabilities(Some(&state(json!(true)))).map(Value::Object),
        Some(row["record"].clone())
    );
}
