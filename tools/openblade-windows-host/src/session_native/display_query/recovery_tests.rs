use super::*;
use std::sync::{Arc, Mutex};

const FIXTURE: &str =
    include_str!("../../../tests/fixtures/display-automation/managed-checkpoints.jsonl");

/// In-memory checkpoint file. Clones share bytes so a restart can reload them;
/// failure switches are per instance.
#[derive(Clone, Default)]
struct Mem {
    bytes: Arc<Mutex<Option<Vec<u8>>>>,
    fail_read: bool,
    fail_replace: bool,
}

impl CheckpointFile for Mem {
    fn read(&mut self) -> io::Result<Option<Vec<u8>>> {
        if self.fail_read {
            return Err(io::Error::other("read failed"));
        }
        Ok(self.bytes.lock().unwrap().clone())
    }

    fn replace(&mut self, bytes: &[u8]) -> io::Result<()> {
        if self.fail_replace {
            return Err(io::Error::other("replace failed"));
        }
        *self.bytes.lock().unwrap() = Some(bytes.to_vec());
        Ok(())
    }
}

impl Mem {
    fn with(bytes: &[u8]) -> Self {
        Self {
            bytes: Arc::new(Mutex::new(Some(bytes.to_vec()))),
            ..Self::default()
        }
    }

    fn stored(&self) -> Value {
        serde_json::from_slice(self.bytes.lock().unwrap().as_ref().unwrap()).unwrap()
    }

    fn raw(&self) -> Option<Vec<u8>> {
        self.bytes.lock().unwrap().clone()
    }
}

fn brightness_only() -> Fields {
    Fields {
        brightness: true,
        ..Fields::default()
    }
}

fn fixtures() -> Vec<Value> {
    FIXTURE
        .lines()
        .map(|line| serde_json::from_str(line).unwrap())
        .collect()
}

fn fixture(case: &str) -> String {
    fixtures()
        .into_iter()
        .find(|row| row["case"] == case)
        .unwrap()["bytes"]
        .as_str()
        .unwrap()
        .to_owned()
}

fn from_fixture(case: &str) -> (Recovery<Mem>, Mem) {
    let file = Mem::with(fixture(case).as_bytes());
    (Recovery::load(file.clone()), file)
}

fn with_brightness() -> Snapshot {
    super::super::observation::fixture_query("brightness-zero")
}

fn without_brightness() -> Snapshot {
    super::super::observation::fixture_query("available")
}

/// Expected stored record (13 managed members) derived from the recovery state
/// itself, never from the bytes under test.
fn expected(state: &Recovery<Mem>) -> Value {
    json!({
        "LastReconciledPowerSource": state.last.unwrap_or(EXTERNAL_UNKNOWN),
        "ApplicationProfileId": state.application_profile_id,
        "Baseline": state.baseline,
        "OwnedRefreshRate": state.owned.refresh,
        "OwnedHdr": state.owned.hdr,
        "OwnedColorProfile": state.owned.color,
        "OwnedBrightness": state.owned.brightness,
        "ManualHold": state.manual_hold,
        "AutomaticWritesPaused": state.paused,
        "UncertainRefreshRate": state.uncertain.refresh,
        "UncertainHdr": state.uncertain.hdr,
        "UncertainColorProfile": state.uncertain.color,
        "UncertainBrightness": state.uncertain.brightness,
    })
}

fn comparable(mut record: Value) -> Value {
    let object = record.as_object_mut().unwrap();
    object.remove("SchemaVersion");
    if let Some(Value::Object(baseline)) = object.get_mut("Baseline") {
        baseline.remove("IsConfigured");
    }
    record
}

#[test]
fn loads_every_managed_fixture_row_and_rewrites_the_same_state() {
    let rows = fixtures();
    assert_eq!(rows.len(), 11);
    for row in rows {
        let case = row["case"].as_str().unwrap();
        let file = Mem::with(row["bytes"].as_str().unwrap().as_bytes());
        let mut recovery = Recovery::load(file.clone());
        if row["state"].is_null() {
            assert_eq!(
                recovery.last,
                row["legacySource"].as_u64().map(|s| s as u8),
                "{case}"
            );
            assert_eq!(
                recovery.flags(),
                (false, false, Fields::default()),
                "{case}"
            );
            assert!(recovery.baseline.is_none(), "{case}");
            continue;
        }
        let state = &row["state"];
        assert_eq!(
            comparable(expected(&recovery)),
            comparable(state.clone()),
            "{case}"
        );
        assert_eq!(
            recovery.unresolved().brightness,
            state["AutomaticWritesPaused"] == true && state["UncertainBrightness"] == true,
            "{case}"
        );
        assert!(recovery.persist(), "{case}");
        let stored = file.stored();
        assert_eq!(stored["SchemaVersion"], 4, "{case}");
        assert_eq!(comparable(stored), comparable(state.clone()), "{case}");
    }
}

