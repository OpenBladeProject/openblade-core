//! Replays the managed vectors (`NativeDiagnosticExportVectorTests`): the
//! `DiagnosticText` redaction, `DiagnosticHistory` retention and truncation,
//! and the exporter's entry text, plus the contract bounds and the archive's
//! failure paths.

use std::{collections::VecDeque, sync::Mutex};

use serde_json::{Value, json};

use super::{
    archive::{Bundle, Request, build_bounded, entries, file_name},
    history::{CommandResult, History, Limits, Snapshot},
    json::Json,
    redact::redact_text,
    zip::{OVERSIZED, deflate, deflate_checked, read},
    *,
};

const VECTORS: &str = include_str!("../../tests/data/diagnostic-export-vectors.json");
const CONTRACT: &str = include_str!("../../tests/data/diagnostic-export-contract.json");

fn vectors() -> Value {
    serde_json::from_str(VECTORS).unwrap()
}

fn strings(value: &Value) -> Vec<String> {
    value
        .as_array()
        .unwrap()
        .iter()
        .map(|item| item.as_str().unwrap().to_owned())
        .collect()
}

pub(crate) fn sequence(times: Vec<Timestamp>) -> Box<dyn Fn() -> Timestamp + Send + Sync> {
    let times = Mutex::new(VecDeque::from(times));
    Box::new(move || times.lock().unwrap().pop_front().expect("a recorded time"))
}

fn at(text: &str) -> Timestamp {
    Timestamp::parse(text).unwrap_or_else(|| panic!("{text}"))
}

fn bundle(versions: Json, history: Snapshot, tokens: Vec<String>) -> Bundle {
    Bundle {
        versions,
        device: Json::object([
            ("model", Json::str("Unavailable")),
            ("vendorId", Json::str("Unavailable")),
            ("productId", Json::str("Unavailable")),
            ("serialNumber", Json::Null),
        ]),
        capabilities: Vec::new(),
        configuration: Vec::new(),
        history,
        tokens,
    }
}

fn versions(input: &Value) -> Json {
    Json::Object(
        [
            "openBladeVersion",
            "windowsVersion",
            "biosVersion",
            "ecVersion",
            "mcuVersion",
            "vbiosVersion",
            "vbiosGpuName",
            "thunderboltFirmwareVersion",
            "touchpadFirmwareVersion",
            "thunderboltFirmwareAccess",
            "touchpadFirmwareAccess",
        ]
        .iter()
        .map(|name| {
            (
                (*name).to_owned(),
                Json::nullable(input[*name].as_str().map(str::to_owned)),
            )
        })
        .collect(),
    )
}

