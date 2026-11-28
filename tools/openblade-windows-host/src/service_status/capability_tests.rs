//! The native capability contract: every status carries this composition's
//! record (`capability_record`), managed's unavailable record until an exact
//! state is confirmed and its identified record beside one, and every
//! projection of one confirmed state, read, retained, unavailable, battery or
//! rebased after a mutation, carries the same record.
use super::capability_record::compiled;
use super::*;
use serde_json::json;

fn identified() -> FeatureRecord {
    compiled(true)
}

fn unidentified() -> FeatureRecord {
    compiled(false)
}

/// The identified record is the exact model's and differs from the
/// unidentified one in every composition: HyperBoost is at least read only.
#[test]
fn identity_selects_the_record() {
    let identified = Value::from(identified());
    let unidentified = Value::from(unidentified());
    assert_ne!(identified, unidentified);
    assert_eq!(unidentified["hyperBoostAccess"], Value::Null);
    assert_eq!(unidentified["supportsTemporaryFullCharge"], false);
    assert!(identified["hyperBoostAccess"].as_i64().unwrap() >= 1);
    // Nothing native does not serve is claimed, whatever the composition.
    for list in ["oneShotLaptopControls", "hostManagedLaptopControls"] {
        assert_eq!(identified[list], json!([]), "{list}");
    }
    assert_eq!(identified["specialKeyBindingsAccess"], Value::Null);
    assert_eq!(identified["supportsPlaybackMuteIndicator"], false);
    assert_eq!(identified["supportsFanStop"], false);
    assert_eq!(identified["cpuPowerLimit"], Value::Null);
    assert_eq!(identified["cpuVoltageOptimizer"], Value::Null);
}

/// The controls each served request family admits, as the record states
/// them for this build.
#[test]
fn the_record_admits_what_this_composition_serves() {
    let record = Value::from(identified());
    let serves = StatusRequest::serves;
    let listed = |name: &str| !record[name].as_array().unwrap().is_empty();
    assert_eq!(
        listed("performancePresets"),
        serves("setPerformanceOverride")
    );
    assert_eq!(
        record["fanModes"].as_array().unwrap().len() > 1,
        serves("setFanOverride")
    );
    assert_eq!(listed("chargeLimits"), serves("setBatteryPolicy"));
    assert_eq!(listed("lightingEffects"), serves("setLightingOverride"));
    assert_eq!(
        record["supportsKeyboardBrightness"],
        serves("stepKeyboardBrightness")
    );
    assert_eq!(
        listed("supportedLightingRefreshRates"),
        serves("setLightingRefreshRate")
    );
    // Two requests are dispatched without their backend, and refuse:
    // `setFullChargeOverride` without the temporary-charge lane
    // (`battery_mutation`), and `setHyperBoost` without the probes the
    // native composition sets only beside the lifecycle triggers
    // (`thermal_mutation` asserts that at compile time).
    assert_eq!(
        record["supportsTemporaryFullCharge"],
        cfg!(feature = "service-temporary-charge-v1")
    );
    // GPU overclock has two blockers: the GPU-only `setGpuClockTuning`,
    // checked here, and the confirmed offsets, which publication checks
    // (`capability_record::serialize_confirmed_state`). Serving the request
    // fails here so the record is reviewed with it.
    assert!(!serves("setGpuClockTuning"));
    assert!(record["gpuOverclock"].is_null());
    assert_eq!(
        record["hyperBoostAccess"] == 2,
        cfg!(all(
            feature = "service-hyperboost-v1",
            feature = "service-profile-triggers-v1"
        ))
    );
    assert_eq!(
        record["hasFanStopTrialEvidence"] == true,
        serves("setFanStopMode")
    );
    // Managed's effects native refuses stay out of the record: Reactive,
    // Ripple, AmbientAwareness, AudioMeter and Sequence.
    let effects = record["lightingEffects"].as_array().unwrap();
    for refused in [4, 9, 10, 11, 13] {
        assert!(!effects.contains(&json!(refused)), "{refused}");
    }
    // Managed's ManualCurve stays out of the fan modes.
    assert!(!record["fanModes"].as_array().unwrap().contains(&json!(2)));
}