#[test]
fn invalid_or_unreadable_files_pause_with_every_field_uncertain() {
    let minimal = fixture("pluggedin-minimal");
    let oversize = format!("{minimal}{}", " ".repeat(MAXIMUM_BYTES));
    let wrong_type = minimal.replace("\"ManualHold\":false", "\"ManualHold\":\"no\"");
    let bad_source = minimal.replace(
        "\"LastReconciledPowerSource\":0",
        "\"LastReconciledPowerSource\":3",
    );
    let bad_guid = fixture("pluggedin-owned-all-with-profile-full-baseline")
        .replace("3f2c8a10-5b6d-4e7a-9c1f-0a2b3c4d5e6f", "not-a-guid");
    for (name, bytes) in [
        ("wrong schema", "{\"SchemaVersion\":9}".to_owned()),
        (
            "legacy source",
            "{\"SchemaVersion\":1,\"LastReconciledPowerSource\":3}".to_owned(),
        ),
        ("oversize", oversize),
        ("malformed", "{\"SchemaVersion\":4,".to_owned()),
        ("not an object", "[]".to_owned()),
        ("empty", String::new()),
        ("wrong member type", wrong_type),
        ("bad profile id", bad_guid),
        ("bad power source", bad_source),
    ] {
        let recovery = Recovery::load(Mem::with(bytes.as_bytes()));
        assert_eq!(recovery.flags(), (false, true, Fields::ALL), "{name}");
        assert!(recovery.unresolved().brightness, "{name}");
    }
    let unreadable = Recovery::load(Mem {
        fail_read: true,
        ..Mem::default()
    });
    assert_eq!(unreadable.flags(), (false, true, Fields::ALL));
    assert!(unreadable.unresolved().brightness);
}

#[test]
fn absent_file_is_not_paused() {
    let recovery = Recovery::load(Mem::default());
    assert_eq!(recovery.flags(), (false, false, Fields::default()));
    assert!(!recovery.unresolved().brightness);
}

#[test]
fn begin_persists_hold_pause_and_brightness_uncertainty_before_returning() {
    let file = Mem::default();
    let mut recovery = Recovery::load(file.clone());
    assert!(recovery.begin(Fields::BRIGHTNESS, Some(1)));
    let stored = file.stored();
    assert_eq!(stored["LastReconciledPowerSource"], 1);
    assert_eq!(stored["ManualHold"], true);
    assert_eq!(stored["AutomaticWritesPaused"], true);
    assert_eq!(stored["UncertainBrightness"], true);
    for member in [
        "UncertainRefreshRate",
        "UncertainHdr",
        "UncertainColorProfile",
    ] {
        assert_eq!(stored[member], false, "{member}");
    }
}

#[test]
fn begin_clears_baseline_ownership_and_profile_like_managed() {
    let (mut recovery, file) = from_fixture("pluggedin-owned-all-with-profile-full-baseline");
    assert!(recovery.begin(Fields::BRIGHTNESS, None));
    let stored = file.stored();
    assert_eq!(stored["LastReconciledPowerSource"], 0);
    assert!(stored["Baseline"].is_null() && stored["ApplicationProfileId"].is_null());
    for member in [
        "OwnedRefreshRate",
        "OwnedHdr",
        "OwnedColorProfile",
        "OwnedBrightness",
    ] {
        assert_eq!(stored[member], false, "{member}");
    }
}

#[test]
fn begin_without_any_confirmed_source_holds_external_unknown() {
    // Managed `lastConfirmedPowerSource ?? ExternalUnknown`, admitted by the
    // store only as a manual hold that owns nothing.
    for source in [None, Some(EXTERNAL_UNKNOWN)] {
        let file = Mem::default();
        let mut recovery = Recovery::load(file.clone());
        assert!(recovery.begin(Fields::BRIGHTNESS, source));
        let stored = file.stored();
        assert_eq!(stored["LastReconciledPowerSource"], 3);
        assert_eq!(stored["ManualHold"], true);
        assert_eq!(recovery.last, None);
        // Complete still saves the hold; an explicit read cannot resume
        // without a confirmed source, as managed requires one.
        recovery.complete(Status::Indeterminate, &Snapshot::unavailable("unknown"));
        assert_eq!(file.stored()["AutomaticWritesPaused"], true);
        assert!(!recovery.explicit_read(&with_brightness()));
        assert!(recovery.flags().1);
        // A confirmed begin records its source again.
        assert!(recovery.begin(Fields::BRIGHTNESS, Some(1)));
        assert_eq!(file.stored()["LastReconciledPowerSource"], 1);
    }
    // A manual-external begin never replaces the last confirmed source.
    let (mut recovery, file) = from_fixture("battery-minimal");
    assert!(recovery.begin(Fields::BRIGHTNESS, Some(EXTERNAL_UNKNOWN)));
    assert_eq!(file.stored()["LastReconciledPowerSource"], 1);
}

