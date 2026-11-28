//! Request-driven reconciliation of saved intent: a changed foreground
//! report, the first admitted report of a session, and a profile save that
//! changes the active application's intent. Every case runs the real framed
//! dispatcher over the in-memory store and the scripted mailbox; no hardware,
//! pipe or operating-system supplier is reached.
use super::*;
use crate::installed::readiness::thermal_mutation::{
    APPLICATION_SESSION_REQUIRED, UNCLASSIFIED_BLOCKED, is_step_request, operation_status,
};

const RECORDED: &str = "The active application profile was recorded.";
const CONFIRMED: &str = "Bounded thermal preset confirmed by independent paired readback. GPU clock handling remains unavailable.";
const AUTOMATIC: &str = "Automatic mode independently observed; no fan setter was submitted and no ownership was established.";
const STORED: &str = "Profiles stored without automatic hardware application.";
const VECTORS: &str = include_str!("../../../../../tests/data/request-trigger-vectors.json");

async fn report(rig: &Rig, sequence: i64, path: Option<&str>) -> Value {
    rig.request(json!({"operation":"reportApplicationContext","version":91,"requestId":format!("bbbbbbbb-bbbb-cccc-dddd-{sequence:012}"),"instanceId":"cccccccc-bbbb-cccc-dddd-000000000003","sequence":sequence,"executablePath":path})).await
}
fn update(n: u8, profiles: &Value) -> Value {
    json!({"operation":"updateProfiles","version":91,"requestId":format!("aaaaaaaa-bbbb-cccc-dddd-{n:012}"),"profiles":profiles})
}
fn writes(rig: &Rig) -> usize {
    rig.port.state.lock().unwrap().writes
}
/// The saved set with the Game rule's plugged-in performance replaced.
fn with_game_performance(files: &MemoryFiles, performance: i32) -> Value {
    let mut profiles = files.document("profiles.json")["profiles"].clone();
    profiles["applicationProfiles"][0]["overrides"]["pluggedIn"]["performance"] =
        json!(performance);
    profiles
}

#[tokio::test]
async fn a_changed_report_applies_the_saved_application_intent_and_stores_nothing() {
    let files = MemoryFiles::default();
    files.profiles(1);
    let rig = Rig::new(files);
    let overrides = rig.files.read("manual-overrides.json").unwrap();
    let applied = report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(applied["result"]["status"], 1, "{applied}");
    assert_eq!(
        applied["result"]["message"],
        format!("{RECORDED} {CONFIRMED} {AUTOMATIC}")
    );
    // The Game rule's Performance, applied from saved intent alone.
    let status = rig.get().await;
    assert_eq!(status["status"]["confirmedState"]["performance"], 2);
    assert_eq!(rig.files.read("manual-overrides.json").unwrap(), overrides);
    assert!(writes(&rig) > 0);
    let after = writes(&rig);
    // A stale sequence and a renewal apply nothing.
    let stale = report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(
        stale["result"],
        json!({"status":0,"message":"A stale application context was ignored."})
    );
    let renewed = report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(
        renewed["result"],
        json!({"status":0,"message":"The active application profile lease was renewed."})
    );
    assert_eq!(writes(&rig), after);
    // Leaving the rule is a change: the power profile's Balanced applies.
    let left = report(&rig, 3, Some(r"C:\Tools\Other.exe")).await;
    assert_eq!(left["result"]["status"], 1, "{left}");
    assert_eq!(
        rig.get().await["status"]["confirmedState"]["performance"],
        1
    );
    let none = report(&rig, 4, None).await;
    assert_eq!(
        none["result"],
        json!({"status":0,"message":"No application profile is active."})
    );
    rig.stop().await;
}

#[tokio::test]
async fn the_first_admitted_report_reconciles_once_and_unknown_power_does_not_consume_it() {
    let files = MemoryFiles::default();
    files.profiles(2);
    let rig = Rig::new(files);
    // Unknown Windows power admits no reconciliation, so startup is kept.
    rig.port.state.lock().unwrap().windows_on_battery = Some(None);
    let unknown = report(&rig, 1, Some(r"C:\Tools\Other.exe")).await;
    assert_eq!(
        unknown["result"],
        json!({"status":0,"message":"No application profile is active."})
    );
    assert_eq!(writes(&rig), 0);
    rig.port.state.lock().unwrap().windows_on_battery = None;
    // No pair changes, but this is the session's first admitted report.
    let startup = report(&rig, 2, Some(r"C:\Tools\Other.exe")).await;
    assert_eq!(startup["result"]["status"], 1, "{startup}");
    assert_eq!(
        startup["result"]["message"],
        format!("{RECORDED} {CONFIRMED} {AUTOMATIC}")
    );
    assert_eq!(
        rig.get().await["status"]["confirmedState"]["performance"],
        2
    );
    let after = writes(&rig);
    let again = report(&rig, 3, Some(r"C:\Tools\Other.exe")).await;
    assert_eq!(
        again["result"],
        json!({"status":0,"message":"No application profile is active."})
    );
    assert_eq!(writes(&rig), after);
    rig.stop().await;
}

#[tokio::test]
async fn fans_returning_to_automatic_go_first_and_carry_the_saved_preset() {
    // The Game rule's Fixed fans, then a report that leaves the rule.
    let rig = Rig::new(managed_application_files(include_bytes!(
        "../../../../../tests/fixtures/app-fans-0-1.json"
    )));
    *rig.custody.origin_for_fan.lock().unwrap() = Some(rig.origin.clone());
    {
        // Balanced with Automatic fans, which the Game rule's fans leave.
        let mut s = rig.port.state.lock().unwrap();
        s.selectors = [0; 2];
        s.fans = [0; 2];
    }
    let manual = report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(manual["result"]["status"], 1, "{manual}");
    // The order reads the confirmed fans, which firmware reports manual.
    assert_eq!(rig.port.state.lock().unwrap().fans, [1; 2]);
    assert_manual_fans_observed(&rig).await;
    let sent = rig.port.state.lock().unwrap().sent.len();
    let left = report(&rig, 2, Some(r"C:\Tools\Other.exe")).await;
    // The fan step goes first and carries the saved Balanced; the performance
    // step that follows finds it confirmed and writes nothing. Performance
    // first would have been refused while the fan change was still owned
    // (`FanHandbackPending`).
    assert_eq!(
        left["result"],
        json!({"status":1,"message":format!("{RECORDED} Fans returned to Automatic with Balanced performance, confirmed by independent paired readback. {CONFIRMED}")})
    );
    let pair = mode_setters(&rig, sent);
    assert_eq!(pair.len(), 2, "{pair:?}");
    assert!(pair.iter().all(|setter| setter[3] == 0), "{pair:?}");
    assert_eq!(rig.get().await["status"]["confirmedState"]["fans"], 0);
    rig.stop().await;
}

/// The Game rule's overrides replaced in the fixture profiles.
fn files_with_game(overrides: Value) -> MemoryFiles {
    let files = MemoryFiles::default();
    files.profiles(1);
    let mut document = files.document("profiles.json");
    document["profiles"]["applicationProfiles"][0]["overrides"] = overrides;
    files.put("profiles.json", document);
    files.commit_configuration();
    files
}
/// A rig whose fan steps reach the fixture, at `selector` with Automatic fans.
fn rig_at(files: MemoryFiles, selector: u8) -> Rig {
    let rig = Rig::new(files);
    *rig.custody.origin_for_fan.lock().unwrap() = Some(rig.origin.clone());
    let mut s = rig.port.state.lock().unwrap();
    s.selectors = [selector; 2];
    s.fans = [0; 2];
    drop(s);
    rig
}
/// The journal's request ids in order, each once.
fn journal_ids(rig: &Rig) -> Vec<String> {
    let mut ids: Vec<String> = Vec::new();
    for line in rig
        .files
        .read("thermal-mutation.journal")
        .unwrap()
        .unwrap_or_default()
        .split(|b| *b == b'\n')
        .filter(|line| !line.is_empty())
    {
        let row: Value = serde_json::from_slice(line).unwrap();
        let id = row["request"].as_str().unwrap().to_owned();
        if !ids.contains(&id) {
            ids.push(id);
        }
    }
    ids
}
const FIXED: &str = "Fan setters completed and independent mode was confirmed. Target RPM and rotation are not readable on this backend.";
const CHANGED: &str = "Saved configuration changed unexpectedly; storage reconciliation is required. No thermal submission is established by this change.";
const PRESET_AND_FIXED: &str = r#"{"pluggedIn":{"performance":2,"fans":1}}"#;