/// Manual fans are ranged exactly for the presets native's fan recipes plan
/// from (`fan_recipe::Plan::select` over each preset's Automatic baseline),
/// and the range is the intent `FanIntent::checked` admits.
#[cfg(feature = "service-fan-mutation-v2")]
#[test]
fn the_fan_ranges_are_the_presets_native_plans_manual_fans_from() {
    use openblade_domain::fan_recipe::{FanIntent, Plan};
    use openblade_domain::thermal_observation::ObservedPerformance as O;
    let record = Value::from(identified());
    let ranges = record["fanSpeedRangesByPerformancePreset"]
        .as_object()
        .unwrap();
    let baselines = [
        ("Silent", O::SilentAutomatic),
        ("Balanced", O::BalancedAutomatic),
        ("Performance", O::PerformanceAutomatic),
        ("BatterySaver", O::BatterySaverAutomatic),
        ("BatteryBalanced", O::BatteryBalancedAutomatic),
        (
            "Custom",
            O::Custom {
                manual: false,
                levels: None,
            },
        ),
    ];
    let fixed = FanIntent::checked(1, 3000).unwrap();
    for (name, baseline) in baselines {
        assert_eq!(
            ranges.contains_key(name),
            Plan::select(baseline, fixed, 0).is_some(),
            "{name}"
        );
    }
    for range in ranges.values().chain([&record["fanSpeedRange"]]) {
        let (minimum, maximum, step) = (
            range["minimumRpm"].as_u64().unwrap() as u16,
            range["maximumRpm"].as_u64().unwrap() as u16,
            range["stepRpm"].as_u64().unwrap() as u16,
        );
        assert!(FanIntent::checked(1, minimum).is_some());
        assert!(FanIntent::checked(1, maximum).is_some());
        assert!(FanIntent::checked(1, minimum - step).is_none());
        assert!(FanIntent::checked(1, maximum + step).is_none());
        assert!(FanIntent::checked(1, minimum + step / 2).is_none());
    }
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

    /// The source files outside tests that call `projection`, relative to
    /// `src`.
    fn publishers(projection: &str) -> Vec<String> {
        let call = format!(".{projection}(");
        fn walk(
            dir: &std::path::Path,
            root: &std::path::Path,
            call: &str,
            found: &mut Vec<String>,
        ) {
            for entry in std::fs::read_dir(dir).unwrap() {
                let path = entry.unwrap().path();
                let relative = path.strip_prefix(root).unwrap().to_string_lossy();
                let relative = relative.replace('\\', "/");
                if relative.contains("test") {
                    continue;
                }
                if path.is_dir() {
                    walk(&path, root, call, found);
                } else if std::fs::read_to_string(&path).is_ok_and(|text| text.contains(call)) {
                    found.push(relative);
                }
            }
        }
        let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("src");
        let mut found = Vec::new();
        walk(&root, &root, &call, &mut found);
        found.sort();
        found
    }

    /// Publishing confirmed GPU offsets while the record keeps managed's
    /// GPU overclock absent fails in development, whatever projection added
    /// them.
    #[cfg(debug_assertions)]
    #[test]
    #[should_panic(expected = "confirmedState.gpuClockOffsets is published")]
    fn publishing_gpu_offsets_needs_the_gpu_overclock_record() {
        let mut observed = read(ObservedPerformance::BalancedAutomatic, true);
        observed
            .confirmed_state
            .as_mut()
            .unwrap()
            .insert("gpuClockOffsets".into(), json!({}));
        let _ = crate::service_status::encode_status_event(&observed);
    }

    /// The identified record is exact RZ09-0581 data and a confirmed state
    /// names no model, so the record is bound to its producers: the
    /// RZ09-0581 Reader and its battery evidence share its identity, and only
    /// the reviewed read, firmware and mutation paths over that Reader call
    /// a projection that confirms a state (`Snapshot::confirm`). A new
    /// caller fails here until it carries an identity of its own.
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
        let expected: [(&str, &[&str]); 3] = [
            (
                "with_mailbox_read",
                &[
                    "windows/scm/installed/fan_mutation.rs",
                    "windows/scm/installed/mailbox_read.rs",
                    "windows/scm/installed/thermal_mutation.rs",
                ],
            ),
            (
                "battery_projection",
                &[
                    "service_status.rs",
                    "windows/scm/installed/battery_mutation.rs",
                ],
            ),
            (
                "with_standard_performance",
                &["windows/scm/installed/firmware_source.rs"],
            ),
        ];
        for (projection, files) in expected {
            assert_eq!(publishers(projection), files, "{projection}");
        }
    }

    /// Before any read the status carries managed's unavailable record; a
    /// read, whatever it observed, and its retained projection carry the
    /// identified record; losing the read returns to the unavailable one.
    #[test]
    fn a_read_and_its_retained_and_unavailable_projections_agree() {
        assert_eq!(capabilities(&base()), Some(&unidentified()));
        for performance in [
            ObservedPerformance::HyperBoostObserved,
            ObservedPerformance::BalancedAutomatic,
        ] {
            let observed = read(performance, true);
            assert_eq!(
                capabilities(&observed),
                Some(&identified()),
                "{performance:?}"
            );
            assert_eq!(
                capabilities(&base().retained_mailbox(&observed, 2)),
                Some(&identified())
            );
            let gone = observed.unavailable_mailbox(3, " Gone.");
            assert_eq!(capabilities(&gone), Some(&unidentified()));
            // Retaining an unavailable read keeps no identity.
            assert_eq!(
                capabilities(&observed.retained_mailbox(&gone, 4)),
                Some(&unidentified())
            );
        }
    }

    /// A mutation's own fresh read publishes its confirmed state with the
    /// record it needs, whatever the previous publication carried; without
    /// a fresh read the previous publication's state keeps its own record.
    #[cfg(feature = "service-thermal-mutation-v1")]
    #[test]
    fn a_rebased_mutation_carries_the_record_of_the_state_it_publishes() {
        let previous = read(ObservedPerformance::HyperBoostObserved, true);
        let unavailable = previous.unavailable_mailbox(2, " Gone.");
        for latest in [&previous, &unavailable] {
            let rebased =
                read(ObservedPerformance::BalancedAutomatic, true).rebase_thermal_mutation(latest);
            assert_eq!(capabilities(&rebased), Some(&identified()));
            assert_eq!(
                rebased.confirmed_state.as_ref().unwrap()["hyperBoostActive"],
                false
            );
        }
        let stale = read(ObservedPerformance::BalancedAutomatic, false);
        let rebased = stale.rebase_thermal_mutation(&previous);
        assert_eq!(capabilities(&rebased), Some(&identified()));
        assert_eq!(
            rebased.confirmed_state.as_ref().unwrap()["hyperBoostActive"],
            true
        );
    }

    /// A battery reading confirms the exact device's state, so a status
    /// that had none takes the identified record with it.
    #[cfg(feature = "service-thermal-mutation-v1")]
    #[test]
    fn a_battery_reading_publishes_the_identified_record() {
        let observed = base().battery_projection(None, Some((0xd0, Some(false))), "");
        assert_eq!(capabilities(&observed), Some(&identified()));
        assert_eq!(
            observed.confirmed_state.as_ref().unwrap()["battery"]["chargeLimitPercent"],
            0x50
        );
        // A reading native cannot publish confirms nothing.
        let none = base().battery_projection(None, Some((0x50, Some(true))), "");
        assert_eq!(capabilities(&none), Some(&unidentified()));
        assert!(none.confirmed_state.is_none());
    }
}

/// Managed's Fan Stop evidence for an exact RZ09-0581 host, whose production
/// factory admits both flags, and for any other host, which has neither. A
/// composition that serves the Fan Stop requests publishes the first; every
/// other keeps the second.
#[test]
fn the_record_carries_the_fan_stop_evidence_of_this_composition() {
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
    let record = Value::from(identified());
    assert_eq!(record["hasFanStopTrialEvidence"], row["evidence"]["trial"]);
    assert_eq!(
        record["hasFanStopSourceEvidence"],
        row["evidence"]["source"]
    );
    let unidentified = Value::from(unidentified());
    assert_eq!(unidentified["hasFanStopTrialEvidence"], false);
    assert_eq!(unidentified["hasFanStopSourceEvidence"], false);
}