#[test]
fn an_external_unknown_checkpoint_loads_only_as_an_unowned_manual_hold() {
    let hold = fixture("externalunknown-manualhold-paused-uncertain-brightness");
    let recovery = Recovery::load(Mem::with(hold.as_bytes()));
    assert_eq!(recovery.last, None);
    assert_eq!(recovery.flags(), (true, true, brightness_only()));
    assert!(recovery.unresolved().brightness);
    for (name, bytes) in [
        ("without hold", hold.replace("\"ManualHold\":true", "\"ManualHold\":false")),
        ("owned", hold.replace("\"OwnedHdr\":false", "\"OwnedHdr\":true")),
        (
            "profile",
            hold.replace(
                "\"ApplicationProfileId\":null",
                "\"ApplicationProfileId\":\"3f2c8a10-5b6d-4e7a-9c1f-0a2b3c4d5e6f\"",
            ),
        ),
        (
            "baseline",
            hold.replace(
                "\"Baseline\":null",
                "\"Baseline\":{\"RefreshRateKey\":null,\"HdrEnabled\":null,\"ColorProfileKey\":null,\"BrightnessPercent\":30}",
            ),
        ),
    ] {
        assert_ne!(bytes, hold, "{name}");
        let recovery = Recovery::load(Mem::with(bytes.as_bytes()));
        assert_eq!(recovery.flags(), (false, true, Fields::ALL), "{name}");
    }
}

#[test]
fn begin_replace_failure_latches_and_restores_prior_uncertainty() {
    let (mut recovery, file) = from_fixture("battery-minimal");
    let before = file.raw();
    recovery.file().fail_replace = true;
    assert!(!recovery.begin(Fields::BRIGHTNESS, Some(0)));
    assert_eq!(recovery.flags(), (true, true, Fields::default()));
    assert_eq!(file.raw(), before);
}

#[test]
fn complete_succeeded_with_brightness_readback_resumes() {
    let (mut recovery, file) = from_fixture("battery-minimal");
    assert!(recovery.begin(Fields::BRIGHTNESS, None));
    recovery.complete(Status::Succeeded, &with_brightness());
    let (_, paused, uncertain) = recovery.flags();
    assert!(!paused && uncertain == Fields::default());
    assert!(!recovery.unresolved().brightness);
    let stored = file.stored();
    assert_eq!(stored["AutomaticWritesPaused"], false);
    assert_eq!(stored["UncertainBrightness"], false);
}

#[test]
fn complete_without_confirmation_or_indeterminate_stays_unresolved() {
    // Durable state follows managed CompleteManualOperation in every case; the
    // native write gate is set only when a submission may be unconfirmed.
    for (status, snapshot, gated) in [
        (Status::Succeeded, without_brightness(), false),
        (Status::Succeeded, Snapshot::unavailable("gone"), false),
        (Status::Indeterminate, with_brightness(), true),
    ] {
        let (mut recovery, file) = from_fixture("battery-minimal");
        assert!(recovery.begin(Fields::BRIGHTNESS, None));
        recovery.complete(status, &snapshot);
        assert_eq!(
            recovery.flags(),
            (true, true, brightness_only()),
            "{status:?}"
        );
        assert_eq!(recovery.unresolved().brightness, gated, "{status:?}");
        assert_eq!(file.stored()["AutomaticWritesPaused"], true);
        assert_eq!(file.stored()["UncertainBrightness"], true);
    }
}

#[test]
fn complete_blocked_or_failed_restores_the_inherited_pause() {
    for status in [Status::Blocked, Status::Failed] {
        let (mut clean, _) = from_fixture("battery-minimal");
        assert!(clean.begin(Fields::BRIGHTNESS, None));
        clean.complete(status, &without_brightness());
        assert!(!clean.flags().1, "{status:?}");
        assert_eq!(clean.flags().2, Fields::default(), "{status:?}");
        assert!(!clean.unresolved().brightness);

        let (mut paused, file) =
            from_fixture("pluggedin-owned-refresh-color-uncertain-refresh-paused");
        assert!(paused.begin(Fields::BRIGHTNESS, None));
        paused.complete(status, &without_brightness());
        let refresh_only = Fields {
            refresh: true,
            ..Fields::default()
        };
        assert!(paused.flags().1, "{status:?}");
        assert_eq!(paused.flags().2, refresh_only, "{status:?}");
        assert!(!paused.unresolved().brightness);
        assert_eq!(file.stored()["UncertainBrightness"], false);
    }
}

#[test]
fn complete_replace_failure_latches_and_keeps_brightness_unresolved() {
    let (mut recovery, _) = from_fixture("battery-minimal");
    assert!(recovery.begin(Fields::BRIGHTNESS, None));
    recovery.file().fail_replace = true;
    recovery.complete(Status::Succeeded, &with_brightness());
    assert_eq!(recovery.flags(), (true, true, brightness_only()));
    assert!(recovery.unresolved().brightness);
}

#[test]
fn indeterminate_survives_restart_until_an_explicit_brightness_read() {
    let (mut recovery, file) = from_fixture("battery-minimal");
    assert!(recovery.begin(Fields::BRIGHTNESS, None));
    recovery.complete(Status::Indeterminate, &Snapshot::unavailable("unknown"));

    let mut restarted = Recovery::load(file.clone());
    assert!(restarted.unresolved().brightness);
    assert_eq!(restarted.flags(), (true, true, brightness_only()));

    assert!(!restarted.explicit_read(&Snapshot::unavailable("still gone")));
    assert!(!restarted.explicit_read(&without_brightness()));
    assert!(restarted.unresolved().brightness);
    assert_eq!(file.stored()["AutomaticWritesPaused"], true);

    assert!(restarted.explicit_read(&with_brightness()));
    assert!(!restarted.unresolved().brightness);
    assert_eq!(restarted.flags(), (false, false, Fields::default()));
    let stored = file.stored();
    assert_eq!(stored["AutomaticWritesPaused"], false);
    assert_eq!(stored["ManualHold"], false);
    assert_eq!(stored["UncertainBrightness"], false);
    assert!(!Recovery::load(file).unresolved().brightness);
}

