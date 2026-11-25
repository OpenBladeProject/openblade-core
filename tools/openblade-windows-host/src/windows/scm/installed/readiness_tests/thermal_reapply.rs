//! Installed `reapplyChangedSettings` through the real dispatcher over the
//! convergence rig: managed's replies, the ledger it leaves and its
//! performance writes, replayed from `tests/data/thermal-reapply-vectors.json`,
//! and the rearm's compare-and-swap and two blocking gates. No hardware or
//! operating-system supplier is reached.
use super::*;
use crate::installed::readiness::thermal_mutation::{
    FANS_BRIDGE, IN_FLIGHT, NOT_RETIRED, PENDING, PERFORMANCE_BRIDGE, UNCLASSIFIED,
};

const VECTORS: &str = include_str!("../../../../../tests/data/thermal-reapply-vectors.json");

fn reapply_request(n: u8) -> Value {
    json!({"operation":"reapplyChangedSettings","version":90,"requestId":format!("ffffffff-bbbb-cccc-dddd-{n:012}")})
}
async fn reapply(rig: &Rig, n: u8) -> Value {
    let reply = rig.request(reapply_request(n)).await;
    assert_eq!(reply["success"], true, "{reply}");
    reply["result"].clone()
}
/// The ledger as the managed rows record it; no file is the empty ledger.
fn summary(rig: &Rig) -> Value {
    let Some(bytes) = raw(rig, LEDGER) else {
        return json!({"tombstones":0,"budgets":0,"terminalDisposition":null,"inFlight":false});
    };
    let state = &serde_json::from_slice::<Value>(&bytes).unwrap()["state"];
    json!({
        "tombstones": state["tombstones"].as_array().unwrap().len(),
        "budgets": state["budgets"].as_array().unwrap().len(),
        "terminalDisposition": state["terminalDisposition"],
        "inFlight": !state["inFlight"].is_null(),
    })
}
/// The loop converges one drift: a Confirmed tombstone and a spent budget.
async fn tombstoned(rig: &Rig) {
    drift(rig, 1).await;
    tick(rig).await;
    assert_eq!(ledger(rig)["tombstones"].as_array().unwrap().len(), 1);
}
/// A stop at the attempt's adapter read leaves its reservation in flight.
async fn reserved(rig: &Rig) {
    drift(rig, 1).await;
    let stop = rig.runtime.convergence_stop();
    rig.port.state.lock().unwrap().on_power =
        Some(Arc::new(move |_| stop.store(true, Ordering::Release)));
    tick(rig).await;
    rig.port.state.lock().unwrap().on_power = None;
    assert!(!ledger(rig)["inFlight"].is_null());
}
/// An explicit performance write whose submission is ambiguous leaves an
/// unsettled journal row (managed's pending write confirmation).
async fn fenced(rig: &Rig) {
    let n = writes(rig);
    rig.port.state.lock().unwrap().fail_write = Some(n + 1);
    rig.set(9, value(1, 1)).await;
    assert!(rig.storage.pending());
}
/// The ledger file changed under the running Service.
fn changed_on_disk(rig: &Rig) {
    let mut bytes = raw(rig, LEDGER).unwrap();
    bytes.push(b' ');
    rig.files.bytes.lock().unwrap().insert(LEDGER.into(), bytes);
}

/// The rig for one managed row: the Game rule applied under Maintain, the
/// row's ledger and its drift, pending write or changed file. A ledger
/// holding only a budget or only a tombstone is edited on disk and read by
/// a restarted Service.
async fn scenario(row: &Value) -> Rig {
    let (rig, _journal) = converging().await;
    match row["seed"].as_str() {
        Some("tombstoned") => tombstoned(&rig).await,
        Some("inFlight") => reserved(&rig).await,
        Some(seed @ ("budget" | "tombstone")) => {
            tombstoned(&rig).await;
            let mut document = rig.files.document(LEDGER);
            if seed == "budget" {
                document["state"]["tombstones"] = json!([]);
                document["state"]["terminalDisposition"] = Value::Null;
                document["state"]["terminalAffectedComponents"] = json!([]);
            } else {
                document["state"]["budgets"] = json!([]);
            }
            rig.files.put(LEDGER, document);
            // A running Service holds the ledger it read; a restarted one
            // reads this one, and applies the Game rule once it is reported.
            let (rig, _journal) = restarted(rig).await;
            report(&rig, 2, Some(r"C:\Games\Game.exe")).await;
            assert_eq!(performance(&observe(&rig, 2).await), 2);
            return finish(rig, row).await;
        }
        _ => {}
    }
    finish(rig, row).await
}
async fn finish(rig: Rig, row: &Value) -> Rig {
    if row["drift"] == true {
        drift(&rig, 3).await;
    }
    if row["changedOnDisk"] == true {
        changed_on_disk(&rig);
    }
    if row["pending"] == true {
        fenced(&rig).await;
    }
    rig
}

