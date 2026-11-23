//! The low-power gate on request-driven reconciliation: a report, a save and
//! the session's first report while the gate is set, a suspend between or
//! inside the steps, the exhausted (unknown) lifecycle, and explicit
//! requests, which store their intent through the storage-only capture. The
//! rig's lifecycle is the gate, as `Runtime::native` reads
//! `Lifecycle::low_power`. No hardware or operating-system supplier is
//! reached.
use super::*;
use crate::installed::readiness::thermal_mutation::{
    LOW_POWER_DEFERRED, LOW_POWER_PERFORMANCE, LOW_POWER_STEP,
};
use openblade_domain::low_power_gate::Transition;
use std::cell::Cell;
use std::rc::Rc;
use windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT as POWER;

const LOW_POWER_VECTORS: &str =
    include_str!("../../../../../tests/data/low-power-trigger-vectors.json");
const SUSPEND: u32 = 4;
const RESUME: u32 = 18;

/// Wires the rig's lifecycle in as the runtime's gate.
fn gated(rig: &Rig) {
    let custody = rig.custody.clone();
    rig.runtime
        .read_low_power(move || custody.lifecycle.low_power());
}
fn suspend(rig: &Rig) {
    rig.custody.lifecycle.ingress(POWER, SUSPEND);
    assert!(rig.custody.lifecycle.low_power_or_unknown());
}
fn resume(rig: &Rig) {
    rig.custody.lifecycle.ingress(POWER, RESUME);
    assert!(!rig.custody.lifecycle.low_power_or_unknown());
}
fn owed(rig: &Rig) -> Option<u8> {
    rig.runtime.owed_attempts(SID, 7)
}
fn deferred_report() -> Value {
    json!({"status":2,"message":format!("{RECORDED} {LOW_POWER_DEFERRED}")})
}

/// The managed report sequence in and out of low power: a changed pair is
/// deferred with managed's reply, a renewal and an inactive report are
/// answered as without the gate, nothing reaches the fixture, and the debt
/// keeps its retries. The gate clearing applies the owed rule once.
///
/// The rig's default `Fresh` (`Custody`) does not pin the lifecycle at
/// capture, as production's `WindowsGuard::capture` does. A trigger that
/// arrives with the gate already set takes the storage capture there instead
/// (`production_capture_rows_in_low_power_match_the_managed_vectors`).
#[tokio::test]
async fn the_low_power_report_sequence_matches_the_managed_vectors() {
    let vectors: Value = serde_json::from_str(LOW_POWER_VECTORS).unwrap();
    let files = MemoryFiles::default();
    files.profiles(1);
    let rig = Rig::new(files);
    gated(&rig);
    for step in vectors["report"].as_array().unwrap() {
        let expected = &step["expect"];
        let low_power = step["lowPower"].as_bool().unwrap();
        if low_power && !rig.custody.lifecycle.low_power_or_unknown() {
            suspend(&rig);
        } else if !low_power && rig.custody.lifecycle.low_power_or_unknown() {
            resume(&rig);
        }
        let before = sent(&rig);
        let reply = report(
            &rig,
            step["sequence"].as_i64().unwrap(),
            step["path"].as_str(),
        )
        .await;
        let result = &reply["result"];
        let message = result["message"].as_str().unwrap();
        if low_power {
            // Managed submits nothing; native reads nothing either.
            assert_eq!(expected["setters"], 0, "{step}");
            assert_eq!(sent(&rig), before, "{step}: {reply}");
            assert_eq!(owed(&rig), Some(0), "{step}: {reply}");
        }
        match expected["transition"].as_bool() {
            // Managed's scripted device leaves battery and lighting
            // unapplied, which native does not compose (request-trigger
            // vectors): only the trigger and its prefix are compared.
            Some(true) => {
                assert!(
                    message.starts_with(&format!("{RECORDED} ")),
                    "{step}: {reply}"
                );
                assert_eq!(result["status"], 1, "{step}: {reply}");
                assert!(sent(&rig) > before, "{step}");
                assert_eq!(owed(&rig), None, "{step}");
            }
            _ => {
                assert_eq!(result["status"], expected["status"], "{step}: {reply}");
                assert_eq!(message, expected["message"], "{step}: {reply}");
            }
        }
    }
    // The gate-clear renewal after the transition read nothing.
    assert_eq!(
        rig.get().await["status"]["confirmedState"]["performance"],
        2
    );
    rig.stop().await;
}

/// The constants are managed's own replies: the transition's deferral, the
/// per-write refusal every thermal write of a transition checks, and the
/// performance write's own refusal an explicit performance request ends with.
#[test]
fn the_low_power_replies_are_managed_replies() {
    let vectors: Value = serde_json::from_str(LOW_POWER_VECTORS).unwrap();
    assert_eq!(
        vectors["startup"],
        json!({"status":2,"message":LOW_POWER_DEFERRED,"setters":0})
    );
    assert_eq!(vectors["writeBlock"]["message"], LOW_POWER_STEP);
    assert_eq!(
        vectors["save"],
        json!({"status":0,"message":format!("Profiles were stored. {LOW_POWER_DEFERRED}"),"setters":0})
    );
    for row in vectors["explicit"].as_array().unwrap() {
        assert_eq!(row["expect"]["status"], 2, "{row}");
        assert_eq!(row["expect"]["setters"], 0, "{row}");
        let refusal = if row["operation"] == "setPerformanceOverride" {
            LOW_POWER_PERFORMANCE
        } else {
            LOW_POWER_STEP
        };
        let message = row["expect"]["message"].as_str().unwrap();
        assert!(message.ends_with(&format!(" {refusal}")), "{row}");
    }
}