/// Both subsystems of a rule that sets a preset and Fixed fans apply, each
/// step under its own journal id, through a changed report, a save and the
/// session's first report. The journal refuses a second row chain under one
/// id, so a shared id applied only the preset.
#[tokio::test]
async fn a_preset_and_fixed_fan_rule_applies_both_steps_under_distinct_journal_ids() {
    let rule: Value = serde_json::from_str(PRESET_AND_FIXED).unwrap();
    let applied = format!("{CONFIRMED} {FIXED}");
    let check = |rig: &Rig, reply: &Value, prefix: &str, trigger: &str| {
        assert_eq!(
            reply["result"],
            json!({"status":1,"message":format!("{prefix} {applied}")}),
        );
        let s = rig.port.state.lock().unwrap();
        assert_eq!((s.selectors, s.fans), ([2; 2], [1; 2]));
        drop(s);
        let ids = journal_ids(rig);
        assert_eq!(ids.len(), 2, "{ids:?}");
        assert!(
            ids.iter().all(|id| is_step_request(id) && id != trigger),
            "{ids:?}"
        );
        assert!(!rig.storage.pending());
    };
    // A changed report.
    let rig = rig_at(files_with_game(rule.clone()), 0);
    let reply = report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    check(
        &rig,
        &reply,
        RECORDED,
        "bbbbbbbb-bbbb-cccc-dddd-000000000001",
    );
    rig.stop().await;
    // A save that changes the active rule.
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":1}})), 0);
    report_game(&rig).await;
    let mut profiles = rig.files.document("profiles.json")["profiles"].clone();
    profiles["applicationProfiles"][0]["overrides"] = rule;
    let reply = rig.request(update(3, &profiles)).await;
    check(
        &rig,
        &reply,
        "Profiles were stored.",
        "aaaaaaaa-bbbb-cccc-dddd-000000000003",
    );
    rig.stop().await;
    // The session's first report, with no rule active: the power profile.
    let files = MemoryFiles::default();
    files.profiles(2);
    let mut document = files.document("profiles.json");
    document["profiles"]["pluggedIn"]["fans"] = json!(1);
    files.put("profiles.json", document);
    files.commit_configuration();
    let rig = rig_at(files, 0);
    let reply = report(&rig, 1, Some(r"C:\Tools\Other.exe")).await;
    check(
        &rig,
        &reply,
        RECORDED,
        "bbbbbbbb-bbbb-cccc-dddd-000000000001",
    );
    // Step ids are fixed per trigger and step, and differ from client ids.
    assert!(!is_step_request("bbbbbbbb-bbbb-4ccc-8ddd-000000000001"));
    rig.stop().await;
}

/// A reconciliation runs under the 11 s reconciliation budget, so both rule
/// shapes finish at a device pace that a 2 s frame could not cover. A pace
/// refusal submits nothing, so the session's startup stays owed.
#[tokio::test]
async fn reconciliations_finish_at_device_pace_and_a_pace_refusal_leaves_startup_owed() {
    for ms in [100, 150] {
        for (rule, message) in [
            (json!({"pluggedIn":{"performance":2}}), AUTOMATIC),
            (serde_json::from_str(PRESET_AND_FIXED).unwrap(), FIXED),
        ] {
            let rig = rig_at(files_with_game(rule.clone()), 0);
            rig.port.state.lock().unwrap().latency = Some(std::time::Duration::from_millis(ms));
            let reply = report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
            rig.port.state.lock().unwrap().latency = None;
            assert_eq!(
                reply["result"],
                json!({"status":1,"message":format!("{RECORDED} {CONFIRMED} {message}")}),
                "{ms} ms {rule}"
            );
            rig.stop().await;
        }
    }
    // Startup at a pace the performance step cannot cover: refused before
    // any setter.
    let files = MemoryFiles::default();
    files.profiles(2);
    let rig = rig_at(files, 0);
    rig.port.state.lock().unwrap().latency = Some(std::time::Duration::from_millis(900));
    let refused = report(&rig, 1, Some(r"C:\Tools\Other.exe")).await;
    rig.port.state.lock().unwrap().latency = None;
    assert_eq!(refused["result"]["status"], 2, "{refused}");
    // The performance step refuses before accepting at this pace; the fan
    // step finds firmware Automatic already. Nothing is journaled.
    assert!(journal_ids(&rig).is_empty(), "{refused}");
    assert_eq!(writes(&rig), 0);
    assert!(!rig.storage.pending());
    // Nothing was submitted, so the next report still reconciles startup.
    let startup = report(&rig, 2, Some(r"C:\Tools\Other.exe")).await;
    assert_eq!(
        startup["result"],
        json!({"status":1,"message":format!("{RECORDED} {CONFIRMED} {AUTOMATIC}")})
    );
    assert_eq!(rig.port.state.lock().unwrap().selectors, [2; 2]);
    let renewed = report(&rig, 3, Some(r"C:\Tools\Other.exe")).await;
    assert_eq!(
        renewed["result"],
        json!({"status":0,"message":"No application profile is active."})
    );
    rig.stop().await;
}

/// Managed orders by the confirmed fans: with firmware fans already
/// Automatic, saved manual intent before the trigger does not put fans
/// first, and the rule's preset applies.
#[tokio::test]
async fn saved_manual_fans_with_automatic_firmware_fans_apply_the_rule_preset() {
    // (power profile edits, rule overrides, selector, adapter level, expected)
    for (power, overrides, selector, actual, expected) in [
        (
            json!({"battery":{"performance":3,"fans":1}}),
            json!({"battery":{"performance":4,"fans":0}}),
            3,
            0,
            6,
        ),
        (
            json!({"pluggedIn":{"performance":0,"fans":1},"usbC":{"performance":0,"fans":1}}),
            json!({"pluggedIn":{"performance":2,"fans":0},"usbC":{"performance":2,"fans":0}}),
            5,
            15,
            2,
        ),
    ] {
        let files = files_with_game(overrides);
        let mut document = files.document("profiles.json");
        for (key, edit) in power.as_object().unwrap() {
            for (member, value) in edit.as_object().unwrap() {
                document["profiles"][key][member] = value.clone();
            }
        }
        files.put("profiles.json", document);
        files.commit_configuration();
        let rig = rig_at(files, selector);
        rig.port.state.lock().unwrap().actual = actual;
        if actual == 0 {
            rig.metadata.store(1, Ordering::Release);
        }
        let reply = report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
        assert_eq!(
            reply["result"],
            json!({"status":1,"message":format!("{RECORDED} {CONFIRMED} {AUTOMATIC}")}),
            "{power}"
        );
        let written = {
            let s = rig.port.state.lock().unwrap();
            (s.selectors, s.fans)
        };
        assert_eq!(written, ([expected; 2], [0; 2]), "{power}");
        rig.stop().await;
    }
}

/// An override document changed outside the Service fences writes before
/// any submission: Blocked with the storage reason, nothing written, and the
/// Service keeps serving. With fans going first the fan step reports it.
#[tokio::test]
async fn an_override_document_changed_outside_the_service_blocks_before_any_write() {
    let empty = json!({"schemaVersion":10,"overrides":{"performance":null,"fans":null,"lighting":null,"customPerformance":null,"fanControl":null,"gpuClockTuning":null}});
    // Performance first.
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    rig.files.put("manual-overrides.json", empty.clone());
    let reply = report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(
        reply["result"],
        json!({"status":2,"message":format!("{RECORDED} {CHANGED}")})
    );
    assert_eq!(writes(&rig), 0);
    assert!(journal_ids(&rig).is_empty());
    assert!(rig.storage.pending());
    assert_eq!(rig.get().await["success"], true);
    rig.stop().await;
    // Fans first: the rule's Fixed fans are owned, and leaving the rule
    // returns them to Automatic.
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"fans":1}})), 0);
    assert_eq!(
        report(&rig, 1, Some(r"C:\Games\Game.exe")).await["result"]["status"],
        1
    );
    let before = writes(&rig);
    rig.files.put("manual-overrides.json", empty);
    let reply = report(&rig, 2, Some(r"C:\Tools\Other.exe")).await;
    assert_eq!(
        reply["result"],
        json!({"status":2,"message":format!("{RECORDED} {CHANGED}")})
    );
    assert_eq!(writes(&rig), before);
    assert_eq!(rig.get().await["success"], true);
    // The owned fan change stays fenced with the store: Stop cannot hand it
    // back without storage reconciliation.
    rig.origin.stop();
    assert!(rig.source.lock().await.drain().await.is_err());
}

/// An adapter the step's own reading leaves unclassified blocks the
/// reconciliation, said once although both steps refuse, and submits
/// nothing, so a resend after classification still reconciles.
#[tokio::test]
async fn an_unclassified_adapter_blocks_once_and_leaves_the_reconciliation_owed() {
    for (rule, message) in [
        (json!({"pluggedIn":{"performance":2}}), AUTOMATIC),
        (json!({"pluggedIn":{"fans":1}}), FIXED),
    ] {
        let rig = rig_at(files_with_game(rule.clone()), 0);
        {
            // 0x10 is an unmapped level: a completed reading that classifies
            // nothing.
            let mut s = rig.port.state.lock().unwrap();
            s.actual = 0x10;
            s.recommended = 15;
        }
        let blocked = report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
        assert_eq!(
            blocked["result"],
            json!({"status":2,"message":format!("{RECORDED} {UNCLASSIFIED_BLOCKED}")}),
            "{rule}"
        );
        assert_eq!(writes(&rig), 0);
        {
            let mut s = rig.port.state.lock().unwrap();
            s.actual = 15;
            s.recommended = 15;
        }
        let applied = report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
        assert_eq!(
            applied["result"],
            json!({"status":1,"message":format!("{RECORDED} {CONFIRMED} {message}")}),
            "{rule}"
        );
        rig.stop().await;
    }
}