#[test]
fn explicit_read_resume_replace_failure_stays_latched() {
    let (mut recovery, _) = from_fixture("battery-minimal");
    assert!(recovery.begin(Fields::BRIGHTNESS, None));
    recovery.complete(Status::Indeterminate, &Snapshot::unavailable("unknown"));
    recovery.file().fail_replace = true;
    assert!(!recovery.explicit_read(&with_brightness()));
    assert_eq!(recovery.flags(), (true, true, brightness_only()));
}

#[test]
fn explicit_read_preserves_baseline_and_ownership_when_resuming() {
    let (mut recovery, file) =
        from_fixture("pluggedin-owned-refresh-color-uncertain-refresh-paused");
    assert!(recovery.explicit_read(&without_brightness()));
    let stored = file.stored();
    assert_eq!(stored["AutomaticWritesPaused"], false);
    assert_eq!(stored["OwnedRefreshRate"], true);
    assert_eq!(stored["OwnedColorProfile"], true);
    assert_eq!(stored["Baseline"]["RefreshRateKey"], "120");
}

/// Writes native checkpoint bytes for the managed `checkpoints-verify` oracle.
#[test]
fn emits_native_rows_for_the_managed_oracle() {
    let Some(path) = std::env::var_os("OPENBLADE_NATIVE_AGENT_DISPLAY_CHECKPOINTS") else {
        return;
    };
    let mut rows = String::new();
    let mut row = |case: &str, recovery: &Recovery<Mem>, file: &Mem| {
        let bytes = String::from_utf8(file.raw().unwrap()).unwrap();
        let line = json!({"case": case, "bytes": bytes, "expected": expected(recovery)});
        rows.push_str(&format!("{line}\n"));
    };

    let file = Mem::default();
    let mut begun = Recovery::load(file.clone());
    assert!(begun.begin(Fields::BRIGHTNESS, Some(1)));
    row("native-after-begin", &begun, &file);
    begun.complete(Status::Indeterminate, &Snapshot::unavailable("unknown"));
    row("native-after-indeterminate", &begun, &file);
    assert!(begun.explicit_read(&with_brightness()));
    row("native-after-resume", &begun, &file);

    let file = Mem::default();
    let mut hdr = Recovery::load(file.clone());
    assert!(hdr.begin(Fields::HDR, Some(2)));
    row("native-hdr-after-begin", &hdr, &file);
    hdr.complete(Status::Indeterminate, &Snapshot::unavailable("unknown"));
    row("native-hdr-after-indeterminate", &hdr, &file);
    assert!(hdr.explicit_read(&hdr_supported()));
    row("native-hdr-after-resume", &hdr, &file);

    let file = Mem::default();
    let mut refresh = Recovery::load(file.clone());
    assert!(refresh.begin(Fields::REFRESH, Some(0)));
    row("native-refresh-after-begin", &refresh, &file);
    refresh.complete(Status::Indeterminate, &Snapshot::unavailable("unknown"));
    row("native-refresh-after-indeterminate", &refresh, &file);
    assert!(refresh.explicit_read(&without_brightness()));
    row("native-refresh-after-resume", &refresh, &file);

    let file = Mem::default();
    let mut color = Recovery::load(file.clone());
    assert!(color.begin(Fields::COLOR, Some(1)));
    row("native-color-after-begin", &color, &file);
    color.complete(Status::Indeterminate, &Snapshot::unavailable("unknown"));
    row("native-color-after-indeterminate", &color, &file);
    assert!(color.explicit_read(&without_brightness()));
    row("native-color-after-resume", &color, &file);

    // A profile checkpoint covers every configured field; a partial outcome
    // keeps the managed durable record whatever the native gate holds.
    let file = Mem::default();
    let mut profile = Recovery::load(file.clone());
    assert!(profile.begin(Fields::ALL, Some(2)));
    row("native-profile-after-begin", &profile, &file);
    profile.complete_with(
        Status::Indeterminate,
        &Snapshot::unavailable("unknown"),
        Fields::HDR,
    );
    row(
        "native-profile-after-partial-indeterminate",
        &profile,
        &file,
    );

    let (mut cleared, file) = from_fixture("pluggedin-owned-all-with-profile-full-baseline");
    assert!(cleared.begin(Fields::BRIGHTNESS, None));
    row("native-begin-clears-baseline", &cleared, &file);

    let (mut resumed, file) =
        from_fixture("pluggedin-owned-refresh-color-uncertain-refresh-paused");
    assert!(resumed.explicit_read(&without_brightness()));
    row("native-resume-preserves-baseline", &resumed, &file);

    let (mut blocked, file) = from_fixture("pluggedin-owned-all-with-profile-full-baseline");
    blocked.complete(Status::Blocked, &without_brightness());
    row(
        "native-complete-preserves-profile-baseline",
        &blocked,
        &file,
    );

    // Main's manual-external hold: no confirmed source yet, so ExternalUnknown.
    let file = Mem::default();
    let mut external = Recovery::load(file.clone());
    assert!(external.begin(Fields::BRIGHTNESS, Some(EXTERNAL_UNKNOWN)));
    row("native-external-unknown-after-begin", &external, &file);
    external.complete(Status::Indeterminate, &Snapshot::unavailable("unknown"));
    row(
        "native-external-unknown-after-indeterminate",
        &external,
        &file,
    );

    let hold = String::from_utf8(file.raw().unwrap()).unwrap();
    for (case, bytes) in [
        ("native-invalid-schema", "{\"SchemaVersion\":9}".to_owned()),
        (
            "native-invalid-external-unknown-without-hold",
            hold.replace("\"ManualHold\":true", "\"ManualHold\":false"),
        ),
        (
            "native-invalid-external-unknown-owned",
            hold.replace("\"OwnedBrightness\":false", "\"OwnedBrightness\":true"),
        ),
    ] {
        assert_ne!(bytes, hold, "{case}");
        let invalid = json!({"case": case, "bytes": bytes, "expectedInvalid": true});
        rows.push_str(&format!("{invalid}\n"));
    }
    let mut output = OpenOptions::new()
        .create(true)
        .append(true)
        .open(path)
        .unwrap();
    output.write_all(rows.as_bytes()).unwrap();
}

