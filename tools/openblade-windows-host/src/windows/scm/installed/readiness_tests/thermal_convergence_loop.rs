//! The thermal convergence loop (`convergence_loop`) after the lifecycle
//! tick: a confirmed contradiction of saved intent under an admitted
//! Maintain policy reserves one claim in the shared ledger, applies only
//! the contradicting subsystems and retires the claim with a tombstone.
//! Each case drives the tick over the rig's store, lifecycle and scripted
//! mailbox; no hardware or operating-system supplier is reached.
use super::*;
use std::sync::Mutex as Shared;

const LEDGER: &str = "thermal-convergence.json";
// Managed `ThermalConvergenceState`.
const MATCHED: i64 = 0;
const AWAITING: i64 = 1;
const CONVERGING: i64 = 2;
const PAUSED: i64 = 3;
const RESTORE_PENDING: i64 = 6;
// Managed `ThermalConvergenceReason`.
const QUARANTINED: i64 = 4;
const HYPERBOOST_PENDING: i64 = 15;
const PENDING_RECONCILIATION: i64 = 17;
const UNAVAILABLE: i64 = 21;
/// The selector another controller leaves (Silent); the Game rule saves 2.
const EXTERNAL: u8 = 5;
/// `WTS_SESSION_LOCK`.
const WTS_SESSION_LOCK: u32 = 7;

/// What the loop reads as managed's HyperBoost journal: `None` fails the
/// read, `Some(None)` is an absent file.
type Journal = Arc<Shared<Option<Option<Vec<u8>>>>>;

fn run_loop(rig: &Rig) -> Journal {
    let journal: Journal = Arc::new(Shared::new(Some(None)));
    let read = journal.clone();
    rig.runtime.run_convergence(Arc::new(move || {
        read.lock()
            .unwrap()
            .clone()
            .ok_or_else(|| io::ErrorKind::PermissionDenied.into())
    }));
    journal
}
/// Selects thermal policy `policy`; the fixture saves Maintain.
async fn select(rig: &Rig, policy: u8) {
    let reply = rig
        .request(json!({"operation":"setThermalControlPolicy","version":85,"requestId":format!("aaaaaaaa-bbbb-cccc-dddd-0000000000c{policy}"),"policy":policy}))
        .await;
    assert_eq!(reply["success"], true, "{reply}");
}
fn convergence(status: &Value) -> &Value {
    &status["status"]["thermalConvergence"]
}
fn ledger(rig: &Rig) -> Value {
    rig.files.document(LEDGER)["state"].clone()
}
/// Another controller changes the preset, which the next reading sees.
async fn drift(rig: &Rig, n: u8) -> Value {
    moved(rig, EXTERNAL, 0);
    observe(rig, n).await
}
/// A rig left with a submission it cannot resolve, drained as Stop does.
async fn abandon(rig: Rig) {
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
}
/// `applied_game` with the loop running under Maintain.
async fn converging() -> (Rig, Journal) {
    let rig = applied_game().await;
    let journal = run_loop(&rig);
    select(&rig, 0).await;
    (rig, journal)
}

/// A drift is converged once, confirmed by readback and tombstoned, and the
/// same drift again is suppressed by that tombstone.
#[tokio::test]
async fn external_drift_converges_once_and_its_tombstone_holds() {
    let (rig, _journal) = converging().await;
    let status = drift(&rig, 1).await;
    assert_ne!(performance(&status), 2, "{status}");
    assert_eq!(convergence(&status)["state"], CONVERGING, "{status}");
    assert_eq!(convergence(&status)["affectedComponents"], json!([0]));
    let written = writes(&rig);
    tick(&rig).await;
    assert!(writes(&rig) > written);
    let status = rig.get().await;
    assert_eq!(performance(&status), 2, "{status}");
    assert_eq!(convergence(&status)["state"], MATCHED, "{status}");
    let state = ledger(&rig);
    assert!(state["inFlight"].is_null(), "{state}");
    assert_eq!(state["tombstones"].as_array().unwrap().len(), 1, "{state}");
    assert_eq!(state["tombstones"][0]["terminalDisposition"], 0, "{state}");
    assert_eq!(state["budgets"][0]["consumedClaims"], 1, "{state}");
    // Nothing new to evaluate.
    let written = writes(&rig);
    tick(&rig).await;
    assert_eq!(writes(&rig), written);
    // Until native serves the explicit reapply, a suppression is published
    // as a contradiction native cannot hold (D6).
    let status = drift(&rig, 2).await;
    assert_eq!(convergence(&status)["state"], PAUSED, "{status}");
    assert_eq!(convergence(&status)["reason"], UNAVAILABLE, "{status}");
    tick(&rig).await;
    assert_eq!(writes(&rig), written);
    assert_eq!(ledger(&rig)["tombstones"], state["tombstones"]);
    rig.stop().await;
}

