use super::*;
use serde_json::{Value, json};

#[test]
fn platform_identity_cannot_resolve_hid_to_exact_model() {
    let expected: Value =
        serde_json::from_str(include_str!("../../fixtures/macos-unverified.json")).unwrap();
    for model in [b"MacBookPro16,1\0".as_slice(), b"Razer Blade 15\0"] {
        let hid = Hid::from_entries([Entry::Irrelevant, Entry::Candidate(0x0255)]);
        let snapshot = Snapshot::new(classify_macos_model(model), hid);
        assert_eq!(serde_json::to_value(snapshot).unwrap(), expected);
    }
}

#[test]
fn missing_malformed_and_overlong_model_data_remain_unavailable() {
    let overlong = [b'X'; MAX_HOST_TEXT + 2];
    for model in [
        b"\0".as_slice(),
        b"MacBookPro16,1",
        b"Mac\0Book\0",
        b"\xff\0",
        &overlong,
    ] {
        let snapshot = Snapshot::new(classify_macos_model(model), Hid::unavailable());
        let value = serde_json::to_value(snapshot).unwrap();
        assert_eq!(value["hostMetadata"], "unavailable");
        assert!(value["exactModel"].is_null());
        assert_eq!(
            value["hid"],
            json!({"state": "unavailable", "candidates": []})
        );
    }
}

#[test]
fn scan_distinguishes_absence_repeated_ids_and_incomplete_enumeration() {
    let empty = Hid::from_entries([Entry::Irrelevant]);
    assert_eq!(
        serde_json::to_value(empty).unwrap(),
        json!({"state":"none","candidates":[]})
    );

    let repeated = Hid::from_entries([
        Entry::Candidate(0x02e0),
        Entry::Candidate(0x0255),
        Entry::Candidate(0x0255),
    ]);
    assert_eq!(
        serde_json::to_value(repeated).unwrap(),
        json!({
            "state": "ambiguous",
            "candidates": [
                {"vendorId": RAZER_VID, "productId": 0x0255, "entries": 2},
                {"vendorId": RAZER_VID, "productId": 0x02e0, "entries": 1},
            ]
        })
    );

    let partial = Hid::from_entries([Entry::Unreadable, Entry::Candidate(0x0255)]);
    assert_eq!(
        serde_json::to_value(partial).unwrap(),
        json!({
            "state": "incomplete",
            "candidates": [{"vendorId": RAZER_VID, "productId": 0x0255, "entries": 1}]
        })
    );
}

#[test]
fn scan_is_bounded_without_reporting_absence_after_overflow() {
    let mut consumed = 0;
    let entries = std::iter::from_fn(|| {
        consumed += 1;
        Some(Entry::Candidate(0x0255))
    });
    let capped = serde_json::to_value(Hid::from_entries(entries)).unwrap();
    assert_eq!(consumed, MAX_ENTRIES + 1);
    assert_eq!(capped["state"], "incomplete");
    assert_eq!(capped["candidates"][0]["entries"], MAX_ENTRIES);

    let exact = Hid::from_entries(std::iter::repeat_with(|| Entry::Irrelevant).take(MAX_ENTRIES));
    assert_eq!(serde_json::to_value(exact).unwrap()["state"], "none");
}