#[test]
fn legacy_record_without_a_source_defaults_to_plugged_in() {
    let recovery = Recovery::load(Mem::with(b"{\"SchemaVersion\":1}"));
    assert_eq!(recovery.flags(), (false, false, Fields::default()));
    assert!(!recovery.unresolved().brightness);
    assert_eq!(recovery.last, Some(0));
}

#[test]
fn nesting_beyond_the_managed_reader_depth_is_invalid() {
    // System.Text.Json's default MaxDepth is 64, counting the root object.
    let nested = |levels: usize| {
        format!(
            "{{\"SchemaVersion\":1,\"Ignored\":{}0{}}}",
            "[".repeat(levels - 1),
            "]".repeat(levels - 1)
        )
    };
    let accepted = Recovery::load(Mem::with(nested(64).as_bytes()));
    assert!(!accepted.unresolved().brightness);
    let rejected = Recovery::load(Mem::with(nested(65).as_bytes()));
    assert_eq!(rejected.flags(), (false, true, Fields::ALL));
}

#[test]
fn shadowed_duplicate_members_are_invalid() {
    let deep = format!("{}0{}", "[".repeat(64), "]".repeat(64));
    for (name, bytes) in [
        (
            "deep shadowed",
            format!("{{\"SchemaVersion\":1,\"Ignored\":{deep},\"Ignored\":0}}"),
        ),
        (
            "mistyped shadowed",
            "{\"SchemaVersion\":\"x\",\"SchemaVersion\":1}".to_owned(),
        ),
        (
            "same value",
            "{\"SchemaVersion\":1,\"SchemaVersion\":1}".to_owned(),
        ),
    ] {
        let recovery = Recovery::load(Mem::with(bytes.as_bytes()));
        assert_eq!(recovery.flags(), (false, true, Fields::ALL), "{name}");
    }
    // Brackets inside strings are not nesting.
    let quoted = format!(
        "{{\"SchemaVersion\":1,\"Note\":\"{}\\\"\"}}",
        "[".repeat(80)
    );
    assert!(!Recovery::load(Mem::with(quoted.as_bytes())).flags().1);
}

#[test]
fn succeeded_without_brightness_confirmation_regates_after_restart() {
    // Managed records an unconfirmed field as uncertain; a restart re-derives
    // the conservative gate until an explicit read observes brightness.
    let file = Mem::default();
    let mut recovery = Recovery::load(file.clone());
    assert!(recovery.begin(Fields::BRIGHTNESS, Some(1)));
    recovery.complete(Status::Succeeded, &without_brightness());
    assert!(!recovery.unresolved().brightness);
    let restarted = Recovery::load(file.clone());
    assert!(restarted.unresolved().brightness);
}

#[test]
fn nested_duplicates_private_tokens_and_negative_zero_are_invalid() {
    let deep = format!("{}0{}", "[".repeat(64), "]".repeat(64));
    for (name, bytes) in [
        (
            "nested duplicate",
            "{\"SchemaVersion\":4,\"Baseline\":{\"HdrEnabled\":\"x\",\"HdrEnabled\":true}}".to_owned(),
        ),
        (
            "private number",
            "{\"SchemaVersion\":{\"$serde_json::private::Number\":\"4\"}}".to_owned(),
        ),
        (
            "escaped private raw value",
            r#"{"SchemaVersion":4,"AutomaticWritesPaused":{"\u0024serde_json::private::RawValue":"false"}}"#.to_owned(),
        ),
        (
            "negative zero byte",
            "{\"SchemaVersion\":4,\"Baseline\":{\"BrightnessPercent\":-0}}".to_owned(),
        ),
        // An escaped quote must not end the string early and hide real nesting.
        (
            "escaped quote before depth",
            format!("{{\"SchemaVersion\":1,\"S\":\"a\\\"b\",\"D\":{deep}}}"),
        ),
    ] {
        let recovery = Recovery::load(Mem::with(bytes.as_bytes()));
        assert_eq!(recovery.flags(), (false, true, Fields::ALL), "{name}");
    }
    // A member name that contains the token text without starting with it.
    let benign = "{\"SchemaVersion\":1,\"see $serde_json::private::\":1}";
    assert!(!Recovery::load(Mem::with(benign.as_bytes())).flags().1);
    // serde_json reinterprets only member names. A saved Display key is text a
    // user can choose, so a key value with the token prefix still loads.
    let keys = r#"{"SchemaVersion":4,"Baseline":{"RefreshRateKey":"$serde_json::private::Number","ColorProfileKey":"$serde_json::private::srgb"}}"#;
    assert!(!Recovery::load(Mem::with(keys.as_bytes())).flags().1);
}

