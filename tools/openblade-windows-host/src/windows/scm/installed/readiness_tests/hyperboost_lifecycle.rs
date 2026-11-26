//! Lifecycle exits of natively owned HyperBoost (`hyperboost::Exit`) at the
//! battery boundary of a reconciliation (managed `ApplyFullTransitionAsync`
//! with `pendingOwnedHyperBoostPowerBoundaryExit`). The exits at the
//! coordinated Stop and at a suspend are in `cleanup`. Ownership is the
//! thermal history's (`Storage::owns_hyperboost`): a HyperBoost reading
//! native did not enter stays external. Each case drives the origin tick
//! over the rig's lifecycle, store and scripted mailbox; no hardware or
//! operating-system supplier is reached.
use super::*;

#[path = "hyperboost_cleanup.rs"]
mod cleanup;

const SELECTOR_HYPERBOOST: u8 = 7;
const SELECTOR_BALANCED: u8 = 0;
const SELECTOR_PERFORMANCE: u8 = 2;

/// One owned HyperBoost request, as the tray sends it.
async fn hyperboost(rig: &Rig, n: u8, enabled: bool) -> Value {
    rig.request(json!({"operation":"setHyperBoost","version":90,"requestId":format!("aaaaaaaa-bbbb-cccc-eeee-{n:012}"),"enabled":enabled}))
        .await
}
/// A rig on a full adapter whose Game rule applied Performance as its
/// user's startup reconciliation, with the tick wired, and whose owned
/// HyperBoost entry then settled. Later reconciliations compare with
/// OpenBlade's own state (`thermal_drift::Mode::WithPrior`).
async fn owned() -> Rig {
    let rig = applied_game().await;
    assert_eq!(selectors(&rig), [SELECTOR_PERFORMANCE; 2]);
    let entered = hyperboost(&rig, 1, true).await;
    assert_eq!(entered["result"]["status"], 1, "{entered}");
    assert_eq!(selectors(&rig), [SELECTOR_HYPERBOOST; 2]);
    assert!(owns(&rig));
    rig
}
/// The setters the fixture accepted; reads are not counted.
fn writes(rig: &Rig) -> usize {
    rig.port.state.lock().unwrap().writes
}
fn selectors(rig: &Rig) -> [u8; 2] {
    rig.port.state.lock().unwrap().selectors
}
fn owns(rig: &Rig) -> bool {
    Storage::open(Box::new(rig.files.clone()), SID)
        .unwrap()
        .owns_hyperboost()
}
fn pending(rig: &Rig) -> bool {
    Storage::open(Box::new(rig.files.clone()), SID)
        .unwrap()
        .pending()
}
fn journal(rig: &Rig) -> Vec<Value> {
    rig.files
        .read("thermal-mutation.journal")
        .unwrap()
        .unwrap_or_default()
        .split(|b| *b == b'\n')
        .filter(|line| !line.is_empty())
        .map(|line| serde_json::from_slice(line).unwrap())
        .collect()
}
/// The rows of the journal after the first `skip`, as (destination, progress).
fn rows_after(rig: &Rig, skip: usize) -> Vec<(i64, u64)> {
    journal(rig)
        .iter()
        .skip(skip)
        .map(|r| {
            (
                r["destination"].as_i64().unwrap(),
                r["progress"].as_u64().unwrap(),
            )
        })
        .collect()
}
/// Managed's journal in `state`, written by its store
/// (`NativeHyperBoostJournalVectorsTests`); 1 is an owned session.
fn managed_journal(state: u8) -> Vec<u8> {
    let vectors: Value = serde_json::from_str(include_str!(
        "../../../../../tests/data/hyperboost-journal-vectors.json"
    ))
    .unwrap();
    vectors["rows"][0]["document"]
        .as_str()
        .unwrap()
        .replace("\"state\": 3,", &format!("\"state\": {state},"))
        .into_bytes()
}
fn hyperboost_off(status: &Value) -> bool {
    status["status"]["confirmedState"]["hyperBoostActive"] == false
}