fn raw(rig: &Rig, name: &str) -> Option<Vec<u8>> {
    rig.files.read(name).unwrap()
}
fn unsafe_journal() -> Option<Vec<u8>> {
    // Managed `RestorePending`, schema 8.
    Some(
        serde_json::to_vec(&json!({
            "schemaVersion": 8, "state": 2, "baseMode": 1,
            "updatedAtUtc": "2026-11-24T00:00:00+00:00", "requiresCoolingPad": false,
            "restorePerformance": 1, "restoreFanControl": {"mode": 0},
        }))
        .unwrap(),
    )
}
/// Runs `change` once before write `first` of the fixture, after the rig's
/// own write checks.
fn before_write(rig: &Rig, change: impl Fn() + Send + Sync + 'static) {
    let mut state = rig.port.state.lock().unwrap();
    let previous = state.before_write.take();
    let first = state.writes + 1;
    state.before_write = Some(Arc::new(move |number| {
        if let Some(previous) = &previous {
            previous(number);
        }
        if number == first {
            change();
        }
    }));
}
/// A restarted Service over `rig`'s files and device, with the tick wired
/// and the loop running.
async fn restarted(rig: Rig) -> (Rig, Journal) {
    let (selectors, fans) = device_state(&rig);
    let files = rig.files.clone();
    abandon(rig).await;
    let rig = Rig::restarted(files);
    *rig.custody.origin_for_fan.lock().unwrap() = Some(rig.origin.clone());
    {
        let mut state = rig.port.state.lock().unwrap();
        state.selectors = selectors;
        state.fans = fans;
    }
    wired(&rig).await;
    let journal = run_loop(&rig);
    rig.get().await;
    tick(&rig).await;
    (rig, journal)
}
fn device_state(rig: &Rig) -> ([u8; 2], [u8; 2]) {
    let state = rig.port.state.lock().unwrap();
    (state.selectors, state.fans)
}

/// Strict policy, an unsafe HyperBoost journal and an unreadable one each
/// hold a drift with no claim spent; an absent journal is safe.
#[tokio::test]
async fn strict_policy_and_an_unsafe_journal_hold_a_drift() {
    let rig = applied_game().await;
    let journal = run_loop(&rig);
    select(&rig, 1).await;
    let status = drift(&rig, 1).await;
    assert_eq!(convergence(&status)["policy"], 1, "{status}");
    let written = writes(&rig);
    tick(&rig).await;
    assert_eq!(writes(&rig), written);
    assert_eq!(raw(&rig, LEDGER), None);
    select(&rig, 0).await;
    let before = raw(&rig, LEDGER);
    for (n, read) in [(2, Some(unsafe_journal())), (3, None)] {
        *journal.lock().unwrap() = read;
        let status = observe(&rig, n).await;
        assert_eq!(convergence(&status)["state"], RESTORE_PENDING, "{status}");
        assert_eq!(convergence(&status)["reason"], HYPERBOOST_PENDING);
        assert_eq!(convergence(&status)["affectedComponents"], json!([0, 1]));
        tick(&rig).await;
        assert_eq!(writes(&rig), written);
        assert_eq!(raw(&rig, LEDGER), before);
    }
    *journal.lock().unwrap() = Some(None);
    assert_eq!(convergence(&observe(&rig, 4).await)["state"], CONVERGING);
    tick(&rig).await;
    assert_eq!(performance(&rig.get().await), 2);
    rig.stop().await;
}

/// A HyperBoost journal change, a competing controller or Strict policy
/// arriving after the attempt's first write refuses its next one. The
/// submission that may have landed stays fenced by its journal row, so the
/// claim retires ReconciliationUnavailable and nothing is replayed.
#[tokio::test]
async fn a_change_mid_recipe_refuses_the_next_write_and_nothing_is_replayed() {
    for case in 0..3 {
        let (rig, journal) = converging().await;
        drift(&rig, 1).await;
        let written = writes(&rig);
        let (files, custody) = (rig.files.clone(), rig.custody.clone());
        before_write(&rig, move || match case {
            0 => *journal.lock().unwrap() = Some(unsafe_journal()),
            1 => custody.controller.store(true, Ordering::Release),
            _ => {
                let mut document = files.document("battery-preferences.json");
                document["preferences"]["thermalControlPolicy"] = json!(1);
                files.put("battery-preferences.json", document);
                files.commit_configuration();
            }
        });
        tick(&rig).await;
        assert_eq!(writes(&rig), written + 1, "{case}");
        let state = ledger(&rig);
        assert!(state["inFlight"].is_null(), "{case} {state}");
        assert_eq!(state["terminalDisposition"], 3, "{case} {state}");
        assert_eq!(state["tombstones"][0]["terminalDisposition"], 3);
        assert_eq!(state["budgets"][0]["consumedClaims"], 1);
        rig.runtime.age_convergence(Duration::from_secs(20));
        tick(&rig).await;
        assert_eq!(writes(&rig), written + 1, "{case}");
        assert_eq!(ledger(&rig), state, "{case}");
        abandon(rig).await;
    }
}