fn hdr_supported() -> Snapshot {
    // The managed fixture reads HDR as supported and leaves brightness absent.
    super::super::observation::fixture_query("available")
}

#[test]
fn hdr_operation_gates_only_hdr_and_explicit_reads_clear_only_observed_fields() {
    let (mut recovery, file) = from_fixture("battery-minimal");
    assert!(recovery.begin(Fields::HDR, None));
    let stored = file.stored();
    assert_eq!(stored["UncertainHdr"], true);
    assert_eq!(stored["UncertainBrightness"], false);
    recovery.complete(Status::Indeterminate, &hdr_supported());
    assert_eq!(recovery.unresolved(), Fields::HDR);

    // A brightness operation adds its own gate without clearing HDR.
    assert!(recovery.begin(Fields::BRIGHTNESS, None));
    recovery.complete(Status::Indeterminate, &Snapshot::unavailable("unknown"));
    let both = Fields {
        hdr: true,
        brightness: true,
        ..Fields::default()
    };
    assert_eq!(recovery.unresolved(), both);
    assert_eq!(recovery.flags(), (true, true, both));

    // Restart restores both gates from the durable record.
    let mut restarted = Recovery::load(file.clone());
    assert_eq!(restarted.unresolved(), both);

    // HDR without brightness clears only HDR; the managed pause remains.
    assert!(!restarted.explicit_read(&hdr_supported()));
    assert_eq!(restarted.unresolved(), Fields::BRIGHTNESS);
    assert_eq!(file.stored()["AutomaticWritesPaused"], true);

    // An available read that reports HDR unsupported observed HDR too, so
    // with brightness it reopens the whole native gate. Managed
    // HasCompleteReadback requires HDR support, so the pause stays and a
    // restart reloads both gates.
    let mut restarted = Recovery::load(file.clone());
    let mut value = serde_json::to_value(with_brightness()).unwrap();
    value["hdrSupported"] = false.into();
    let no_hdr: Snapshot = serde_json::from_value(value).unwrap();
    assert!(no_hdr.observes(both) && !no_hdr.confirms(Fields::HDR));
    assert!(!restarted.explicit_read(&no_hdr));
    assert_eq!(restarted.unresolved(), Fields::default());
    assert_eq!(restarted.flags(), (true, true, both));
    let mut restarted = Recovery::load(file.clone());
    assert_eq!(restarted.unresolved(), both);

    // A read observing both resumes managed automation as before.
    assert!(restarted.explicit_read(&with_brightness()));
    assert_eq!(restarted.unresolved(), Fields::default());
    assert_eq!(restarted.flags(), (false, false, Fields::default()));
}

#[test]
fn refresh_operation_gates_only_refresh_and_reopens_on_any_observed_refresh_state() {
    let (mut recovery, file) = from_fixture("battery-minimal");
    assert!(recovery.begin(Fields::REFRESH, None));
    let stored = file.stored();
    assert_eq!(stored["UncertainRefreshRate"], true);
    assert_eq!(stored["UncertainHdr"], false);
    assert_eq!(stored["UncertainBrightness"], false);
    recovery.complete(Status::Indeterminate, &Snapshot::unavailable("unknown"));
    assert_eq!(recovery.unresolved(), Fields::REFRESH);

    // HDR and brightness operations add their own gates without clearing it.
    assert!(recovery.begin(Fields::HDR, None));
    recovery.complete(Status::Indeterminate, &Snapshot::unavailable("unknown"));
    let both = Fields {
        refresh: true,
        hdr: true,
        ..Fields::default()
    };
    assert_eq!(recovery.unresolved(), both);

    // Restart restores both gates. An unavailable read observes nothing.
    let mut restarted = Recovery::load(file.clone());
    assert_eq!(restarted.unresolved(), both);
    assert!(!restarted.explicit_read(&Snapshot::unavailable("gone")));
    assert_eq!(restarted.unresolved(), both);

    // An available read whose mode matches no option (issue #884) observed
    // the refresh state, so the native gate reopens for the repairing
    // request. The managed pause keeps HasCompleteReadback: it persists.
    let mut value = serde_json::to_value(without_brightness()).unwrap();
    value["currentRefreshKey"] = serde_json::Value::Null;
    let unclassified: Snapshot = serde_json::from_value(value).unwrap();
    assert!(!restarted.explicit_read(&unclassified));
    assert_eq!(restarted.unresolved(), Fields::default());
    assert_eq!(restarted.flags(), (true, true, both));
    assert_eq!(file.stored()["AutomaticWritesPaused"], true);
    assert_eq!(file.stored()["UncertainRefreshRate"], true);
    let mut restarted = Recovery::load(file.clone());
    assert!(!restarted.explicit_read(&unclassified));
    assert_eq!(restarted.flags(), (true, true, both));

    // Only a complete readback resumes managed automation.
    assert!(restarted.explicit_read(&without_brightness()));
    assert_eq!(restarted.unresolved(), Fields::default());
    assert_eq!(restarted.flags(), (false, false, Fields::default()));
}