/// A save that changes nothing a reconciliation applies is Stored: only the
/// other external class's override, or only battery protection.
#[tokio::test]
async fn a_save_that_changes_no_applied_thermal_intent_is_stored() {
    for edit in ["usbC", "battery"] {
        let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
        assert_eq!(
            report(&rig, 1, Some(r"C:\Games\Game.exe")).await["result"]["status"],
            1
        );
        let before = writes(&rig);
        let mut profiles = rig.files.document("profiles.json")["profiles"].clone();
        if edit == "usbC" {
            // The full adapter is connected; the limited one's override changes.
            profiles["applicationProfiles"][0]["overrides"]["usbC"] = json!({"performance":0});
        } else {
            profiles["pluggedIn"]["batteryProtectionEnabled"] = json!(false);
        }
        let saved = rig.request(update(2, &profiles)).await;
        assert_eq!(
            saved["result"],
            json!({"status":0,"message":STORED}),
            "{edit}"
        );
        assert_eq!(writes(&rig), before, "{edit}");
        // Nothing is left owed: the same path again renews the lease.
        let again = report(&rig, 3, Some(r"C:\Games\Game.exe")).await;
        assert_eq!(
            again["result"],
            json!({"status":0,"message":RENEWED}),
            "{edit}"
        );
        rig.stop().await;
    }
}

#[tokio::test]
async fn a_save_that_changes_the_active_application_intent_applies_it() {
    let files = MemoryFiles::default();
    files.profiles(1);
    let rig = Rig::new(files);
    report_game(&rig).await;
    assert_eq!(writes(&rig), 0);
    let profiles = with_game_performance(&rig.files, 0);
    let saved = rig.request(update(1, &profiles)).await;
    assert_eq!(saved["result"]["status"], 1, "{saved}");
    assert_eq!(
        saved["result"]["message"],
        format!("Profiles were stored. {CONFIRMED} {AUTOMATIC}")
    );
    assert_eq!(
        rig.get().await["status"]["confirmedState"]["performance"],
        0
    );
    // The same set again changes nothing.
    let after = writes(&rig);
    let again = rig.request(update(2, &profiles)).await;
    assert_eq!(again["result"], json!({"status":0,"message":STORED}));
    assert_eq!(writes(&rig), after);
    rig.stop().await;
}

#[tokio::test]
async fn a_save_without_an_active_application_or_under_an_override_applies_nothing() {
    // No report: managed's predicate needs an application active before it.
    let files = MemoryFiles::default();
    files.profiles(1);
    let rig = Rig::new(files);
    let saved = rig
        .request(update(1, &with_game_performance(&rig.files, 0)))
        .await;
    assert_eq!(saved["result"], json!({"status":0,"message":STORED}));
    assert_eq!(writes(&rig), 0);
    rig.stop().await;
    // A persistent performance override decides the preset whatever the
    // rule says, so editing the rule's preset is no change.
    let files = MemoryFiles::default();
    files.profiles(1);
    let rig = Rig::new(files);
    assert_eq!(rig.set(1, value(2, 1)).await["result"]["status"], 1);
    report_game(&rig).await;
    let after = writes(&rig);
    let saved = rig
        .request(update(2, &with_game_performance(&rig.files, 0)))
        .await;
    assert_eq!(saved["result"], json!({"status":0,"message":STORED}));
    assert_eq!(writes(&rig), after);
    rig.stop().await;
}

/// Managed `ToOperationResult`, `DeviceSubsystemOrder.ForTransition` and
/// `ReportApplicationContextAsync`, as the managed vector test recorded them.
#[test]
fn reconciliation_replies_match_the_managed_vectors() {
    let vectors: Value = serde_json::from_str(VECTORS).unwrap();
    let rows = vectors["toOperationResult"].as_array().unwrap();
    assert_eq!(rows.len(), 32);
    for row in rows {
        let flag = |name: &str| row[name].as_bool().unwrap();
        assert_eq!(
            u64::from(operation_status(
                flag("fullyApplied"),
                flag("intentStored"),
                flag("submissionPossible")
                    || flag("anyConfirmedWrites")
                    || flag("anyWritesAttempted"),
            )),
            row["status"].as_u64().unwrap(),
            "{row}"
        );
    }
    for row in vectors["forTransition"].as_array().unwrap() {
        assert_eq!(
            crate::installed::readiness::thermal_mutation::fans_first(
                row["initialManual"].as_bool().unwrap(),
                !row["desiredAutomatic"].as_bool().unwrap(),
            ),
            row["fansFirst"].as_bool().unwrap(),
            "{row}"
        );
    }
}

#[tokio::test]
async fn the_report_sequence_matches_the_managed_vectors() {
    let vectors: Value = serde_json::from_str(VECTORS).unwrap();
    let files = MemoryFiles::default();
    files.profiles(1);
    let rig = Rig::new(files);
    for step in vectors["report"].as_array().unwrap() {
        let expected = &step["expect"];
        if step["controlsSession"] == false {
            // The pipe refuses such a caller before dispatch; the runtime's
            // own check, behind it, replies as managed does.
            assert_eq!(expected["status"], 2);
            assert_eq!(expected["message"], APPLICATION_SESSION_REQUIRED);
            continue;
        }
        let reply = report(
            &rig,
            step["sequence"].as_i64().unwrap(),
            step["path"].as_str(),
        )
        .await;
        let result = &reply["result"];
        let message = result["message"].as_str().unwrap();
        // A transition's status is the device's result: managed's scripted
        // device leaves battery and lighting unapplied, which native does
        // not compose, so only the trigger and its prefix are compared.
        match expected["transition"].as_bool() {
            Some(true) => assert!(
                message.starts_with(&format!("{RECORDED} ")),
                "{step}: {reply}"
            ),
            _ => {
                assert_eq!(result["status"], expected["status"], "{step}: {reply}");
                assert_eq!(message, expected["message"], "{step}: {reply}");
            }
        }
    }
    rig.stop().await;
}

#[tokio::test]
async fn comparing_a_save_at_either_external_class_retires_no_temporary() {
    // On AC the save compares both external classes; reading the class the
    // adapter did not report must not retire a temporary saved on the other.
    let files = MemoryFiles::default();
    files.profiles(1);
    let rig = Rig::new(files);
    assert_eq!(rig.set(1, value(2, 0)).await["result"]["status"], 1);
    let temporary = serde_json::to_value(rig.storage.current_override(0).unwrap()).unwrap();
    assert!(!temporary.is_null());
    report_game(&rig).await;
    let saved = rig
        .request(update(2, &with_game_performance(&rig.files, 0)))
        .await;
    // The temporary decides the preset, so the edit is no change.
    assert_eq!(saved["result"], json!({"status":0,"message":STORED}));
    assert_eq!(
        serde_json::to_value(rig.storage.current_override(0).unwrap()).unwrap(),
        temporary
    );
    rig.stop().await;
}

const RENEWED: &str = "The active application profile lease was renewed.";
const INACTIVE: &str = "No application profile is active.";

/// Managed orders by the fans firmware reports, not by who set them: Fixed
/// fans no OpenBlade write established still go first when the rule keeps
/// Automatic fans, and the fan step carries the rule's preset.
#[tokio::test]
async fn firmware_manual_fans_the_service_does_not_own_go_first() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    rig.port.state.lock().unwrap().fans = [1; 2];
    assert_manual_fans_observed(&rig).await;
    assert!(!rig.storage.outstanding_fan());
    let sent = rig.port.state.lock().unwrap().sent.len();
    let reply = report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(
        reply["result"],
        json!({"status":1,"message":format!("{RECORDED} Fans returned to Automatic with Performance performance, confirmed by independent paired readback. {CONFIRMED}")})
    );
    let pair = mode_setters(&rig, sent);
    assert!(pair.iter().all(|setter| setter[3] == 0), "{pair:?}");
    rig.stop().await;
}

/// On AC the limited adapter's class reconciles from its own reading: the
/// rule's USB-C preset applies.
#[tokio::test]
async fn a_limited_adapter_reconciles_the_rules_usb_c_intent() {
    let rig = rig_at(files_with_game(json!({"usbC":{"performance":0}})), 2);
    rig.port.state.lock().unwrap().actual = 10;
    let reply = report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(
        reply["result"],
        json!({"status":1,"message":format!("{RECORDED} {CONFIRMED} {AUTOMATIC}")})
    );
    assert_eq!(
        rig.get().await["status"]["confirmedState"]["performance"],
        0
    );
    rig.stop().await;
}

