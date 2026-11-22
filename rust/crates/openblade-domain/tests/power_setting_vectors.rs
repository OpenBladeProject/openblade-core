//! The managed vectors for the native power-setting notices.
//!
//! `PowerSettingNativeVectorsTests` records each row from managed
//! `NativeWindowsLifecycleNotificationBackend` and verifies the same file.
//! Here the display rows drive `classify_console_display_transition`, the
//! decode rows drive `decode` over exactly the recorded bytes, and each
//! mapping case drives `power_event_signal` from an unknown display state,
//! decoding a step's bytes only for a power-setting change, as the SCM
//! handler does.
use openblade_domain::{
    low_power_gate::Signal,
    power_setting::{
        ConsoleDisplay, DisplayTransition, PBT_POWERSETTINGCHANGE, PowerSetting,
        classify_console_display_transition, decode, power_event_signal,
    },
};
use serde_json::{Map, Value};

const VECTORS: &str =
    include_str!("../../../../tools/openblade-windows-host/tests/data/power-setting-vectors.json");

fn root() -> Map<String, Value> {
    let root: Value = serde_json::from_str(VECTORS).unwrap();
    let root = root.as_object().unwrap().clone();
    let mut keys: Vec<_> = root.keys().map(String::as_str).collect();
    keys.sort_unstable();
    assert_eq!(keys, ["comment", "decode", "display", "mapping"]);
    root
}

fn rows<'a>(root: &'a Map<String, Value>, section: &str) -> &'a Vec<Value> {
    let rows = root[section].as_array().unwrap();
    assert!(!rows.is_empty(), "{section} has no rows");
    rows
}

fn dword(value: &Value) -> Option<u32> {
    (!value.is_null()).then(|| u32::try_from(value.as_u64().unwrap()).unwrap())
}

fn bytes(value: &Value) -> Vec<u8> {
    let hex = value.as_str().unwrap();
    assert_eq!(hex.len() % 2, 0, "{hex}");
    (0..hex.len())
        .step_by(2)
        .map(|at| u8::from_str_radix(&hex[at..at + 2], 16).unwrap())
        .collect()
}

#[test]
fn the_display_classifier_matches_managed() {
    let root = root();
    for row in rows(&root, "display") {
        let expected = match row["expect"].as_str() {
            None => None,
            Some("on") => Some(DisplayTransition::On),
            Some("off") => Some(DisplayTransition::Off),
            Some(other) => panic!("unexpected transition {other}"),
        };
        let current = dword(&row["current"]).unwrap();
        assert_eq!(
            classify_console_display_transition(dword(&row["previous"]), current),
            expected,
            "{row}"
        );
    }
}

/// The decoded value for each GUID, as managed reads it per registration.
fn by_guid(setting: Option<PowerSetting>) -> (Option<u32>, Option<u32>) {
    match setting {
        Some(PowerSetting::PowerSource(value)) => (Some(value), None),
        Some(PowerSetting::ConsoleDisplay(value)) => (None, Some(value)),
        None => (None, None),
    }
}

#[test]
fn the_power_setting_decode_matches_managed() {
    let root = root();
    let mut refused = 0;
    for row in rows(&root, "decode") {
        let expect = &row["expect"];
        let expected = (
            dword(&expect["powerSource"]),
            dword(&expect["consoleDisplay"]),
        );
        let decoded = decode(&bytes(&row["bytes"]));
        refused += usize::from(decoded.is_none());
        assert_eq!(by_guid(decoded), expected, "{}", row["name"]);
    }
    assert!(refused >= 10, "the malformed rows are recorded");
}

#[test]
fn the_notice_to_signal_mapping_matches_managed() {
    let root = root();
    for case in rows(&root, "mapping") {
        let mut display = ConsoleDisplay::unknown();
        let steps = case["steps"].as_array().unwrap();
        let expected = case["expect"].as_array().unwrap();
        assert_eq!(steps.len(), expected.len(), "{}", case["name"]);
        for (step, expected) in steps.iter().zip(expected) {
            let event = dword(&step["event"]).unwrap();
            let setting = step
                .get("bytes")
                .filter(|_| event == PBT_POWERSETTINGCHANGE)
                .and_then(|value| decode(&bytes(value)));
            let expected = match expected.as_str() {
                None => None,
                Some("suspend") => Some(Signal::Suspend),
                Some("resume") => Some(Signal::Resume),
                Some("displayOn") => Some(Signal::DisplayOn),
                Some(other) => panic!("unexpected signal {other}"),
            };
            assert_eq!(
                power_event_signal(&mut display, event, setting),
                expected,
                "{}: {step}",
                case["name"]
            );
        }
    }
}