#[test]
fn hdr_succeeded_without_hdr_readback_keeps_the_pause_but_not_the_gate() {
    let (mut recovery, file) = from_fixture("battery-minimal");
    assert!(recovery.begin(Fields::HDR, Some(0)));
    let mut value = serde_json::to_value(hdr_supported()).unwrap();
    value["hdrSupported"] = false.into();
    let unsupported: Snapshot = serde_json::from_value(value).unwrap();
    recovery.complete(Status::Succeeded, &unsupported);
    assert_eq!(recovery.unresolved(), Fields::default());
    assert_eq!(recovery.flags(), (true, true, Fields::HDR));
    assert_eq!(file.stored()["UncertainHdr"], true);
    // Managed CompleteManualOperation then resumes once HDR reads back.
    recovery.complete(Status::Succeeded, &hdr_supported());
    assert_eq!(recovery.flags(), (true, false, Fields::default()));
}

#[test]
fn complete_with_gates_exactly_the_given_fields_beside_the_managed_record() {
    let profile = Fields::REFRESH.include(Fields::HDR).include(Fields::COLOR);

    // Indeterminate: the durable record keeps every field of the operation
    // uncertain, while the native gate gains only the given ones.
    let (mut recovery, file) = from_fixture("battery-minimal");
    assert!(recovery.begin(profile, Some(1)));
    recovery.complete_with(Status::Indeterminate, &without_brightness(), Fields::HDR);
    assert_eq!(recovery.unresolved(), Fields::HDR);
    assert_eq!(recovery.flags(), (true, true, profile));
    assert_eq!(file.stored()["UncertainColorProfile"], true);
    assert_eq!(Recovery::load(file.clone()).unresolved(), profile);

    // Succeeded with a complete readback resumes the managed pause, and the
    // given fields are still gated in this Agent.
    let (mut recovery, file) = from_fixture("battery-minimal");
    assert!(recovery.begin(profile, Some(1)));
    recovery.complete_with(Status::Succeeded, &without_brightness(), Fields::COLOR);
    assert_eq!(recovery.flags(), (true, false, Fields::default()));
    assert_eq!(file.stored()["AutomaticWritesPaused"], false);
    assert_eq!(recovery.unresolved(), Fields::COLOR);

    // Failed restores the inherited state and keeps earlier gates.
    let (mut recovery, file) = from_fixture("battery-minimal");
    assert!(recovery.begin(Fields::BRIGHTNESS, Some(1)));
    recovery.complete(Status::Indeterminate, &Snapshot::unavailable("unknown"));
    assert!(recovery.begin(profile, Some(1)));
    recovery.complete_with(Status::Failed, &without_brightness(), Fields::default());
    assert_eq!(recovery.unresolved(), Fields::BRIGHTNESS);
    assert_eq!(recovery.flags(), (true, true, Fields::BRIGHTNESS));
    assert_eq!(file.stored()["UncertainRefreshRate"], false);
}

#[test]
fn only_a_paused_load_restores_the_hdr_gate() {
    // Managed uncertainty without a pause (an in-flight hold) gates nothing.
    let (recovery, _) = from_fixture("battery-owned-brightness-uncertain-hdr-hold");
    assert!(recovery.flags().2.hdr && !recovery.flags().1);
    assert_eq!(recovery.unresolved(), Fields::default());
    // A paused record restores exactly its uncertain fields.
    let paused = Mem::with(
        br#"{"SchemaVersion":4,"LastReconciledPowerSource":1,"ManualHold":true,"AutomaticWritesPaused":true,"UncertainHdr":true}"#,
    );
    assert_eq!(Recovery::load(paused).unresolved(), Fields::HDR);
}

