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
        "LastReconciledPowerSource": state.last,
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
    assert_eq!(rows.len(), 9);
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
fn begin_without_any_power_source_fails_and_writes_nothing() {
    let file = Mem::default();
    let mut recovery = Recovery::load(file.clone());
    assert!(!recovery.begin(Fields::BRIGHTNESS, None));
    assert!(file.raw().is_none());
    assert!(recovery.flags().1);
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

    let invalid = json!({
        "case": "native-invalid-schema",
        "bytes": "{\"SchemaVersion\":9}",
        "expectedInvalid": true,
    });
    rows.push_str(&format!("{invalid}\n"));
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
    // The token text inside an ordinary value that does not start with it.
    let benign = "{\"SchemaVersion\":1,\"Note\":\"see $serde_json::private::\"}";
    assert!(!Recovery::load(Mem::with(benign.as_bytes())).flags().1);
}