fn texts(entries: Vec<(&'static str, String)>) -> Value {
    Value::Array(
        entries
            .into_iter()
            .map(|(name, text)| json!([name, text]))
            .collect(),
    )
}

#[test]
fn redaction_matches_the_managed_vectors() {
    for row in vectors()["redact"].as_array().unwrap() {
        let observed = redact_text(row["input"].as_str().unwrap(), &strings(&row["tokens"]));
        assert_eq!(observed, row["expect"].as_str().unwrap(), "{}", row["name"]);
    }
}

#[test]
fn history_retention_and_truncation_match_the_managed_vectors() {
    for row in vectors()["history"].as_array().unwrap() {
        let limits = &row["limits"];
        let records = row["records"].as_array().unwrap();
        let history = History::new(
            Limits {
                entries: limits["entries"].as_u64().unwrap() as usize,
                characters: limits["characters"].as_u64().unwrap() as usize,
                line: limits["line"].as_u64().unwrap() as usize,
            },
            Vec::new(),
            sequence(
                records
                    .iter()
                    .map(|record| at(record["at"].as_str().unwrap()))
                    .collect(),
            ),
        );
        for record in records {
            history.record_summary(
                record["status"].as_str().unwrap(),
                record["summary"].as_str().unwrap(),
            );
        }
        let versions = Json::object([
            ("openBladeVersion", Json::str("1.0.0")),
            ("windowsVersion", Json::str("Windows")),
            ("biosVersion", Json::str("Unavailable")),
        ]);
        let entries = entries(
            &bundle(versions, history.snapshot().unwrap(), Vec::new()),
            &Cancel::default(),
        )
        .unwrap();
        let text = |name: &str| {
            entries
                .iter()
                .find(|(entry, _)| *entry == name)
                .unwrap()
                .1
                .clone()
        };
        assert_eq!(
            json!({
                "commandResults": text("command-results.json"),
                "serviceHistory": text("service-history.json"),
            }),
            row["expect"],
            "{}",
            row["name"]
        );
    }
}

fn read_bundle(input: &Value) -> Bundle {
    let results: Vec<CommandResult> = input["commandResults"]
        .as_array()
        .unwrap()
        .iter()
        .map(|result| CommandResult {
            timestamp: at(result["timestamp"].as_str().unwrap()),
            subsystem: result["subsystem"].as_str().unwrap().into(),
            action: result["action"].as_str().unwrap().into(),
            status: result["status"].as_str().unwrap().into(),
            detail: result["detail"].as_str().map(str::to_owned),
        })
        .collect();
    let log_lines = strings(&input["logLines"]);
    let device = &input["device"];
    let sorted = |value: &Value| -> Vec<(String, Value)> {
        let mut members: Vec<(String, Value)> = value
            .as_object()
            .unwrap()
            .iter()
            .map(|(key, value)| (key.clone(), value.clone()))
            .collect();
        members.sort_by(|left, right| left.0.cmp(&right.0));
        members
    };
    Bundle {
        versions: versions(&input["versions"]),
        device: Json::object([
            ("model", Json::str(device["model"].as_str().unwrap())),
            ("vendorId", Json::str(device["vendorId"].as_str().unwrap())),
            (
                "productId",
                Json::str(device["productId"].as_str().unwrap()),
            ),
            ("serialNumber", Json::Null),
        ]),
        capabilities: sorted(&input["capabilities"])
            .into_iter()
            .map(|(key, value)| (key, value.as_bool().unwrap()))
            .collect(),
        configuration: sorted(&input["configuration"])
            .into_iter()
            .map(|(key, value)| (key, value.as_str().unwrap().to_owned()))
            .collect(),
        history: Snapshot {
            retained_entries: results.len() + log_lines.len(),
            results,
            log_lines,
            evicted: input["evicted"].as_u64().unwrap(),
            dropped: input["dropped"].as_u64().unwrap(),
            truncated: input["truncated"].as_u64().unwrap(),
            retained_characters: input["characters"].as_u64().unwrap() as usize,
        },
        tokens: strings(&input["tokens"]),
    }
}

#[test]
fn entry_text_and_redaction_match_the_managed_exporter() {
    for row in vectors()["bundles"].as_array().unwrap() {
        let observed = entries(&read_bundle(&row["input"]), &Cancel::default()).unwrap();
        assert_eq!(texts(observed), row["expect"], "{}", row["name"]);
    }
}

#[test]
fn the_contract_bounds_are_the_native_bounds() {
    let contract: Value = serde_json::from_str(CONTRACT).unwrap();
    assert_eq!(
        contract["history"],
        json!({
            "maximumEntries": Limits::DEFAULT.entries,
            "maximumTotalCharacters": Limits::DEFAULT.characters,
            "maximumLineCharacters": Limits::DEFAULT.line,
        })
    );
    assert_eq!(
        contract["maximumArchiveBytes"],
        json!(MAXIMUM_ARCHIVE_BYTES)
    );
    assert_eq!(
        contract["maximumResponseBytes"],
        json!(MAXIMUM_RESPONSE_BYTES)
    );
    // Every enum the sections name is recorded.
    for name in contract["enums"].as_object().unwrap().keys() {
        assert!(!sections::enum_name(name, 0).is_empty());
    }
    assert_eq!(sections::enum_name("SpecialKeySlotSet", 0), "None");
    assert_eq!(sections::enum_name("SpecialKeySlotSet", 5), "M1, M3");
    assert_eq!(sections::enum_name("SpecialKeySlotSet", 31), "All");
    assert_eq!(sections::enum_name("SpecialKeySlotSet", 64), "64");
    assert_eq!(sections::enum_name("PerformancePreset", 42), "42");
}

#[test]
fn models_are_found_by_prefix_ignoring_case() {
    assert_eq!(
        sections::find_model(Some("  rz09-0581xyz "))
            .unwrap()
            .model_number,
        "RZ09-0581"
    );
    assert!(sections::find_model(Some("RZ09-05")).is_none());
    assert!(sections::find_model(Some("   ")).is_none());
    assert!(sections::find_model(None).is_none());
}

#[test]
fn timestamps_and_file_names_use_the_managed_text() {
    let instant = at("2026-11-23T10:00:01.1200000+00:00");
    assert_eq!(json::timestamp(instant), "2026-11-23T10:00:01.12+00:00");
    assert_eq!(
        json::timestamp(at("2026-02-28T23:59:59+00:00")),
        "2026-02-28T23:59:59+00:00"
    );
    assert_eq!(
        file_name(instant),
        "OpenBlade-diagnostics-20261123-100001Z.zip"
    );
    assert_eq!(at("2024-02-29T00:00:00+00:00").compact(), "20240229-000000");
    let (time, date) = at("2026-11-23T10:00:03+00:00").dos();
    assert_eq!(
        (time, date),
        ((10 << 11) | 1, ((2026 - 1980) << 9 | 11 << 5 | 23) as u16)
    );
}

#[test]
fn base64_matches_rfc_4648() {
    for (bytes, text) in [
        (&b""[..], ""),
        (b"f", "Zg=="),
        (b"fo", "Zm8="),
        (b"foo", "Zm9v"),
        (b"foobar", "Zm9vYmFy"),
        (&[0xfb, 0xff], "+/8="),
    ] {
        assert_eq!(base64(bytes), text);
    }
}

#[test]
fn deflate_round_trips_and_compresses_repetition() {
    let mut inputs: Vec<Vec<u8>> = vec![
        Vec::new(),
        b"a".to_vec(),
        b"abcabcabcabcabcabc".to_vec(),
        (0..=255u8).collect(),
    ];
    let mut noisy = Vec::new();
    let mut state = 0x1234_5678u32;
    for _ in 0..200_000 {
        state = state.wrapping_mul(1_103_515_245).wrapping_add(12_345);
        noisy.push(b"{}\":, abcdefghij\r\n"[(state >> 16) as usize % 18]);
    }
    inputs.push(noisy);
    inputs.push(vec![b'x'; 100_000]);
    for input in inputs {
        let deflated = deflate(&input, &Cancel::default()).unwrap();
        assert_eq!(read::inflate(&deflated), input);
    }
    assert!(deflate(&[b'x'; 100_000], &Cancel::default()).unwrap().len() < 1_000);
}

/// A long run of one byte matches 258 bytes at a time, so its positions step
/// over every multiple of the check interval. The cancellation must still be
/// observed within one interval of being raised.
#[test]
fn deflate_observes_a_cancel_raised_mid_entry_in_a_long_run() {
    const RUN: usize = 4 * 1024 * 1024;
    let input = vec![b'x'; RUN];
    let mut checks = 0usize;
    let canceled = deflate_checked(&input, || {
        checks += 1;
        if checks == 3 {
            Err(Failure::new("canceled"))
        } else {
            Ok(())
        }
    });
    assert_eq!(canceled.unwrap_err(), Failure::new("canceled"));
    assert_eq!(checks, 3);

    // Uncanceled, the checks are evenly spread over the whole entry.
    let mut checks = 0usize;
    deflate_checked(&input, || {
        checks += 1;
        Ok(())
    })
    .unwrap();
    assert!(checks >= RUN / (65_536 + 258), "{checks}");
}

fn metadata(tokens: Vec<String>) -> HostMetadata {
    HostMetadata {
        windows_version: "Microsoft Windows NT 10.0.26300.0".into(),
        tokens,
        captured_at: at("2026-11-23T10:00:00+00:00"),
        local_time: at("2026-11-23T11:00:00+00:00"),
    }
}

fn native_status() -> Value {
    json!({
        "revision": 4, "deviceVerified": false, "writesBlocked": true,
        "potentialControllers": ["Synapse"], "controllerDetectionWarning": null,
        "externalChanges": [0, 3], "message": "Device unavailable.", "capabilities": null,
        "confirmedState": null,
        "profiles": {
            "pluggedIn": {"performance": 1, "fans": 0, "lighting": {"effect": 0}},
            "battery": {"performance": 3, "fans": 0, "lighting": {"effect": 0}},
            "usbC": {"performance": 1, "fans": 0, "lighting": {"effect": 0}},
            "batteryPolicy": {"chargeLimitPercent": 80},
        },
        "desiredState": {
            "performance": {"value": 1, "source": 1}, "fans": {"value": 0, "source": 1},
            "lighting": {"value": {"effect": 0}, "source": 1},
            "battery": {"protectionEnabled": true},
        },
        "activePowerSource": 3, "liveReadings": null, "performanceOverride": null,
        "fanOverride": null, "lightingOverride": null, "fullChargeOverride": false,
        "controlAccess": 0, "serviceVersion": "0.0.0",
    })
}

fn request(status: Value, history: Snapshot) -> Request {
    Request {
        status,
        history,
        metadata: metadata(Vec::new()),
        live_readings_enabled: false,
    }
}

#[test]
fn an_archive_reads_back_with_the_managed_entry_order() {
    let archive = build(
        request(native_status(), Snapshot::default()),
        &Cancel::default(),
    )
    .unwrap();
    assert_eq!(
        archive.file_name,
        "OpenBlade-diagnostics-20261123-100000Z.zip"
    );
    assert_eq!(archive.bytes[..4], [0x50, 0x4b, 0x03, 0x04]);
    let names: Vec<String> = read::entries(&archive.bytes)
        .into_iter()
        .map(|(name, _)| name)
        .collect();
    assert_eq!(
        names,
        [
            "versions.json",
            "device.json",
            "device-verification.json",
            "capabilities.json",
            "configuration.json",
            "command-results.json",
            "service-history.json",
            "firmware-queries.json",
            "hyperboost-ownership.json",
            "logs.jsonl",
        ]
    );
}

#[test]
fn an_archive_past_the_bound_is_refused_with_the_managed_message() {
    // A xorshift stream: text deflate cannot shrink below about 4.7 bits
    // a letter, so the bound is reached by content.
    let mut state = 0x9E37_79B9_7F4A_7C15u64;
    let mut letter = move || {
        state ^= state << 13;
        state ^= state >> 7;
        state ^= state << 17;
        char::from(b'a' + (state % 26) as u8)
    };
    let results = (0..64)
        .map(|_| CommandResult {
            timestamp: at("2026-11-23T10:00:00+00:00"),
            subsystem: "Service".into(),
            action: "Reconcile".into(),
            status: "Stored".into(),
            detail: Some((0..512).map(|_| letter()).collect()),
        })
        .collect();
    let history = Snapshot {
        results,
        ..Snapshot::default()
    };
    let small = build_bounded(
        request(native_status(), history.clone()),
        &Cancel::default(),
        8_192,
    );
    assert_eq!(small.unwrap_err(), Failure::new(OVERSIZED));
    assert_eq!(
        Failure::new(OVERSIZED).reply(),
        "Diagnostic export failed: Diagnostic archive exceeds the 16 MiB limit."
    );
    build_bounded(
        request(native_status(), history),
        &Cancel::default(),
        1 << 20,
    )
    .unwrap();
}

#[test]
fn a_canceled_build_stops_without_an_archive() {
    let cancel = Cancel::default();
    cancel.cancel();
    let failure = build(request(native_status(), Snapshot::default()), &cancel).unwrap_err();
    assert_eq!(
        failure.reply(),
        "Diagnostic export failed: The diagnostic export was canceled."
    );
    let cancel = Cancel::default();
    drop(CancelOnDrop(cancel.clone()));
    assert!(cancel.check().is_err());
}

#[test]
fn a_poisoned_history_is_unreadable() {
    let history = History::new(
        Limits::DEFAULT,
        Vec::new(),
        Box::new(|| Timestamp::from_ticks(0)),
    );
    history.record_summary("Stored", "kept");
    history.poison();
    assert_eq!(
        history.snapshot().unwrap_err().reply(),
        "Diagnostic export failed: Diagnostic history is unavailable."
    );
    // Recording continues into a poisoned history.
    history.record_summary("Stored", "after");
}

#[test]
fn capability_members_without_a_native_summary_refuse_the_export() {
    for (member, value) in [
        ("cpuPowerLimit", json!({})),
        ("intelCpuPowerLimit", json!({})),
        ("cpuVoltageOptimizer", json!({})),
        ("observedPowerSourceBehaviors", json!([{}])),
    ] {
        let mut status = native_status();
        status["capabilities"] = json!({"performancePresets": [], member: value});
        let failure = build(request(status, Snapshot::default()), &Cancel::default()).unwrap_err();
        assert!(failure.0.contains(member), "{member}");
    }
    let mut status = native_status();
    status["capabilities"] = json!({
        "performancePresets": [1, 2], "fanSpeedRangesByPerformancePreset": {},
        "observedPowerSourceBehaviors": [], "customPerformance": null,
    });
    build(request(status, Snapshot::default()), &Cancel::default()).unwrap();
}

/// Managed's per-preset fan range summary: preset order, not map order, and
/// Unavailable for an empty map. Managed replays a non-empty map through the
/// identified capability record sample.
#[test]
fn per_preset_fan_ranges_are_summarized_in_preset_order() {
    let range = json!({"minimumRpm": 2000, "maximumRpm": 5400, "stepRpm": 100});
    let summary = |ranges: Value| {
        let mut status = native_status();
        status["capabilities"] = json!({"fanSpeedRangesByPerformancePreset": ranges});
        sections::configuration(&status, true).map(|configuration| {
            configuration["capability.fanSpeedRangesByPerformancePreset"].clone()
        })
    };
    assert_eq!(
        summary(json!({"Performance": range, "Balanced": range})).unwrap(),
        "Balanced:2000-5400/100, Performance:2000-5400/100"
    );
    assert_eq!(summary(json!({})).unwrap(), "Unavailable");
    assert!(summary(json!({"Unknown": range})).is_err());
    assert!(summary(json!([])).is_err());
}

/// An archive's entries as text.
pub(crate) fn archive_texts(bytes: &[u8]) -> Vec<(String, String)> {
    read::entries(bytes)
        .into_iter()
        .map(|(name, content)| (name, String::from_utf8(content).unwrap()))
        .collect()
}

#[test]
fn configuration_reads_absent_members_as_managed_defaults() {
    let configuration = sections::configuration(&native_status(), true).unwrap();
    let value = |key: &str| configuration[key].as_str();
    assert_eq!(value("status.potentialControllers"), "Synapse");
    assert_eq!(value("status.externalChanges"), "Performance, Lighting");
    assert_eq!(value("status.unconfirmedWrites"), "");
    assert_eq!(value("status.hyperBoostBlockingControllers"), "Unavailable");
    assert_eq!(value("laptop.keyboardDriverModeActive"), "Unavailable");
    assert_eq!(value("power.source"), "ExternalUnknown");
    assert_eq!(value("liveReadings.enabled"), "True");
    assert_eq!(value("profile.battery.performance"), "BatterySaver");
    assert_eq!(value("lighting.refreshRate"), "ThirtyHz");
    assert_eq!(value("support.status"), "NotEstablished");
    assert_eq!(value("confirmed.hyperBoostActive"), "Unavailable");
    assert_eq!(value("capability.supportsTemporaryFullCharge"), "False");
    assert_eq!(value("capability.hyperBoostAccess"), "Unavailable");
    let mut status = native_status();
    status["firmware"] = json!({"modelNumber": "RZ09-0581"});
    status["confirmedState"] =
        json!({"performance": 1, "fans": 0, "battery": null, "lighting": null});
    // The identified record of the read-only composition.
    status["capabilities"] = capability_row("read only");
    let configuration = sections::configuration(&status, false).unwrap();
    let value = |key: &str| configuration[key].as_str();
    assert_eq!(value("support.basis"), "PhysicallyValidated");
    assert_eq!(value("support.physicalValidation"), "Validated");
    assert_eq!(value("confirmed.hyperBoostActive"), "False");
    assert_eq!(value("confirmed.performance"), "Balanced");
    assert_eq!(value("capability.hyperBoostAccess"), "ReadOnly");
    assert_eq!(
        value("capability.hyperBoostWithoutCoolingPadAccess"),
        "ObservedOnly"
    );
    assert_eq!(value("capability.hyperBoostBase.Balanced"), "ReadOnly");
    assert_eq!(value("capability.hyperBoostBase.Custom"), "ReadOnly");
    assert_eq!(value("capability.hyperBoostBase.Turbo"), "Unavailable");
    assert_eq!(value("capability.thxAudioAccess"), "ReadWrite");
    assert_eq!(value("capability.writableSpecialKeySlots"), "None");
    let capabilities = sections::capabilities(&status).unwrap();
    assert!(capabilities["performance.hyperBoost.observed"]);
    assert!(!capabilities["performance.hyperBoost.write"]);
    assert!(capabilities["performance.hyperBoost.withoutCoolingPad.gate.crashRecovery"]);
    assert!(capabilities["lighting.breathingColorMode.Dual"]);
    // The read-only composition completes no temporary full charge.
    assert!(!capabilities["battery.temporaryFullCharge"]);
    assert!(capabilities["display.localDimming.observed"]);
    assert!(!capabilities["display.localDimming.clear"]);
    assert!(!capabilities["performance.customCombinations"]);
    assert!(!capabilities["performance.gpuOverclock.observed"]);
}

/// A managed capability-record row (`capability-record-vectors.json`).
fn capability_row(name: &str) -> Value {
    let vectors: Value = serde_json::from_str(include_str!(
        "../../tests/data/capability-record-vectors.json"
    ))
    .unwrap();
    vectors["rows"]
        .as_array()
        .unwrap()
        .iter()
        .find(|row| row["name"] == name)
        .unwrap()["record"]
        .clone()
}

/// Managed's summaries of the typed records the full composition carries:
/// a well-formed custom performance table without Maximum fans, observed
/// local dimming without Clear, a read-write GPU overclock with its enable
/// toggle, and read-write HyperBoost base modes.
#[test]
fn the_typed_records_of_the_full_composition_are_summarized() {
    let mut status = native_status();
    status["capabilities"] = capability_row("every group");
    let capabilities = sections::capabilities(&status).unwrap();
    for (key, expected) in [
        ("performance.customCombinations", true),
        ("performance.customMaximumFans", false),
        ("display.localDimming.observed", true),
        ("display.localDimming.clear", false),
        ("performance.gpuOverclock.observed", true),
        ("performance.gpuOverclock.write", true),
        ("performance.gpuOverclock.enableDisableObserved", true),
        ("performance.hyperBoost.write", true),
        ("performance.hyperBoost.withoutCoolingPad.write", true),
    ] {
        assert_eq!(capabilities[key], expected, "{key}");
    }
    let configuration = sections::configuration(&status, false).unwrap();
    assert_eq!(
        configuration["capability.hyperBoostBase.Balanced"],
        "ReadWrite"
    );
    assert_eq!(
        configuration["capability.hyperBoostBase.Custom"],
        "ReadWrite"
    );
    assert_eq!(
        configuration["capability.hyperBoostBase.Turbo"],
        "Unavailable"
    );
    // A malformed table or range is not well formed, as managed reads it.
    let mut malformed = status.clone();
    malformed["capabilities"]["customPerformance"]["allowedCombinations"] =
        json!([{"cpu": 0, "gpu": 3}]);
    malformed["capabilities"]["gpuOverclock"]["coreOffsetMegahertz"]["step"] = json!(3);
    let capabilities = sections::capabilities(&malformed).unwrap();
    assert!(!capabilities["performance.customCombinations"]);
    assert!(!capabilities["performance.gpuOverclock.observed"]);
    assert!(capabilities["performance.gpuOverclock.write"]);
}

#[test]
fn an_archive_exactly_at_the_bound_is_kept_and_one_byte_over_is_refused() {
    let request = || request(native_status(), Snapshot::default());
    let size = build_bounded(request(), &Cancel::default(), usize::MAX)
        .unwrap()
        .bytes
        .len();
    let at = build_bounded(request(), &Cancel::default(), size).unwrap();
    assert_eq!(at.bytes.len(), size);
    assert_eq!(
        build_bounded(request(), &Cancel::default(), size - 1).unwrap_err(),
        Failure::new(OVERSIZED)
    );
}

/// Managed `Add` drops an entry only when it alone exceeds the total and
/// evicts only while the new total would exceed it: an entry, or a pair of
/// entries, exactly at the bound is kept.
#[test]
fn history_entries_exactly_at_the_character_bound_are_kept() {
    // "Service", "Reconcile" and "Stored" are 22 characters; "abc" makes 25.
    for (characters, entries) in [(25, 1), (50, 2)] {
        let limits = Limits {
            characters,
            ..Limits::DEFAULT
        };
        let history = History::new(limits, Vec::new(), Box::new(|| Timestamp::from_ticks(0)));
        for _ in 0..entries {
            history.record_summary("Stored", "abc");
        }
        let snapshot = history.snapshot().unwrap();
        assert_eq!(snapshot.retained_entries, entries);
        assert_eq!(snapshot.retained_characters, characters);
        assert_eq!((snapshot.dropped, snapshot.evicted), (0, 0));
    }
}

/// `DiagnosticText.Redact` orders the distinct tokens longest first, so a
/// token inside a longer one never splits it.
#[test]
fn redaction_replaces_the_longer_of_two_nested_tokens() {
    let tokens = ["CONTOSO".to_owned(), "contoso-pc".to_owned()];
    assert_eq!(
        redact_text("on CONTOSO-PC now", &tokens),
        "on [REDACTED] now"
    );
}

/// The archive reads the status by member name and writes no status record
/// whole, so no lighting parameters, whose `kind` managed needs first, reach
/// its bytes; an entry that ever writes one must use `kind_first`.
#[test]
fn an_archive_writes_no_lighting_parameters_from_the_status() {
    let mut status = native_status();
    let lighting = json!({
        "effect": 1, "color": {"red": 200, "green": 10, "blue": 5}, "brightness": 10,
        "parameters": {"kind": "static", "color": {"red": 200, "green": 10, "blue": 5}},
        "logoMode": null,
    });
    status["lightingOverride"] = json!({"value": lighting, "lifetime": 1});
    status["desiredState"]["lighting"] = json!({"value": lighting, "source": 4});
    let archive = build(request(status, Snapshot::default()), &Cancel::default()).unwrap();
    for (name, text) in archive_texts(&archive.bytes) {
        assert!(!text.contains("parameters"), "{name}: {text}");
        assert!(!text.contains("\"kind\""), "{name}: {text}");
    }
}

/// Managed `AddLog`: each logical line of the redacted message, a carriage
/// return read as a break and empty lines skipped, under managed's prefix
/// (`GetUtcNow():O`, pid, run, version, level and category), cut so each
/// line fits the line bound with its prefix. A cut keeps whole characters,
/// where a managed UTF-16 substring could split a surrogate pair.
#[test]
fn a_log_line_carries_managed_prefix_redaction_and_line_cuts() {
    let prefix =
        "2026-11-26T10:00:00.1234567+00:00 pid=42 run=0123abcd version=1.2.3 Warning Cat: ";
    let history = History::new(
        Limits {
            line: prefix.len() + 5,
            ..Limits::DEFAULT
        },
        vec!["CONTOSO".to_owned()],
        Box::new(|| at("2026-11-26T10:00:00.1234567+00:00")),
    )
    .with_process(42, "0123abcd", "1.2.3");
    history.record_summary("Stored", "kept");
    history.record_log("Warning", "Cat", "abc\r\n\nabcd\u{1F600}e\rCONTOSO");
    let snapshot = history.snapshot().unwrap();
    let lines: Vec<String> = ["abc", "abcd", "\u{1F600}e", "[REDA", "CTED]"]
        .iter()
        .map(|content| format!("{prefix}{content}"))
        .collect();
    assert_eq!(snapshot.log_lines, lines);
    assert_eq!(snapshot.results.len(), 1);
    assert_eq!(snapshot.retained_entries, 6);
    assert_eq!(
        snapshot.retained_characters,
        "ServiceReconcileStoredkept".len()
            + lines
                .iter()
                .map(|line| line.encode_utf16().count())
                .sum::<usize>()
    );
    assert_eq!(
        (snapshot.dropped, snapshot.evicted, snapshot.truncated),
        (0, 0, 0)
    );
}

/// A prefix past the line bound leaves one character of content a line, and
/// managed `Clamp` cuts each line to the bound and counts it truncated.
#[test]
fn a_log_line_longer_than_the_bound_is_clamped_and_counted() {
    let history = History::new(
        Limits {
            line: 20,
            ..Limits::DEFAULT
        },
        Vec::new(),
        Box::new(|| Timestamp::from_ticks(0)),
    )
    .with_process(1, "run", "1");
    history.record_log("Warning", "Category", "ab");
    let snapshot = history.snapshot().unwrap();
    assert_eq!(snapshot.log_lines.len(), 2);
    for line in &snapshot.log_lines {
        assert_eq!(line.len(), 20, "{line}");
        assert!(line.ends_with("...[truncated]"), "{line}");
    }
    assert_eq!(snapshot.truncated, 2);
}