/// A step that submitted and then failed its readback is Failed, stops the
/// reconciliation before the fan step, and settles the trigger: the next
/// report renews the lease and never replays the submission.
#[tokio::test]
async fn a_submitted_step_that_fails_readback_is_failed_and_not_replayed() {
    let rig = rig_at(
        files_with_game(serde_json::from_str(PRESET_AND_FIXED).unwrap()),
        0,
    );
    rig.port.state.lock().unwrap().mismatch = true;
    let failed = report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(
        failed["result"],
        json!({"status":3,"message":format!("{RECORDED} A thermal command may have been submitted. Read-only reconciliation is required and no mutation will be replayed.")})
    );
    let after = writes(&rig);
    assert!(after > 0);
    rig.port.state.lock().unwrap().mismatch = false;
    let again = report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(again["result"], json!({"status":0,"message":RENEWED}));
    assert_eq!(writes(&rig), after);
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
}

/// A save whose reconciliation is refused before any submission is Stored
/// with the refusal, and leaves the reconciliation owed to the next report.
#[tokio::test]
async fn a_save_refused_before_submission_is_stored_and_stays_owed() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    assert_eq!(
        report(&rig, 1, Some(r"C:\Games\Game.exe")).await["result"]["status"],
        1
    );
    let before = writes(&rig);
    {
        let mut s = rig.port.state.lock().unwrap();
        s.actual = 0x10;
        s.recommended = 15;
    }
    let saved = rig
        .request(update(2, &with_game_performance(&rig.files, 0)))
        .await;
    assert_eq!(
        saved["result"],
        json!({"status":0,"message":format!("Profiles were stored. {UNCLASSIFIED_BLOCKED}")})
    );
    assert_eq!(writes(&rig), before);
    rig.port.state.lock().unwrap().actual = 15;
    let applied = report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(
        applied["result"],
        json!({"status":1,"message":format!("{RECORDED} {CONFIRMED} {AUTOMATIC}")})
    );
    assert_eq!(
        rig.get().await["status"]["confirmedState"]["performance"],
        0
    );
    rig.stop().await;
}

/// A save that moves the active rule off the reported path ends the active
/// pair (`ProfileAutomationState.UpdateProfiles`): the same path reported
/// again is no change.
#[tokio::test]
async fn a_save_that_ends_the_active_rule_updates_the_reported_pair() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    assert_eq!(
        report(&rig, 1, Some(r"C:\Games\Game.exe")).await["result"]["status"],
        1
    );
    let mut profiles = rig.files.document("profiles.json")["profiles"].clone();
    profiles["applicationProfiles"][0]["executablePath"] = json!(r"C:\Games\Other.exe");
    let saved = rig.request(update(2, &profiles)).await;
    assert_eq!(saved["result"]["status"], 1, "{saved}");
    let after = writes(&rig);
    let again = report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(again["result"], json!({"status":0,"message":INACTIVE}));
    assert_eq!(writes(&rig), after);
    rig.stop().await;
}

/// Managed `ControlCoordinator` replies for a reconciliation excluded by
/// drift outside OpenBlade.
const PAUSED: &str = "Automatic performance profile application is paused because the setting changed outside OpenBlade. Automatic fans profile application is paused because the setting changed outside OpenBlade.";

/// A reconciliation under a persistent performance override publishes the
/// override, as the status reports it, also when drift outside OpenBlade
/// pauses it.
#[tokio::test]
async fn a_reconciliation_under_an_override_reports_that_override() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 2);
    // The session's startup reconciliation, which applies over drift,
    // settles first.
    assert_eq!(
        report(&rig, 1, Some(r"C:\Tools\Other.exe")).await["result"]["status"],
        1
    );
    assert_eq!(rig.set(1, value(0, 1)).await["result"]["status"], 1);
    rig.port.state.lock().unwrap().selectors = [2; 2];
    let before = writes(&rig);
    let paused = report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(
        paused["result"],
        json!({"status":2,"message":format!("{RECORDED} {PAUSED}")})
    );
    assert_eq!(writes(&rig), before);
    let status = rig.get().await;
    assert_eq!(status["status"]["externalChanges"], json!([0]), "{status}");
    assert_eq!(status["status"]["performanceOverride"]["value"], 0);
    assert_eq!(status["status"]["desiredState"]["performance"]["source"], 4);
    rig.stop().await;
}

/// A thermal observation (`observeState`), after the readiness probe
/// production performs first, that confirms manual fans.
async fn assert_manual_fans_observed(rig: &Rig) {
    assert_eq!(rig.get().await["success"], true);
    let observed = rig
        .request(json!({"operation":"observeState","version":91,"requestId":"dddddddd-bbbb-cccc-dddd-000000000077","includeLiveReadings":false,"requestedLiveReadings":0,"effectiveLiveReadings":0}))
        .await;
    let fans = &observed["status"]["confirmedState"]["fans"];
    assert!(fans.as_i64().is_some_and(|fans| fans != 0), "{observed}");
}

/// A reconciliation validates no saved combination, as managed
/// `ApplyFullTransitionAsync` does not: a profile save that pairs a new
/// plugged-in preset with the saved ManualCurve fan override is applied
/// through the step's own gates instead of being rejected.
#[tokio::test]
async fn a_reconciliation_does_not_reject_a_saved_combination() {
    let files = MemoryFiles::default();
    files.profiles(2);
    let rig = rig_at(files, 0);
    let curve = rig.request(fan_request(1, Some(2), 3700)).await;
    // Stored; this backend has no native ManualCurve recipe to apply it.
    assert_eq!(curve["result"]["status"], 2, "{curve}");
    assert_eq!(
        rig.files.document("manual-overrides.json")["overrides"]["fans"],
        2
    );
    let mut profiles = rig.files.document("profiles.json")["profiles"].clone();
    profiles["pluggedIn"]["performance"] = json!(3);
    let saved = rig.request(update(2, &profiles)).await;
    assert_eq!(saved["result"], json!({"status":0,"message":STORED}));
    let reply = report(&rig, 1, Some(r"C:\Tools\Other.exe")).await;
    // The preset applies; the fan step finds no ManualCurve recipe, after a
    // submission, so the trigger is Failed.
    assert_eq!(
        reply["result"],
        json!({"status":3,"message":format!("{RECORDED} {CONFIRMED} This source or observed mode needs a native recipe that is not yet implemented, or admission/transaction capacity did not complete. No applied target is claimed.")})
    );
    assert!(writes(&rig) > 0);
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
}

/// Frames the fixture has received.
fn sent(rig: &Rig) -> usize {
    rig.port.state.lock().unwrap().sent.len()
}
const NO_RECIPE: &str = "This source or observed mode needs a native recipe that is not yet implemented, or admission/transaction capacity did not complete. No applied target is claimed.";
const SUBMITTED: &str = "A thermal command may have been submitted. Read-only reconciliation is required and no mutation will be replayed.";
const UNFINISHED: &str = "The reconciliation did not finish within its budget.";
const FAN_DEADLINE: &str = "The remaining request time cannot cover this recipe at the measured device pace; no fan setter was submitted.";

/// A reconciliation that reads firmware settles the pair's debt, unless a
/// step was refused for a cause that may clear by itself before any
/// submission; that debt is retried on at most two later reports. A lease
/// renewal arrives every 5 s, so an unbounded debt would read firmware, and
/// draw transaction pairs, on each one. Explicit requests still have pairs
/// afterwards.
#[tokio::test]
async fn an_owed_reconciliation_reads_firmware_a_bounded_number_of_times() {
    let gpu = MemoryFiles::default();
    gpu.profiles_gpu(2);
    let curve = MemoryFiles::default();
    curve.profiles(2);
    let battery = MemoryFiles::default();
    battery.profiles(2);
    let mut document = battery.document("profiles.json");
    document["profiles"]["battery"]["performance"] = json!(3);
    document["profiles"]["battery"]["fans"] = json!(1);
    battery.put("profiles.json", document);
    battery.commit_configuration();
    let unclassified = files_with_game(json!({"pluggedIn":{"performance":2}}));
    // (case, files, selector, adapter level, reconciliations that read
    // firmware, then the adapter level and explicit request that apply)
    for (case, files, selector, actual, reads, after, explicit) in [
        // The plugged-in profile's GPU tuning: refused after the reading.
        ("gpu", gpu, 2, 15, 1, 10, value(0, 1)),
        // A saved ManualCurve fan override, which has no native recipe.
        ("curve", curve, 2, 15, 1, 15, value(0, 1)),
        // Fixed fans in the battery profile: no native plan on battery.
        ("battery", battery, 3, 0, 1, 0, value(4, 1)),
        // An adapter that stays unclassified: transient, so retried twice.
        ("unclassified", unclassified, 0, 0x10, 3, 15, value(0, 1)),
    ] {
        let rig = rig_at(files, selector);
        {
            let mut s = rig.port.state.lock().unwrap();
            s.actual = actual;
            s.recommended = 15;
        }
        if case == "battery" {
            rig.metadata.store(1, Ordering::Release);
        }
        if case == "curve" {
            let curve = rig.request(fan_request(1, Some(2), 3700)).await;
            assert_eq!(curve["result"]["status"], 2, "{curve}");
        }
        let mut reached = 0;
        for sequence in 1..=45 {
            let before = sent(&rig);
            report(&rig, sequence, Some(r"C:\Tools\Other.exe")).await;
            reached += usize::from(sent(&rig) > before);
        }
        assert_eq!(reached, reads, "{case}");
        rig.port.state.lock().unwrap().actual = after;
        let applied = rig.set(9, explicit).await;
        assert_eq!(
            applied["result"],
            json!({"status":1,"message":CONFIRMED}),
            "{case}"
        );
        rig.origin.stop();
        let _ = rig.source.lock().await.drain().await;
    }
}

