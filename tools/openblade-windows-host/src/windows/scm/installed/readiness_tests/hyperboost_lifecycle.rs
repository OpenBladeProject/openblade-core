//! Lifecycle exits of natively owned HyperBoost (`hyperboost::Exit`) at the
//! battery boundary of a reconciliation (managed `ApplyFullTransitionAsync`
//! with `pendingOwnedHyperBoostPowerBoundaryExit`), and the coordinated Stop
//! (managed `PrepareForShutdownAsync`), which does not exit yet. Ownership
//! is the thermal history's (`Storage::owns_hyperboost`): a HyperBoost
//! reading native did not enter stays external. Each case drives the origin
//! tick or the Stop drain over the rig's lifecycle, store and scripted
//! mailbox; no hardware or operating-system supplier is reached.
use super::*;

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
    rig.stop().await;
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
    rig.stop().await;
}

/// Managed `PrepareForShutdownAsync` exits owned HyperBoost before its
/// keyboard and fan cleanup; native does not yet. SCM STOP revokes the
/// loaded origin before the drain (`scm_host::installed_notice`), so no
/// ordinary write capture is admitted at Stop, and the owned-Stop cleanup
/// admission covers only the keyboard release and the fan handback. Stop
/// therefore leaves owned HyperBoost with no setter and no row, completes
/// its drain, and the ownership survives for the next battery boundary of a
/// later start.
#[tokio::test]
async fn stop_leaves_owned_hyperboost_without_a_write() {
    let rig = owned().await;
    let (before, rows) = (writes(&rig), journal(&rig).len());
    rig.origin.stop();
    rig.stop().await;
    assert_eq!(writes(&rig), before);
    assert_eq!(journal(&rig).len(), rows);
    assert_eq!(selectors(&rig), [SELECTOR_HYPERBOOST; 2]);
    assert!(owns(&rig));
    assert!(!pending(&rig));
}
