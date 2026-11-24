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
        "restorePowerSource": 0,
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

/// Managed `IsWellFormed` over each member a Relinquished journal carries:
/// a document it does not admit blocks, whatever its state says.
#[test]
fn a_journal_managed_would_not_admit_blocks() {
    let safe = |schema: i64, edit: &dyn Fn(&mut Value)| {
        let mut document = relinquished(schema);
        edit(&mut document);
        journal_safe(Some(&journal(document)))
    };
    let custom = json!({"cpu": 0, "gpu": 2, "maximumFans": false});
    // Restore intent: a defined preset, Custom settings exactly for Custom,
    // and a defined fan mode.
    for preset in [-1, 10, 5] {
        assert!(
            !safe(8, &|d| d["restorePerformance"] = json!(preset)),
            "{preset}"
        );
    }
    assert!(safe(8, &|d| {
        d["restorePerformance"] = json!(5);
        d["restoreCustomPerformance"] = custom.clone();
    }));
    assert!(!safe(8, &|d| d["restoreCustomPerformance"] = custom.clone()));
    assert!(!safe(8, &|d| d["restoreFanControl"]["mode"] = json!(5)));
    assert!(!safe(8, &|d| d["restoreGpuClockTuning"] = json!(1)));
    // The base mode: Silent, Balanced or Performance, or Custom with its
    // restore settings. A legacy schema restores its base mode.
    for mode in [3, 4, 5, 6] {
        assert!(!safe(8, &|d| d["baseMode"] = json!(mode)), "{mode}");
    }
    assert!(safe(8, &|d| {
        d["baseMode"] = json!(5);
        d["restorePerformance"] = json!(5);
        d["restoreCustomPerformance"] = custom.clone();
    }));
    assert!(!safe(2, &|d| d["baseMode"] = json!(5)));
    // Schema 8 binds the restore to a power source.
    for source in [json!(null), json!(3)] {
        assert!(
            !safe(8, &|d| d["restorePowerSource"] = source.clone()),
            "{source}"
        );
    }
    assert!(safe(7, &|d| d["restorePowerSource"] = json!(null)));
    // No retry, each from the schema that added it.
    assert!(!safe(5, &|d| d["restoreRetryDisposition"] = json!(1)));
    assert!(safe(4, &|d| d["restoreRetryDisposition"] = json!(1)));
    assert!(!safe(6, &|d| d["restoreRetryTarget"] = json!({"mode": 0})));
    assert!(safe(5, &|d| d["restoreRetryTarget"] = json!({"mode": 0})));
    // A transition ID, when present, is a non-empty GUID.
    for id in [
        json!("00000000-0000-0000-0000-000000000000"),
        json!("transition"),
        json!(1),
    ] {
        assert!(!safe(7, &|d| d["transitionId"] = id.clone()), "{id}");
    }
    assert!(safe(7, &|d| {
        d["transitionId"] = json!("3f2504e0-4f89-41d3-9a0c-0305e82c3301");
    }));
    assert!(safe(6, &|d| d["transitionId"] = json!("transition")));
    // A recovery baseline only under RestorePending.
    assert!(!safe(4, &|d| d["recoveryBaseline"] = json!({})));
    assert!(safe(3, &|d| d["recoveryBaseline"] = json!({})));
    // A written timestamp.
    assert!(!safe(8, &|d| {
        d["updatedAtUtc"] = json!("0001-01-01T00:00:00+00:00");
    }));
    assert!(!safe(8, &|d| {
        d.as_object_mut().unwrap().remove("updatedAtUtc");
    }));
}