/// A rule that saved intent alone refuses at every class Windows power
/// admits reads no firmware and reserves no transaction pair: GPU tuning has
/// no native writer and Fixed fans have no plan on battery. The refusal
/// settles the debt.
#[tokio::test]
async fn a_rule_refused_by_saved_intent_alone_reads_no_firmware() {
    let files = MemoryFiles::default();
    files.profiles(2);
    let mut document = files.document("profiles.json");
    document["profiles"]["battery"]["performance"] = json!(3);
    document["profiles"]["battery"]["fans"] = json!(1);
    document["profiles"]["battery"]["gpuClockTuning"] =
        json!({"enabled":true,"offsets":{"coreMegahertz":10,"memoryMegahertz":20}});
    files.put("profiles.json", document);
    files.commit_configuration();
    let rig = rig_at(files, 3);
    rig.port.state.lock().unwrap().actual = 0;
    rig.metadata.store(1, Ordering::Release);
    let before = {
        let s = rig.port.state.lock().unwrap();
        (s.opened, s.sent.len(), s.power_reads)
    };
    let refused = report(&rig, 1, Some(r"C:\Tools\Other.exe")).await;
    assert_eq!(
        refused["result"],
        json!({"status":2,"message":format!("{RECORDED} GPU clock handling is incomplete; no thermal setter was submitted. {NO_RECIPE}")})
    );
    let after = {
        let s = rig.port.state.lock().unwrap();
        (s.opened, s.sent.len(), s.power_reads)
    };
    // No job opened, so no pair was reserved.
    assert_eq!(after, before);
    let again = report(&rig, 2, Some(r"C:\Tools\Other.exe")).await;
    assert_eq!(again["result"], json!({"status":0,"message":INACTIVE}));
    rig.stop().await;
}

/// A save whose reconciliation finds the adapter at a class the save did not
/// change applies nothing there, and leaves a debt owed from before it as it
/// was: here the first report's, on unknown Windows power, which the next
/// report applies.
#[tokio::test]
async fn a_save_at_another_class_leaves_an_earlier_debt_owed() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    rig.port.state.lock().unwrap().windows_on_battery = Some(None);
    let first = report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(first["result"], json!({"status":0,"message":RECORDED}));
    rig.port.state.lock().unwrap().windows_on_battery = None;
    // The full adapter is connected; only the limited one's override changes.
    let mut profiles = rig.files.document("profiles.json")["profiles"].clone();
    profiles["applicationProfiles"][0]["overrides"]["usbC"] = json!({"performance":0});
    let saved = rig.request(update(2, &profiles)).await;
    assert_eq!(saved["result"], json!({"status":0,"message":STORED}));
    assert_eq!(writes(&rig), 0);
    let next = report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(
        next["result"],
        json!({"status":1,"message":format!("{RECORDED} {CONFIRMED} {AUTOMATIC}")})
    );
    assert_eq!(rig.port.state.lock().unwrap().selectors, [2; 2]);
    rig.stop().await;
}

/// Fans this Service set and still owns go first when the rule that set them
/// ends, even once firmware has returned them to Automatic outside OpenBlade
/// and an observation saw it: performance first would be refused while the
/// fan change is owned (`FanHandbackPending`). The fan step closes the
/// ownership without a setter, and the power profile's Balanced applies.
#[tokio::test]
async fn owned_fans_go_first_after_firmware_returned_them_to_automatic() {
    let rig = rig_at(
        files_with_game(serde_json::from_str(PRESET_AND_FIXED).unwrap()),
        0,
    );
    let entered = report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(entered["result"]["status"], 1, "{entered}");
    assert!(rig.storage.outstanding_fan());
    rig.port.state.lock().unwrap().fans = [0; 2];
    assert_eq!(rig.get().await["success"], true);
    let observed = rig
        .request(json!({"operation":"observeState","version":91,"requestId":"dddddddd-bbbb-cccc-dddd-000000000078","includeLiveReadings":false,"requestedLiveReadings":0,"effectiveLiveReadings":0}))
        .await;
    assert_eq!(
        observed["status"]["confirmedState"]["fans"], 0,
        "{observed}"
    );
    let left = report(&rig, 2, Some(r"C:\Tools\Other.exe")).await;
    assert_eq!(
        left["result"],
        json!({"status":1,"message":format!("{RECORDED} {AUTOMATIC} {CONFIRMED}")})
    );
    assert_eq!(rig.port.state.lock().unwrap().selectors, [0; 2]);
    assert!(!rig.storage.outstanding_fan());
    rig.stop().await;
}

/// The adapter turns limited between the steps of a save that changed only
/// the full adapter's class: the performance step applies that class, and
/// the fan step finds a class the save did not change, which needs nothing,
/// so the save is Applied.
#[tokio::test]
async fn a_class_change_between_the_steps_needs_nothing_of_the_unchanged_class() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":1}})), 0);
    assert_eq!(
        report(&rig, 1, Some(r"C:\Games\Game.exe")).await["result"]["status"],
        1
    );
    {
        // The performance step reads the adapter three times; the fan step's
        // first reading finds the limited adapter.
        let mut s = rig.port.state.lock().unwrap();
        s.change_power = Some((s.power_reads + 4, 10));
    }
    let saved = rig
        .request(update(2, &with_game_performance(&rig.files, 2)))
        .await;
    assert_eq!(
        saved["result"],
        json!({"status":1,"message":format!("Profiles were stored. {CONFIRMED}")})
    );
    assert_eq!(rig.port.state.lock().unwrap().selectors, [2; 2]);
    rig.stop().await;
}

/// A reconciliation step publishes the override layer it applied over, read
/// again after its hardware result, so an override document changed outside
/// the Service during the step fences the store, though the fan step that
/// would have checked it finds a class the save did not change.
#[tokio::test]
async fn an_override_document_changed_during_the_performance_step_fences_the_store() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":1}})), 0);
    assert_eq!(
        report(&rig, 1, Some(r"C:\Games\Game.exe")).await["result"]["status"],
        1
    );
    let files = rig.files.clone();
    let empty = json!({"schemaVersion":10,"overrides":{"performance":null,"fans":null,"lighting":null,"customPerformance":null,"fanControl":null,"gpuClockTuning":null}});
    {
        let mut s = rig.port.state.lock().unwrap();
        s.change_power = Some((s.power_reads + 4, 10));
        let second = s.writes + 2;
        s.before_write = Some(Arc::new(move |number| {
            if number == second {
                files.put("manual-overrides.json", empty.clone());
            }
        }));
    }
    let saved = rig
        .request(update(2, &with_game_performance(&rig.files, 2)))
        .await;
    rig.port.state.lock().unwrap().before_write = None;
    assert_eq!(
        saved["result"],
        json!({"status":0,"message":format!("Profiles were stored. {CHANGED}")})
    );
    assert!(rig.storage.pending());
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
}

/// At a device pace where the preset fits the budget and the fan step that
/// follows does not, the fan step is refused before any setter, which may
/// clear by itself. The report is Failed for its confirmed preset, as
/// managed's `AnyConfirmedWrites` is, and stays owed: the next report finds
/// the preset confirmed and applies the Fixed fans.
#[tokio::test]
async fn a_fan_step_refused_at_its_pace_is_retried_on_the_next_report() {
    let rig = rig_at(
        files_with_game(serde_json::from_str(PRESET_AND_FIXED).unwrap()),
        0,
    );
    rig.port.state.lock().unwrap().latency = Some(std::time::Duration::from_millis(250));
    let paced = report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    rig.port.state.lock().unwrap().latency = None;
    assert_eq!(
        paced["result"],
        json!({"status":3,"message":format!("{RECORDED} {CONFIRMED} {FAN_DEADLINE}")})
    );
    assert_eq!(rig.port.state.lock().unwrap().fans, [0; 2]);
    assert!(!rig.storage.pending());
    let next = report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(
        next["result"],
        json!({"status":1,"message":format!("{RECORDED} {CONFIRMED} {FIXED}")})
    );
    let device = {
        let s = rig.port.state.lock().unwrap();
        (s.selectors, s.fans)
    };
    assert_eq!(device, ([2; 2], [1; 2]));
    rig.stop().await;
}