/// A save in low power is stored with managed's reply and reads nothing.
/// Renewals while the gate stays set read nothing and spend no retry; the
/// first renewal after it clears applies the saved intent exactly once.
#[tokio::test]
async fn a_save_in_low_power_is_stored_and_applied_once_after_the_gate_clears() {
    let vectors: Value = serde_json::from_str(LOW_POWER_VECTORS).unwrap();
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    gated(&rig);
    assert_eq!(
        report(&rig, 1, Some(r"C:\Games\Game.exe")).await["result"]["status"],
        1
    );
    assert_eq!(owed(&rig), None);
    suspend(&rig);
    let before = sent(&rig);
    let saved = rig
        .request(update(2, &with_game_performance(&rig.files, 0)))
        .await;
    let mut expected = vectors["save"].clone();
    expected.as_object_mut().unwrap().remove("setters");
    assert_eq!(saved["result"], expected);
    assert_eq!(
        rig.files.document("profiles.json")["profiles"]["applicationProfiles"][0]["overrides"]["pluggedIn"]
            ["performance"],
        0
    );
    assert_eq!(owed(&rig), Some(0));
    for sequence in 2..=20 {
        let renewed = report(&rig, sequence, Some(r"C:\Games\Game.exe")).await;
        assert_eq!(
            renewed["result"],
            json!({"status":0,"message":RENEWED}),
            "{sequence}"
        );
    }
    assert_eq!(sent(&rig), before);
    assert_eq!(owed(&rig), Some(0));
    resume(&rig);
    let applied = report(&rig, 21, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(
        applied["result"],
        json!({"status":1,"message":format!("{RECORDED} {CONFIRMED} {AUTOMATIC}")})
    );
    assert_eq!(
        rig.get().await["status"]["confirmedState"]["performance"],
        0
    );
    assert_eq!(owed(&rig), None);
    let after = sent(&rig);
    let renewed = report(&rig, 22, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(renewed["result"], json!({"status":0,"message":RENEWED}));
    assert_eq!(sent(&rig), after);
    rig.stop().await;
}

/// The session's first report in low power is answered as managed answers
/// an unchanged pair and keeps startup owed without a retry; repeated reports
/// read nothing, and the first after the gate clears applies startup once.
#[tokio::test]
async fn a_startup_in_low_power_stays_owed_until_the_gate_clears() {
    let files = MemoryFiles::default();
    files.profiles(2);
    let rig = Rig::new(files);
    gated(&rig);
    suspend(&rig);
    for sequence in 1..=12 {
        let reply = report(&rig, sequence, Some(r"C:\Tools\Other.exe")).await;
        assert_eq!(
            reply["result"],
            json!({"status":0,"message":INACTIVE}),
            "{sequence}"
        );
    }
    assert_eq!(sent(&rig), 0);
    assert_eq!(owed(&rig), Some(0));
    resume(&rig);
    let startup = report(&rig, 13, Some(r"C:\Tools\Other.exe")).await;
    assert_eq!(
        startup["result"],
        json!({"status":1,"message":format!("{RECORDED} {CONFIRMED} {AUTOMATIC}")})
    );
    assert_eq!(
        rig.get().await["status"]["confirmedState"]["performance"],
        2
    );
    assert_eq!(owed(&rig), None);
    let after = sent(&rig);
    let again = report(&rig, 14, Some(r"C:\Tools\Other.exe")).await;
    assert_eq!(again["result"], json!({"status":0,"message":INACTIVE}));
    assert_eq!(sent(&rig), after);
    rig.stop().await;
}

/// A suspend after the first step stops the second before its capture,
/// reading or pair: the first step's result stands, the second is refused
/// with managed's per-write reply, and the debt is kept without a retry for
/// the first report after the gate clears.
#[tokio::test]
async fn a_suspend_between_the_steps_stops_the_second_step() {
    let rule: Value = serde_json::from_str(PRESET_AND_FIXED).unwrap();
    let rig = rig_at(files_with_game(rule), 0);
    // The gate check before the second step finds the suspend that arrived
    // after the first.
    let calls = Rc::new(Cell::new(0u32));
    let custody = rig.custody.clone();
    let counted = calls.clone();
    rig.runtime.read_low_power(move || {
        counted.set(counted.get() + 1);
        if counted.get() == 2 {
            custody.lifecycle.ingress(POWER, SUSPEND);
        }
        custody.lifecycle.low_power()
    });
    let reply = report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(calls.get(), 2);
    assert_eq!(
        reply["result"],
        json!({"status":3,"message":format!("{RECORDED} {CONFIRMED} {LOW_POWER_STEP}")})
    );
    {
        let s = rig.port.state.lock().unwrap();
        assert_eq!((s.selectors, s.fans), ([2; 2], [0; 2]));
    }
    assert_eq!(journal_ids(&rig).len(), 1);
    assert_eq!(owed(&rig), Some(0));
    let before = sent(&rig);
    for sequence in 2..=6 {
        assert_eq!(
            report(&rig, sequence, Some(r"C:\Games\Game.exe")).await["result"],
            json!({"status":0,"message":RENEWED})
        );
    }
    assert_eq!(sent(&rig), before);
    assert_eq!(owed(&rig), Some(0));
    resume(&rig);
    let applied = report(&rig, 7, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(
        applied["result"],
        json!({"status":1,"message":format!("{RECORDED} {CONFIRMED} {FIXED}")})
    );
    {
        let s = rig.port.state.lock().unwrap();
        assert_eq!((s.selectors, s.fans), ([2; 2], [1; 2]));
    }
    assert_eq!(owed(&rig), None);
    assert_eq!(journal_ids(&rig).len(), 2);
    rig.stop().await;
}

/// A suspend during a step's reading is refused by that step's write
/// admission before any setter (`authorize_for`); the gate classifies the
/// refusal as the deferral and keeps the debt without a retry.
#[tokio::test]
async fn a_suspend_inside_a_step_is_refused_at_submission_and_deferred() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    gated(&rig);
    let custody = rig.custody.clone();
    rig.port.state.lock().unwrap().on_power = Some(Arc::new(move |read| {
        if read == 1 {
            custody.lifecycle.ingress(POWER, SUSPEND);
        }
    }));
    let refused = report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    rig.port.state.lock().unwrap().on_power = None;
    assert_eq!(
        refused["result"],
        json!({"status":2,"message":format!("{RECORDED} {LOW_POWER_STEP}")})
    );
    assert_eq!(writes(&rig), 0);
    assert_eq!(rig.port.state.lock().unwrap().selectors, [0; 2]);
    assert!(!rig.storage.pending());
    assert_eq!(owed(&rig), Some(0));
    let before = sent(&rig);
    for sequence in 2..=6 {
        report(&rig, sequence, Some(r"C:\Games\Game.exe")).await;
    }
    assert_eq!(sent(&rig), before);
    assert_eq!(owed(&rig), Some(0));
    resume(&rig);
    let applied = report(&rig, 7, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(
        applied["result"],
        json!({"status":1,"message":format!("{RECORDED} {CONFIRMED} {AUTOMATIC}")})
    );
    assert_eq!(owed(&rig), None);
    rig.stop().await;
}

/// A save admitted before a suspend that arrives inside its step is stored,
/// replies the per-write refusal, and owes the change without spending the
/// save's attempt (`Reconciled::spent`); it applies once after the gate
/// clears.
#[tokio::test]
async fn a_save_deferred_inside_its_step_spends_no_attempt() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    gated(&rig);
    assert_eq!(
        report(&rig, 1, Some(r"C:\Games\Game.exe")).await["result"]["status"],
        1
    );
    let custody = rig.custody.clone();
    let once = Arc::new(AtomicBool::new(false));
    rig.port.state.lock().unwrap().on_power = Some(Arc::new(move |_| {
        if !once.swap(true, Ordering::AcqRel) {
            custody.lifecycle.ingress(POWER, SUSPEND);
        }
    }));
    let before = writes(&rig);
    let saved = rig
        .request(update(2, &with_game_performance(&rig.files, 0)))
        .await;
    rig.port.state.lock().unwrap().on_power = None;
    assert_eq!(
        saved["result"],
        json!({"status":0,"message":format!("Profiles were stored. {LOW_POWER_STEP}")})
    );
    assert_eq!(writes(&rig), before);
    assert_eq!(owed(&rig), Some(0));
    let reads = sent(&rig);
    for sequence in 2..=6 {
        report(&rig, sequence, Some(r"C:\Games\Game.exe")).await;
    }
    assert_eq!(sent(&rig), reads);
    assert_eq!(owed(&rig), Some(0));
    resume(&rig);
    let applied = report(&rig, 7, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(
        applied["result"],
        json!({"status":1,"message":format!("{RECORDED} {CONFIRMED} {AUTOMATIC}")})
    );
    assert_eq!(owed(&rig), None);
    rig.stop().await;
}

/// An exhausted lifecycle is unknown, which the gate treats as low power:
/// managed's gate has no unknown state, and native fails closed. Nothing is
/// read and the debt is kept.
#[tokio::test]
async fn an_unknown_gate_state_defers_as_low_power() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    gated(&rig);
    rig.custody.lifecycle.exhaust();
    assert!(rig.custody.lifecycle.low_power().is_none());
    let reply = report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(reply["result"], deferred_report());
    assert_eq!(sent(&rig), 0);
    assert_eq!(owed(&rig), Some(0));
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
}

/// The gate only classifies a refusal: another refusal while the gate is
/// clear, and unknown Windows power while it is set, keep their own
/// replies, as managed reaches its gate only on classified power.
#[tokio::test]
async fn the_gate_classifies_only_a_refusal_it_caused() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    gated(&rig);
    rig.custody.fresh_refuse.store(true, Ordering::Release);
    let refused = report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(refused["result"], json!({"status":0,"message":RECORDED}));
    let saved = rig
        .request(update(2, &with_game_performance(&rig.files, 0)))
        .await;
    assert_eq!(saved["result"], json!({"status":0,"message":STORED}));
    rig.custody.fresh_refuse.store(false, Ordering::Release);
    suspend(&rig);
    rig.port.state.lock().unwrap().windows_on_battery = Some(None);
    let unknown = report(&rig, 3, Some(r"C:\Tools\Other.exe")).await;
    assert_eq!(unknown["result"], json!({"status":0,"message":RECORDED}));
    assert_eq!(sent(&rig), 0);
    rig.port.state.lock().unwrap().windows_on_battery = None;
    let deferred = report(&rig, 4, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(deferred["result"], deferred_report());
    assert_eq!(sent(&rig), 0);
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
}

/// The production capture (`WindowsGuard::capture`, modelled by
/// `CapturedFresh`) requires an active lifecycle, so a report or save that
/// arrives in low power takes the storage capture
/// (`WindowsGuard::capture_storage`, modelled by `CapturedStorage`) instead:
/// it is recorded or stored with managed's deferral, nothing is read, and the
/// debt spends no retry. The first report after the gate clears applies the
/// saved intent once.
#[tokio::test]
async fn a_production_capture_in_low_power_reads_nothing_and_applies_after_the_gate_clears() {
    let vectors: Value = serde_json::from_str(LOW_POWER_VECTORS).unwrap();
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    gated(&rig);
    rig.custody.scoped_fresh.store(true, Ordering::Release);
    suspend(&rig);
    let deferred = report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(deferred["result"], deferred_report());
    let saved = rig
        .request(update(2, &with_game_performance(&rig.files, 1)))
        .await;
    let mut expected = vectors["save"].clone();
    expected.as_object_mut().unwrap().remove("setters");
    assert_eq!(saved["result"], expected);
    assert_eq!(
        rig.files.document("profiles.json")["profiles"]["applicationProfiles"][0]["overrides"]["pluggedIn"]
            ["performance"],
        1
    );
    assert_eq!(sent(&rig), 0);
    assert_eq!(owed(&rig), Some(0));
    // `CapturedFresh` models the capture only; the steps use the rig's own
    // admission, as every other trigger test does.
    rig.custody.scoped_fresh.store(false, Ordering::Release);
    resume(&rig);
    let applied = report(&rig, 3, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(applied["result"]["status"], 1, "{applied}");
    assert_eq!(owed(&rig), None);
    let after = sent(&rig);
    assert!(after > 0);
    let renewed = report(&rig, 4, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(renewed["result"], json!({"status":0,"message":RENEWED}));
    assert_eq!(sent(&rig), after);
    rig.stop().await;
}

/// A battery rig whose startup debt one report settles before `gate` wires
/// the runtime's low-power probe.
async fn battery_rig(gate: impl FnOnce(&Rig)) -> Rig {
    let files = MemoryFiles::default();
    files.profiles(2);
    let rig = rig_at(files, 3);
    rig.port.state.lock().unwrap().actual = 0;
    rig.metadata.store(1, Ordering::Release);
    report(&rig, 1, Some(r"C:\Tools\Other.exe")).await;
    assert_eq!(owed(&rig), None);
    gate(&rig);
    rig
}
/// The managed explicit rows recorded at `power`.
fn explicit_rows(vectors: &Value, power: &str) -> Vec<Value> {
    vectors["explicit"]
        .as_array()
        .unwrap()
        .iter()
        .filter(|row| row["power"] == power)
        .cloned()
        .collect()
}
/// One managed explicit row as a persistent native request with id `n`.
async fn explicit(rig: &Rig, n: u32, row: &Value) -> Value {
    let requested = row["value"].as_i64().unwrap();
    if row["operation"] == "setPerformanceOverride" {
        rig.set(u8::try_from(n).unwrap(), value(requested as i32, 1))
            .await
    } else {
        rig.request(fan_request(n, Some(requested as u8), 3700))
            .await
    }
}
fn overrides(rig: &Rig) -> Value {
    rig.files.document("manual-overrides.json")["overrides"].clone()
}

/// Managed's explicit rows on battery, which Windows alone classifies: under
/// the production capture each override is stored with managed's reply,
/// nothing is read or submitted, and the pair owes a reconciliation with no
/// retry spent. Renewals while the gate stays set read nothing; the first
/// report after it clears applies the stored intent once.
#[tokio::test]
async fn explicit_requests_on_battery_in_low_power_store_and_apply_once() {
    let vectors: Value = serde_json::from_str(LOW_POWER_VECTORS).unwrap();
    let rig = battery_rig(gated).await;
    rig.custody.scoped_fresh.store(true, Ordering::Release);
    suspend(&rig);
    let before = sent(&rig);
    let rows = explicit_rows(&vectors, "battery");
    assert_eq!(rows.len(), 3);
    for (n, row) in (2..).zip(&rows) {
        let reply = explicit(&rig, n, row).await;
        let expected = &row["expect"];
        assert_eq!(
            reply["result"]["status"], expected["status"],
            "{row}: {reply}"
        );
        assert_eq!(
            reply["result"]["message"], expected["message"],
            "{row}: {reply}"
        );
        assert_eq!(owed(&rig), Some(0), "{row}");
    }
    assert_eq!(sent(&rig), before);
    assert_eq!(writes(&rig), 0);
    let stored = overrides(&rig);
    assert_eq!(stored["performance"], 3, "{stored}");
    assert_eq!(stored["fans"], 0, "{stored}");
    for sequence in 10..=12 {
        let renewed = report(&rig, sequence, Some(r"C:\Tools\Other.exe")).await;
        assert_eq!(
            renewed["result"],
            json!({"status":0,"message":INACTIVE}),
            "{sequence}"
        );
    }
    assert_eq!(sent(&rig), before);
    assert_eq!(owed(&rig), Some(0));
    rig.custody.scoped_fresh.store(false, Ordering::Release);
    resume(&rig);
    let applied = report(&rig, 13, Some(r"C:\Tools\Other.exe")).await;
    assert!(sent(&rig) > before, "{applied}");
    assert_eq!(owed(&rig), None, "{applied}");
    // The stored preset and Automatic fans, confirmed once by paired readback.
    assert_eq!(applied["result"]["status"], 1, "{applied}");
    {
        let s = rig.port.state.lock().unwrap();
        assert_eq!((s.selectors, s.fans), ([3; 2], [0; 2]), "{applied}");
    }
    let after = sent(&rig);
    let renewed = report(&rig, 14, Some(r"C:\Tools\Other.exe")).await;
    assert_eq!(renewed["result"], json!({"status":0,"message":INACTIVE}));
    assert_eq!(sent(&rig), after);
    rig.stop().await;
}

/// Managed's explicit rows on AC. Production Windows reports AC to managed
/// only as `ExternalUnknown`, so managed samples the adapter from firmware
/// while its gate is set and stores only when that reading classifies; these
/// rows were recorded against a test double that classifies it. Native
/// declines any firmware read in low power, so it stores nothing and replies
/// as unclassified power, as managed does when its reading does not
/// classify, with the same status and no setter.
#[tokio::test]
async fn explicit_requests_on_ac_in_low_power_store_nothing() {
    let vectors: Value = serde_json::from_str(LOW_POWER_VECTORS).unwrap();
    let files = MemoryFiles::default();
    files.profiles(1);
    let rig = rig_at(files, 0);
    gated(&rig);
    rig.custody.scoped_fresh.store(true, Ordering::Release);
    let document = rig.files.read("manual-overrides.json").unwrap();
    suspend(&rig);
    let rows = explicit_rows(&vectors, "pluggedIn");
    assert_eq!(rows.len(), 2);
    for (n, row) in (1..).zip(&rows) {
        let reply = explicit(&rig, n, row).await;
        assert_eq!(
            reply["result"]["status"], row["expect"]["status"],
            "{row}: {reply}"
        );
        assert_eq!(
            reply["result"]["message"], UNCLASSIFIED_BLOCKED,
            "{row}: {reply}"
        );
        assert_ne!(reply["result"]["message"], row["expect"]["message"]);
    }
    assert_eq!(writes(&rig), 0);
    assert_eq!(sent(&rig), 0);
    assert_eq!(rig.files.read("manual-overrides.json").unwrap(), document);
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
}

/// With the lifecycle out of the active state but the gate read clear, no
/// storage admission is taken: the request is refused at its write
/// admission, as before, and nothing is stored or submitted.
#[tokio::test]
async fn explicit_requests_store_nothing_unless_the_gate_reads_set() {
    for scoped in [false, true] {
        let rig = battery_rig(|rig| {
            rig.runtime.read_low_power(|| {
                Some(Transition {
                    generation: 0,
                    timestamp: 0,
                })
            })
        })
        .await;
        rig.custody.scoped_fresh.store(scoped, Ordering::Release);
        let document = rig.files.read("manual-overrides.json").unwrap();
        let before = sent(&rig);
        suspend(&rig);
        // The write capture admits the rig's own `Fresh`, whose write
        // admission then refuses; the production-modelled capture refuses
        // first. Neither falls back to storage.
        let refused = if scoped {
            [
                "Saved thermal configuration or fresh admission is unavailable: permission denied",
                "Fan configuration admission failed: permission denied",
            ]
        } else {
            [
                "Current thermal admission is unavailable; no intent or command was submitted.",
                "Current fan admission is unavailable.",
            ]
        };
        let performance = rig.set(2, value(4, 1)).await;
        let fans = rig.request(fan_request(3, Some(3), 3700)).await;
        for (reply, message) in [&performance, &fans].into_iter().zip(refused) {
            assert_eq!(
                reply["result"],
                json!({"status":2,"message":message}),
                "{scoped}"
            );
        }
        assert_eq!(writes(&rig), 0);
        assert_eq!(sent(&rig), before);
        assert_eq!(rig.files.read("manual-overrides.json").unwrap(), document);
        assert_eq!(owed(&rig), None, "{scoped}");
        rig.origin.stop();
        let _ = rig.source.lock().await.drain().await;
    }
}

/// An exhausted (unknown) lifecycle reads as low power: the override is
/// stored with the deferral reply, and no write is ever admitted, because the
/// write capture needs an active epoch the exhausted lifecycle never has.
#[tokio::test]
async fn an_exhausted_lifecycle_stores_intent_and_never_writes() {
    for scoped in [false, true] {
        let rig = battery_rig(gated).await;
        rig.custody.scoped_fresh.store(scoped, Ordering::Release);
        let before = sent(&rig);
        rig.custody.lifecycle.exhaust();
        let performance = rig.set(2, value(4, 1)).await;
        assert_eq!(
            performance["result"],
            json!({"status":2,"message":format!("Performance override intent was stored. Hardware result: {LOW_POWER_PERFORMANCE}")}),
            "{scoped}"
        );
        let fans = rig.request(fan_request(3, Some(3), 3700)).await;
        assert_eq!(
            fans["result"],
            json!({"status":2,"message":format!("Fan override intent was stored. Hardware result: {LOW_POWER_STEP}")}),
            "{scoped}"
        );
        assert_eq!(overrides(&rig)["performance"], 4);
        assert_eq!(overrides(&rig)["fans"], 3);
        assert_eq!(owed(&rig), Some(0));
        for sequence in 4..=8 {
            report(&rig, sequence, Some(r"C:\Tools\Other.exe")).await;
        }
        assert_eq!(writes(&rig), 0);
        assert_eq!(sent(&rig), before);
        assert_eq!(owed(&rig), Some(0));
        rig.origin.stop();
        let _ = rig.source.lock().await.drain().await;
    }
}

/// Storage keeps every admission but the lifecycle: a console or session
/// that is no longer the caller's, a refused Windows supplier (a competing
/// controller included), a saved document the domain decoder refuses, and a
/// fenced store each refuse the request in low power, store nothing and
/// leave the debt as it was.
#[tokio::test]
async fn storage_in_low_power_keeps_every_other_admission() {
    const CAPTURE: &str =
        "Saved thermal configuration or fresh admission is unavailable: permission denied";
    const FAN_CAPTURE: &str = "Fan configuration admission failed: permission denied";
    const FENCED: &str = "An earlier thermal submission requires read-only reconciliation.";
    const NOT_ACCEPTED: [&str; 2] = [
        "Performance intent was not accepted; admission or protected persistence failed.",
        "Fan intent was not accepted; fresh admission or persistence did not complete.",
    ];
    // `scoped` is the production-modelled capture, which refuses at capture;
    // the rig's own `Fresh` reaches the storage admission after its write
    // admission refused. The rig's `Fresh` models no console.
    for (case, scoped, refused) in [
        ("console", true, [CAPTURE, FAN_CAPTURE]),
        ("session", true, [CAPTURE, FAN_CAPTURE]),
        ("session", false, [CAPTURE, FAN_CAPTURE]),
        ("supplier", true, [CAPTURE, FAN_CAPTURE]),
        (
            "supplier",
            false,
            [
                "Current thermal admission is unavailable; no intent or command was submitted.",
                "Current fan admission is unavailable.",
            ],
        ),
        ("configuration", true, NOT_ACCEPTED),
        ("configuration", false, NOT_ACCEPTED),
        ("fenced", true, [FENCED, FENCED]),
        ("fenced", false, [FENCED, FENCED]),
    ] {
        let rig = battery_rig(gated).await;
        rig.custody.scoped_fresh.store(scoped, Ordering::Release);
        match case {
            "console" => rig.custody.current_console.store(false, Ordering::Release),
            "session" => rig.custody.current_session.store(8, Ordering::Release),
            "supplier" => rig.custody.fresh_refuse.store(true, Ordering::Release),
            "configuration" => {
                let mut document = rig.files.document("profiles.json");
                document["profiles"]["battery"]["performance"] = json!(99);
                rig.files.put("profiles.json", document);
            }
            _ => rig.storage.quarantine_settlement().unwrap(),
        }
        let document = rig.files.read("manual-overrides.json").unwrap();
        let before = sent(&rig);
        suspend(&rig);
        let performance = rig.set(2, value(4, 1)).await;
        let fans = rig.request(fan_request(3, Some(3), 3700)).await;
        for (reply, message) in [&performance, &fans].into_iter().zip(refused) {
            assert_eq!(
                reply["result"],
                json!({"status":2,"message":message}),
                "{case} {scoped}"
            );
        }
        assert_eq!(writes(&rig), 0, "{case}");
        assert_eq!(sent(&rig), before, "{case}");
        assert_eq!(
            rig.files.read("manual-overrides.json").unwrap(),
            document,
            "{case}"
        );
        assert_eq!(owed(&rig), None, "{case}");
        rig.origin.stop();
        let _ = rig.source.lock().await.drain().await;
    }
}

/// The gate clears between the storage and the next report: the debt the
/// request recorded at the set gate is applied by that report once, and a
/// gate that read set only at the request's check reads clear for it.
#[tokio::test]
async fn the_gate_clearing_after_storage_applies_the_intent_once() {
    let mut calls = None;
    let rig = battery_rig(|rig| calls = Some(gate_set_at_check(rig, 1))).await;
    let calls = calls.unwrap();
    suspend(&rig);
    let before = sent(&rig);
    let performance = rig.set(2, value(4, 1)).await;
    assert_eq!(
        performance["result"],
        json!({"status":2,"message":format!("Performance override intent was stored. Hardware result: {LOW_POWER_PERFORMANCE}")})
    );
    assert_eq!(calls.get(), 1);
    assert_eq!(owed(&rig), Some(0));
    assert_eq!(sent(&rig), before);
    resume(&rig);
    let applied = report(&rig, 3, Some(r"C:\Tools\Other.exe")).await;
    assert!(sent(&rig) > before, "{applied}");
    assert_eq!(owed(&rig), None, "{applied}");
    assert_eq!(applied["result"]["status"], 1, "{applied}");
    assert_eq!(
        rig.port.state.lock().unwrap().selectors,
        [6; 2],
        "{applied}"
    );
    let after = sent(&rig);
    report(&rig, 4, Some(r"C:\Tools\Other.exe")).await;
    assert_eq!(sent(&rig), after);
    rig.stop().await;
}

/// A gate that reads set at its `at`-th check and cleared again at every later
/// one: the gate set after a step finished. It counts the checks.
fn gate_set_at_check(rig: &Rig, at: u32) -> Rc<Cell<u32>> {
    let calls = Rc::new(Cell::new(0u32));
    let counted = calls.clone();
    rig.runtime.read_low_power(move || {
        counted.set(counted.get() + 1);
        let generation = match counted.get().cmp(&at) {
            std::cmp::Ordering::Less => 0,
            std::cmp::Ordering::Equal => 1,
            std::cmp::Ordering::Greater => 2,
        };
        Some(Transition {
            generation,
            timestamp: 0,
        })
    });
    calls
}

/// A step that fenced the store replies the fence and the gate is not read
/// after it: the storage reconciliation decides what happens next.
#[tokio::test]
async fn a_step_that_fenced_the_store_is_not_a_deferral() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    // Check 1 is before the first step; a check after it would be the second.
    let calls = gate_set_at_check(&rig, 2);
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
    assert_eq!(calls.get(), 1);
    assert_eq!(writes(&rig), 0);
    assert!(rig.storage.pending());
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
}

/// A step that confirmed firmware already at its target is not reclassified
/// by a gate that sets after it: the next step's own check defers that one.
#[tokio::test]
async fn a_confirmed_step_is_not_a_deferral() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 2);
    let calls = gate_set_at_check(&rig, 2);
    let reply = report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(
        reply["result"],
        json!({"status":2,"message":format!("{RECORDED} {CONFIRMED} {LOW_POWER_STEP}")})
    );
    assert_eq!(calls.get(), 2);
    assert_eq!(writes(&rig), 0);
    assert_eq!(owed(&rig), Some(0));
    // The fan step that was deferred runs on the next report.
    let applied = report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(
        applied["result"],
        json!({"status":1,"message":format!("{RECORDED} {CONFIRMED} {AUTOMATIC}")})
    );
    assert_eq!(owed(&rig), None);
    rig.stop().await;
}

/// A suspend after the performance step's Confirmed row and before its last
/// admission check: that step may have submitted, so it keeps its own
/// result and is not relabelled, and the fan step is deferred at its check.
/// The deferral keeps the debt, though the performance step may not be
/// retried, so the fans apply once after the gate clears and performance,
/// already confirmed by firmware, is not written again.
#[tokio::test]
async fn a_deferral_after_a_possibly_submitted_step_keeps_the_debt() {
    let rule: Value = serde_json::from_str(PRESET_AND_FIXED).unwrap();
    let rig = rig_at(files_with_game(rule), 0);
    gated(&rig);
    let custody = rig.custody.clone();
    let once = Arc::new(AtomicBool::new(false));
    rig.port.state.lock().unwrap().on_mutation_retired = Some(Arc::new(move || {
        if !once.swap(true, Ordering::AcqRel) {
            custody.lifecycle.ingress(POWER, SUSPEND);
        }
    }));
    let reply = report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    rig.port.state.lock().unwrap().on_mutation_retired = None;
    let message = reply["result"]["message"].as_str().unwrap();
    assert_eq!(reply["result"]["status"], 3, "{reply}");
    assert!(
        message.starts_with(&format!(
            "{RECORDED} Thermal result lost caller or owner admission"
        )),
        "{reply}"
    );
    assert!(message.ends_with(LOW_POWER_STEP), "{reply}");
    {
        let s = rig.port.state.lock().unwrap();
        assert_eq!((s.selectors, s.fans), ([2; 2], [0; 2]));
    }
    assert!(!rig.storage.pending());
    assert_eq!(journal_ids(&rig).len(), 1);
    assert_eq!(owed(&rig), Some(0));
    let reads = sent(&rig);
    for sequence in 2..=4 {
        report(&rig, sequence, Some(r"C:\Games\Game.exe")).await;
    }
    assert_eq!(sent(&rig), reads);
    assert_eq!(owed(&rig), Some(0));
    resume(&rig);
    let applied = report(&rig, 5, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(
        applied["result"],
        json!({"status":1,"message":format!("{RECORDED} {CONFIRMED} {FIXED}")})
    );
    {
        let s = rig.port.state.lock().unwrap();
        assert_eq!((s.selectors, s.fans), ([2; 2], [1; 2]));
    }
    // Only the fan step journaled: performance found its target confirmed.
    assert_eq!(journal_ids(&rig).len(), 2);
    assert_eq!(owed(&rig), None);
    let after = sent(&rig);
    let renewed = report(&rig, 6, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(renewed["result"], json!({"status":0,"message":RENEWED}));
    assert_eq!(sent(&rig), after);
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
}

/// A suspend and a resume that both arrive inside one step: the gate is
/// clear again when the step's refusal is classified, but its generation
/// moved, so the refusal is the deferral and the debt is kept.
#[tokio::test]
async fn a_suspend_and_resume_inside_a_step_is_a_deferral() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    gated(&rig);
    let custody = rig.custody.clone();
    rig.port.state.lock().unwrap().on_power = Some(Arc::new(move |read| {
        if read == 1 {
            custody.lifecycle.ingress(POWER, SUSPEND);
            custody.lifecycle.ingress(POWER, RESUME);
        }
    }));
    let reply = report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    rig.port.state.lock().unwrap().on_power = None;
    assert!(!rig.custody.lifecycle.low_power_or_unknown());
    assert_eq!(
        reply["result"],
        json!({"status":2,"message":format!("{RECORDED} {LOW_POWER_STEP}")})
    );
    assert_eq!(writes(&rig), 0);
    assert_eq!(owed(&rig), Some(0));
    let applied = report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(
        applied["result"],
        json!({"status":1,"message":format!("{RECORDED} {CONFIRMED} {AUTOMATIC}")})
    );
    assert_eq!(rig.port.state.lock().unwrap().selectors, [2; 2]);
    assert_eq!(owed(&rig), None);
    rig.stop().await;
}

/// A suspend after the last step's worker retired, when that step found a
/// class the save did not change: the step needed nothing, so it is not a
/// deferral, the save is Stored and the pair owes nothing.
#[tokio::test]
async fn a_suspend_after_an_unchanged_last_step_is_not_a_deferral() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    gated(&rig);
    assert_eq!(
        report(&rig, 1, Some(r"C:\Games\Game.exe")).await["result"]["status"],
        1
    );
    // The full adapter is connected; only the limited one's override changes.
    let mut profiles = rig.files.document("profiles.json")["profiles"].clone();
    profiles["applicationProfiles"][0]["overrides"]["usbC"] = json!({"performance":0});
    let custody = rig.custody.clone();
    let retired = Arc::new(AtomicUsize::new(0));
    let counted = retired.clone();
    rig.port.state.lock().unwrap().on_mutation_retired = Some(Arc::new(move || {
        if counted.fetch_add(1, Ordering::AcqRel) == 1 {
            custody.lifecycle.ingress(POWER, SUSPEND);
        }
    }));
    let saved = rig.request(update(2, &profiles)).await;
    rig.port.state.lock().unwrap().on_mutation_retired = None;
    assert_eq!(retired.load(Ordering::Acquire), 2);
    assert!(rig.custody.lifecycle.low_power_or_unknown());
    assert_eq!(saved["result"], json!({"status":0,"message":STORED}));
    assert_eq!(owed(&rig), None);
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
}

/// The managed report rows under the production-modelled capture
/// (`CapturedFresh`, which pins the lifecycle as `WindowsGuard::capture`
/// does): every row that arrives with the gate set takes the storage capture
/// (`CapturedStorage`), so it gets managed's reply, reads nothing and keeps
/// the debt with no retry spent.
#[tokio::test]
async fn production_capture_rows_in_low_power_match_the_managed_vectors() {
    let vectors: Value = serde_json::from_str(LOW_POWER_VECTORS).unwrap();
    let files = MemoryFiles::default();
    files.profiles(1);
    let rig = Rig::new(files);
    gated(&rig);
    rig.custody.scoped_fresh.store(true, Ordering::Release);
    suspend(&rig);
    let rows: Vec<&Value> = vectors["report"]
        .as_array()
        .unwrap()
        .iter()
        .filter(|row| row["lowPower"] == true)
        .collect();
    assert_eq!(rows.len(), 4);
    for row in rows {
        let reply = report(
            &rig,
            row["sequence"].as_i64().unwrap(),
            row["path"].as_str(),
        )
        .await;
        let expected = &row["expect"];
        assert_eq!(
            reply["result"]["status"], expected["status"],
            "{row}: {reply}"
        );
        assert_eq!(
            reply["result"]["message"], expected["message"],
            "{row}: {reply}"
        );
    }
    assert_eq!(sent(&rig), 0);
    assert_eq!(owed(&rig), Some(0));
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
}

/// A suspend while the step's read is held past the reconciliation bound:
/// the step had journaled nothing that could be submitted, so the unfinished
/// step is a deferral and spends no retry. The next report after the gate
/// clears applies the rule.
#[tokio::test]
async fn a_suspend_during_an_unfinished_step_is_a_deferral() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    gated(&rig);
    rig.get().await;
    let release = Arc::new(AtomicBool::new(false));
    let holding = {
        let mut s = rig.port.state.lock().unwrap();
        s.held_read = Some(release.clone());
        s.holding.clone()
    };
    let custody = rig.custody.clone();
    let suspend_when_held = async {
        holding.notified().await;
        custody.lifecycle.ingress(POWER, SUSPEND);
    };
    let (reply, ()) = tokio::join!(
        report(&rig, 1, Some(r"C:\Games\Game.exe")),
        suspend_when_held
    );
    assert_eq!(
        reply["result"],
        json!({"status":2,"message":format!("{RECORDED} {LOW_POWER_STEP}")})
    );
    assert_eq!(writes(&rig), 0);
    assert!(!rig.storage.pending());
    assert_eq!(owed(&rig), Some(0));
    release.store(true, Ordering::Release);
    rig.port.state.lock().unwrap().held_read = None;
    resume(&rig);
    let applied = report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(
        applied["result"],
        json!({"status":1,"message":format!("{RECORDED} {CONFIRMED} {AUTOMATIC}")})
    );
    assert_eq!(owed(&rig), None);
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
}