/// Managed 16413-16415: on Battery the owned session leaves before the rest
/// of the transition, through H2's exit to the saved battery intent, settled
/// by its own readback. Ownership ends with the confirmed exit, so the next
/// tick writes nothing more.
#[tokio::test]
async fn an_unplug_exits_owned_hyperboost_to_the_saved_battery_intent() {
    let rig = owned().await;
    let rows = journal(&rig).len();
    let before = writes(&rig);
    unplug(&rig);
    tick(&rig).await;
    let status = rig.get().await;
    assert!(hyperboost_off(&status), "{status}");
    assert_eq!(performance(&status), &saved_battery_performance(&rig));
    assert!(writes(&rig) > before);
    let exit = rows_after(&rig, rows);
    assert_eq!(exit.last().map(|r| r.1), Some(5), "{exit:?}");
    assert!(exit.iter().all(|r| r.0 != 100), "{exit:?}");
    assert!(!owns(&rig));
    assert!(!pending(&rig));
    let after = writes(&rig);
    unlock(&rig);
    tick(&rig).await;
    assert_eq!(writes(&rig), after);
    rig.stop().await;
}

/// Managed `RestoreFirmwareExitedHyperBoostForPowerSourceTransitionAsync`:
/// firmware that already left HyperBoost gets no exit write. The reading
/// becomes OpenBlade's own, so the transition applies the battery intent
/// instead of pausing on drift, and ownership settles in the journal with a
/// HyperBoost row Prepared and KnownNotSubmitted.
/// Firmware left for Balanced, not the Performance native owned before the
/// entry, so only that marking keeps the reading from reading as drift.
#[tokio::test]
async fn firmware_already_out_of_hyperboost_settles_ownership_read_only() {
    let rig = owned().await;
    rig.port.state.lock().unwrap().selectors = [SELECTOR_BALANCED; 2];
    let rows = journal(&rig).len();
    unplug(&rig);
    tick(&rig).await;
    let exit = rows_after(&rig, rows);
    assert_eq!(exit[..2], [(100, 0), (100, 6)], "{exit:?}");
    // No restore pair: the battery step writes from the firmware's state.
    assert!(exit[2..].iter().all(|r| r.0 != 100 && r.1 < 80), "{exit:?}");
    assert_eq!(exit.last().map(|r| r.1), Some(5), "{exit:?}");
    assert_eq!(
        performance(&rig.get().await),
        &saved_battery_performance(&rig)
    );
    assert!(!owns(&rig));
    assert!(!pending(&rig));
    rig.stop().await;
}

/// A HyperBoost reading native did not enter is external: the boundary
/// exits nothing, the drift decision excludes it as before, and nothing is
/// written.
#[tokio::test]
async fn an_external_hyperboost_stays_excluded_at_the_battery_boundary() {
    let files = MemoryFiles::default();
    files.profiles(1);
    let rig = rig_at(files, SELECTOR_HYPERBOOST);
    wired(&rig).await;
    assert!(!owns(&rig));
    let before = writes(&rig);
    let rows = journal(&rig).len();
    unplug(&rig);
    tick(&rig).await;
    assert_eq!(writes(&rig), before);
    assert_eq!(journal(&rig).len(), rows);
    assert_eq!(selectors(&rig), [SELECTOR_HYPERBOOST; 2]);
    rig.stop().await;
}

/// Only Battery is the boundary: a reconciliation on AC while native owns
/// HyperBoost exits nothing.
#[tokio::test]
async fn a_reconciliation_on_ac_leaves_owned_hyperboost() {
    let rig = owned().await;
    let before = writes(&rig);
    report(&rig, 1, Some(r"C:\Tools\Other.exe")).await;
    rig.runtime.expire_application_lease();
    tick(&rig).await;
    assert_eq!(writes(&rig), before);
    assert_eq!(selectors(&rig), [SELECTOR_HYPERBOOST; 2]);
    assert!(owns(&rig));
    rig.stop_in_hyperboost().await;
}

/// Managed owns the recovery of its own journal: one it has not
/// relinquished refuses the boundary exit before any row or setter, the
/// transition does not go on, and ownership stays: once managed
/// relinquishes it, the next attempt exits.
#[tokio::test]
async fn a_pending_managed_journal_refuses_the_boundary_exit_without_a_write() {
    let rig = owned().await;
    *rig.hyperboost.journal.lock().unwrap() = Some(Some(managed_journal(1)));
    let before = writes(&rig);
    let rows = journal(&rig).len();
    unplug(&rig);
    tick(&rig).await;
    assert_eq!(writes(&rig), before);
    assert_eq!(journal(&rig).len(), rows);
    assert_eq!(selectors(&rig), [SELECTOR_HYPERBOOST; 2]);
    assert!(owns(&rig));
    // Once managed relinquishes its journal, the next boundary exits.
    *rig.hyperboost.journal.lock().unwrap() = Some(Some(managed_journal(3)));
    replug(&rig, 15);
    tick(&rig).await;
    assert_eq!(writes(&rig), before);
    unplug(&rig);
    tick(&rig).await;
    assert!(writes(&rig) > before);
    assert_eq!(
        performance(&rig.get().await),
        &saved_battery_performance(&rig)
    );
    assert!(hyperboost_off(&rig.get().await));
    assert!(!owns(&rig));
    assert!(!pending(&rig));
    rig.stop().await;
}