/// A reconciliation whose step waits on a transfer that does not complete
/// answers `RECONCILE_MARGIN` after its deadline and releases the gate. The
/// step is dropped as an explicit request is at its own deadline: the Reader
/// keeps its worker, so a later mutation first waits for that worker to
/// retire, and the journal row past its intent stays unsettled, so a later
/// thermal request is refused before any submission. A report and a save
/// that committed both reply Failed. Real time: a held transfer keeps the
/// real clock.
#[tokio::test]
async fn a_reconciliation_waiting_on_a_transfer_answers_at_its_bound() {
    keep_real_clock();
    use std::time::Instant;
    let reporting = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    let saving = rig_at(files_with_game(json!({"pluggedIn":{"performance":1}})), 0);
    // The rule's preset is already confirmed, so this report writes nothing.
    assert_eq!(
        report(&saving, 1, Some(r"C:\Games\Game.exe")).await["result"]["status"],
        1
    );
    let release = Arc::new(AtomicBool::new(false));
    for rig in [&reporting, &saving] {
        let mut s = rig.port.state.lock().unwrap();
        s.held_write = Some((s.writes + 1, release.clone()));
    }
    let started = Instant::now();
    let (reported, saved) = tokio::join!(
        report(&reporting, 1, Some(r"C:\Games\Game.exe")),
        saving.request(update(2, &with_game_performance(&saving.files, 2))),
    );
    let elapsed = started.elapsed();
    // The deadline is taken after `started`, so the replies cannot come
    // before the bound; the transfer is never released, so an unbounded wait
    // would never reply. The upper limit leaves room for a loaded host.
    let bound = crate::service_frame::RECONCILE_BUDGET + crate::service_frame::RECONCILE_MARGIN;
    assert!(elapsed >= bound && elapsed < bound * 2, "{elapsed:?}");
    assert_eq!(
        reported["result"],
        json!({"status":3,"message":format!("{RECORDED} {UNFINISHED} {SUBMITTED}")})
    );
    assert_eq!(
        saved["result"],
        json!({"status":3,"message":format!("Profiles were stored. {UNFINISHED} {SUBMITTED}")})
    );
    assert_eq!(
        saving.files.document("profiles.json")["profiles"]["applicationProfiles"][0]["overrides"]["pluggedIn"]
            ["performance"],
        2
    );
    for rig in [&reporting, &saving] {
        // The gate is free while the transfer is still held: status replies,
        // and a thermal request is refused by the unsettled row.
        assert_eq!(rig.get().await["success"], true);
        let explicit = rig.set(9, value(0, 1)).await;
        assert_eq!(explicit["result"], json!({"status":2,"message":SUBMITTED}),);
        assert_eq!(writes(rig), 1);
        assert!(rig.storage.pending());
        // Settled: the next report submits nothing. Its reply is not
        // compared: the 15 s lease the first report took can lapse on a
        // loaded host after the 12 s bound, and the report then selects the
        // pair afresh and is refused by the unsettled row instead.
        assert_eq!(rig.runtime.owed_attempts(SID, 7), None);
        let next = report(rig, 3, Some(r"C:\Games\Game.exe")).await;
        assert_eq!(writes(rig), 1, "{next}");
        assert!(
            matches!(next["result"]["status"].as_u64(), Some(0 | 2)),
            "{next}"
        );
    }
    release.store(true, std::sync::atomic::Ordering::Release);
    for rig in [&reporting, &saving] {
        rig.origin.stop();
        let _ = rig.source.lock().await.drain().await;
    }
}

/// A debt a refused save left at its own class survives a later save whose
/// reading finds another class: that reconciliation applied nothing of the
/// earlier class's intent. The next report at the earlier class applies it.
#[tokio::test]
async fn a_save_keeps_an_earlier_debt_at_a_class_its_reading_did_not_find() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    assert_eq!(
        report(&rig, 1, Some(r"C:\Games\Game.exe")).await["result"]["status"],
        1
    );
    // The full adapter's class changes while the adapter is unclassified:
    // refused transiently, so that class stays owed.
    rig.port.state.lock().unwrap().actual = 0x10;
    let refused = rig
        .request(update(2, &with_game_performance(&rig.files, 0)))
        .await;
    assert_eq!(
        refused["result"],
        json!({"status":0,"message":format!("Profiles were stored. {UNCLASSIFIED_BLOCKED}")})
    );
    // The limited adapter's class changes and its reading finds that class.
    rig.port.state.lock().unwrap().actual = 10;
    let mut profiles = rig.files.document("profiles.json")["profiles"].clone();
    profiles["applicationProfiles"][0]["overrides"]["usbC"] = json!({"performance":2});
    let limited = rig.request(update(3, &profiles)).await;
    assert_eq!(
        limited["result"],
        json!({"status":1,"message":format!("Profiles were stored. {CONFIRMED} {AUTOMATIC}")})
    );
    rig.port.state.lock().unwrap().actual = 15;
    let full = report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(
        full["result"],
        json!({"status":1,"message":format!("{RECORDED} {CONFIRMED} {AUTOMATIC}")})
    );
    assert_eq!(
        rig.get().await["status"]["confirmedState"]["performance"],
        0
    );
    rig.stop().await;
}

/// A debt owed at fewer classes than Windows power admits runs no
/// saved-intent check before its steps (`refused_by_saved_intent`); the
/// steps' own checks still find an override document changed outside the
/// Service and fence the store before any write.
#[tokio::test]
async fn a_step_of_a_partial_debt_checks_the_override_document_before_any_write() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":1}})), 0);
    assert_eq!(
        report(&rig, 1, Some(r"C:\Games\Game.exe")).await["result"]["status"],
        1
    );
    // Refused transiently, so the full adapter's class alone stays owed.
    rig.port.state.lock().unwrap().actual = 0x10;
    let saved = rig
        .request(update(2, &with_game_performance(&rig.files, 2)))
        .await;
    assert_eq!(
        saved["result"],
        json!({"status":0,"message":format!("Profiles were stored. {UNCLASSIFIED_BLOCKED}")})
    );
    rig.port.state.lock().unwrap().actual = 15;
    let before = writes(&rig);
    rig.files.put(
        "manual-overrides.json",
        json!({"schemaVersion":10,"overrides":{"performance":null,"fans":null,"lighting":null,"customPerformance":null,"fanControl":null,"gpuClockTuning":null}}),
    );
    let refused = report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(
        refused["result"],
        json!({"status":2,"message":format!("{RENEWED} {CHANGED}")})
    );
    assert_eq!(writes(&rig), before);
    assert!(rig.storage.pending());
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
}

/// A limited adapter at a classified reading with a fixture recommendation.
fn limited_adapter(rig: &Rig) {
    let mut s = rig.port.state.lock().unwrap();
    s.actual = 10;
    s.recommended = 15;
}

/// The performance pre-check reads each class in its own
/// `Storage::unexpired` scope. A temporary Balanced saved on the limited
/// adapter is still selected there after the full adapter's class, read
/// first, would have expired it. Both external profiles select no
/// destination, so a shared scope refused the step as having none.
#[tokio::test]
async fn a_temporary_preset_on_the_limited_adapter_survives_the_performance_pre_check() {
    let files = MemoryFiles::default();
    files.profiles_with(6, Some(6));
    let rig = rig_at(files, 2);
    limited_adapter(&rig);
    let set = rig.set(1, value(1, 0)).await;
    assert_eq!(set["result"]["status"], 1, "{set}");
    // Firmware moved before a restart, which forgets drift, so the
    // reconciliation has work.
    rig.port.state.lock().unwrap().selectors = [2; 2];
    rig.runtime.restart_drift();
    let reply = report(&rig, 1, Some(r"C:\Tools\Other.exe")).await;
    assert_eq!(
        reply["result"],
        json!({"status":1,"message":format!("{RECORDED} {CONFIRMED} {AUTOMATIC}")})
    );
    assert_eq!(rig.port.state.lock().unwrap().selectors, [0; 2]);
    rig.stop().await;
}

/// The fan pre-check reads each class in its own scope too: a temporary
/// Fixed override saved on the limited adapter over ManualCurve fans in both
/// external profiles is the desired fan intent there, not a mode with no
/// recipe.
#[tokio::test]
async fn a_temporary_fan_override_on_the_limited_adapter_survives_the_fan_pre_check() {
    let files = files_with_game(json!({"pluggedIn":{"performance":2}}));
    let mut document = files.document("profiles.json");
    document["profiles"]["usbC"]["fans"] = json!(2);
    document["profiles"]["pluggedIn"]["fans"] = json!(2);
    files.put("profiles.json", document);
    files.commit_configuration();
    let rig = rig_at(files, 0);
    limited_adapter(&rig);
    let mut temporary = fan_request(2, Some(1), 3700);
    temporary["value"]["lifetime"] = json!(0);
    let fixed = rig.request(temporary).await;
    assert_eq!(fixed["result"]["status"], 1, "{fixed}");
    let reply = report(&rig, 1, Some(r"C:\Tools\Other.exe")).await;
    // The fan step applies the temporary Fixed fans; the preset waits for
    // Automatic fans, so the reply is Failed for the fan submission.
    assert_eq!(
        reply["result"],
        json!({"status":3,"message":format!("{RECORDED} Performance intent was stored and will be applied when fans return to Automatic. {FIXED}")})
    );
    assert_eq!(rig.port.state.lock().unwrap().fans, [1; 2]);
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
}

