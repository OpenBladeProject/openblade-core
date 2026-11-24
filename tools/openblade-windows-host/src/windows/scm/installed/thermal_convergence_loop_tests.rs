use super::*;
use serde_json::json;

fn journal(document: Value) -> Vec<u8> {
    serde_json::to_vec(&document).unwrap()
}

fn relinquished(schema: i64) -> Value {
    json!({
        "schemaVersion": schema, "state": RELINQUISHED, "baseMode": 1,
        "updatedAtUtc": "2026-11-24T00:00:00+00:00", "requiresCoolingPad": false,
        "restorePerformance": 1, "restoreFanControl": {"mode": 0},
    })
}

/// Managed `IsSafeMaintainHyperBoostJournalBaseline`: absent or a
/// recognized Relinquished document is safe; anything else blocks.
#[test]
fn only_an_absent_or_relinquished_journal_is_safe() {
    assert!(journal_safe(None));
    for schema in 2..=8 {
        assert!(
            journal_safe(Some(&journal(relinquished(schema)))),
            "{schema}"
        );
    }
    let mut first = relinquished(1);
    first.as_object_mut().unwrap().remove("requiresCoolingPad");
    assert!(journal_safe(Some(&journal(first))));
    // Schema 1 carries no cooling-pad member; a later one must.
    assert!(!journal_safe(Some(&journal(relinquished(1)))));
    let mut missing = relinquished(8);
    missing.as_object_mut().unwrap().remove("restoreFanControl");
    assert!(!journal_safe(Some(&journal(missing))));
    for state in [0, 1, 2] {
        let mut pending = relinquished(8);
        pending["state"] = json!(state);
        assert!(!journal_safe(Some(&journal(pending))), "{state}");
    }
    // A newer or unknown schema, a named enum and a damaged file block.
    assert!(!journal_safe(Some(&journal(relinquished(9)))));
    assert!(!journal_safe(Some(&journal(relinquished(0)))));
    let mut named = relinquished(8);
    named["state"] = json!("Relinquished");
    assert!(!journal_safe(Some(&journal(named))));
    assert!(!journal_safe(Some(b"{\"schemaVersion\":8")));
    assert!(!journal_safe(Some(b"[]")));
}

#[test]
fn attempt_ids_are_distinct_version_four_guids() {
    let first = new_guid("S-1-5-21-1");
    let second = new_guid("S-1-5-21-1");
    assert_ne!(first, second);
    for id in [first, second] {
        let text = id.to_string();
        assert_eq!(&text[14..15], "4", "{text}");
        assert!(matches!(&text[19..20], "8" | "9" | "a" | "b"), "{text}");
        assert_eq!(ledger::Guid::parse(&text), Some(id));
    }
}

#[test]
fn the_classifier_flags_are_managed_contradictions() {
    assert_eq!(contradiction(false, false), ledger::Contradiction::NONE);
    assert_eq!(
        contradiction(true, false),
        ledger::Contradiction::PERFORMANCE
    );
    assert_eq!(
        contradiction(false, true),
        ledger::Contradiction::FAN_TOPOLOGY
    );
    assert_eq!(contradiction(true, true), ledger::Contradiction(3));
}

/// A reserved target classifies as the intent it was saved from.
#[test]
fn a_reserved_target_is_the_classifier_intent() {
    let target: ledger::Intent = serde_json::from_value(json!({
        "performance": {"value": CUSTOM, "source": 1},
        "fans": {"value": 1, "source": 3},
        "lighting": null, "battery": null,
        "customPerformance": {"cpu": 0, "gpu": 2, "maximumFans": false},
    }))
    .unwrap();
    let intent = target_intent(&target);
    assert_eq!(intent.performance, Destination::custom(0, 2, false));
    assert_eq!(intent.fans, FanMode(1));
    let preset = ledger::Intent {
        performance: ledger::Resolved {
            value: 2,
            source: 1,
        },
        custom_performance: None,
        ..target
    };
    assert_eq!(
        target_intent(&preset).performance,
        Preset::from_wire(2).map(Destination::Preset)
    );
}
