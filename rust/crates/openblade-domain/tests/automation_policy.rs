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
fn decoded_performance_source_matches_shared_matrix() {
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
            assert_eq!(
                automation::performance_source_eligible(preset, source),
                eligible,
                "preset {preset}, source {source}"
            );
        }
    }
}