/// A fan step whose fresh admission is refused after its Prepared row and
/// before any setter journals the row as not submitted. That may clear by
/// itself, so the debt stays owed and the next report applies the Fixed fans.
#[tokio::test]
async fn a_fan_step_that_loses_admission_after_its_prepared_row_is_retried() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"fans":1}})), 0);
    let custody = Arc::downgrade(&rig.custody);
    // The third adapter reading is the fan step's check before its first
    // setter, after its Prepared row and before the setter's intent row.
    rig.port.state.lock().unwrap().on_power = Some(Arc::new(move |read| {
        if read == 3 {
            custody
                .upgrade()
                .unwrap()
                .fresh_refuse
                .store(true, Ordering::Release);
        }
    }));
    let refused = report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    rig.port.state.lock().unwrap().on_power = None;
    rig.custody.fresh_refuse.store(false, Ordering::Release);
    assert_eq!(
        refused["result"],
        json!({"status":2,"message":format!("{RECORDED} {CONFIRMED} {NO_RECIPE}")})
    );
    assert_eq!(writes(&rig), 0);
    assert!(!rig.storage.pending());
    let next = report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(
        next["result"],
        json!({"status":1,"message":format!("{RECORDED} {CONFIRMED} {FIXED}")})
    );
    assert_eq!(rig.port.state.lock().unwrap().fans, [1; 2]);
    rig.stop().await;
}

/// An override document changed outside the Service while the performance
/// step reads its baseline, after the trigger's checks and the saved-intent
/// pre-check, is found by the step's own check before any setter.
#[tokio::test]
async fn an_override_document_changed_during_the_baseline_reading_fences_before_any_write() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    let files = rig.files.clone();
    rig.port.state.lock().unwrap().on_power = Some(Arc::new(move |read| {
        if read == 1 {
            files.put(
                "manual-overrides.json",
                json!({"schemaVersion":10,"overrides":{"performance":null,"fans":null,"lighting":null,"customPerformance":null,"fanControl":null,"gpuClockTuning":null}}),
            );
        }
    }));
    let refused = report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    rig.port.state.lock().unwrap().on_power = None;
    assert_eq!(
        refused["result"],
        json!({"status":2,"message":format!("{RECORDED} {CHANGED}")})
    );
    assert_eq!(writes(&rig), 0);
    assert!(rig.storage.pending());
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
}

/// A reconciliation that hands manual fans back first decides at its fan
/// step: performance changed outside OpenBlade excludes it, so the owned
/// Fixed fans are not handed back and nothing is written.
#[tokio::test]
async fn a_fans_first_reconciliation_is_excluded_at_its_fan_step() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"fans":1}})), 0);
    let applied = report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(applied["result"]["status"], 1, "{applied}");
    assert_eq!(rig.port.state.lock().unwrap().fans, [1; 2]);
    // Silent, with the Fixed fans OpenBlade owns.
    rig.port.state.lock().unwrap().selectors = [5; 2];
    let before = writes(&rig);
    let frames = (sent(&rig), rig.port.state.lock().unwrap().power_reads);
    let paused = report(&rig, 2, Some(r"C:\Tools\Other.exe")).await;
    assert_eq!(paused["result"]["status"], 2, "{paused}");
    assert_eq!(writes(&rig), before);
    // The excluded fan step's reading and power read only: the performance
    // step after it does not run.
    assert_eq!(
        (sent(&rig), rig.port.state.lock().unwrap().power_reads),
        (frames.0 + 3, frames.1 + 1)
    );
    assert_eq!(rig.port.state.lock().unwrap().fans, [1; 2]);
    assert_eq!(rig.get().await["status"]["externalChanges"], json!([0]));
    // Firmware changed outside OpenBlade, so the drain's fan handback
    // cannot confirm it and retains its evidence.
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
}

/// An explicit fan request's confirmed fans are OpenBlade's own: the next
/// reconciliation over performance changed outside OpenBlade is paused for
/// that change alone, where the session's startup reconciliation would
/// apply over it.
#[tokio::test]
async fn an_explicit_fan_write_is_own_state_of_the_next_reconciliation() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    assert_eq!(
        report(&rig, 1, Some(r"C:\Tools\Other.exe")).await["result"]["status"],
        1
    );
    let fixed = rig.request(fan_request(1, Some(1), 3700)).await;
    assert_eq!(fixed["result"]["status"], 1, "{fixed}");
    // Silent, with the Fixed fans.
    rig.port.state.lock().unwrap().selectors = [5; 2];
    let before = writes(&rig);
    let paused = report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(paused["result"]["status"], 2, "{paused}");
    assert_eq!(writes(&rig), before);
    assert_eq!(rig.get().await["status"]["externalChanges"], json!([0]));
    // Firmware changed outside OpenBlade, so the drain's fan handback
    // cannot confirm it and retains its evidence.
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
}
/// `files_with_game` over a plugged-in power profile of `performance` and
/// `fans`.
fn files_with_profile(performance: i32, fans: i32, game: Value) -> MemoryFiles {
    let files = files_with_game(game);
    let mut document = files.document("profiles.json");
    document["profiles"]["pluggedIn"]["performance"] = json!(performance);
    document["profiles"]["pluggedIn"]["fans"] = json!(fans);
    files.put("profiles.json", document);
    files.commit_configuration();
    files
}
/// A fresh exact-device reading (observeState), then the published status.
async fn observed(rig: &Rig, n: u8) -> Value {
    let observed = rig
        .request(json!({"operation":"observeState","version":91,"requestId":format!("eeeeeeee-bbbb-cccc-dddd-{n:012}"),"includeLiveReadings":false,"requestedLiveReadings":0,"effectiveLiveReadings":0}))
        .await;
    assert_eq!(observed["success"], true, "{observed}");
    rig.get().await
}
fn device(rig: &Rig) -> ([u8; 2], [u8; 2]) {
    let s = rig.port.state.lock().unwrap();
    (s.selectors, s.fans)
}

/// Saved fan intent native cannot apply is not OpenBlade's own. A plugged-in
/// Silent + Fixed profile has no native fan recipe, so the fans stay
/// Automatic, which managed `ResolveExperimentalFanHardwareDesired` projects
/// the intent onto. The Game rule's Performance then applies with the Fixed
/// fans instead of reading the untouched Automatic fans as drift.
#[tokio::test]
async fn unapplied_fan_intent_is_not_own_state_and_the_next_rule_applies() {
    let rig = rig_at(
        files_with_profile(0, 1, json!({"pluggedIn":{"performance":2}})),
        0,
    );
    report(&rig, 1, Some(r"C:\Tools\Other.exe")).await;
    assert_eq!(device(&rig), ([5; 2], [0; 2]));
    let game = report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(
        game["result"],
        json!({"status":1,"message":format!("{RECORDED} {CONFIRMED} {FIXED}")})
    );
    assert_eq!(device(&rig), ([2; 2], [1; 2]));
    assert_eq!(
        observed(&rig, 1).await["status"]["externalChanges"],
        json!([])
    );
    rig.stop().await;
}

/// A stored ManualCurve has no native recipe either: the next rule's
/// performance applies over the Automatic fans it left.
#[tokio::test]
async fn a_stored_manual_curve_is_not_own_state() {
    let rig = rig_at(
        files_with_profile(1, 2, json!({"pluggedIn":{"performance":2}})),
        0,
    );
    report(&rig, 1, Some(r"C:\Tools\Other.exe")).await;
    assert_eq!(device(&rig), ([0; 2], [0; 2]));
    let before = writes(&rig);
    let game = report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    assert!(
        !game["result"]["message"]
            .as_str()
            .unwrap()
            .contains("changed outside OpenBlade"),
        "{game}"
    );
    assert!(writes(&rig) > before);
    assert_eq!(device(&rig).0, [2; 2]);
    assert_eq!(
        observed(&rig, 1).await["status"]["externalChanges"],
        json!([])
    );
    rig.stop().await;
}

/// An explicit performance request's readback makes only its destination
/// OpenBlade's own, not the saved fan intent it did not apply: the next
/// report applies the Fixed fans.
#[tokio::test]
async fn an_explicit_performance_readback_does_not_own_unapplied_fans() {
    let rig = rig_at(
        files_with_profile(0, 1, json!({"pluggedIn":{"performance":2}})),
        0,
    );
    report(&rig, 1, Some(r"C:\Tools\Other.exe")).await;
    // A temporary Balanced, which native can apply Fixed fans under.
    let set = rig.set(1, value(1, 0)).await;
    assert_eq!(set["result"]["status"], 1, "{set}");
    assert_eq!(device(&rig), ([0; 2], [0; 2]));
    let game = report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(game["result"]["status"], 1, "{game}");
    assert_eq!(device(&rig), ([0; 2], [1; 2]));
    rig.stop().await;
}

