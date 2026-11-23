//! What the journal-fixture histories quarantine at startup.
use super::*;
use serde_json::Value;
use std::cell::RefCell;

fn fixture(name: &str) -> Value {
    serde_json::from_str(match name {
        "fan" => include_str!("../../../../tests/data/fan-history-v3.json"),
        "battery" => include_str!("../../../../tests/data/battery-history-v1.json"),
        "tfc" => include_str!("../../../../tests/data/temporary-charge-history-v1.json"),
        _ => unreachable!(),
    })
    .unwrap()
}

/// The journal `key` of fixture `name`, or the `tfc` case it names.
fn journal(name: &str, key: &str) -> Vec<u8> {
    let v = fixture(name);
    let raw = if name == "tfc" {
        v["cases"]
            .as_array()
            .unwrap()
            .iter()
            .find(|case| case["name"] == key)
            .unwrap()["journal"]
            .clone()
    } else {
        v[key].clone()
    };
    raw.as_str().unwrap().as_bytes().to_vec()
}

/// The first `rows` rows of `journal`.
fn prefix(journal: &[u8], rows: usize) -> Vec<u8> {
    journal
        .split_inclusive(|b| *b == b'\n')
        .take(rows)
        .flatten()
        .copied()
        .collect()
}

fn range(ids: std::ops::RangeInclusive<u8>) -> Vec<u8> {
    ids.collect()
}

#[test]
fn an_unsettled_final_request_quarantines_what_it_may_have_sent() {
    let owned = journal("fan", "owned");
    // A setter chain stopped at 11: its setter and readback IDs, not its
    // preflight (1 to 4) or the cleanup it has not begun (17 to 26).
    assert_eq!(unanswered(&prefix(&owned, 3)).unwrap(), range(5..=16));
    // The owner's cleanup stopped at 41: only the cleanup IDs.
    let released = journal("fan", "released");
    assert_eq!(unanswered(&prefix(&released, 13)).unwrap(), range(17..=26));
    // A battery write and a temporary-charge enable stopped after their intent.
    let battery = journal("battery", "standalone");
    assert_eq!(unanswered(&prefix(&battery, 2)).unwrap(), range(100..=113));
    let enable = journal("tfc", "enable");
    assert_eq!(unanswered(&prefix(&enable, 2)).unwrap(), range(100..=107));
    // Prepared alone reached no submission intent, but the IDs are named.
    assert_eq!(unanswered(&prefix(&battery, 1)).unwrap(), range(100..=113));
}

#[test]
fn a_confirmed_history_quarantines_nothing() {
    for (name, key) in [
        ("fan", "released"),
        ("fan", "observedAutomatic"),
        ("battery", "standalone"),
        ("battery", "released"),
        ("tfc", "cancel-off-unknown-policy"),
        ("tfc", "noop-false-unknown-policy"),
        ("tfc", "source-cancel-origin-reserved"),
    ] {
        assert!(
            unanswered(&journal(name, key)).unwrap().is_empty(),
            "{name} {key}"
        );
    }
    assert!(unanswered(b"").unwrap().is_empty());
}

#[test]
fn live_owners_quarantine_their_reservations() {
    // A manual fan owner's Stop cleanup can send its IDs and fail before
    // its first cleanup row, and a temporary-charge source cancel can send
    // the reserved IDs before its Prepared row.
    assert_eq!(
        unanswered(&journal("fan", "owned")).unwrap(),
        range(17..=26)
    );
    assert_eq!(
        unanswered(&journal("battery", "interleaved")).unwrap(),
        range(17..=26)
    );
    assert_eq!(
        unanswered(&journal("tfc", "enable")).unwrap(),
        range(108..=115)
    );
}

