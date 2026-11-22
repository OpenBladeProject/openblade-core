//! The low-power gate on request-driven reconciliation: a report, a save and
//! the session's first report while the gate is set, a suspend between or
//! inside the steps, the exhausted (unknown) lifecycle, and explicit
//! requests. The rig's lifecycle is the gate, as `Runtime::native` reads
//! `Lifecycle::low_power`. No hardware or operating-system
//! supplier is reached.
use super::*;
use crate::installed::readiness::thermal_mutation::{LOW_POWER_DEFERRED, LOW_POWER_STEP};
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
/// This covers only the window where the gate sets after the trigger's
/// capture: the rig's default `Fresh` (`Custody`) does not pin the
/// lifecycle at capture, as production's `WindowsGuard::capture` does. A
/// trigger that arrives with the gate already set is refused at that
/// capture (`production_capture_rows_in_low_power_are_refused_at_capture`).
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

/// The constants are managed's own replies: the transition's deferral and
/// the per-write refusal every thermal write of a transition checks.
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
/// `CapturedFresh`) already requires an active lifecycle, so a report or
/// save that arrives in low power is refused there, before the gate check:
/// nothing is read and the report keeps the prior pair. The first report
/// after the gate clears applies the rule once.
#[tokio::test]
async fn a_production_capture_in_low_power_reads_nothing_and_applies_after_the_gate_clears() {
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    gated(&rig);
    rig.custody.scoped_fresh.store(true, Ordering::Release);
    suspend(&rig);
    let refused = report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(refused["result"]["status"], 2, "{refused}");
    assert!(
        refused["result"]["message"]
            .as_str()
            .unwrap()
            .starts_with("Saved configuration or the current session is unavailable:"),
        "{refused}"
    );
    let saved = rig
        .request(update(2, &with_game_performance(&rig.files, 1)))
        .await;
    assert_eq!(saved["result"]["status"], 2, "{saved}");
    assert_eq!(
        rig.files.document("profiles.json")["profiles"]["applicationProfiles"][0]["overrides"]["pluggedIn"]
            ["performance"],
        2
    );
    assert_eq!(sent(&rig), 0);
    // `CapturedFresh` models the capture only; the steps use the rig's own
    // admission, as every other trigger test does.
    rig.custody.scoped_fresh.store(false, Ordering::Release);
    resume(&rig);
    let applied = report(&rig, 3, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(
        applied["result"],
        json!({"status":1,"message":format!("{RECORDED} {CONFIRMED} {AUTOMATIC}")})
    );
    let after = sent(&rig);
    let renewed = report(&rig, 4, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(renewed["result"], json!({"status":0,"message":RENEWED}));
    assert_eq!(sent(&rig), after);
    rig.stop().await;
}

/// Explicit performance and fan requests in low power submit nothing and
/// reply Blocked, as managed's do. Native stores no intent either: its
/// capture admission refuses before the request is accepted, where managed
/// stores the override and defers the write.
#[tokio::test]
async fn explicit_requests_in_low_power_submit_nothing() {
    let files = MemoryFiles::default();
    files.profiles(1);
    let rig = rig_at(files, 0);
    let overrides = rig.files.read("manual-overrides.json").unwrap();
    suspend(&rig);
    let performance = rig.set(1, value(0, 1)).await;
    assert_eq!(performance["result"]["status"], 2, "{performance}");
    let fans = rig.request(fan_request(2, Some(1), 3700)).await;
    assert_eq!(fans["result"]["status"], 2, "{fans}");
    assert_eq!(writes(&rig), 0);
    assert_eq!(rig.files.read("manual-overrides.json").unwrap(), overrides);
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
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
/// does): every row that arrives with the gate set is refused at capture,
/// before the gate is read, reads nothing and keeps the debt. Production's
/// reason is "fresh installed mutation admission is unavailable"; the rig's
/// capture factory words it "permission denied".
#[tokio::test]
async fn production_capture_rows_in_low_power_are_refused_at_capture() {
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
        assert_eq!(
            reply["result"],
            json!({"status":2,"message":"Saved configuration or the current session is unavailable: permission denied"}),
            "{row}"
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
