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
const SUPPRESSED: i64 = 4;
const RESTORE_PENDING: i64 = 6;
// Managed `ThermalConvergenceReason`.
const QUARANTINED: i64 = 4;
const HYPERBOOST_PENDING: i64 = 15;
const PENDING_RECONCILIATION: i64 = 17;
const BUDGET_EXHAUSTED: i64 = 18;
const TOMBSTONED: i64 = 19;
/// The selector another controller leaves (Silent); the Game rule saves 2.
const EXTERNAL: u8 = 5;
/// `WTS_SESSION_LOCK`.
const WTS_SESSION_LOCK: u32 = 7;

/// What the loop reads as managed's HyperBoost journal: `None` fails the
/// read, `Some(None)` is an absent file.
type Journal = Arc<Shared<Option<Option<Vec<u8>>>>>;

fn run_loop(rig: &Rig) -> Journal {
    glitching(rig).0
}
/// `run_loop` with a flag that makes the next journal read, and only that
/// one, unsafe.
fn glitching(rig: &Rig) -> (Journal, Arc<AtomicBool>) {
    let journal: Journal = Arc::new(Shared::new(Some(None)));
    let glitch = Arc::new(AtomicBool::new(false));
    let (read, once) = (journal.clone(), glitch.clone());
    rig.runtime.run_convergence(Arc::new(move || {
        if once.swap(false, Ordering::AcqRel) {
            return Ok(unsafe_journal());
        }
        read.lock()
            .unwrap()
            .clone()
            .ok_or_else(|| io::ErrorKind::PermissionDenied.into())
    }));
    (journal, glitch)
}
/// Selects thermal policy `policy`; the fixture saves Maintain.
async fn select(rig: &Rig, policy: u8) {
    let reply = rig
        .request(json!({"operation":"setThermalControlPolicy","version":90,"requestId":format!("aaaaaaaa-bbbb-cccc-dddd-0000000000c{policy}"),"policy":policy}))
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
    // Suppressed until an explicit reapply rearms it.
    let status = drift(&rig, 2).await;
    assert_eq!(convergence(&status)["state"], SUPPRESSED, "{status}");
    assert_eq!(convergence(&status)["reason"], TOMBSTONED, "{status}");
    assert_eq!(convergence(&status)["explicitReapplyRequired"], true);
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
    assert_eq!(convergence(&status)["state"], SUPPRESSED, "{status}");
    assert_eq!(convergence(&status)["reason"], BUDGET_EXHAUSTED, "{status}");
    assert_eq!(convergence(&status)["explicitReapplyRequired"], true);
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

/// A rig whose Game rule applies Performance with owned Fixed fans, with
/// the loop running under Maintain.
async fn fixed_fans() -> (Rig, Journal) {
    let rule: Value = serde_json::from_str(PRESET_AND_FIXED).unwrap();
    let rig = rig_at(files_with_game(rule), 0);
    report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    wired(&rig).await;
    let journal = run_loop(&rig);
    select(&rig, 0).await;
    (rig, journal)
}
/// `converging` with the journal flag of `glitching`.
async fn converging_glitching() -> (Rig, Journal, Arc<AtomicBool>) {
    let rig = applied_game().await;
    let (journal, glitch) = glitching(&rig);
    select(&rig, 0).await;
    (rig, journal, glitch)
}
fn fans(status: &Value) -> &Value {
    &status["status"]["confirmedState"]["fans"]
}

/// D9 for fans, as managed projects them: saved fan intent native has no
/// recipe for, ManualCurve on AC or Fixed on battery, reads as Automatic
/// over Automatic fans. It contradicts nothing, spends no claim, and a
/// real performance drift still converges alone.
#[tokio::test]
async fn unsupported_fan_intent_is_automatic_and_a_performance_drift_still_converges() {
    for battery in [false, true] {
        let (source, rule, fan) = if battery {
            ("battery", json!({"battery":{"performance":4}}), 1)
        } else {
            ("pluggedIn", json!({"pluggedIn":{"performance":2}}), 2)
        };
        let files = files_with_game(rule);
        let mut document = files.document("profiles.json");
        document["profiles"][source]["fans"] = json!(fan);
        files.put("profiles.json", document);
        files.commit_configuration();
        let rig = rig_at(files, 0);
        if battery {
            rig.port.state.lock().unwrap().actual = 0;
            rig.metadata.store(1, Ordering::Release);
        }
        report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
        wired(&rig).await;
        let _journal = run_loop(&rig);
        select(&rig, 0).await;
        let status = observe(&rig, 1).await;
        let saved = performance(&status).clone();
        assert_eq!(fans(&status), 0, "{battery} {status}");
        assert_eq!(convergence(&status)["state"], MATCHED, "{battery} {status}");
        let written = writes(&rig);
        tick(&rig).await;
        tick(&rig).await;
        assert_eq!(writes(&rig), written, "{battery}");
        let status = drift(&rig, 2).await;
        assert_eq!(
            convergence(&status)["state"],
            CONVERGING,
            "{battery} {status}"
        );
        assert_eq!(convergence(&status)["affectedComponents"], json!([0]));
        tick(&rig).await;
        let status = observe(&rig, 3).await;
        assert_eq!(performance(&status), &saved, "{battery} {status}");
        assert_eq!(convergence(&status)["state"], MATCHED, "{battery} {status}");
        let state = ledger(&rig);
        assert_eq!(state["tombstones"][0]["terminalDisposition"], 0, "{state}");
        assert_eq!(state["tombstones"][0]["contradiction"], 1, "{state}");
        assert_eq!(state["budgets"].as_array().unwrap().len(), 1, "{state}");
        // Manual fans another controller set, under the Automatic
        // projection, spend no claim either.
        let selector = device_state(&rig).0[0];
        moved(&rig, selector, 1);
        let status = observe(&rig, 4).await;
        // Firmware reports no preset under them, so only their topology
        // contradicts (managed `ManualTopologyOnly`).
        assert_eq!(convergence(&status)["state"], PAUSED, "{battery} {status}");
        assert_eq!(convergence(&status)["reason"], 16, "{battery} {status}");
        let (written, before) = (writes(&rig), raw(&rig, LEDGER));
        tick(&rig).await;
        assert_eq!(
            (writes(&rig), raw(&rig, LEDGER)),
            (written, before),
            "{battery}"
        );
        rig.stop().await;
    }
}

/// A contradiction read while a lifecycle cause is owed awaits it: its
/// reconciliation runs first, so the status names no convergence.
#[tokio::test]
async fn a_contradiction_awaits_an_owed_lifecycle_cause() {
    let (rig, _journal) = converging().await;
    // A report deferred in low power is owed to the resume tick.
    suspend(&rig);
    report(&rig, 2, None).await;
    resume(&rig);
    let status = drift(&rig, 1).await;
    assert_eq!(convergence(&status)["state"], AWAITING, "{status}");
    assert_eq!(convergence(&status)["reason"], 7, "{status}");
    abandon(rig).await;
}

/// D9 for performance: saved GPU clock tuning native has no writer for
/// leaves no destination to converge to, so a drift spends no claim.
#[tokio::test]
async fn a_performance_contradiction_without_a_recipe_spends_no_claim() {
    let (rig, _journal) = converging().await;
    let mut document = rig.files.document("profiles.json");
    document["profiles"]["applicationProfiles"][0]["overrides"]["pluggedIn"]["gpuClockTuning"] =
        json!({"enabled":true,"offsets":{"coreMegahertz":10,"memoryMegahertz":20}});
    rig.files.put("profiles.json", document);
    rig.files.commit_configuration();
    let before = raw(&rig, LEDGER);
    let status = drift(&rig, 1).await;
    assert_eq!(convergence(&status)["state"], AWAITING, "{status}");
    let written = writes(&rig);
    tick(&rig).await;
    tick(&rig).await;
    assert_eq!((writes(&rig), raw(&rig, LEDGER)), (written, before));
    abandon(rig).await;
}

/// Managed evaluates an observation: a reconciliation step's own baseline
/// read and readback are no reading, so after a report writes the
/// performance preset idle ticks evaluate nothing, capture nothing and
/// spend no claim.
#[tokio::test]
async fn a_reconciliations_own_readings_are_no_evidence() {
    let (rig, _journal) = converging().await;
    // The first tick asks for the loop's reading and the next evaluates it.
    tick(&rig).await;
    tick(&rig).await;
    let before = raw(&rig, LEDGER);
    let written = writes(&rig);
    report(&rig, 2, None).await;
    assert!(writes(&rig) > written);
    let captured = captures(&rig);
    for _ in 0..3 {
        tick(&rig).await;
    }
    assert_eq!((captures(&rig), raw(&rig, LEDGER)), (captured, before));
    rig.stop().await;
}

/// Between its periodic reads the loop reads no console user and captures
/// nothing. Each 15 s it captures once; only Maintain then reads the
/// device (D5), and the next tick evaluates that reading. Strict sends no
/// frame.
#[tokio::test]
async fn idle_ticks_read_nothing_and_only_maintain_reads_the_device() {
    for policy in [1u8, 0] {
        let rig = reported_game().await;
        let (_named, reads) = probed(&rig, true).await;
        let _journal = run_loop(&rig);
        select(&rig, policy).await;
        tick(&rig).await;
        tick(&rig).await;
        let counts = |rig: &Rig| (reads.get(), captures(rig), sent(rig));
        let idle = counts(&rig);
        for _ in 0..4 {
            tick(&rig).await;
        }
        assert_eq!(counts(&rig), idle, "{policy}");
        rig.runtime.age_convergence(Duration::from_secs(15));
        tick(&rig).await;
        let (probe, captured, frames) = counts(&rig);
        // Maintain's read admits itself too.
        assert_eq!(probe, idle.0 + 1, "{policy}");
        assert_eq!(captured == idle.1 + 1, policy != 0, "{policy} {captured}");
        assert_eq!(frames > idle.2, policy == 0, "{policy}");
        tick(&rig).await;
        let maintain = usize::from(policy == 0);
        let evaluated = (probe + maintain, captured + maintain, frames);
        assert_eq!(counts(&rig), evaluated, "{policy}");
        for _ in 0..3 {
            tick(&rig).await;
        }
        assert_eq!(counts(&rig), evaluated, "{policy}");
        rig.stop().await;
    }
}

/// D7 kept by a suspend at the attempt's adapter read, then resumed: the
/// writes before it and the ledger it left.
async fn refused_once(rig: &Rig) -> (usize, Value) {
    drift(rig, 1).await;
    let written = writes(rig);
    let custody = rig.custody.clone();
    let once = Arc::new(AtomicBool::new(false));
    rig.port.state.lock().unwrap().on_power = Some(Arc::new(move |_| {
        if !once.swap(true, Ordering::AcqRel) {
            custody.lifecycle.ingress(POWER, SUSPEND);
        }
    }));
    tick(rig).await;
    rig.port.state.lock().unwrap().on_power = None;
    let held = ledger(rig);
    assert!(!held["inFlight"].is_null(), "{held}");
    resume(rig);
    (written, held)
}

/// A due D7 retry asks for its reading once per 5 s, reading no console
/// user and capturing nothing in between; under Strict that read sends no
/// frame. A reading under Strict retires the claim Blocked without running
/// the attempt.
#[tokio::test]
async fn a_due_retry_asks_once_per_interval_and_strict_retires_it_unread() {
    let rig = reported_game().await;
    let (_named, reads) = probed(&rig, true).await;
    let _journal = run_loop(&rig);
    select(&rig, 0).await;
    let (written, held) = refused_once(&rig).await;
    select(&rig, 1).await;
    tick(&rig).await;
    let counts = |rig: &Rig| (reads.get(), captures(rig), sent(rig));
    for _ in 0..2 {
        rig.runtime.age_convergence(Duration::from_secs(5));
        let before = counts(&rig);
        let asked = (before.0 + 1, before.1 + 1, before.2);
        tick(&rig).await;
        assert_eq!(counts(&rig), asked);
        for _ in 0..3 {
            tick(&rig).await;
        }
        assert_eq!(counts(&rig), asked);
    }
    assert_eq!(ledger(&rig), held);
    observe(&rig, 2).await;
    let frames = sent(&rig);
    tick(&rig).await;
    assert_eq!((writes(&rig), sent(&rig)), (written, frames));
    let state = ledger(&rig);
    assert!(state["inFlight"].is_null(), "{state}");
    assert_eq!(state["tombstones"][0]["terminalDisposition"], 4, "{state}");
    assert_eq!(state["budgets"], held["budgets"]);
    rig.stop().await;
}

/// D7 retries once: a retry refused again is tombstoned Blocked, and no
/// third attempt runs.
#[tokio::test]
async fn a_retry_refused_again_is_blocked_and_never_run_again() {
    let (rig, _journal) = converging().await;
    drift(&rig, 1).await;
    let written = writes(&rig);
    let custody = rig.custody.clone();
    rig.port.state.lock().unwrap().on_power = Some(Arc::new(move |_| {
        custody.lifecycle.ingress(POWER, SUSPEND);
    }));
    tick(&rig).await;
    let held = ledger(&rig);
    assert!(!held["inFlight"].is_null(), "{held}");
    resume(&rig);
    rig.runtime.age_convergence(Duration::from_secs(5));
    for _ in 0..3 {
        tick(&rig).await;
        resume(&rig);
    }
    let state = ledger(&rig);
    assert!(state["inFlight"].is_null(), "{state}");
    assert_eq!(state["tombstones"][0]["terminalDisposition"], 4, "{state}");
    assert_eq!(state["budgets"], held["budgets"]);
    rig.runtime.age_convergence(Duration::from_secs(20));
    for _ in 0..3 {
        tick(&rig).await;
        resume(&rig);
    }
    assert_eq!((writes(&rig), ledger(&rig)), (written, state));
    rig.port.state.lock().unwrap().on_power = None;
    abandon(rig).await;
}

/// Before reserving, managed's executor needs a settled store and a fresh
/// write admission: an unsettled journal row, or an admission refused
/// after the capture, holds a drift with no claim spent.
#[tokio::test]
async fn an_unsettled_row_or_a_refused_admission_spends_no_claim() {
    // An earlier submission's row is unsettled.
    let rig = rig_at(files_with_game(json!({"pluggedIn":{"performance":2}})), 0);
    rig.port.state.lock().unwrap().fail_write = Some(1);
    report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    assert!(rig.storage.pending());
    wired(&rig).await;
    let _journal = run_loop(&rig);
    // The loop asks for its reading, then evaluates it.
    tick(&rig).await;
    tick(&rig).await;
    assert_eq!((writes(&rig), raw(&rig, LEDGER)), (1, None));
    rig.stop().await;
    // The capture passes, its write admission does not.
    let (rig, _journal) = converging().await;
    drift(&rig, 1).await;
    let (written, before) = (writes(&rig), raw(&rig, LEDGER));
    rig.custody.fresh_refuse.store(true, Ordering::Release);
    tick(&rig).await;
    assert_eq!((writes(&rig), raw(&rig, LEDGER)), (written, before));
    rig.custody.fresh_refuse.store(false, Ordering::Release);
    rig.stop().await;
}

/// The attempt revalidates its reservation before each setter: a ledger
/// changed between two writes refuses the next one, and the changed file
/// is kept.
#[tokio::test]
async fn a_ledger_changed_mid_recipe_refuses_the_next_write() {
    let (rig, _journal) = converging().await;
    drift(&rig, 1).await;
    let written = writes(&rig);
    let files = rig.files.clone();
    let changed = Arc::new(Shared::new(None));
    let kept = changed.clone();
    before_write(&rig, move || {
        let mut document = files.document(LEDGER);
        document["state"]["budgets"] = json!([]);
        files.put(LEDGER, document);
        *kept.lock().unwrap() = files.read(LEDGER).unwrap();
    });
    tick(&rig).await;
    assert_eq!(writes(&rig), written + 1);
    assert_eq!(raw(&rig, LEDGER), changed.lock().unwrap().clone());
    abandon(rig).await;
}

/// Once one write of an attempt is refused no later step runs, even when
/// its cause cleared: a journal that is unsafe for one read at the first
/// step's admission leaves the second step unrun and the claim Blocked.
#[tokio::test]
async fn one_refusal_refuses_every_later_step() {
    let (rig, _journal, glitch) = converging_glitching().await;
    // Silent with manual fans another controller set: both contradict.
    moved(&rig, EXTERNAL, 1);
    let status = observe(&rig, 1).await;
    assert_eq!(
        convergence(&status)["affectedComponents"],
        json!([0, 1]),
        "{status}"
    );
    let (written, frames) = (writes(&rig), sent(&rig));
    rig.port.state.lock().unwrap().on_power = Some(Arc::new(move |_| {
        glitch.store(true, Ordering::Release);
    }));
    tick(&rig).await;
    rig.port.state.lock().unwrap().on_power = None;
    // The first step read its baseline; no later step read anything.
    assert_eq!(sent(&rig) - frames, 6);
    assert_eq!(writes(&rig), written);
    let state = ledger(&rig);
    assert!(state["inFlight"].is_null(), "{state}");
    assert_eq!(state["tombstones"][0]["terminalDisposition"], 4, "{state}");
    abandon(rig).await;
}

/// A fans-only contradiction runs the fan step alone, which returns the
/// owned Fixed fans; a journal change after its first setter refuses the
/// next one, which leaves the claim to its fenced row; and Stop at its
/// adapter read admits no setter.
#[tokio::test]
async fn the_fan_step_converges_and_admits_each_setter_afresh() {
    for case in 0..3 {
        let (rig, journal) = fixed_fans().await;
        tick(&rig).await;
        moved(&rig, 2, 0);
        let rest = fans(&rig.get().await).clone();
        let status = observe(&rig, 1).await;
        assert_eq!(
            convergence(&status)["affectedComponents"],
            json!([1]),
            "{case} {status}"
        );
        let (written, frames) = (writes(&rig), sent(&rig));
        match case {
            1 => before_write(&rig, move || {
                *journal.lock().unwrap() = Some(unsafe_journal())
            }),
            2 => {
                let stop = rig.runtime.convergence_stop();
                rig.port.state.lock().unwrap().on_power =
                    Some(Arc::new(move |_| stop.store(true, Ordering::Release)));
            }
            _ => {}
        }
        tick(&rig).await;
        rig.port.state.lock().unwrap().on_power = None;
        let state = ledger(&rig);
        match case {
            0 => {
                assert!(writes(&rig) > written + 1);
                assert_eq!(fans(&observe(&rig, 2).await), &rest);
                assert_eq!(state["tombstones"][0]["terminalDisposition"], 0, "{state}");
            }
            1 => {
                assert_eq!(writes(&rig), written + 1);
                assert_eq!(sent(&rig) - frames, 6);
                assert_eq!(state["tombstones"][0]["terminalDisposition"], 3, "{state}");
            }
            _ => {
                assert_eq!(writes(&rig), written);
                // The step read the adapter, then admitted no fan read.
                assert_eq!(sent(&rig) - frames, 3);
                assert!(!state["inFlight"].is_null(), "{state}");
            }
        }
        abandon(rig).await;
    }
}

/// Stop's drain closes the loop.
#[tokio::test]
async fn the_drain_stops_the_loop() {
    let (rig, _journal) = converging().await;
    let stop = rig.runtime.convergence_stop();
    assert!(!stop.load(Ordering::Acquire));
    let _ = rig.source.lock().await.drain().await;
    assert!(stop.load(Ordering::Acquire));
}

/// A reservation a stop left before any submission is retired Confirmed by
/// the restarted Service once its reading shows the reserved target.
#[tokio::test]
async fn a_left_reservation_whose_target_is_shown_retires_confirmed() {
    let (rig, _journal) = converging().await;
    drift(&rig, 1).await;
    let stop = rig.runtime.convergence_stop();
    rig.port.state.lock().unwrap().on_power =
        Some(Arc::new(move |_| stop.store(true, Ordering::Release)));
    tick(&rig).await;
    rig.port.state.lock().unwrap().on_power = None;
    let held = ledger(&rig);
    assert!(!held["inFlight"].is_null(), "{held}");
    let (rig, _journal) = restarted(rig).await;
    report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(performance(&observe(&rig, 1).await), 2);
    tick(&rig).await;
    let state = ledger(&rig);
    assert!(state["inFlight"].is_null(), "{state}");
    assert_eq!(state["tombstones"][0]["terminalDisposition"], 0, "{state}");
    assert_eq!(state["budgets"], held["budgets"]);
    rig.stop().await;
}

/// A tombstone suppresses its contradiction by itself: with the claim's
/// budget cleared, the same drift is still not converged.
#[tokio::test]
async fn a_tombstone_suppresses_its_contradiction_without_its_budget() {
    let (rig, _journal) = converging().await;
    drift(&rig, 1).await;
    tick(&rig).await;
    let mut document = rig.files.document(LEDGER);
    document["state"]["budgets"] = json!([]);
    rig.files.put(LEDGER, document);
    let (rig, _journal) = restarted(rig).await;
    report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
    assert_eq!(performance(&observe(&rig, 2).await), 2);
    let before = raw(&rig, LEDGER);
    drift(&rig, 3).await;
    let written = writes(&rig);
    tick(&rig).await;
    tick(&rig).await;
    assert_eq!((writes(&rig), raw(&rig, LEDGER)), (written, before));
    rig.stop().await;
}

/// The loop's periodic read is bounded and a failed or stalled one is no
/// reading: the tick returns, and after a stall the next one still carries
/// lifecycle work.
#[tokio::test]
async fn a_failed_or_stalled_periodic_read_leaves_the_tick_its_work() {
    for stall in [false, true] {
        let (rig, _journal) = converging().await;
        let held = Arc::new(AtomicBool::new(false));
        {
            let mut state = rig.port.state.lock().unwrap();
            if stall {
                state.held_read = Some(held.clone());
            } else {
                state.read_worker_error = Some(Arc::new(|| io::ErrorKind::Other.into()));
            }
        }
        let frames = sent(&rig);
        tokio::time::timeout(Duration::from_secs(15), tick(&rig))
            .await
            .expect("the periodic read is bounded");
        assert!(sent(&rig) > frames, "{stall}");
        if !stall {
            // A failed job leaves the reader to its own recovery; the tick
            // itself went on.
            abandon(rig).await;
            continue;
        }
        held.store(true, Ordering::Release);
        rig.port.state.lock().unwrap().held_read = None;
        // The released job retires before the next tick stops its reader.
        for _ in 0..100 {
            let retired = {
                let state = rig.port.state.lock().unwrap();
                state.opened == state.retired
            };
            if retired {
                break;
            }
            tokio::time::sleep(Duration::from_millis(20)).await;
        }
        let before = sent(&rig);
        unplug(&rig);
        tick(&rig).await;
        assert!(sent(&rig) > before, "{stall}");
        assert_ne!(performance(&rig.get().await), 2, "{stall}");
        abandon(rig).await;
    }
}

/// A restarted Service after a crash, which handed nothing back: the
/// earlier Service's owned fans are still to be handed back, over `rig`'s
/// files and device, with the tick wired and the loop running.
async fn crashed(rig: Rig) -> (Rig, Journal) {
    let (selectors, fans) = device_state(&rig);
    let files = rig.files.clone();
    rig.origin.stop();
    drop(rig);
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

/// D9 for an owned fan row no live owner holds: after a crash, the Fixed
/// fans the earlier Service owned are still to be handed back, which only
/// that owner's continuity may do. Their contradiction against the power
/// profile's Automatic intent is not shown converging, spends no claim and
/// leaves no tombstone.
#[tokio::test]
async fn an_owned_fan_row_without_its_live_owner_spends_no_claim() {
    let (rig, _journal) = fixed_fans().await;
    tick(&rig).await;
    assert_eq!(fans(&rig.get().await), 3);
    let (rig, _journal) = crashed(rig).await;
    // Nothing was handed back, and Balanced shows no preset under manual
    // fans, so only the fans contradict.
    assert_eq!(writes(&rig), 0);
    moved(&rig, 0, 1);
    let status = observe(&rig, 1).await;
    assert_eq!(fans(&status), 3, "{status}");
    assert_eq!(convergence(&status)["state"], PAUSED, "{status}");
    assert_eq!(convergence(&status)["reason"], 21, "{status}");
    let before = raw(&rig, LEDGER);
    for n in 2..4 {
        tick(&rig).await;
        observe(&rig, n).await;
    }
    tick(&rig).await;
    assert_eq!((writes(&rig), raw(&rig, LEDGER)), (0, before));
    abandon(rig).await;
}

/// A step's own read between a publication and the tick that would
/// evaluate it retires that publication as evidence: after a report
/// applies the power profile, the next tick evaluates nothing, captures
/// nothing and leaves the ledger as it was.
#[tokio::test]
async fn a_step_read_after_a_publication_retires_it_as_evidence() {
    let (rig, _journal) = converging().await;
    tick(&rig).await;
    tick(&rig).await;
    observe(&rig, 1).await;
    let written = writes(&rig);
    report(&rig, 2, None).await;
    assert!(writes(&rig) > written);
    let (captured, before) = (captures(&rig), raw(&rig, LEDGER));
    tick(&rig).await;
    assert_eq!((captures(&rig), raw(&rig, LEDGER)), (captured, before));
    rig.stop().await;
}

/// An attempt whose first step was confirmed and whose last step was
/// refused before submitting retires its claim Blocked: the target is
/// known not to be applied, so it is not an External result. Manual fans
/// another controller set under the Automatic intent run the fan step
/// first; a suspend at the performance step's adapter read defers it.
#[tokio::test]
async fn a_last_step_refused_after_a_confirmed_one_is_blocked() {
    let (rig, _journal) = converging().await;
    tick(&rig).await;
    moved(&rig, EXTERNAL, 1);
    let status = observe(&rig, 1).await;
    assert_eq!(
        convergence(&status)["affectedComponents"],
        json!([0, 1]),
        "{status}"
    );
    let written = writes(&rig);
    let setters = Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let counted = setters.clone();
    {
        let mut state = rig.port.state.lock().unwrap();
        let previous = state.before_write.take();
        state.before_write = Some(Arc::new(move |number| {
            if let Some(previous) = &previous {
                previous(number);
            }
            counted.fetch_add(1, Ordering::AcqRel);
        }));
        let (custody, suspended) = (rig.custody.clone(), Arc::new(AtomicBool::new(false)));
        state.on_power = Some(Arc::new(move |_| {
            if setters.load(Ordering::Acquire) == FAN_SETTERS
                && !suspended.swap(true, Ordering::AcqRel)
            {
                custody.lifecycle.ingress(POWER, SUSPEND);
            }
        }));
    }
    tick(&rig).await;
    rig.port.state.lock().unwrap().on_power = None;
    rig.custody.lifecycle.ingress(POWER, RESUME);
    let state = ledger(&rig);
    assert_eq!(writes(&rig), written + FAN_SETTERS, "{state}");
    assert!(state["inFlight"].is_null(), "{state}");
    assert_eq!(state["tombstones"][0]["terminalDisposition"], 4, "{state}");
    abandon(rig).await;
}
/// The fan step's setters returning manual fans to Automatic.
const FAN_SETTERS: usize = 2;

/// Delta review F1 for convergence: a pending GPU clock confirmation holds
/// a drift before its reservation, so no claim is spent and nothing is
/// written, and the drift converges once a read resolves the management.
/// Before the fix the attempt reserved, its step was refused, and the claim
/// was tombstoned Blocked.
#[cfg(feature = "service-gpu-clock-tuning-v1")]
#[tokio::test]
async fn a_pending_gpu_confirmation_holds_a_drift_with_no_claim_spent() {
    use crate::installed::readiness::gpu_clock_offsets::fake::Fake;
    use crate::installed::readiness::tests::mutation::gpu_clock_tuning::{
        compose, leave_uncertain,
    };
    let (rig, _journal) = converging().await;
    let fake = Fake::new(0, 0);
    let lane = compose(&rig, &fake, Arc::default());
    leave_uncertain(&rig, &lane, &fake);
    drift(&rig, 1).await;
    let (written, before) = (writes(&rig), raw(&rig, LEDGER));
    tick(&rig).await;
    tick(&rig).await;
    assert_eq!((writes(&rig), raw(&rig, LEDGER)), (written, before));
    fake.with(|d| d.queries_fail = false);
    let _ = lane.call_for_tests(|m| {
        m.rearm();
        let _ = m.read();
    });
    drift(&rig, 2).await;
    tick(&rig).await;
    let state = ledger(&rig);
    assert!(state["inFlight"].is_null(), "{state}");
    assert_eq!(state["tombstones"][0]["terminalDisposition"], 0, "{state}");
    assert_eq!(performance(&rig.get().await), 2);
    rig.stop().await;
}

/// Delta review F1 for a D7 retry: a retry due under a pending GPU clock
/// confirmation is retired Blocked without running, as one under an
/// unsettled journal row is, and reads nothing for its attempt.
#[cfg(feature = "service-gpu-clock-tuning-v1")]
#[tokio::test]
async fn a_retry_due_under_a_pending_gpu_confirmation_is_blocked_unrun() {
    use crate::installed::readiness::gpu_clock_offsets::fake::Fake;
    use crate::installed::readiness::tests::mutation::gpu_clock_tuning::{
        compose, leave_uncertain,
    };
    let (rig, _journal) = converging().await;
    let (written, held) = refused_once(&rig).await;
    let fake = Fake::new(0, 0);
    let lane = compose(&rig, &fake, Arc::default());
    leave_uncertain(&rig, &lane, &fake);
    rig.runtime.age_convergence(Duration::from_secs(5));
    // Due: the tick asks for the reading the next one evaluates.
    tick(&rig).await;
    let frames = sent(&rig);
    tick(&rig).await;
    assert_eq!((writes(&rig), sent(&rig)), (written, frames));
    let state = ledger(&rig);
    assert!(state["inFlight"].is_null(), "{state}");
    assert_eq!(state["tombstones"][0]["terminalDisposition"], 4, "{state}");
    assert_eq!(state["budgets"], held["budgets"]);
    abandon(rig).await;
}

#[path = "thermal_reapply.rs"]
mod reapply;