/// The save form of a deferral after a possibly submitted step: the save is
/// stored, the performance step keeps its own result, the fan step is
/// deferred, and the debt is kept for the first report after the gate
/// clears (`Reconciled::deferred`), though the performance step may not be
/// retried.
#[tokio::test]
async fn a_save_deferred_after_a_possibly_submitted_step_keeps_the_debt() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":1}})), 0);
    gated(&rig);
    assert_eq!(
        report(&rig, 1, Some(r"C:\Games\Game.exe")).await["result"]["status"],
        1
    );
    assert_eq!(owed(&rig), None);
    let mut profiles = rig.files.document("profiles.json")["profiles"].clone();
    profiles["applicationProfiles"][0]["overrides"] =
        serde_json::from_str(PRESET_AND_FIXED).unwrap();
    let custody = rig.custody.clone();
    let once = Arc::new(AtomicBool::new(false));
    rig.port.state.lock().unwrap().on_mutation_retired = Some(Arc::new(move || {
        if !once.swap(true, Ordering::AcqRel) {
            custody.lifecycle.ingress(POWER, SUSPEND);
        }
    }));
    let saved = rig.request(update(2, &profiles)).await;
    rig.port.state.lock().unwrap().on_mutation_retired = None;
    let message = saved["result"]["message"].as_str().unwrap();
    // A save is Stored whatever the hardware result (`ToOperationResult`).
    assert_eq!(saved["result"]["status"], 0, "{saved}");
    assert!(
        message.starts_with("Profiles were stored. Thermal result lost caller or owner admission"),
        "{saved}"
    );
    assert!(message.ends_with(LOW_POWER_STEP), "{saved}");
    assert_eq!(rig.port.state.lock().unwrap().fans, [0; 2]);
    assert_eq!(owed(&rig), Some(0));
    resume(&rig);
    let applied = report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(
        applied["result"],
        json!({"status":1,"message":format!("{RECORDED} {CONFIRMED} {FIXED}")})
    );
    {
        let s = rig.port.state.lock().unwrap();
        assert_eq!((s.selectors, s.fans), ([2; 2], [1; 2]));
    }
    assert_eq!(owed(&rig), None);
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
}