/// Stop during an attempt admits no later write and leaves the reservation.
/// Before any submission, the restarted Service reconciles it read-only
/// from its first reading, which is not the Game target: its own start
/// applies the power profile, since no application was reported to it.
/// After one, the journal row fences the store and nothing is reconciled
/// or written.
#[tokio::test]
async fn a_stop_mid_attempt_leaves_its_reservation_to_a_read_only_restart() {
    for submitted in [false, true] {
        let (rig, _journal) = converging().await;
        drift(&rig, 1).await;
        let written = writes(&rig);
        let stop = rig.runtime.convergence_stop();
        if submitted {
            before_write(&rig, move || stop.store(true, Ordering::Release));
        } else {
            rig.port.state.lock().unwrap().on_power =
                Some(Arc::new(move |_| stop.store(true, Ordering::Release)));
        }
        tick(&rig).await;
        rig.port.state.lock().unwrap().on_power = None;
        assert_eq!(writes(&rig), written + usize::from(submitted));
        let held = ledger(&rig);
        assert!(!held["inFlight"].is_null(), "{held}");
        let status = rig.get().await;
        assert_eq!(convergence(&status)["state"], AWAITING, "{status}");
        let (rig, _journal) = restarted(rig).await;
        if submitted {
            for _ in 0..2 {
                rig.get().await;
                tick(&rig).await;
            }
            assert_eq!(writes(&rig), 0);
            assert_eq!(ledger(&rig), held);
            abandon(rig).await;
            continue;
        }
        observe(&rig, 1).await;
        tick(&rig).await;
        let state = ledger(&rig);
        assert!(state["inFlight"].is_null(), "{state}");
        assert_eq!(state["tombstones"][0]["terminalDisposition"], 2, "{state}");
        assert_eq!(state["budgets"], held["budgets"]);
        assert_eq!(performance(&observe(&rig, 2).await), 1);
        let written = writes(&rig);
        tick(&rig).await;
        assert_eq!((writes(&rig), ledger(&rig)), (written, state));
        rig.stop().await;
    }
}

/// D7: a suspend or a lock while the attempt's step reads the adapter
/// refuses it before any submission. The claim stays reserved for one retry at least
/// `RETRY_DELAY` later, from a fresh reading: the same contradiction runs
/// again with the claim already spent, and one an external change already
/// resolved retires the claim Confirmed with no write.
#[tokio::test]
async fn a_refusal_before_any_submission_is_retried_once_from_a_fresh_reading() {
    for (lock, resolved) in [(false, false), (false, true), (true, false)] {
        let (rig, _journal) = converging().await;
        drift(&rig, 1).await;
        let written = writes(&rig);
        let custody = rig.custody.clone();
        let (kind, notice) = if lock {
            (SESSION_CHANGE, WTS_SESSION_LOCK)
        } else {
            (POWER, SUSPEND)
        };
        let once = Arc::new(AtomicBool::new(false));
        let fired = once.clone();
        rig.port.state.lock().unwrap().on_power = Some(Arc::new(move |_| {
            if !fired.swap(true, Ordering::AcqRel) {
                custody.lifecycle.ingress(kind, notice);
            }
        }));
        tick(&rig).await;
        rig.port.state.lock().unwrap().on_power = None;
        assert!(once.load(Ordering::Acquire), "the step read the adapter");
        assert_eq!(writes(&rig), written);
        let held = ledger(&rig);
        assert!(!held["inFlight"].is_null(), "{held}");
        assert_eq!(held["budgets"][0]["consumedClaims"], 1);
        if lock {
            unlock(&rig);
        } else {
            resume(&rig);
        }
        if resolved {
            moved(&rig, 2, 0);
        }
        // Not before its delay, even from a new reading.
        observe(&rig, 2).await;
        tick(&rig).await;
        assert_eq!((writes(&rig), ledger(&rig)), (written, held.clone()));
        rig.runtime.age_convergence(Duration::from_secs(5));
        // Due: the tick asks for the reading the next one evaluates.
        tick(&rig).await;
        tick(&rig).await;
        let state = ledger(&rig);
        assert!(state["inFlight"].is_null(), "{state}");
        assert_eq!(state["tombstones"][0]["terminalDisposition"], 0, "{state}");
        assert_eq!(state["budgets"], held["budgets"]);
        assert_eq!(performance(&rig.get().await), 2);
        assert_eq!(writes(&rig) > written, !resolved);
        rig.stop().await;
    }
}