/// A failed exit setter may have been submitted: its row stays unsettled,
/// fences the store and is never replayed by a later tick.
#[tokio::test]
async fn an_ambiguous_boundary_exit_fences_the_store_without_replay() {
    let rig = owned().await;
    let writes = rig.port.state.lock().unwrap().writes;
    rig.port.state.lock().unwrap().fail_write = Some(writes + 1);
    unplug(&rig);
    tick(&rig).await;
    assert_eq!(rig.port.state.lock().unwrap().writes, writes + 1);
    assert!(pending(&rig));
    for _ in 0..2 {
        unlock(&rig);
        age(&rig, pace());
        tick(&rig).await;
    }
    assert_eq!(rig.port.state.lock().unwrap().writes, writes + 1);
    assert!(pending(&rig));
    // Stop reports the unresolved submission and replays nothing either.
    let mut source = rig.source.lock().await;
    let drained = source.drain().await;
    source.confirm_drained().unwrap();
    drop(source);
    let error = drained.unwrap_err().to_string();
    assert!(error.contains("unresolved"), "{error}");
    assert_eq!(rig.port.state.lock().unwrap().writes, writes + 1);
}

/// A pending GPU clock confirmation fences the attempt before any read, so
/// the exit runs nothing and ownership stays (`TriggerAdmission::ThermalPending`).
#[cfg(feature = "service-gpu-clock-tuning-v1")]
#[tokio::test]
async fn a_pending_gpu_confirmation_holds_the_boundary_exit() {
    use super::super::super::super::gpu_clock_tuning::{compose, leave_uncertain};
    let rig = owned().await;
    let fake = crate::installed::readiness::gpu_clock_offsets::fake::Fake::new(0, 0);
    let lane = compose(&rig, &fake, Arc::default());
    leave_uncertain(&rig, &lane, &fake);
    fake.with(|d| d.queries_fail = true);
    let before = writes(&rig);
    unplug(&rig);
    tick(&rig).await;
    assert_eq!(writes(&rig), before);
    assert_eq!(selectors(&rig), [SELECTOR_HYPERBOOST; 2]);
    assert!(owns(&rig));
    rig.stop_in_hyperboost().await;
}

const SELECTOR_SILENT: u8 = 5;
const ALREADY_OFF: &str = "HyperBoost is already off.";

fn set_selectors(rig: &Rig, selector: u8) {
    rig.port.state.lock().unwrap().selectors = [selector; 2];
}
/// What a tick's reconciliation on battery left, for comparing a rig with
/// native history against one without: the setters it sent, the device, and
/// what a fresh reading then reports as confirmed and as changed outside
/// OpenBlade.
async fn after_unplug(rig: &Rig) -> (usize, [u8; 2], Value, Value) {
    let before = writes(rig);
    unplug(rig);
    tick(rig).await;
    let status = observed(rig, 9).await;
    (
        writes(rig) - before,
        selectors(rig),
        status["status"]["confirmedState"].clone(),
        status["status"]["externalChanges"].clone(),
    )
}

/// Managed clears `ownsHyperBoost` at any published observation with
/// HyperBoost inactive (18394-18405). Once a reading shows firmware out of
/// owned HyperBoost, native owns nothing either, whatever appears next: an
/// external Silent keeps its drift pause at the battery boundary and an
/// external HyperBoost stays excluded, exactly as on a rig that never
/// entered HyperBoost. The observation holds no write admission, so the
/// journal still names the entry until the boundary's first admitted write
/// settles it; nothing is written to firmware.
#[tokio::test]
async fn an_observed_firmware_exit_ends_ownership_whatever_appears_next() {
    for next in [SELECTOR_SILENT, SELECTOR_HYPERBOOST] {
        let rig = owned().await;
        set_selectors(&rig, SELECTOR_BALANCED);
        observed(&rig, 1).await;
        // Only the tracker knows yet; the journal is settled at the boundary.
        assert!(owns(&rig), "{next}");
        set_selectors(&rig, next);
        observed(&rig, 2).await;
        let rows = journal(&rig).len();
        let left = after_unplug(&rig).await;
        assert_eq!((left.0, left.1), (0, [next; 2]), "{next}");
        assert_eq!(rows_after(&rig, rows), [(100, 0), (100, 6)], "{next}");
        assert!(!owns(&rig) && !pending(&rig), "{next}");
        // The control: the same readings with no native HyperBoost history.
        let control = applied_game().await;
        set_selectors(&control, SELECTOR_BALANCED);
        observed(&control, 1).await;
        set_selectors(&control, next);
        observed(&control, 2).await;
        let rows = journal(&control).len();
        assert_eq!(after_unplug(&control).await, left, "{next}");
        assert_eq!(journal(&control).len(), rows, "{next}");
        rig.stop().await;
        control.stop().await;
    }
}