/// A suspend and a resume while the step's read is held past the bound: the
/// gate is clear again when the unfinished step is classified, but its
/// generation moved, so the step is a deferral and spends no retry.
#[tokio::test]
async fn a_suspend_and_resume_during_an_unfinished_step_is_a_deferral() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    gated(&rig);
    rig.get().await;
    let release = Arc::new(AtomicBool::new(false));
    let holding = {
        let mut s = rig.port.state.lock().unwrap();
        s.held_read = Some(release.clone());
        s.holding.clone()
    };
    let custody = rig.custody.clone();
    let interrupt_when_held = async {
        holding.notified().await;
        custody.lifecycle.ingress(POWER, SUSPEND);
        custody.lifecycle.ingress(POWER, RESUME);
    };
    let (reply, ()) = tokio::join!(
        report(&rig, 1, Some(r"C:\Games\Game.exe")),
        interrupt_when_held
    );
    assert!(!rig.custody.lifecycle.low_power_or_unknown());
    assert_eq!(
        reply["result"],
        json!({"status":2,"message":format!("{RECORDED} {LOW_POWER_STEP}")})
    );
    assert_eq!(writes(&rig), 0);
    assert_eq!(owed(&rig), Some(0));
    release.store(true, Ordering::Release);
    rig.port.state.lock().unwrap().held_read = None;
    let applied = report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(
        applied["result"],
        json!({"status":1,"message":format!("{RECORDED} {CONFIRMED} {AUTOMATIC}")})
    );
    assert_eq!(owed(&rig), None);
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
}