#[tokio::test]
async fn managed_reapply_vectors_replay_through_the_dispatcher() {
    let vectors: Value = serde_json::from_str(VECTORS).unwrap();
    let mut replayed = 0;
    for row in vectors["replies"].as_array().unwrap() {
        let name = row["name"].as_str().unwrap();
        let rig = scenario(row).await;
        if row["sessionChanged"] == true {
            // Native admits a mutation from the control session only, so a
            // caller outside it is closed without a reply where managed's
            // `stillControlsSession` answers Blocked; nothing is written.
            let prior = (writes(&rig), raw(&rig, LEDGER));
            let other = Caller {
                control_session: Some(9),
                ..USER
            };
            assert!(unanswered(&rig, &reapply_request(1), other).await, "{name}");
            assert_eq!((writes(&rig), raw(&rig, LEDGER)), prior, "{name}");
            abandon(rig).await;
            continue;
        }
        let written = writes(&rig);
        let result = reapply(&rig, 1).await;
        assert_eq!(result, row["result"], "{name}");
        assert_eq!(summary(&rig), row["ledger"], "{name}");
        assert_eq!(json!(writes(&rig) > written), row["wrote"], "{name}");
        replayed += 1;
        abandon(rig).await;
    }
    assert_eq!(replayed, 10);
}

/// Whether the Service closes `request` from `caller` without a reply.
async fn unanswered(rig: &Rig, request: &Value, caller: Caller) -> bool {
    let (mut client, server) = duplex(65536);
    let source = rig.source.clone();
    let publications = source.lock().await.publications();
    let served = serve_connection(
        Box::new(SidStream {
            inner: test_stream(server, caller, Fault::None),
            sid: SID.to_owned(),
        }),
        source,
        publications,
        Rc::new(Subscriptions::new(2)),
    );
    let payload = serde_json::to_vec(request).unwrap();
    let exchange = async {
        crate::service_frame::write_payload(&mut client, &payload)
            .await
            .unwrap();
        let mut reply = Vec::new();
        tokio::io::AsyncReadExt::read_to_end(&mut client, &mut reply)
            .await
            .unwrap();
        reply
    };
    let (served, reply) = tokio::join!(served, exchange);
    assert!(!matches!(served, Err(ExchangeError::Runtime(_))));
    reply.is_empty()
}

/// A rearm clears the suppression for the convergence loop: the same drift
/// that the tombstone suppressed converges again afterwards, with the claim
/// it spends counted afresh.
#[tokio::test]
async fn a_rearm_lets_the_same_drift_converge_again() {
    let (rig, _journal) = converging().await;
    tombstoned(&rig).await;
    let status = drift(&rig, 2).await;
    assert_eq!(convergence(&status)["state"], SUPPRESSED, "{status}");
    assert_eq!(convergence(&status)["reason"], TOMBSTONED, "{status}");
    assert_eq!(convergence(&status)["explicitReapplyRequired"], true);
    // The reapply rearms, then applies the drift itself, spending no claim.
    let written = writes(&rig);
    let result = reapply(&rig, 1).await;
    assert_eq!(
        result,
        json!({"status":1,"message":"OpenBlade reapplied Power mode from the current settings."})
    );
    assert!(writes(&rig) > written);
    assert_eq!(performance(&rig.get().await), 2);
    let state = ledger(&rig);
    assert_eq!(state["tombstones"], json!([]), "{state}");
    assert_eq!(state["budgets"], json!([]), "{state}");
    let status = drift(&rig, 4).await;
    assert_eq!(convergence(&status)["state"], CONVERGING, "{status}");
    let written = writes(&rig);
    tick(&rig).await;
    assert!(writes(&rig) > written);
    let state = ledger(&rig);
    assert_eq!(state["budgets"][0]["consumedClaims"], 1, "{state}");
    rig.stop().await;
}

/// The rearm is a compare-and-swap on the bytes the store holds: a ledger
/// changed under it is kept, the reply blocks before any reapply, and the
/// ledger is closed (Strict and quarantined in status).
#[tokio::test]
async fn a_rearm_that_cannot_persist_blocks_and_keeps_the_file() {
    let (rig, _journal) = converging().await;
    tombstoned(&rig).await;
    drift(&rig, 2).await;
    changed_on_disk(&rig);
    let (kept, written) = (raw(&rig, LEDGER), writes(&rig));
    let result = reapply(&rig, 1).await;
    assert_eq!(result["status"], 2, "{result}");
    assert_eq!(result["message"], NOT_RETIRED, "{result}");
    assert_eq!((raw(&rig, LEDGER), writes(&rig)), (kept, written));
    // Subscribers see the closed ledger at once, not at the next reading.
    let published = rig.source.lock().await.publications().unwrap();
    let active = serde_json::to_value(&published.borrow().active).unwrap();
    assert_eq!(
        active["thermalConvergence"]["reason"], QUARANTINED,
        "{active}"
    );
    let status = rig.get().await;
    assert_eq!(convergence(&status)["reason"], QUARANTINED, "{status}");
    rig.stop().await;
}

