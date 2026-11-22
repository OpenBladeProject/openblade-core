//! Request-driven reconciliation of saved intent: a changed foreground
//! report, the first admitted report of a session, and a profile save that
//! changes the active application's intent. Every case runs the real framed
//! dispatcher over the in-memory store and the scripted mailbox; no hardware,
//! pipe or operating-system supplier is reached.
use super::*;
use crate::installed::readiness::thermal_mutation::{
    APPLICATION_SESSION_REQUIRED, operation_status,
};

const RECORDED: &str = "The active application profile was recorded.";
const CONFIRMED: &str = "Bounded thermal preset confirmed by independent paired readback. GPU clock handling and broad device capabilities remain unavailable; other native controls are incomplete.";
const AUTOMATIC: &str = "Automatic mode independently observed; no fan setter was submitted and no ownership was established.";
const STORED: &str = "Profiles stored without automatic hardware application.";
const VECTORS: &str = include_str!("../../../../../tests/data/request-trigger-vectors.json");

async fn report(rig: &Rig, sequence: i64, path: Option<&str>) -> Value {
    rig.request(json!({"operation":"reportApplicationContext","version":85,"requestId":format!("bbbbbbbb-bbbb-cccc-dddd-{sequence:012}"),"instanceId":"cccccccc-bbbb-cccc-dddd-000000000003","sequence":sequence,"executablePath":path})).await
}
fn update(n: u8, profiles: &Value) -> Value {
    json!({"operation":"updateProfiles","version":85,"requestId":format!("aaaaaaaa-bbbb-cccc-dddd-{n:012}"),"profiles":profiles})
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
    assert_ne!(rig.get().await["status"]["confirmedState"]["fans"], 0);
    let sent = rig.port.state.lock().unwrap().sent.len();
    let left = report(&rig, 2, Some(r"C:\Tools\Other.exe")).await;
    // One fan step: the performance step would have been refused while the
    // fan change was still owned (`FanHandbackPending`).
    assert_eq!(
        left["result"],
        json!({"status":1,"message":format!("{RECORDED} Fans returned to Automatic with Balanced performance, confirmed by independent paired readback.")})
    );
    let pair = mode_setters(&rig, sent);
    assert_eq!(pair.len(), 2, "{pair:?}");
    assert!(pair.iter().all(|setter| setter[3] == 0), "{pair:?}");
    assert_eq!(rig.get().await["status"]["confirmedState"]["fans"], 0);
    rig.stop().await;
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