/// Managed's explicit disable that finds HyperBoost already off clears its
/// ownership and retires its journal (6965-6994). Native's reply is the
/// same, and it ends the ownership too, in the journal under the request's
/// own capture: a HyperBoost entered outside OpenBlade afterwards is not
/// left at the battery boundary, as on a rig that never entered it.
#[tokio::test]
async fn an_explicit_exit_that_finds_hyperboost_off_ends_ownership() {
    let rig = owned().await;
    set_selectors(&rig, SELECTOR_BALANCED);
    let rows = journal(&rig).len();
    let off = hyperboost(&rig, 2, false).await;
    assert_eq!(off["result"], json!({"status":1,"message":ALREADY_OFF}));
    assert_eq!(rows_after(&rig, rows), [(100, 0), (100, 6)]);
    assert!(!owns(&rig) && !pending(&rig));
    set_selectors(&rig, SELECTOR_HYPERBOOST);
    let left = after_unplug(&rig).await;
    assert_eq!((left.0, left.1), (0, [SELECTOR_HYPERBOOST; 2]));
    let control = applied_game().await;
    set_selectors(&control, SELECTOR_BALANCED);
    let off = hyperboost(&control, 2, false).await;
    assert_eq!(off["result"], json!({"status":1,"message":ALREADY_OFF}));
    set_selectors(&control, SELECTOR_HYPERBOOST);
    assert_eq!(after_unplug(&control).await, left);
    rig.stop().await;
    control.stop().await;
}

/// A restarted Service has no reading of its own yet, and the journal still
/// names the entry. Its first reading decides: HyperBoost keeps the
/// journal's ownership, and the battery boundary exits it; anything else
/// ends it, so a HyperBoost that appears afterwards was entered outside
/// OpenBlade and stays, as on a rig with no history.
#[tokio::test]
async fn a_restarted_service_owns_hyperboost_only_while_its_first_reading_shows_it() {
    for first in [SELECTOR_HYPERBOOST, SELECTOR_BALANCED] {
        let entered = owned().await;
        let files = entered.files.clone();
        entered.stop_in_hyperboost().await;
        let rig = Rig::restarted(files);
        *rig.custody.origin_for_fan.lock().unwrap() = Some(rig.origin.clone());
        rig.port.state.lock().unwrap().fans = [0; 2];
        set_selectors(&rig, first);
        rig.get().await;
        observed(&rig, 1).await;
        assert!(owns(&rig), "{first}");
        set_selectors(&rig, SELECTOR_HYPERBOOST);
        observed(&rig, 2).await;
        wired(&rig).await;
        let left = after_unplug(&rig).await;
        if first == SELECTOR_HYPERBOOST {
            assert!(left.0 > 0, "{left:?}");
            assert_eq!(left.2["hyperBoostActive"], false, "{left:?}");
            assert_eq!(left.2["performance"], saved_battery_performance(&rig));
        } else {
            assert_eq!((left.0, left.1), (0, [SELECTOR_HYPERBOOST; 2]), "{left:?}");
        }
        assert!(!owns(&rig) && !pending(&rig), "{first}");
        rig.stop_in_hyperboost().await;
    }
}

/// With an owned fan change outstanding, the battery boundary names that
/// cause, as it does on a rig with no HyperBoost history: firmware moved
/// from owned HyperBoost to Balanced, the user took Fixed fans, and the
/// reconciliation on battery cannot write a performance row behind the fan
/// owner. The ended ownership is no reason of its own, and nothing is
/// written.
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn the_battery_boundary_names_the_outstanding_fan_change_not_hyperboost() {
    let mut replies = Vec::new();
    for history in [true, false] {
        let rig = if history {
            owned().await
        } else {
            applied_game().await
        };
        set_selectors(&rig, SELECTOR_BALANCED);
        observed(&rig, 1).await;
        let fan = rig.request(fan_request(170, Some(1), 3700)).await;
        assert_eq!(fan["result"]["status"], 1, "{fan}");
        let (rows, before) = (journal(&rig).len(), writes(&rig));
        unplug(&rig);
        tick(&rig).await;
        let reply = report(&rig, 5, Some(r"C:\Tools\Other.exe")).await;
        assert_eq!(writes(&rig), before, "{history}");
        assert_eq!(journal(&rig).len(), rows, "{history}");
        let message = reply["result"]["message"].as_str().unwrap().to_owned();
        assert!(!message.contains(ALREADY_OFF), "{history}: {message}");
        replies.push(reply["result"].clone());
        rig.origin.stop();
        let _ = rig.source.lock().await.drain().await;
    }
    assert_eq!(replies[0], replies[1]);
}