/// An unsettled journal row blocks before the ledger is read or rearmed,
/// and a reservation in flight blocks before its rearm: neither writes.
#[tokio::test]
async fn a_pending_row_or_a_reservation_blocks_before_anything_is_written() {
    for pending in [true, false] {
        let (rig, _journal) = converging().await;
        if pending {
            tombstoned(&rig).await;
            drift(&rig, 2).await;
            fenced(&rig).await;
        } else {
            reserved(&rig).await;
        }
        let (kept, written) = (raw(&rig, LEDGER), writes(&rig));
        let result = reapply(&rig, 1).await;
        assert_eq!(result["status"], 2, "{pending} {result}");
        let message = if pending { PENDING } else { IN_FLIGHT };
        assert_eq!(result["message"], message, "{pending} {result}");
        assert_eq!(
            (raw(&rig, LEDGER), writes(&rig)),
            (kept, written),
            "{pending}"
        );
        abandon(rig).await;
    }
}

/// The convergence member subscribers see now.
async fn published(rig: &Rig) -> Value {
    let published = rig.source.lock().await.publications().unwrap();
    let active = serde_json::to_value(&published.borrow().active).unwrap();
    active["thermalConvergence"].clone()
}

/// A rearm is published before any step runs, as managed publishes right
/// after its rearm: a client that disconnects while the first step's
/// adapter read is held drops the request there, and subscribers already
/// see the suppression retired. Nothing was written.
#[tokio::test]
async fn a_rearm_is_published_before_a_disconnect_drops_the_request() {
    let (rig, _journal) = converging().await;
    tombstoned(&rig).await;
    drift(&rig, 2).await;
    assert_eq!(published(&rig).await["state"], SUPPRESSED);
    let written = writes(&rig);
    let (mut client, server) = duplex(65536);
    let source = rig.source.clone();
    let publications = source.lock().await.publications();
    // The read's job thread waits under the fixture's lock until the
    // request was dropped, so no later step can run first.
    let (reached, release) = (
        Arc::new(AtomicBool::new(false)),
        Arc::new(AtomicBool::new(false)),
    );
    let (at, held) = (reached.clone(), release.clone());
    rig.port.state.lock().unwrap().on_power = Some(Arc::new(move |_| {
        if !at.swap(true, Ordering::AcqRel) {
            let end = std::time::Instant::now() + HANG_GUARD;
            while !held.load(Ordering::Acquire) && std::time::Instant::now() < end {
                std::thread::sleep(Duration::from_millis(1));
            }
        }
    }));
    let served = serve_connection(
        Box::new(SidStream {
            inner: test_stream(server, USER, Fault::None),
            sid: SID.to_owned(),
        }),
        source,
        publications,
        Rc::new(Subscriptions::new(2)),
    );
    let payload = serde_json::to_vec(&reapply_request(1)).unwrap();
    let exchange = async move {
        crate::service_frame::write_payload(&mut client, &payload)
            .await
            .unwrap();
        let end = Instant::now() + HANG_GUARD;
        while !reached.load(Ordering::Acquire) {
            assert!(Instant::now() < end);
            tokio::time::sleep(Duration::from_millis(1)).await;
        }
        drop(client);
    };
    let (served, ()) = tokio::join!(served, exchange);
    assert!(!matches!(served, Err(ExchangeError::Runtime(_))));
    let member = published(&rig).await;
    release.store(true, Ordering::Release);
    assert_ne!(member["state"], SUPPRESSED, "{member}");
    assert_eq!(member["explicitReapplyRequired"], false, "{member}");
    rig.port.state.lock().unwrap().on_power = None;
    assert_eq!(writes(&rig), written);
    let state = ledger(&rig);
    assert_eq!(state["tombstones"], json!([]), "{state}");
    abandon(rig).await;
}