#[test]
fn indeterminate_gates_only_the_operation_fields_in_process() {
    // Inherited, unpaused brightness uncertainty is not a gate; an HDR
    // Indeterminate must not turn it into one.
    let file = Mem::with(br#"{"SchemaVersion":4,"UncertainBrightness":true}"#);
    let mut recovery = Recovery::load(file.clone());
    assert_eq!(recovery.unresolved(), Fields::default());
    assert!(recovery.begin(Fields::HDR, Some(0)));
    recovery.complete(Status::Indeterminate, &Snapshot::unavailable("unknown"));
    assert_eq!(recovery.unresolved(), Fields::HDR);
}

#[test]
fn exponents_and_short_escapes_are_not_private_tokens_or_negative_zero() {
    for (name, bytes) in [
        ("negative exponent", r#"{"SchemaVersion":4,"X":1e-05}"#),
        (
            "negative exponent zero",
            r#"{"SchemaVersion":4,"X":[1E-0,1.5e-07]}"#,
        ),
        (
            "carriage return escape",
            r#"{"SchemaVersion":4,"$se\rde_json::private::x":1}"#,
        ),
        (
            "tab escape",
            r#"{"SchemaVersion":4,"$serde_json::priva\te::x":1}"#,
        ),
        (
            "newline escape",
            r#"{"SchemaVersion":4,"$serde_jso\n::private::x":1}"#,
        ),
    ] {
        let recovery = Recovery::load(Mem::with(bytes.as_bytes()));
        assert!(!recovery.flags().1, "{name}");
    }
    // A unicode escape inside the token is still decoded and refused.
    let escaped = r#"{"SchemaVersion":4,"X":{"$serde_json::priv\u0061te::Number":"1"}}"#;
    assert_eq!(
        Recovery::load(Mem::with(escaped.as_bytes())).flags(),
        (false, true, Fields::ALL)
    );
}

#[test]
fn brightness_indeterminate_does_not_gate_hdr() {
    let file = Mem::default();
    let mut recovery = Recovery::load(file.clone());
    assert!(recovery.begin(Fields::BRIGHTNESS, Some(1)));
    recovery.complete(Status::Indeterminate, &Snapshot::unavailable("unknown"));
    assert_eq!(recovery.unresolved(), Fields::BRIGHTNESS);
    assert!(!recovery.unresolved().hdr);
}

/// Managed `CompleteManualOperation` resumes only on `HasCompleteReadback`.
/// A Succeeded refresh write whose reply mode is unclassified (issue #884)
/// keeps the durable pause; only the native gate uses the weaker rule.
#[test]
fn refresh_succeeded_with_an_unclassified_reply_keeps_the_managed_pause() {
    let (mut recovery, file) = from_fixture("battery-minimal");
    assert!(recovery.begin(Fields::REFRESH, Some(0)));
    let mut value = serde_json::to_value(without_brightness()).unwrap();
    value["currentRefreshKey"] = serde_json::Value::Null;
    let unclassified: Snapshot = serde_json::from_value(value).unwrap();
    recovery.complete(Status::Succeeded, &unclassified);
    assert_eq!(recovery.unresolved(), Fields::default());
    assert_eq!(recovery.flags(), (true, true, Fields::REFRESH));
    assert_eq!(file.stored()["AutomaticWritesPaused"], true);
    assert_eq!(file.stored()["UncertainRefreshRate"], true);
    // A classified reply is the complete readback that resumes it.
    assert!(recovery.explicit_read(&without_brightness()));
    assert_eq!(recovery.flags(), (false, false, Fields::default()));
}

/// The available snapshot with these managed members replaced.
fn reading(changes: Value) -> Snapshot {
    let mut value = serde_json::to_value(without_brightness()).unwrap();
    for (member, change) in changes.as_object().unwrap() {
        value[member] = change.clone();
    }
    serde_json::from_value(value).unwrap()
}

/// The color gate reopens on any available read: it always completed the WCS
/// read, and SDR without a listed default shows no active profile, which only
/// a color write can change. The managed pause keeps `HasCompleteReadback`,
/// which an HDR-on read satisfies for the color field.
#[test]
fn color_operation_gates_only_color_and_reopens_on_any_available_read() {
    let (mut recovery, file) = from_fixture("battery-minimal");
    assert!(recovery.begin(Fields::COLOR, None));
    let stored = file.stored();
    assert_eq!(stored["UncertainColorProfile"], true);
    assert_eq!(stored["UncertainRefreshRate"], false);
    assert_eq!(stored["UncertainHdr"], false);
    assert_eq!(stored["UncertainBrightness"], false);
    recovery.complete(Status::Indeterminate, &Snapshot::unavailable("unknown"));
    assert_eq!(recovery.unresolved(), Fields::COLOR);

    let mut restarted = Recovery::load(file.clone());
    assert_eq!(restarted.unresolved(), Fields::COLOR);
    assert!(!restarted.explicit_read(&Snapshot::unavailable("gone")));
    assert_eq!(restarted.unresolved(), Fields::COLOR);

    // SDR with no active profile: the gate reopens, the managed pause stays.
    let unselected = reading(json!({"activeColorProfileId": null}));
    assert!(!unselected.confirms(Fields::COLOR) && unselected.observes(Fields::COLOR));
    assert!(!restarted.explicit_read(&unselected));
    assert_eq!(restarted.unresolved(), Fields::default());
    assert_eq!(restarted.flags(), (true, true, Fields::COLOR));
    assert_eq!(file.stored()["UncertainColorProfile"], true);
    let mut restarted = Recovery::load(file.clone());
    assert_eq!(restarted.unresolved(), Fields::COLOR);

    // HDR on: color profiles are disabled and none is active, yet managed
    // HasCompleteReadback accepts the color field, so automation resumes.
    let hdr_on = reading(json!({
        "hdrEnabled": true,
        "colorProfilesEnabled": false,
        "activeColorProfileId": null,
    }));
    assert!(hdr_on.confirms(Fields::COLOR));
    assert!(restarted.explicit_read(&hdr_on));
    assert_eq!(restarted.unresolved(), Fields::default());
    assert_eq!(restarted.flags(), (false, false, Fields::default()));
}