/// The low-power gate skips the boundary exit: an unplug while suspended
/// reads and writes nothing, ownership stays, and the tick after the resume
/// exits to the saved battery intent.
#[tokio::test]
async fn the_low_power_gate_defers_the_boundary_exit_to_the_resume() {
    let rig = owned().await;
    let (before, rows) = (sent(&rig), journal(&rig).len());
    suspend(&rig);
    unplug(&rig);
    for _ in 0..2 {
        tick(&rig).await;
    }
    assert_eq!(sent(&rig), before);
    assert_eq!(journal(&rig).len(), rows);
    assert!(owns(&rig));
    resume(&rig);
    tick(&rig).await;
    assert!(hyperboost_off(&rig.get().await));
    assert_eq!(
        performance(&rig.get().await),
        &saved_battery_performance(&rig)
    );
    assert!(!owns(&rig) && !pending(&rig));
    rig.stop().await;
}

/// A report and a save on battery are unscoped reconciliations too, so each
/// exits owned HyperBoost before its transition, without the tick.
#[tokio::test]
async fn a_report_and_a_save_on_battery_exit_owned_hyperboost() {
    for save in [false, true] {
        let rig = owned().await;
        // Windows reports battery; no tick runs before the trigger.
        rig.port.state.lock().unwrap().actual = 0;
        rig.metadata.store(1, Ordering::Release);
        let before = writes(&rig);
        let reply = if save {
            let mut profiles = rig.files.document("profiles.json")["profiles"].clone();
            profiles["battery"]["performance"] = json!(4);
            rig.request(update(3, &profiles)).await
        } else {
            report(&rig, 2, Some(r"C:\Tools\Other.exe")).await
        };
        assert_eq!(reply["result"]["status"], 1, "{save}: {reply}");
        assert!(writes(&rig) > before, "{save}");
        let status = rig.get().await;
        assert!(hyperboost_off(&status), "{save}: {status}");
        assert_eq!(
            performance(&status),
            &saved_battery_performance(&rig),
            "{save}"
        );
        assert!(!owns(&rig) && !pending(&rig), "{save}");
        rig.stop().await;
    }
}

/// An exit dropped at the reconciliation's bound is not finished and is
/// reported so. A host stall before its first setter, past the real-time
/// bound, leaves that setter's journaled intent unsettled: the reply is
/// Failed, the store is fenced, and neither the next tick nor a Stop
/// replays the exit. Real time.
#[tokio::test]
async fn an_exit_dropped_at_its_bound_fences_the_store_without_replay() {
    keep_real_clock();
    let rig = owned().await;
    rig.port.state.lock().unwrap().actual = 0;
    rig.metadata.store(1, Ordering::Release);
    let first = writes(&rig) + 1;
    let bound = crate::service_frame::RECONCILE_BUDGET + crate::service_frame::RECONCILE_MARGIN;
    rig.port.state.lock().unwrap().before_write = Some(Arc::new(move |number| {
        if number == first {
            std::thread::sleep(bound + std::time::Duration::from_secs(3));
        }
    }));
    let reply = report(&rig, 2, Some(r"C:\Tools\Other.exe")).await;
    assert_eq!(reply["result"]["status"], 3, "{reply}");
    assert!(
        reply["result"]["message"]
            .as_str()
            .unwrap()
            .contains(UNFINISHED),
        "{reply}"
    );
    rig.reader.drain_active().await.unwrap();
    rig.port.state.lock().unwrap().before_write = None;
    assert!(pending(&rig));
    let after = writes(&rig);
    assert_eq!(after, first);
    unlock(&rig);
    age(&rig, pace());
    tick(&rig).await;
    assert_eq!(writes(&rig), after);
    rig.origin.stop();
    assert!(rig.source.lock().await.drain().await.is_err());
    assert_eq!(writes(&rig), after);
}