/// Runs `first` at the first mutation worker's retirement and then holds that
/// worker there until the returned flag is set: the step's chain has run, but
/// the worker has not handed its result back at the bound.
fn hold_first_retirement(rig: &Rig, first: impl Fn() + Send + Sync + 'static) -> Arc<AtomicBool> {
    let release = Arc::new(AtomicBool::new(false));
    let released = release.clone();
    let once = Arc::new(AtomicBool::new(false));
    rig.port.state.lock().unwrap().on_mutation_retired = Some(Arc::new(move || {
        if !once.swap(true, Ordering::AcqRel) {
            first();
            while !released.load(Ordering::Acquire) {
                std::thread::sleep(std::time::Duration::from_millis(5));
            }
        }
    }));
    release
}

/// The performance step's chain reached its Confirmed row, the gate set, and
/// the worker had not handed back at the bound: that step may have submitted,
/// so the unfinished arm keeps its own result and the debt settles. This pins
/// the arm's `!possible`.
#[tokio::test]
async fn a_possibly_submitted_step_unfinished_at_the_bound_is_not_a_deferral() {
    let rule: Value = serde_json::from_str(PRESET_AND_FIXED).unwrap();
    let rig = rig_at(files_with_game(rule), 0);
    gated(&rig);
    let custody = rig.custody.clone();
    let release = hold_first_retirement(&rig, move || {
        custody.lifecycle.ingress(POWER, SUSPEND);
    });
    let reply = report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    let owed_after = owed(&rig);
    let writes_after = writes(&rig);
    release.store(true, Ordering::Release);
    rig.port.state.lock().unwrap().on_mutation_retired = None;
    assert_eq!(
        reply["result"],
        json!({"status":3,"message":format!("{RECORDED} {UNFINISHED}")})
    );
    assert_eq!(writes_after, 2);
    assert_eq!(owed_after, None);
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
}

/// The step fenced the store with a changed override document and submitted
/// nothing, the gate set, and the worker had not handed back at the bound:
/// the storage reconciliation decides what happens next, so the unfinished
/// arm keeps its own result and the debt settles. This pins the arm's
/// `!storage.pending()`.
#[tokio::test]
async fn a_fenced_step_unfinished_at_the_bound_is_not_a_deferral() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    gated(&rig);
    let files = rig.files.clone();
    rig.port.state.lock().unwrap().on_power = Some(Arc::new(move |read| {
        if read == 1 {
            files.put(
                "manual-overrides.json",
                json!({"schemaVersion":10,"overrides":{"performance":null,"fans":null,"lighting":null,"customPerformance":null,"fanControl":null,"gpuClockTuning":null}}),
            );
        }
    }));
    let custody = rig.custody.clone();
    let release = hold_first_retirement(&rig, move || {
        custody.lifecycle.ingress(POWER, SUSPEND);
    });
    let reply = report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    let owed_after = owed(&rig);
    let pending = rig.storage.pending();
    release.store(true, Ordering::Release);
    {
        let mut s = rig.port.state.lock().unwrap();
        s.on_mutation_retired = None;
        s.on_power = None;
    }
    assert_eq!(
        reply["result"],
        json!({"status":2,"message":format!("{RECORDED} The reconciliation did not finish within its budget; no thermal setter was submitted.")})
    );
    assert!(pending);
    assert_eq!(writes(&rig), 0);
    assert_eq!(owed_after, None);
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
}

/// A save whose performance step is confirmed and whose fan step is deferred
/// replies the same whether the gate stopped the fan step at its check or set
/// while that step's read was held past the bound: a deferred step is not an
/// unfinished one that may still submit (`Reconciled::into_outcome`).
#[tokio::test]
async fn a_save_deferred_at_the_bound_replies_as_one_deferred_at_its_check() {
    async fn save(rig: &Rig) -> Value {
        let mut profiles = rig.files.document("profiles.json")["profiles"].clone();
        profiles["applicationProfiles"][0]["overrides"] =
            serde_json::from_str(PRESET_AND_FIXED).unwrap();
        rig.request(update(2, &profiles)).await
    }
    let expected =
        json!({"status":0,"message":format!("Profiles were stored. {CONFIRMED} {LOW_POWER_STEP}")});
    // The gate set after the performance step; the fan step's check stops it.
    let at_check = rig_at(files_with_game(json!({"pluggedIn":{"performance":1}})), 0);
    assert_eq!(
        report(&at_check, 1, Some(r"C:\Games\Game.exe")).await["result"]["status"],
        1
    );
    let calls = gate_set_at_check(&at_check, 2);
    assert_eq!(save(&at_check).await["result"], expected);
    assert_eq!(calls.get(), 2);
    assert_eq!(owed(&at_check), Some(0));
    at_check.stop().await;
    // The gate set while the fan step's read was held past the bound.
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":1}})), 0);
    gated(&rig);
    assert_eq!(
        report(&rig, 1, Some(r"C:\Games\Game.exe")).await["result"]["status"],
        1
    );
    let release = Arc::new(AtomicBool::new(false));
    let holding = rig.port.state.lock().unwrap().holding.clone();
    let state = rig.port.state.clone();
    let held = release.clone();
    let once = Arc::new(AtomicBool::new(false));
    rig.port.state.lock().unwrap().on_mutation_retired = Some(Arc::new(move || {
        if !once.swap(true, Ordering::AcqRel) {
            state.lock().unwrap().held_read = Some(held.clone());
        }
    }));
    let custody = rig.custody.clone();
    let suspend_when_held = async {
        holding.notified().await;
        custody.lifecycle.ingress(POWER, SUSPEND);
    };
    let (saved, ()) = tokio::join!(save(&rig), suspend_when_held);
    release.store(true, Ordering::Release);
    {
        let mut s = rig.port.state.lock().unwrap();
        s.held_read = None;
        s.on_mutation_retired = None;
    }
    assert_eq!(saved["result"], expected);
    assert_eq!(rig.port.state.lock().unwrap().fans, [0; 2]);
    assert_eq!(owed(&rig), Some(0));
    resume(&rig);
    let applied = report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(
        applied["result"],
        json!({"status":1,"message":format!("{RECORDED} {CONFIRMED} {FIXED}")})
    );
    assert_eq!(owed(&rig), None);
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
}