/// A claim spent on a contradiction suppresses it after its tombstone is
/// gone: managed's budget for that component at this destination.
#[tokio::test]
async fn a_spent_budget_suppresses_the_same_contradiction() {
    let (rig, _journal) = converging().await;
    drift(&rig, 1).await;
    tick(&rig).await;
    let mut document = rig.files.document(LEDGER);
    document["state"]["tombstones"] = json!([]);
    document["state"]["terminalDisposition"] = Value::Null;
    document["state"]["terminalAffectedComponents"] = json!([]);
    rig.files.put(LEDGER, document);
    // A running Service holds the ledger it read; a restarted one reads
    // this one. Its start applies the power profile until the Game is
    // reported again.
    let (rig, _journal) = restarted(rig).await;
    report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(performance(&observe(&rig, 2).await), 2);
    let before = raw(&rig, LEDGER);
    let status = drift(&rig, 3).await;
    // D6 until native serves the explicit reapply.
    assert_eq!(convergence(&status)["reason"], UNAVAILABLE, "{status}");
    let written = writes(&rig);
    tick(&rig).await;
    tick(&rig).await;
    assert_eq!(writes(&rig), written);
    assert_eq!(raw(&rig, LEDGER), before);
    rig.stop().await;
}

/// A damaged ledger, or one of a newer schema, closes convergence: the
/// status is quarantined, nothing is written and the file is kept. A
/// running Service holds the ledger it read, and its next replacement
/// refuses a file that changed under it; a restarted one reads it.
#[tokio::test]
async fn a_damaged_or_newer_ledger_closes_convergence_and_is_kept() {
    for (newer, restart) in [(false, false), (false, true), (true, true)] {
        let (rig, _journal) = converging().await;
        // The loop's first tick reads the device (D5); no later one is due.
        tick(&rig).await;
        if newer {
            rig.files
                .put(LEDGER, json!({"schemaVersion": 99, "state": {}}));
        } else {
            let damaged = b"{\"schemaVersion\":1";
            let held = raw(&rig, LEDGER);
            rig.files.replace(LEDGER, held.as_deref(), damaged).unwrap();
        }
        let before = raw(&rig, LEDGER);
        let (rig, _journal) = if restart {
            let restarted = restarted(rig).await;
            // The first reading starts its user.
            observe(&restarted.0, 0).await;
            tick(&restarted.0).await;
            restarted
        } else {
            (rig, _journal)
        };
        let status = drift(&rig, 1).await;
        let (read, written) = (sent(&rig), writes(&rig));
        if !restart {
            // The reservation fails before any step reads the device.
            tick(&rig).await;
            assert_eq!(sent(&rig), read);
        }
        let status = if restart { status } else { rig.get().await };
        assert_eq!(convergence(&status)["reason"], QUARANTINED, "{status}");
        tick(&rig).await;
        tick(&rig).await;
        assert_eq!(writes(&rig), written);
        assert_eq!(raw(&rig, LEDGER), before);
        rig.stop().await;
    }
}

/// The no-settle ambiguity: a later write of the attempt fails after its
/// first may have landed. Its journal row fences the store, so the claim
/// retires ReconciliationUnavailable and the status waits for that row's
/// settlement; neither the running Service nor a restarted one writes again.
#[tokio::test]
async fn an_ambiguous_submission_is_unavailable_and_never_replayed() {
    let (rig, _journal) = converging().await;
    drift(&rig, 1).await;
    let written = writes(&rig);
    rig.port.state.lock().unwrap().fail_write = Some(written + 2);
    tick(&rig).await;
    assert_eq!(writes(&rig), written + 2);
    let state = ledger(&rig);
    assert!(state["inFlight"].is_null(), "{state}");
    assert_eq!(state["tombstones"][0]["terminalDisposition"], 3, "{state}");
    let status = rig.get().await;
    assert_eq!(convergence(&status)["state"], PAUSED, "{status}");
    assert_eq!(convergence(&status)["reason"], PENDING_RECONCILIATION);
    rig.runtime.age_convergence(Duration::from_secs(20));
    tick(&rig).await;
    tick(&rig).await;
    assert_eq!((writes(&rig), ledger(&rig)), (written + 2, state.clone()));
    let (rig, _journal) = restarted(rig).await;
    for n in 1..3 {
        observe(&rig, n).await;
        tick(&rig).await;
    }
    assert_eq!((writes(&rig), ledger(&rig)), (0, state));
    abandon(rig).await;
}