#[test]
fn known_unsent_and_superseded_sets_are_quarantined_newest_first() {
    // The reserved cancel stopped at 6 after its getter, and the fresh-ID
    // retry names the same IDs as superseded.
    assert_eq!(
        unanswered(&journal(
            "tfc",
            "source-cancel-fresh-ids-after-known-unsent"
        ))
        .unwrap(),
        range(108..=115)
    );
    assert_eq!(
        unanswered(&journal("tfc", "source-cancel-fresh-ids-first")).unwrap(),
        range(108..=115)
    );
    // A fresh-ID retry that stopped at 6, under an earlier enable.
    let mut expected = range(120..=127);
    expected.extend(108..=115);
    assert_eq!(
        unanswered(&journal("tfc", "fresh-ids-bound-resets-at-next-enable")).unwrap(),
        expected
    );
}

/// A battery request `n` over `ids` whose rows reach each of `progress`.
fn battery_rows(n: u32, ids: [u8; 14], progress: &[u8]) -> Vec<u8> {
    let raw = journal("battery", "standalone");
    let first = raw.split(|b| *b == b'\n').next().unwrap();
    let mut entry: Entry = serde_json::from_slice(first).unwrap();
    entry.request = format!("aaaaaaaa-bbbb-cccc-dddd-{n:012}");
    let battery = entry.battery.as_mut().unwrap();
    battery.ids = ids;
    entry.intent = battery.hash().unwrap();
    let mut bytes = Vec::new();
    for p in progress {
        entry.progress = *p;
        bytes.extend(serde_json::to_vec(&entry).unwrap());
        bytes.push(b'\n');
    }
    bytes
}

fn ids_from(first: u8) -> [u8; 14] {
    std::array::from_fn(|i| first + i as u8)
}

#[test]
fn the_seed_is_bounded_and_keeps_the_live_set_whole() {
    // Ten known-not-submitted battery requests name 140 IDs.
    let mut history = Vec::new();
    for k in 0..10u8 {
        history.extend(battery_rows(
            u32::from(k) + 1,
            ids_from(1 + 14 * k),
            &[0, 6],
        ));
    }
    crate::thermal_history::rows(&history, |_, _| Ok(())).unwrap();
    let seeded = unanswered(&history).unwrap();
    assert_eq!(seeded.len(), LIMIT);
    // The newest come first: requests 10, 9 and 8 whole, then part of 7.
    let expected: Vec<u8> = [9u8, 8, 7, 6]
        .iter()
        .flat_map(|k| ids_from(1 + 14 * k))
        .take(LIMIT)
        .collect();
    assert_eq!(seeded, expected);
    // An unsettled final request is kept whole ahead of the history.
    let mut unsettled = history.clone();
    unsettled.extend(battery_rows(99, ids_from(200), &[0, 60]));
    let seeded = unanswered(&unsettled).unwrap();
    assert_eq!(seeded.len(), LIMIT);
    assert_eq!(seeded[..14], ids_from(200));
    assert_eq!(seeded[14..28], ids_from(1 + 14 * 9));
}

#[test]
fn an_unreadable_absent_or_invalid_journal_quarantines_nothing() {
    let calls = RefCell::new(Vec::new());
    let record = |ids: &[u8]| calls.borrow_mut().push(ids.to_vec());
    quarantine_unanswered(Err(io::ErrorKind::PermissionDenied.into()), &record);
    quarantine_unanswered(Ok(None), &record);
    quarantine_unanswered(Ok(Some(b"not a journal\n".to_vec())), &record);
    // A row that no Prepared row precedes is invalid history.
    let battery = journal("battery", "standalone");
    let tail: Vec<u8> = battery
        .split_inclusive(|b| *b == b'\n')
        .skip(1)
        .flatten()
        .copied()
        .collect();
    quarantine_unanswered(Ok(Some(tail)), &record);
    assert!(calls.borrow().is_empty());
    quarantine_unanswered(Ok(Some(prefix(&battery, 2))), &record);
    assert_eq!(*calls.borrow(), [range(100..=113)]);
}
