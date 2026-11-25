use super::*;
use serde_json::json;

fn journal(document: Value) -> Vec<u8> {
    serde_json::to_vec(&document).unwrap()
}

const JOURNAL_VECTORS: &str =
    include_str!("../../../../tests/data/hyperboost-journal-vectors.json");

/// Recorded by `NativeHyperBoostJournalVectorsTests`: each document and
/// whether managed's store reads it as a safe Relinquished journal.
fn journal_vectors() -> Vec<(String, String, bool)> {
    let vectors: Value = serde_json::from_str(JOURNAL_VECTORS).unwrap();
    vectors["rows"]
        .as_array()
        .unwrap()
        .iter()
        .map(|row| {
            (
                row["name"].as_str().unwrap().to_owned(),
                row["document"].as_str().unwrap().to_owned(),
                row["managedSafe"].as_bool().unwrap(),
            )
        })
        .collect()
}

/// The Relinquished Balanced journal managed's store writes, at `schema`.
fn relinquished(schema: i64) -> Value {
    let written = journal_vectors()
        .into_iter()
        .find(|(name, ..)| name == "written/balanced")
        .unwrap();
    let mut document: Value = serde_json::from_str(&written.1).unwrap();
    assert_eq!(document["state"], RELINQUISHED);
    document["schemaVersion"] = json!(schema);
    document
}

/// Every recorded row replays through the native reader. Native never calls
/// a journal safe that managed does not, and it agrees with managed except
/// where it fails closed on a document managed reads differently from how it
/// is written: a duplicate or differently cased member (managed takes the
/// last match in any case), an unknown member (ignored by managed), a number
/// in a string at the top level (a settings record reads one, as managed
/// does), a timestamp without its full time, offset or with more than
/// seven fraction digits, and a Turbo base.
#[test]
fn managed_journal_vectors_replay_through_the_native_reader() {
    const STRICTER: [&str; 11] = [
        "written/turbo-battery",
        "duplicate/case-variant-later-relinquished",
        "duplicate/exact",
        "duplicate/nested-case-variant",
        "case/pascal-member",
        "unknown/member",
        "unknown/nested-member",
        "type/schema-string",
        "date/date-only",
        "date/no-offset",
        "date/eight-fraction-digits",
    ];
    let rows = journal_vectors();
    assert_eq!(rows.len(), 36);
    for (name, document, managed) in rows {
        let native = journal_safe(Some(document.as_bytes()));
        assert!(
            !native || managed,
            "{name}: native admits what managed refuses"
        );
        let expected = managed && !STRICTER.contains(&name.as_str());
        assert_eq!(native, expected, "{name}");
    }
    // Managed takes the last of two members that differ only in case: a
    // pending state written after the Relinquished one is pending.
    let (_, pending, managed) = journal_vectors()
        .into_iter()
        .find(|(name, ..)| name == "duplicate/case-variant-later-pending")
        .unwrap();
    assert!(!managed);
    assert!(!journal_safe(Some(pending.as_bytes())));
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
    for mode in [3, 4, 5, 6, 7] {
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
    assert!(!safe(6, &|d| d["restoreRetryTarget"] = json!(1)));
    assert!(safe(5, &|d| d["restoreRetryTarget"] = json!(1)));
    // Managed binds every member before it ignores one a schema predates,
    // so a member of the wrong type blocks in every schema.
    assert!(!safe(5, &|d| d["restoreRetryTarget"] = json!({"mode": 0})));
    assert!(!safe(4, &|d| d["restoreRetryDisposition"] = json!("0")));
    assert!(!safe(8, &|d| d["requiresCoolingPad"] = json!(0)));
    assert!(!safe(8, &|d| d["restoreUsesCurrentPowerDestination"] =
        json!(null)));
    assert!(!safe(8, &|d| d["restoreGpuClockTuning"] =
        json!({"enabled": true, "offsets": null})));
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
    assert!(!safe(6, &|d| d["transitionId"] = json!("transition")));
    assert!(safe(6, &|d| d["transitionId"] =
        json!("00000000-0000-0000-0000-000000000000")));
    // A recovery baseline only under RestorePending; native refuses one in
    // every schema.
    assert!(!safe(4, &|d| d["recoveryBaseline"] = json!({})));
    assert!(!safe(3, &|d| d["recoveryBaseline"] = json!({})));
    // A written timestamp.
    assert!(!safe(8, &|d| {
        d["updatedAtUtc"] = json!("0001-01-01T00:00:00+00:00");
    }));
    assert!(!safe(8, &|d| {
        d.as_object_mut().unwrap().remove("updatedAtUtc");
    }));
}