/// The inverse: manual fans set outside OpenBlade under an unapplied manual
/// fan intent are not OpenBlade's own, so the next rule's Automatic fans
/// are not written over them.
#[tokio::test]
async fn manual_fans_set_outside_openblade_are_not_taken_for_unapplied_intent() {
    let rig = rig_at(
        files_with_profile(0, 1, json!({"pluggedIn":{"performance":2,"fans":0}})),
        0,
    );
    report(&rig, 1, Some(r"C:\Tools\Other.exe")).await;
    assert_eq!(device(&rig), ([5; 2], [0; 2]));
    // Silent with manual fans, outside OpenBlade.
    rig.port.state.lock().unwrap().fans = [1; 2];
    let before = writes(&rig);
    let paused = report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(
        paused["result"],
        json!({"status":2,"message":format!("{RECORDED} {PAUSED}")})
    );
    assert_eq!(writes(&rig), before);
    assert_eq!(device(&rig), ([5; 2], [1; 2]));
    assert_eq!(rig.get().await["status"]["externalChanges"], json!([1]));
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
}

/// A destination confirmed inside a reconciliation that is then retried is
/// OpenBlade's own at its readback: the Game preset is confirmed, its fan
/// step is refused for pace, and leaving the rule applies the power profile
/// instead of reading that preset as drift.
#[tokio::test]
async fn a_destination_confirmed_in_a_retried_reconciliation_is_own_state() {
    let rig = rig_at(
        files_with_game(serde_json::from_str(PRESET_AND_FIXED).unwrap()),
        0,
    );
    assert_eq!(
        report(&rig, 1, Some(r"C:\Tools\Other.exe")).await["result"]["status"],
        1
    );
    rig.port.state.lock().unwrap().latency = Some(std::time::Duration::from_millis(250));
    let paced = report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    rig.port.state.lock().unwrap().latency = None;
    assert_eq!(
        paced["result"],
        json!({"status":3,"message":format!("{RECORDED} {CONFIRMED} {FAN_DEADLINE}")})
    );
    assert_eq!(device(&rig), ([2; 2], [0; 2]));
    let left = report(&rig, 3, Some(r"C:\Tools\Other.exe")).await;
    assert_eq!(
        left["result"],
        json!({"status":1,"message":format!("{RECORDED} {CONFIRMED} {AUTOMATIC}")})
    );
    assert_eq!(device(&rig), ([0; 2], [0; 2]));
    rig.stop().await;
}

/// A save over drift outside OpenBlade is paused as a report is: managed
/// `UpdateProfilesAsync` preserves observed external changes.
#[tokio::test]
async fn a_save_over_drift_is_paused() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    assert_eq!(
        report(&rig, 1, Some(r"C:\Games\Game.exe")).await["result"]["status"],
        1
    );
    // Silent, outside OpenBlade.
    rig.port.state.lock().unwrap().selectors = [5; 2];
    let before = writes(&rig);
    let saved = rig
        .request(update(2, &with_game_performance(&rig.files, 1)))
        .await;
    assert_eq!(
        saved["result"],
        json!({"status":0,"message":format!("Profiles were stored. {PAUSED}")})
    );
    assert_eq!(writes(&rig), before);
    assert_eq!(device(&rig).0, [5; 2]);
    rig.stop().await;
}

/// A fan readback that confirms OpenBlade's fan intent without a write is
/// OpenBlade's own, as is the carried destination it shows: an explicit
/// return to Automatic over firmware that already reports the saved
/// Balanced with Automatic fans clears both changes recorded outside
/// OpenBlade.
#[tokio::test]
async fn an_explicit_fan_no_op_readback_is_own_state() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    report(&rig, 1, Some(r"C:\Tools\Other.exe")).await;
    let fixed = rig.request(fan_request(1, Some(1), 3700)).await;
    assert_eq!(fixed["result"]["status"], 1, "{fixed}");
    // Silent with manual fans, then Balanced with Automatic fans, outside
    // OpenBlade.
    rig.port.state.lock().unwrap().selectors = [5; 2];
    assert_eq!(
        observed(&rig, 1).await["status"]["externalChanges"],
        json!([0])
    );
    {
        let mut s = rig.port.state.lock().unwrap();
        s.selectors = [0; 2];
        s.fans = [0; 2];
    }
    assert_eq!(
        observed(&rig, 2).await["status"]["externalChanges"],
        json!([0, 1])
    );
    let before = writes(&rig);
    let automatic = rig.request(fan_request(2, Some(0), 3700)).await;
    assert_eq!(automatic["result"]["status"], 1, "{automatic}");
    assert_eq!(writes(&rig), before);
    assert_eq!(rig.get().await["status"]["externalChanges"], json!([]));
    assert_eq!(
        observed(&rig, 3).await["status"]["externalChanges"],
        json!([])
    );
    rig.stop().await;
}

/// Each reconciliation's setter count on a fresh rig, then the rig.
async fn counted(rule: &str, steps: &[&str]) -> (Rig, Vec<usize>) {
    let rig = rig_at(files_with_game(serde_json::from_str(rule).unwrap()), 0);
    let mut counts = Vec::new();
    for (n, path) in steps.iter().enumerate() {
        let before = writes(&rig);
        report(&rig, n as i64 + 1, Some(path)).await;
        counts.push(writes(&rig) - before);
    }
    (rig, counts)
}
/// Reports `before`, then `dropped` with a blocking host stall before its
/// last setter, past the real-time bound; the device then shows
/// `device_after` and a fresh reading reports no change outside OpenBlade.
async fn dropped_at_bound(
    rule: &str,
    before: &[&str],
    dropped: &str,
    device_after: ([u8; 2], [u8; 2]),
) -> Rig {
    let steps: Vec<&str> = before.iter().copied().chain([dropped]).collect();
    let (probe, counts) = counted(rule, &steps).await;
    probe.stop().await;
    let total = *counts.last().unwrap();
    assert!(total > 0);
    let (rig, _) = counted(rule, before).await;
    let last = writes(&rig) + total;
    let bound = crate::service_frame::RECONCILE_BUDGET + crate::service_frame::RECONCILE_MARGIN;
    rig.port.state.lock().unwrap().before_write = Some(Arc::new(move |number| {
        if number == last {
            std::thread::sleep(bound + std::time::Duration::from_secs(3));
        }
    }));
    let reply = report(&rig, before.len() as i64 + 1, Some(dropped)).await;
    assert_eq!(reply["result"]["status"], 3, "{reply}");
    assert!(
        reply["result"]["message"]
            .as_str()
            .unwrap()
            .ends_with(&format!("{UNFINISHED} {SUBMITTED}")),
        "{reply}"
    );
    rig.reader.drain_active().await.unwrap();
    rig.port.state.lock().unwrap().before_write = None;
    assert_eq!(device(&rig), device_after);
    // The write landed after the reconciliation answered: a fresh reading
    // of it is OpenBlade's own. The row the dropped step left unsettled
    // still fences writes.
    let read = observed(&rig, 1).await;
    assert_eq!(
        read["status"]["externalChanges"],
        json!([]),
        "{rule} {dropped}: {read}"
    );
    assert!(rig.storage.pending());
    rig
}
/// A step dropped at its bound still confirms its own completed write. A
/// host stall before its last setter, past the real-time bound, drops the
/// reconciliation after that setter's journaled intent; the setter then
/// lands with no readback of the step's own. The next fresh reading that
/// shows it reads it as OpenBlade's own write, not as drift: a performance
/// write (`record`), a Fixed fan write and a return to Automatic that
/// carries the next destination (`record_fan`, the last changing both
/// subsystems). Real time.
#[tokio::test]
async fn a_step_dropped_at_its_bound_records_its_own_completed_write() {
    keep_real_clock();
    const OTHER: &str = r"C:\Tools\Other.exe";
    const GAME: &str = r"C:\Games\Game.exe";
    let rigs = tokio::join!(
        dropped_at_bound(
            r#"{"pluggedIn":{"performance":2}}"#,
            &[OTHER],
            GAME,
            ([2; 2], [0; 2])
        ),
        dropped_at_bound(PRESET_AND_FIXED, &[OTHER], GAME, ([2; 2], [1; 2])),
        dropped_at_bound(PRESET_AND_FIXED, &[OTHER, GAME], OTHER, ([0; 2], [0; 2])),
    );
    for rig in [rigs.0, rigs.1, rigs.2] {
        rig.origin.stop();
        let _ = rig.source.lock().await.drain().await;
    }
}
/// An explicit request whose readback already shows its destination writes
/// nothing, and that readback is OpenBlade's own (managed
/// `ConfirmIntentional`): a change recorded outside OpenBlade clears.
#[tokio::test]
async fn an_explicit_no_op_readback_is_own_state() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    // Balanced, outside OpenBlade.
    rig.port.state.lock().unwrap().selectors = [0; 2];
    assert_eq!(
        observed(&rig, 1).await["status"]["externalChanges"],
        json!([0])
    );
    let before = writes(&rig);
    let set = rig.set(2, value(1, 0)).await;
    assert_eq!(set["result"]["status"], 1, "{set}");
    assert_eq!(writes(&rig), before);
    assert_eq!(rig.get().await["status"]["externalChanges"], json!([]));
    rig.stop().await;
}
#[path = "request_triggers_low_power.rs"]
mod low_power;