/// A rearm with no drift left replies Stored, and the published member is
/// the rearmed ledger's: no suppression, no reapply asked for.
#[tokio::test]
async fn a_stored_reply_after_a_rearm_publishes_the_rearmed_ledger() {
    let (rig, _journal) = converging().await;
    tombstoned(&rig).await;
    let written = writes(&rig);
    let result = reapply(&rig, 1).await;
    assert_eq!(
        result,
        json!({"status":0,"message":"No settings changed outside OpenBlade."})
    );
    assert_eq!(writes(&rig), written);
    assert_eq!(summary(&rig)["tombstones"], 0);
    let member = published(&rig).await;
    assert_eq!(member["state"], MATCHED, "{member}");
    assert_eq!(member["explicitReapplyRequired"], false, "{member}");
    assert_eq!(member["reason"], 0, "{member}");
    rig.stop().await;
}

/// Unknown Windows power after a drift blocks with managed's
/// unclassified-adapter reply, before any step reads the device.
#[tokio::test]
async fn unknown_windows_power_blocks_the_reapply_unclassified() {
    let (rig, _journal) = converging().await;
    drift(&rig, 1).await;
    let (written, frames) = (writes(&rig), sent(&rig));
    rig.port.state.lock().unwrap().windows_on_battery = Some(None);
    let result = reapply(&rig, 1).await;
    rig.port.state.lock().unwrap().windows_on_battery = None;
    assert_eq!(result, json!({"status":2,"message":UNCLASSIFIED}));
    assert_eq!((writes(&rig), sent(&rig)), (written, frames));
    rig.stop().await;
}

/// The frames a performance step sends over this rig: its capture, the
/// write and the readback.
const PERFORMANCE_STEP_FRAMES: usize = 9;

/// A performance-only reapply runs the performance step alone: the fan
/// step, which the drift does not name, reads nothing.
#[tokio::test]
async fn a_performance_reapply_runs_no_fan_step() {
    let (rig, _journal) = converging().await;
    drift(&rig, 1).await;
    let frames = sent(&rig);
    let result = reapply(&rig, 1).await;
    assert_eq!(
        result,
        json!({"status":1,"message":"OpenBlade reapplied Power mode from the current settings."})
    );
    assert_eq!(sent(&rig) - frames, PERFORMANCE_STEP_FRAMES);
    rig.stop().await;
}

/// Managed's manual-fan bridge on battery, where no performance step
/// defers itself: manual fans read back and saved manual fans leave a
/// performance reapply unconfirmable, so neither subsystem runs, each with
/// managed's reason, and nothing is written.
#[tokio::test]
async fn manual_fans_on_both_sides_block_a_battery_reapply() {
    let files = files_with_game(json!({"battery":{"performance":4}}));
    let mut document = files.document("profiles.json");
    document["profiles"]["battery"]["fans"] = json!(1);
    files.put("profiles.json", document);
    files.commit_configuration();
    let rig = rig_at(files, 0);
    rig.port.state.lock().unwrap().actual = 0;
    rig.metadata.store(1, Ordering::Release);
    report(&rig, 1, Some(r"C:\Games\Game.exe")).await;
    wired(&rig).await;
    let _journal = run_loop(&rig);
    select(&rig, 0).await;
    observe(&rig, 1).await;
    moved(&rig, EXTERNAL, 1);
    let status = observe(&rig, 2).await;
    assert_eq!(
        status["status"]["externalChanges"],
        json!([0, 1]),
        "{status}"
    );
    let written = writes(&rig);
    let result = reapply(&rig, 1).await;
    assert_eq!(
        result,
        json!({"status":2,"message":format!("OpenBlade could not reapply the changed settings. Still changed: Power mode, Fans. {PERFORMANCE_BRIDGE} {FANS_BRIDGE}")})
    );
    assert_eq!(writes(&rig), written);
    rig.stop().await;
}

/// Delta review F1 for `reapplyChangedSettings`: a pending GPU clock
/// confirmation blocks with managed's pending-confirmation reply before the
/// ledger is rearmed or any firmware is read, as an unsettled journal row
/// does (`ServiceControlRuntime.cs:3842`).
#[cfg(feature = "service-gpu-clock-tuning-v1")]
#[tokio::test]
async fn a_pending_gpu_confirmation_blocks_a_reapply_before_anything_is_read() {
    use crate::installed::readiness::gpu_clock_offsets::fake::Fake;
    use crate::installed::readiness::tests::mutation::gpu_clock_tuning::{
        compose, leave_uncertain,
    };
    let (rig, _journal) = converging().await;
    tombstoned(&rig).await;
    drift(&rig, 2).await;
    let fake = Fake::new(0, 0);
    let lane = compose(&rig, &fake, Arc::default());
    leave_uncertain(&rig, &lane, &fake);
    let (kept, frames) = (raw(&rig, LEDGER), sent(&rig));
    let result = reapply(&rig, 1).await;
    assert_eq!(result, json!({"status":2,"message":PENDING}));
    assert_eq!((raw(&rig, LEDGER), sent(&rig)), (kept, frames));
    abandon(rig).await;
}