/// A fan change OpenBlade still owns stores no fan intent in low power: the
/// handback it owes needs a write the gate refuses, and its continuity is
/// proved only by a write admission. The owned change is kept.
#[tokio::test]
async fn a_fan_request_in_low_power_with_an_owed_handback_stores_nothing() {
    let rig = fan_rig(0, 0);
    rig.get().await;
    let owned = rig.request(fan_request(1, Some(1), 3700)).await;
    assert_eq!(owned["result"]["status"], 1, "{owned}");
    assert!(rig.storage.outstanding_fan());
    {
        let mut s = rig.port.state.lock().unwrap();
        s.windows_on_battery = Some(Some(true));
        s.actual = 0;
    }
    gated(&rig);
    rig.custody.scoped_fresh.store(true, Ordering::Release);
    let document = rig.files.read("manual-overrides.json").unwrap();
    let writes = writes(&rig);
    suspend(&rig);
    let fans = rig.request(fan_request(2, Some(3), 3700)).await;
    assert_eq!(
        fans["result"],
        json!({"status":2,"message":"Fan intent was not stored: OpenBlade still owns a fan change that has not been handed back to firmware Automatic, and the laptop is in a low-power transition."})
    );
    assert_eq!(rig.port.state.lock().unwrap().writes, writes);
    assert_eq!(rig.files.read("manual-overrides.json").unwrap(), document);
    assert!(rig.storage.outstanding_fan());
    rig.port.state.lock().unwrap().selectors = [0, 0];
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
}

/// A gate probe that, once `armed`, reads clear at its next check and sets
/// the gate right after it, as a suspend arriving between a step's pre-step
/// check and its own write admission. Otherwise it reads the rig's lifecycle.
fn suspend_after_next_check(rig: &Rig, armed: Rc<Cell<bool>>) {
    let custody = rig.custody.clone();
    rig.runtime.read_low_power(move || {
        if armed.replace(false) {
            custody.lifecycle.ingress(POWER, SUSPEND);
            return Some(Transition {
                generation: 0,
                timestamp: 0,
            });
        }
        custody.lifecycle.low_power()
    });
}

/// A reconciliation step whose own write admission refuses in low power is
/// deferred and stores nothing: only an explicit request stores intent. The
/// step carries no override, so storing it would clear the saved one.
#[tokio::test]
async fn a_step_refused_at_its_own_admission_in_low_power_stores_nothing() {
    let armed = Rc::new(Cell::new(false));
    let rig = battery_rig(|rig| suspend_after_next_check(rig, armed.clone())).await;
    let applied = rig.set(2, value(4, 1)).await;
    assert_eq!(applied["result"]["status"], 1, "{applied}");
    let document = rig.files.read("manual-overrides.json").unwrap();
    let before = writes(&rig);
    armed.set(true);
    let deferred = report(&rig, 3, Some(r"C:\Games\Game.exe")).await;
    // The deferred step adds managed's per-write refusal.
    assert_eq!(
        deferred["result"],
        json!({"status":2,"message":format!("{RECORDED} {LOW_POWER_STEP}")})
    );
    assert!(!armed.get());
    assert_eq!(rig.files.read("manual-overrides.json").unwrap(), document);
    assert_eq!(overrides(&rig)["performance"], 4);
    assert_eq!(writes(&rig), before);
    assert_eq!(owed(&rig), Some(0));
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
}

/// A fan request alone owes the reconciliation too: the first report after
/// the gate clears applies it once.
#[tokio::test]
async fn a_fan_request_alone_in_low_power_is_applied_once_after_the_gate_clears() {
    let rig = battery_rig(gated).await;
    rig.custody.scoped_fresh.store(true, Ordering::Release);
    suspend(&rig);
    let before = sent(&rig);
    let fans = rig.request(fan_request(2, Some(3), 3700)).await;
    assert_eq!(
        fans["result"],
        json!({"status":2,"message":format!("Fan override intent was stored. Hardware result: {LOW_POWER_STEP}")})
    );
    assert_eq!(overrides(&rig)["fans"], 3);
    assert_eq!(owed(&rig), Some(0));
    assert_eq!(sent(&rig), before);
    rig.custody.scoped_fresh.store(false, Ordering::Release);
    resume(&rig);
    let applied = report(&rig, 3, Some(r"C:\Tools\Other.exe")).await;
    assert!(sent(&rig) > before, "{applied}");
    assert_eq!(owed(&rig), None, "{applied}");
    // Maximum fans on battery have no native recipe: the reconciliation reads
    // firmware, applies nothing and says so.
    assert!(
        applied["result"]["message"]
            .as_str()
            .is_some_and(|m| m.contains("needs a native recipe")),
        "{applied}"
    );
    assert_eq!(writes(&rig), 0, "{applied}");
    assert_eq!(rig.port.state.lock().unwrap().fans, [0; 2], "{applied}");
    let after = sent(&rig);
    report(&rig, 4, Some(r"C:\Tools\Other.exe")).await;
    assert_eq!(sent(&rig), after);
    rig.stop().await;
}

/// A committed saved set whose active application rule the domain decoder
/// refuses stores no explicit intent in low power, though the power
/// profile alone would validate the combination.
#[tokio::test]
async fn an_invalid_active_rule_stores_no_intent_in_low_power() {
    let rig = battery_rig(gated).await;
    let game = report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(game["result"]["status"], 1, "{game}");
    let mut document = rig.files.document("profiles.json");
    document["profiles"]["applicationProfiles"][0]["overrides"]["battery"] =
        json!({"performance":99});
    rig.files.put("profiles.json", document);
    rig.files.commit_configuration();
    rig.custody.scoped_fresh.store(true, Ordering::Release);
    let stored = rig.files.read("manual-overrides.json").unwrap();
    let before = writes(&rig);
    suspend(&rig);
    let performance = rig.set(3, value(4, 1)).await;
    let fans = rig.request(fan_request(4, Some(3), 3700)).await;
    assert_eq!(
        performance["result"],
        json!({"status":2,"message":"Performance intent was not accepted; admission or protected persistence failed."})
    );
    assert_eq!(
        fans["result"],
        json!({"status":2,"message":"Fan intent was not accepted; fresh admission or persistence did not complete."})
    );
    assert_eq!(rig.files.read("manual-overrides.json").unwrap(), stored);
    assert_eq!(writes(&rig), before);
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
}

