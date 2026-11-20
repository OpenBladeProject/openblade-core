use openblade_domain::automation::{self, Provenance, ValueOrigin};
use serde::Deserialize;
use std::collections::HashSet;

const JSON: &str = include_str!("../../../../tests/Fixtures/Parity/v1/automation-policy.json");

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Fixture {
    schema_version: u32,
    suite: String,
    source_revision: String,
    cases: Vec<Case>,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Case {
    id: String,
    operation: u32,
    profile: bool,
    confirmed: bool,
    temporary: bool,
    persistent: bool,
    external_unknown: bool,
    application: bool,
    origin: u32,
    provenance: u32,
}

fn read(json: &str) -> Result<Fixture, String> {
    let fixture: Fixture = serde_json::from_str(json).map_err(|e| e.to_string())?;
    if fixture.schema_version != 1
        || fixture.suite != "automation-selection"
        || fixture.source_revision != "9e53ed92bbe3985e1ea899ed6c1124f6f6dd020e"
        || fixture.cases.len() != 18
    {
        return Err("fixture provenance/count".into());
    }
    let mut ids = HashSet::new();
    for case in &fixture.cases {
        if case.id.trim().is_empty()
            || !ids.insert(&case.id)
            || case.operation > 1
            || case.origin > 3
            || case.provenance > 4
        {
            return Err("fixture case".into());
        }
    }
    Ok(fixture)
}

#[test]
fn shared_canonical_selection_cases() {
    for case in read(JSON).unwrap().cases {
        let actual = if case.operation == 0 {
            automation::performance(case.temporary, case.persistent, case.application)
        } else {
            automation::lighting(
                case.profile,
                case.confirmed,
                case.temporary,
                case.persistent,
                case.external_unknown,
                case.application,
            )
        };
        let origin = match actual.origin {
            ValueOrigin::Profile => 0,
            ValueOrigin::Confirmed => 1,
            ValueOrigin::Temporary => 2,
            ValueOrigin::Persistent => 3,
        };
        let source = match actual.provenance {
            Provenance::ConfirmedFirmwareState => 0,
            Provenance::PowerProfile => 1,
            Provenance::ApplicationProfile => 2,
            Provenance::TemporaryOverride => 3,
            Provenance::PersistentOverride => 4,
        };
        assert_eq!(
            (origin, source),
            (case.origin, case.provenance),
            "{}",
            case.id
        );
    }
}

#[test]
fn malformed_fixture_fails_closed() {
    for invalid in [
        JSON.replace("\"schemaVersion\": 1", "\"schemaVersion\": 2"),
        JSON.replace("automation-selection", "unknown"),
        JSON.replace("9e53ed92bbe3985e1ea899ed6c1124f6f6dd020e", "unknown"),
        JSON.replace("\"operation\":0", "\"operation\":9"),
        JSON.replacen("\"profile\":true", "\"profile\":\"true\"", 1),
        JSON.replacen("\"profile\":true", "\"profile\":true,\"profile\":false", 1),
        JSON.replacen("\"profile\":true,", "", 1),
        JSON.replacen("\"origin\":0", "\"origin\":0,\"extra\":0", 1),
        JSON.replace("performance-application", "performance-power"),
    ] {
        assert!(read(&invalid).is_err());
    }
}

#[test]
fn decoded_performance_source_expands_external_policy_from_frozen_matrix() {
    #[derive(Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct PerformanceSourceFixture {
        schema_version: u32,
        source_revision: String,
        sources: Vec<i32>,
        cases: Vec<(i32, Vec<bool>)>,
    }
    let fixture: PerformanceSourceFixture = serde_json::from_str(include_str!(
        "../../../../tests/Fixtures/Parity/v1/performance-source.json"
    ))
    .unwrap();
    assert_eq!(fixture.schema_version, 1);
    assert_eq!(
        fixture.source_revision,
        "8a6e49d7bcffc52c84e8d447711f9192385e9da6"
    );
    assert_eq!(fixture.sources, [0, 1, 2, 3, -1, i32::MIN, i32::MAX]);
    assert_eq!(fixture.cases.len(), 14);
    for (preset, expected) in fixture.cases {
        assert_eq!(expected.len(), fixture.sources.len());
        for (&source, eligible) in fixture.sources.iter().zip(expected) {
            // Preserve the historical matrix as provenance; the intended change
            // admits every defined preset on either confirmed external source.
            let expected = if source == 0 || source == 2 {
                (0..=9).contains(&preset)
            } else {
                eligible
            };
            assert_eq!(
                automation::performance_source_eligible(preset, source),
                expected,
                "preset {preset}, source {source}"
            );
        }
    }
}

/// Managed `ProfileAutomationState.IsOrdinaryExternalOverride` on
/// `PowerSource.ExternalUnknown`; profiles keep the frozen refusal.
#[test]
fn unclassified_external_admits_only_ordinary_explicit_overrides() {
    let source = automation::UNCLASSIFIED_EXTERNAL;
    assert_eq!(source, 3);
    for preset in -1..=10 {
        assert_eq!(
            automation::override_source_eligible(preset, source),
            matches!(preset, 0..=2 | 5..=9),
            "preset {preset}"
        );
        assert!(!automation::performance_source_eligible(preset, source));
        for classified in [0, 1, 2] {
            assert_eq!(
                automation::override_source_eligible(preset, classified),
                automation::performance_source_eligible(preset, classified)
            );
        }
    }
}

#[test]
fn temporary_expiry_keeps_external_intent_while_unclassified() {
    // A classified class retires every other saved class.
    for observed in [0, 1, 2] {
        for saved in [0, 1, 2, 3] {
            assert_eq!(
                automation::temporary_expired(saved, observed),
                saved != observed
            );
        }
    }
    // Unclassified external power retires only battery intent.
    for (saved, expired) in [(0, false), (1, true), (2, false), (3, false)] {
        assert_eq!(automation::temporary_expired(saved, 3), expired);
    }
    for observed in [-1, 4] {
        assert!(!automation::temporary_expired(1, observed));
    }
}

#[test]
fn unclassified_temporary_follows_classification_within_one_power_generation() {
    use automation::temporary_after as after;
    // Managed `PreserveOverridesForExternalClassification`: classified in the
    // same generation keeps it, refined to that class.
    for class in [0, 2, 3] {
        assert_eq!(after(3, Some(7), class, 7), Some(class));
        // A Windows power-kind change since the save retires it.
        assert_eq!(after(3, Some(7), class, 8), None);
        assert_eq!(after(3, None, class, 7), None);
    }
    // Battery always retires it; an unknown reading retires nothing.
    assert_eq!(after(3, Some(7), 1, 7), None);
    assert_eq!(after(3, Some(7), -1, 8), Some(3));
    // Every other saved class follows `temporary_expired`, whatever the generation.
    for saved in [0, 1, 2] {
        for observed in [-1, 0, 1, 2, 3, 4] {
            for generation in [7, 8] {
                assert_eq!(
                    after(saved, Some(7), observed, generation),
                    (!automation::temporary_expired(saved, observed)).then_some(saved)
                );
            }
        }
    }
}