/// The same with the fan step first: firmware reports manual fans the rule
/// returns to Automatic. The fan step's refused admission stores nothing,
/// and the saved Automatic fan override is kept.
#[tokio::test]
async fn a_fan_step_refused_at_its_own_admission_in_low_power_stores_nothing() {
    let armed = Rc::new(Cell::new(false));
    let rig = battery_rig(|rig| suspend_after_next_check(rig, armed.clone())).await;
    let automatic = rig.request(fan_request(2, Some(0), 3700)).await;
    assert_eq!(automatic["result"]["status"], 1, "{automatic}");
    assert_eq!(overrides(&rig)["fans"], 0);
    rig.port.state.lock().unwrap().fans = [1; 2];
    assert_manual_fans_observed(&rig).await;
    assert!(!rig.storage.outstanding_fan());
    let document = rig.files.read("manual-overrides.json").unwrap();
    let before = writes(&rig);
    armed.set(true);
    let deferred = report(&rig, 3, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(
        deferred["result"],
        json!({"status":2,"message":format!("{RECORDED} {LOW_POWER_STEP}")})
    );
    assert!(!armed.get());
    assert_eq!(rig.files.read("manual-overrides.json").unwrap(), document);
    assert_eq!(writes(&rig), before);
    assert_eq!(owed(&rig), Some(0));
    rig.port.state.lock().unwrap().fans = [0; 2];
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
}

/// The reply of an explicit performance request stored in low power.
fn stored_performance() -> Value {
    json!({"status":2,"message":format!("Performance override intent was stored. Hardware result: {LOW_POWER_PERFORMANCE}")})
}
/// The reply of an explicit fan request stored in low power.
fn stored_fans() -> Value {
    json!({"status":2,"message":format!("Fan override intent was stored. Hardware result: {LOW_POWER_STEP}")})
}
/// Suspends the rig at the `at`-th adapter reading from now.
fn suspend_at_power_read(rig: &Rig, at: usize) {
    let custody = rig.custody.clone();
    let target = rig.port.state.lock().unwrap().power_reads + at;
    rig.port.state.lock().unwrap().on_power = Some(Arc::new(move |read| {
        if read == target {
            custody.lifecycle.ingress(POWER, SUSPEND);
        }
    }));
}

/// An explicit request that the gate overtakes after its own authorization,
/// during its first setter's adapter reading, has stored its intent and
/// submitted nothing: it replies, owes and is applied after the gate clears
/// exactly as the same request does when the gate was already set.
///
/// The overtaken request has journaled Prepared. The rig's journal refuses a
/// plain append while the lifecycle reads low power, as `ReadLease` does, so
/// this pins that the Prepared row settles through the custody's retirement
/// and the store is left neither quarantined nor pending.
#[tokio::test]
async fn an_explicit_request_the_gate_overtakes_before_submission_is_a_deferral() {
    let mut applied_writes = None;
    for overtaken in [false, true] {
        let rig = battery_rig(gated).await;
        rig.custody.scoped_fresh.store(true, Ordering::Release);
        if overtaken {
            suspend_at_power_read(&rig, 2);
        } else {
            suspend(&rig);
        }
        let rows = fan_rows(&rig).len();
        let reply = rig.set(2, value(4, 1)).await;
        rig.port.state.lock().unwrap().on_power = None;
        assert_eq!(reply["result"], stored_performance(), "{overtaken}");
        assert_eq!(overrides(&rig)["performance"], 4, "{overtaken}");
        assert_eq!(owed(&rig), Some(0), "{overtaken}");
        assert!(!rig.storage.pending(), "{overtaken}");
        let journaled: Vec<_> = fan_rows(&rig)[rows..]
            .iter()
            .map(|row| row["progress"].as_u64().unwrap())
            .collect();
        // Prepared, settled as known not submitted; the storage capture
        // journals nothing.
        let expected: &[u64] = if overtaken { &[0, 6] } else { &[] };
        assert_eq!(journaled, expected, "{overtaken}");
        resume(&rig);
        // Nothing is written until the next report reconciles.
        assert_eq!(writes(&rig), 0, "{overtaken}");
        let applied = report(&rig, 3, Some(r"C:\Tools\Other.exe")).await;
        assert_eq!(applied["result"]["status"], 1, "{overtaken} {applied}");
        assert_eq!(owed(&rig), None, "{overtaken} {applied}");
        assert_eq!(rig.port.state.lock().unwrap().selectors, [6; 2]);
        // One apply: the same writes as the control, and none after it.
        let after = writes(&rig);
        assert_eq!(*applied_writes.get_or_insert(after), after, "{overtaken}");
        report(&rig, 4, Some(r"C:\Tools\Other.exe")).await;
        assert_eq!(writes(&rig), after, "{overtaken}");
        rig.stop().await;
    }
}

/// The same for a fan request, which on battery has no native recipe and so
/// is never confirmed: a gate that set and cleared again while it ran,
/// after its store and with nothing submitted, makes it the deferral a store
/// at the set gate is, owing the same reconciliation.
#[tokio::test]
async fn a_fan_request_the_gate_overtakes_before_submission_is_a_deferral() {
    for overtaken in [false, true] {
        let mut calls = None;
        let rig = battery_rig(|rig| {
            if overtaken {
                // Check 1 is the request's own after its write admission.
                calls = Some(gate_set_at_check(rig, 2));
            } else {
                gated(rig);
            }
        })
        .await;
        rig.custody.scoped_fresh.store(true, Ordering::Release);
        if !overtaken {
            suspend(&rig);
        }
        let reply = rig.request(fan_request(2, Some(3), 3700)).await;
        assert_eq!(calls.map(|c| c.get()), overtaken.then_some(2));
        assert_eq!(reply["result"], stored_fans(), "{overtaken}");
        assert_eq!(overrides(&rig)["fans"], 3, "{overtaken}");
        assert_eq!(owed(&rig), Some(0), "{overtaken}");
        assert_eq!(writes(&rig), 0, "{overtaken}");
        rig.origin.stop();
        let _ = rig.source.lock().await.drain().await;
    }
}

/// A request the gate overtakes after a setter may have been submitted keeps
/// its own result: it is never relabelled a deferral and owes nothing new.
#[tokio::test]
async fn a_possibly_submitted_request_the_gate_overtakes_keeps_its_result() {
    let rig = battery_rig(gated).await;
    rig.custody.scoped_fresh.store(true, Ordering::Release);
    // The reading after the first setter, at its readback.
    suspend_at_power_read(&rig, 3);
    let reply = rig.set(2, value(4, 1)).await;
    rig.port.state.lock().unwrap().on_power = None;
    assert!(writes(&rig) > 0, "{reply}");
    assert_ne!(reply["result"], stored_performance(), "{reply}");
    assert_eq!(owed(&rig), None, "{reply}");
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
}

/// A request whose setter was submitted and its chain settled, which the
/// gate overtakes after the worker retired and before the request's final
/// authorization, keeps its own result. Its journal is settled and the store
/// is not pending, so only its own submitted row (`submission_possible`)
/// tells it from a store with nothing submitted.
#[tokio::test]
async fn a_settled_submission_the_gate_overtakes_before_its_final_authorization_keeps_its_result() {
    let rig = battery_rig(gated).await;
    rig.custody.scoped_fresh.store(true, Ordering::Release);
    let custody = rig.custody.clone();
    let once = AtomicBool::new(false);
    rig.port.state.lock().unwrap().on_mutation_retired = Some(Arc::new(move || {
        if !once.swap(true, Ordering::AcqRel) {
            custody.lifecycle.ingress(POWER, SUSPEND);
        }
    }));
    let reply = rig.set(2, value(4, 1)).await;
    rig.port.state.lock().unwrap().on_mutation_retired = None;
    assert!(writes(&rig) > 0, "{reply}");
    assert!(!rig.storage.pending(), "{reply}");
    let last = fan_rows(&rig).last().unwrap().clone();
    assert_eq!(last["request"], "aaaaaaaa-bbbb-cccc-dddd-000000000002");
    assert!(!matches!(last["progress"].as_u64(), Some(0 | 6)), "{last}");
    assert_eq!(
        reply["result"],
        json!({"status":2,"message":"Thermal result lost caller or owner admission; submission evidence is retained."})
    );
    assert_eq!(owed(&rig), None, "{reply}");
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
}

/// A fan clear whose resulting profile fan settings the domain decoder
/// refuses (a fixed speed above the 5400 RPM bound) stores nothing and owes
/// nothing, in low power and awake: everything the save leaves is read before
/// it commits (`Storage::plan_fan_save`).
#[tokio::test]
async fn a_fan_clear_to_refused_profile_settings_stores_nothing() {
    const NOT_ACCEPTED: &str =
        "Fan intent was not accepted; fresh admission or persistence did not complete.";
    for low_power in [true, false] {
        let rig = battery_rig(gated).await;
        let mut document = rig.files.document("profiles.json");
        let mut control = crate::installed::readiness::thermal_storage::legacy_fan(0);
        control["fixedSpeedRpm"] = json!(5500);
        document["profiles"]["battery"]["fanControl"] = control;
        rig.files.put("profiles.json", document);
        rig.files.commit_configuration();
        rig.storage
            .save_fan(
                Some(crate::service_status::FanOverride {
                    value: 3,
                    lifetime: 1,
                }),
                None,
                1,
            )
            .unwrap();
        let document = rig.files.read("manual-overrides.json").unwrap();
        rig.custody.scoped_fresh.store(true, Ordering::Release);
        if low_power {
            suspend(&rig);
        }
        let cleared = rig.request(fan_request(2, None, 3700)).await;
        assert_eq!(
            cleared["result"],
            json!({"status":2,"message":NOT_ACCEPTED}),
            "{low_power}"
        );
        assert_eq!(
            rig.files.read("manual-overrides.json").unwrap(),
            document,
            "{low_power}"
        );
        assert_eq!(owed(&rig), None, "{low_power}");
        assert_eq!(writes(&rig), 0, "{low_power}");
        rig.origin.stop();
        let _ = rig.source.lock().await.drain().await;
    }
}

/// A Reader that cannot activate a mutation owner (activation known
/// unpublished, or uncertain and fenced) stores nothing awake, while the
/// storage capture stores in low power, where no activation is attempted.
/// After the gate clears the reconciliation it owes is refused at the same
/// activation, writes nothing and settles the debt.
#[tokio::test]
async fn an_unactivated_reader_stores_only_in_low_power_and_writes_nothing() {
    for refusal in [1u8, 2] {
        // No report has activated the Reader's owner yet.
        let files = MemoryFiles::default();
        files.profiles(2);
        let rig = rig_at(files, 3);
        rig.port.state.lock().unwrap().actual = 0;
        rig.metadata.store(1, Ordering::Release);
        gated(&rig);
        rig.custody
            .activation_refusal
            .store(refusal, Ordering::Release);
        let awake = rig.set(2, value(4, 1)).await;
        assert_eq!(
            awake["result"],
            json!({"status":2,"message":"Performance intent was not accepted; admission or protected persistence failed."}),
            "{refusal}"
        );
        assert!(
            rig.files.read("manual-overrides.json").unwrap().is_none(),
            "{refusal}"
        );
        rig.custody.scoped_fresh.store(true, Ordering::Release);
        suspend(&rig);
        let asleep = rig.set(3, value(4, 1)).await;
        assert_eq!(asleep["result"], stored_performance(), "{refusal}");
        assert_eq!(owed(&rig), Some(0), "{refusal}");
        rig.custody.scoped_fresh.store(false, Ordering::Release);
        resume(&rig);
        let after = report(&rig, 4, Some(r"C:\Tools\Other.exe")).await;
        assert_eq!(after["result"]["status"], 2, "{refusal} {after}");
        assert_eq!(owed(&rig), None, "{refusal} {after}");
        assert_eq!(writes(&rig), 0, "{refusal}");
        assert_eq!(rig.custody.activations.load(Ordering::Acquire), 0);
        rig.origin.stop();
        let _ = rig.source.lock().await.drain().await;
    }
}

/// A storage capture never authorizes a read or write, even when the gate
/// clears between the capture and the request's own authorization: the
/// request then neither stores nor submits.
#[tokio::test]
async fn a_storage_capture_never_authorizes_after_the_gate_clears() {
    let calls = Rc::new(Cell::new(0u32));
    let counted = calls.clone();
    let rig = battery_rig(|rig| {
        let custody = rig.custody.clone();
        rig.runtime.read_low_power(move || {
            counted.set(counted.get() + 1);
            let read = custody.lifecycle.low_power();
            if counted.get() == 1 && read.is_some_and(|t| t.suspending()) {
                custody.lifecycle.ingress(POWER, RESUME);
            }
            read
        });
    })
    .await;
    rig.custody.scoped_fresh.store(true, Ordering::Release);
    let document = rig.files.read("manual-overrides.json").unwrap();
    suspend(&rig);
    let frames = sent(&rig);
    let reply = rig.set(2, value(4, 1)).await;
    assert_eq!(
        reply["result"],
        json!({"status":2,"message":"Current thermal admission is unavailable; no intent or command was submitted."})
    );
    assert_eq!(calls.get(), 2);
    assert_eq!(writes(&rig), 0);
    assert_eq!(sent(&rig), frames);
    assert_eq!(rig.files.read("manual-overrides.json").unwrap(), document);
    assert_eq!(owed(&rig), None);
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
}

/// Unknown Windows power classifies nothing in low power: no request stores.
#[tokio::test]
async fn unknown_windows_power_in_low_power_stores_nothing() {
    for scoped in [true, false] {
        let rig = battery_rig(gated).await;
        rig.custody.scoped_fresh.store(scoped, Ordering::Release);
        suspend(&rig);
        rig.port.state.lock().unwrap().windows_on_battery = Some(None);
        let document = rig.files.read("manual-overrides.json").unwrap();
        let performance = rig.set(2, value(4, 1)).await;
        let fans = rig.request(fan_request(3, Some(3), 3700)).await;
        for reply in [&performance, &fans] {
            assert_eq!(reply["result"]["status"], 2, "{scoped} {reply}");
        }
        assert_eq!(rig.files.read("manual-overrides.json").unwrap(), document);
        assert_eq!(owed(&rig), None);
        rig.origin.stop();
        let _ = rig.source.lock().await.drain().await;
    }
}

/// Intent stored on battery in low power owes every class: the laptop that
/// resumes on AC applies it.
#[tokio::test]
async fn a_battery_store_in_low_power_is_reconciled_on_ac_after_resume() {
    let rig = battery_rig(gated).await;
    rig.custody.scoped_fresh.store(true, Ordering::Release);
    suspend(&rig);
    let fans = rig.request(fan_request(2, Some(3), 3700)).await;
    assert_eq!(fans["result"], stored_fans());
    assert_eq!(owed(&rig), Some(0));
    rig.custody.scoped_fresh.store(false, Ordering::Release);
    {
        let mut s = rig.port.state.lock().unwrap();
        s.windows_on_battery = None;
        s.actual = s.recommended;
    }
    rig.metadata.store(0, Ordering::Release);
    resume(&rig);
    let frames = sent(&rig);
    let applied = report(&rig, 3, Some(r"C:\Tools\Other.exe")).await;
    assert!(sent(&rig) > frames, "{applied}");
    assert_eq!(owed(&rig), None, "{applied}");
    assert_eq!(rig.port.state.lock().unwrap().fans, [1; 2], "{applied}");
    rig.port.state.lock().unwrap().fans = [0; 2];
    rig.port.state.lock().unwrap().selectors = [0; 2];
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
}

/// Managed's own validation rejects before storing in low power as awake:
/// Failed, nothing stored, nothing owed.
#[tokio::test]
async fn rejected_requests_store_nothing_in_low_power() {
    let rig = battery_rig(gated).await;
    rig.custody.scoped_fresh.store(true, Ordering::Release);
    suspend(&rig);
    let document = rig.files.read("manual-overrides.json").unwrap();
    // Balanced is not a battery preset (`PerformancePowerSourcePolicy.IsAllowed`).
    let performance = rig.set(2, value(1, 1)).await;
    assert_eq!(performance["result"]["status"], 3, "{performance}");
    // A manual curve needs full external power on this backend.
    let fans = rig.request(fan_request(3, Some(2), 3700)).await;
    assert_eq!(fans["result"]["status"], 3, "{fans}");
    assert_eq!(rig.files.read("manual-overrides.json").unwrap(), document);
    assert_eq!(owed(&rig), None);
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
}

/// A temporary override stored in low power on battery is saved at the
/// battery class, so the first report after the gate clears applies it.
#[tokio::test]
async fn a_temporary_override_stored_in_low_power_is_applied() {
    let rig = battery_rig(gated).await;
    rig.custody.scoped_fresh.store(true, Ordering::Release);
    suspend(&rig);
    let stored = rig.set(2, value(4, 0)).await;
    assert_eq!(stored["result"], stored_performance());
    rig.custody.scoped_fresh.store(false, Ordering::Release);
    resume(&rig);
    let before = writes(&rig);
    let applied = report(&rig, 3, Some(r"C:\Tools\Other.exe")).await;
    assert_eq!(applied["result"]["status"], 1, "{applied}");
    assert!(writes(&rig) > before, "{applied}");
    assert_eq!(rig.port.state.lock().unwrap().selectors, [6; 2]);
    assert_eq!(owed(&rig), None);
    rig.stop().await;
}

/// A report and a save in low power keep their storage admission: a lost
/// console or a refused supplier refuses both before anything is recorded.
#[tokio::test]
async fn reports_and_saves_in_low_power_keep_their_storage_admission() {
    const UNAVAILABLE: &str =
        "Saved configuration or the current session is unavailable: permission denied";
    for case in ["console", "supplier"] {
        let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
        gated(&rig);
        rig.custody.scoped_fresh.store(true, Ordering::Release);
        suspend(&rig);
        match case {
            "console" => rig.custody.current_console.store(false, Ordering::Release),
            _ => rig.custody.fresh_refuse.store(true, Ordering::Release),
        }
        let reported = report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
        assert_eq!(
            reported["result"]["message"], UNAVAILABLE,
            "{case} {reported}"
        );
        let before = rig.files.read("profiles.json").unwrap();
        let saved = rig
            .request(update(2, &with_game_performance(&rig.files, 1)))
            .await;
        assert_eq!(saved["result"]["message"], UNAVAILABLE, "{case} {saved}");
        assert_eq!(rig.files.read("profiles.json").unwrap(), before, "{case}");
        assert_eq!(sent(&rig), 0, "{case}");
        rig.origin.stop();
        let _ = rig.source.lock().await.drain().await;
    }
}

/// Only a request that stored its intent is relabelled. A performance
/// request refused at its own authorization before its store, or one that
/// fenced the store, keeps its own result; so does a fan request its own
/// checks reject, and a fan request that confirmed while the gate moved.
#[tokio::test]
async fn only_a_stored_unconfirmed_request_is_a_deferral() {
    // Refused before the store: the suspend lands at the request's own
    // adapter reading, ahead of its authorization and save.
    let rig = battery_rig(gated).await;
    rig.custody.scoped_fresh.store(true, Ordering::Release);
    suspend_at_power_read(&rig, 1);
    let refused = rig.set(2, value(4, 1)).await;
    rig.port.state.lock().unwrap().on_power = None;
    assert_ne!(refused["result"], stored_performance(), "{refused}");
    assert!(
        rig.files.read("manual-overrides.json").unwrap().is_none(),
        "{refused}"
    );
    assert_eq!(owed(&rig), None, "{refused}");
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;

    // Stored, then the store fenced with nothing submitted: the fence's
    // storage reconciliation decides what happens next.
    let rig = battery_rig(gated).await;
    rig.custody.scoped_fresh.store(true, Ordering::Release);
    let custody = rig.custody.clone();
    let storage = rig.storage.clone();
    let target = rig.port.state.lock().unwrap().power_reads + 2;
    rig.port.state.lock().unwrap().on_power = Some(Arc::new(move |read| {
        if read == target {
            storage.quarantine_settlement().unwrap();
            custody.lifecycle.ingress(POWER, SUSPEND);
        }
    }));
    let fenced = rig.set(2, value(4, 1)).await;
    rig.port.state.lock().unwrap().on_power = None;
    assert!(rig.storage.pending());
    assert_eq!(writes(&rig), 0, "{fenced}");
    assert_ne!(fenced["result"], stored_performance(), "{fenced}");
    assert_eq!(owed(&rig), None, "{fenced}");
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;

    // A fan request its own checks reject, while the gate moves.
    let mut calls = None;
    let rig = battery_rig(|rig| calls = Some(gate_set_at_check(rig, 2))).await;
    rig.custody.scoped_fresh.store(true, Ordering::Release);
    let rejected = rig.request(fan_request(2, Some(2), 3700)).await;
    assert_eq!(rejected["result"]["status"], 3, "{rejected}");
    assert_eq!(owed(&rig), None, "{rejected}");
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;

    // A fan request that confirms firmware already at its target, with no
    // setter and so no journal row, while the gate generation moves without
    // setting the gate (a new resume moves it by two).
    let generation = Arc::new(std::sync::atomic::AtomicU64::new(0));
    let probe = generation.clone();
    let rig = battery_rig(|rig| {
        rig.runtime.read_low_power(move || {
            Some(Transition {
                generation: probe.load(Ordering::Acquire),
                timestamp: 0,
            })
        })
    })
    .await;
    rig.custody.scoped_fresh.store(true, Ordering::Release);
    let target = rig.port.state.lock().unwrap().power_reads + 1;
    let moved = generation.clone();
    rig.port.state.lock().unwrap().on_power = Some(Arc::new(move |read| {
        if read == target {
            moved.fetch_add(2, Ordering::AcqRel);
        }
    }));
    let confirmed = rig.request(fan_request(2, Some(0), 3700)).await;
    rig.port.state.lock().unwrap().on_power = None;
    assert_eq!(generation.load(Ordering::Acquire), 2);
    assert_eq!(confirmed["result"]["status"], 1, "{confirmed}");
    assert_eq!(writes(&rig), 0, "{confirmed}");
    assert_eq!(owed(&rig), None, "{confirmed}");
    rig.stop().await;
}

#[path = "request_triggers_lifecycle.rs"]
mod lifecycle;
