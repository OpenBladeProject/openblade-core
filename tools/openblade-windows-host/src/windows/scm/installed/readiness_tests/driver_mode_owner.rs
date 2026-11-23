//! Keyboard Driver-mode ownership (`service-keyboard-driver-mode-v1`): the
//! origin tick's 15 s acquire and maintain step and the shutdown release,
//! over the rig's injected custody, lifecycle, console probe and in-memory
//! port. No hardware or operating-system supplier is reached.
use super::*;
use crate::installed::readiness::thermal_mutation::INTERVAL;
use std::cell::Cell;
use std::rc::Rc;
use windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT as POWER;

const SUSPEND: u32 = 4;
const RESUME: u32 = 18;
const NORMAL: u8 = 0;
const DRIVER: u8 = 3;

/// A rig with a verified published status, the lifecycle wired in as the
/// low-power gate and a console probe naming the rig's user while the
/// returned flag is set, as `Runtime::native` wires them.
async fn owner_rig(mode: u8) -> (Rig, Rc<Cell<bool>>) {
    let files = MemoryFiles::default();
    files.profiles(1);
    let rig = Rig::new(files);
    rig.port.state.lock().unwrap().device_mode = mode;
    observe(&rig).await;
    let custody = rig.custody.clone();
    rig.runtime
        .read_low_power(move || custody.lifecycle.low_power());
    let named = Rc::new(Cell::new(true));
    let (custody, console) = (rig.custody.clone(), named.clone());
    rig.runtime.read_console(move || {
        console.get().then(|| {
            (
                custody.current_sid.lock().unwrap().clone(),
                custody.current_session.load(Ordering::Acquire) as u32,
            )
        })
    });
    (rig, named)
}
/// An observeState request: a fresh exact-device mailbox read.
async fn observe(rig: &Rig) {
    assert_eq!(rig.get().await["success"], true);
    let observed = rig
        .request(json!({"operation":"observeState","version":85,"requestId":"dddddddd-bbbb-cccc-dddd-000000000071","includeLiveReadings":false,"requestedLiveReadings":0,"effectiveLiveReadings":0}))
        .await;
    assert_eq!(observed["success"], true, "{observed}");
    assert!(rig.reader.driver_mode_verified());
}
/// One due origin-tick step, as the serving loop runs it.
async fn step(rig: &Rig) {
    rig.runtime.driver_mode_due_for_tests();
    tick(rig).await;
}
/// The serving loop's step without forcing it due.
async fn tick(rig: &Rig) {
    let maintain = rig.source.lock().await.maintain_driver_mode();
    if let Some(maintain) = maintain {
        maintain.await.unwrap();
    }
}
/// The mode a fresh observeState status carries: what the read cadence holds
/// (`ReadSource::record_driver_mode`). The fixture read job sends no 00/84
/// query of its own, so only the owner records a mode here.
async fn published(rig: &Rig) -> Option<bool> {
    let observed = rig
        .request(json!({"operation":"observeState","version":85,"requestId":"dddddddd-bbbb-cccc-dddd-000000000072","includeLiveReadings":false,"requestedLiveReadings":0,"effectiveLiveReadings":0}))
        .await;
    observed["status"]["keyboardDriverModeActive"].as_bool()
}
/// The subscription feed's revision, which the owner never advances.
async fn revision(rig: &Rig) -> i64 {
    let publications = rig.source.lock().await.publications().unwrap();
    publications.borrow().active.revision()
}
/// Every keyboard device-mode exchange sent: (command, payload, ID).
fn keyboard(rig: &Rig) -> Vec<(u8, Vec<u8>, u8)> {
    rig.port
        .state
        .lock()
        .unwrap()
        .sent
        .iter()
        .filter(|(class, ..)| *class == 0)
        .map(|(_, command, payload, id)| (*command, payload.clone(), *id))
        .collect()
}
fn sets(rig: &Rig) -> usize {
    rig.port.state.lock().unwrap().device_mode_sets
}
fn mode(rig: &Rig) -> u8 {
    rig.port.state.lock().unwrap().device_mode
}
/// (owns, release uncertain, indeterminate, recovery latch).
fn owner(rig: &Rig) -> (bool, bool, bool, bool) {
    rig.runtime.driver_mode_state_for_tests()
}
fn mailbox(rig: &Rig) -> MailboxSource {
    MailboxSource::Reader(rig.reader.clone())
}
/// Arms a competing controller from the `nth` fresh authorization after
/// the first 00/84 query of the next job is sent.
fn refuse_after_first_query(rig: &Rig, nth: usize) {
    let custody = Arc::downgrade(&rig.custody);
    let start = rig.port.state.lock().unwrap().device_mode_queries + 1;
    rig.port.state.lock().unwrap().device_mode_on_query = Some(Arc::new(move |query| {
        if query == start {
            custody
                .upgrade()
                .unwrap()
                .refuse_at
                .store(nth, Ordering::Release);
        }
    }));
}
async fn shutdown(rig: &Rig) -> io::Result<()> {
    rig.origin.stop();
    let mut source = rig.source.lock().await;
    let drained = source.drain().await;
    source.confirm_drained().unwrap();
    drained
}

/// Managed `MaintainDriverModeAsync` from Normal: the 00/84 read, the claim,
/// the 00/04:0300 setter, its exact acknowledgement and an independent 00/84
/// readback on its own ID, then true is published.
#[tokio::test]
async fn acquire_from_normal_writes_driver_and_confirms_by_independent_readback() {
    let (rig, _) = owner_rig(NORMAL).await;
    assert_eq!(published(&rig).await, None);
    let feed = revision(&rig).await;
    tick(&rig).await;
    let sent = keyboard(&rig);
    assert_eq!(
        sent.iter()
            .map(|(command, payload, _)| (*command, payload.clone()))
            .collect::<Vec<_>>(),
        [(0x84, vec![0, 0]), (0x04, vec![3, 0]), (0x84, vec![0, 0])]
    );
    let ids: std::collections::BTreeSet<_> = sent.iter().map(|(.., id)| *id).collect();
    assert_eq!(ids.len(), 3, "each exchange has its own ID: {sent:?}");
    assert_eq!(mode(&rig), DRIVER);
    assert_eq!(owner(&rig), (true, false, false, false));
    assert_eq!(published(&rig).await, Some(true));
    assert_eq!(revision(&rig).await, feed);
    rig.stop().await;
}

/// Managed returns true for a keyboard already in Driver mode, with no
/// write and no claim; shutdown then leaves it in Driver mode.
#[tokio::test]
async fn already_driver_reports_true_without_a_write_or_a_claim() {
    let (rig, _) = owner_rig(DRIVER).await;
    tick(&rig).await;
    assert_eq!(keyboard(&rig).len(), 1);
    assert_eq!(sets(&rig), 0);
    assert_eq!(owner(&rig), (false, false, false, false));
    assert_eq!(published(&rig).await, Some(true));
    shutdown(&rig).await.unwrap();
    assert_eq!((sets(&rig), mode(&rig)), (0, DRIVER));
}

/// Ownership is claimed before the setter can be submitted: a setter whose
/// submission fails, so it may have reached the firmware, leaves the claim
/// and stops the owner (Indeterminate) with null published.
#[tokio::test]
async fn the_claim_precedes_submission_and_a_failed_submission_is_indeterminate() {
    let (rig, _) = owner_rig(DRIVER).await;
    tick(&rig).await;
    assert_eq!(published(&rig).await, Some(true));
    {
        let mut port = rig.port.state.lock().unwrap();
        port.device_mode = NORMAL;
        port.device_mode_fail_set = true;
    }
    step(&rig).await;
    assert_eq!(sets(&rig), 1);
    assert_eq!(owner(&rig), (true, false, true, false));
    assert_eq!(published(&rig).await, None);
    // The owner stops: no read, no write and no recovery, ever again.
    let sent = keyboard(&rig).len();
    rig.port.state.lock().unwrap().device_mode_fail_set = false;
    for _ in 0..3 {
        step(&rig).await;
    }
    assert_eq!((keyboard(&rig).len(), sets(&rig)), (sent, 1));
    // Shutdown restores Normal from the claim when it reads Driver mode.
    rig.port.state.lock().unwrap().device_mode = DRIVER;
    shutdown(&rig).await.unwrap();
    assert_eq!((sets(&rig), mode(&rig)), (2, NORMAL));
}

/// A setter refused before submission (managed `KnownNotApplied`) clears
/// the claim; the step publishes null and shutdown writes nothing.
#[tokio::test]
async fn a_setter_refused_before_submission_clears_the_claim() {
    let (rig, _) = owner_rig(NORMAL).await;
    // The query's two checkpoints authorize twice each; the fifth
    // authorization after its send is the claim's checkpoint.
    refuse_after_first_query(&rig, 5);
    tick(&rig).await;
    assert_eq!(sets(&rig), 0);
    assert_eq!(
        rig.runtime.driver_mode_claims_for_tests(),
        1,
        "claimed, then cleared"
    );
    assert_eq!(keyboard(&rig).len(), 1);
    assert_eq!(owner(&rig), (false, false, false, true));
    assert_eq!(published(&rig).await, None);
    assert_eq!(mode(&rig), NORMAL);
}

/// An acknowledgement that does not echo the setter, and a readback that
/// does not confirm Driver or times out, are Indeterminate: null is
/// published, the claim is kept and nothing is ever replayed.
#[tokio::test]
async fn an_ambiguous_acknowledgement_or_readback_publishes_null_and_stops_writes() {
    for case in ["bad ack", "ignored setter", "readback timeout"] {
        let (rig, _) = owner_rig(DRIVER).await;
        tick(&rig).await;
        assert_eq!(published(&rig).await, Some(true), "{case}");
        {
            let mut port = rig.port.state.lock().unwrap();
            port.device_mode = NORMAL;
            match case {
                "bad ack" => port.device_mode_bad_ack = true,
                "ignored setter" => port.device_mode_ignore_set = true,
                _ => port.device_mode_timeout = Some(port.device_mode_queries + 2),
            }
        }
        step(&rig).await;
        assert_eq!(sets(&rig), 1, "{case}");
        assert_eq!(owner(&rig), (true, false, true, false), "{case}");
        assert_eq!(published(&rig).await, None, "{case}");
        let sent = keyboard(&rig).len();
        for _ in 0..3 {
            step(&rig).await;
        }
        assert_eq!((keyboard(&rig).len(), sets(&rig)), (sent, 1), "{case}");
        // A clean shutdown restores Normal or finds it there.
        {
            let mut port = rig.port.state.lock().unwrap();
            port.device_mode_bad_ack = false;
            port.device_mode_ignore_set = false;
            port.device_mode_timeout = None;
        }
        rig.stop().await;
    }
}

/// One recovery per failure streak: a failed baseline read is followed in
/// the same step by one fresh capture and read-before-write.
#[tokio::test]
async fn a_failed_step_recovers_once_with_a_fresh_read_before_write() {
    let (rig, _) = owner_rig(NORMAL).await;
    rig.port.state.lock().unwrap().device_mode_timeout = Some(1);
    let captures = rig.factory_calls.load(Ordering::Acquire);
    tick(&rig).await;
    assert_eq!(rig.factory_calls.load(Ordering::Acquire), captures + 2);
    assert_eq!(rig.port.state.lock().unwrap().device_mode_queries, 3);
    assert_eq!((sets(&rig), mode(&rig)), (1, DRIVER));
    assert_eq!(owner(&rig), (true, false, false, false));
    assert_eq!(published(&rig).await, Some(true));
    rig.stop().await;
}

/// The recovery latch holds through a failure streak and resets on success.
#[tokio::test]
async fn the_recovery_latch_holds_for_a_failure_streak_and_resets_on_success() {
    let (rig, _) = owner_rig(NORMAL).await;
    let captures = || rig.factory_calls.load(Ordering::Acquire);
    rig.custody.fresh_refuse.store(true, Ordering::Release);
    let start = captures();
    tick(&rig).await;
    assert_eq!(captures(), start + 2, "a failure and its one recovery");
    assert!(owner(&rig).3);
    step(&rig).await;
    assert_eq!(captures(), start + 3, "the streak has no second recovery");
    rig.custody.fresh_refuse.store(false, Ordering::Release);
    step(&rig).await;
    assert_eq!(owner(&rig), (true, false, false, false));
    assert_eq!(published(&rig).await, Some(true));
    rig.custody.fresh_refuse.store(true, Ordering::Release);
    let start = captures();
    step(&rig).await;
    assert_eq!(captures(), start + 2, "a new streak recovers once again");
    assert_eq!(published(&rig).await, None);
    assert_eq!(keyboard(&rig).len(), 3);
    rig.custody.fresh_refuse.store(false, Ordering::Release);
    rig.stop().await;
}

/// The deliberate deviation from managed: the step writes under the console
/// user's capture, so with nobody signed in, or a capture refused for its
/// session, it reads and writes nothing, keeps the last confirmed status
/// and consumes no recovery; it runs again at the next step.
#[tokio::test]
async fn no_console_user_or_a_refused_session_capture_keeps_the_last_status() {
    let (rig, named) = owner_rig(DRIVER).await;
    tick(&rig).await;
    let confirmed = published(&rig).await;
    assert_eq!(confirmed, Some(true));
    rig.port.state.lock().unwrap().device_mode = NORMAL;
    named.set(false);
    let captures = rig.factory_calls.load(Ordering::Acquire);
    step(&rig).await;
    assert_eq!(rig.factory_calls.load(Ordering::Acquire), captures);
    named.set(true);
    // A locked or switched console refuses the scoped capture.
    rig.custody.scoped_fresh.store(true, Ordering::Release);
    rig.custody.current_console.store(false, Ordering::Release);
    step(&rig).await;
    assert_eq!(rig.factory_calls.load(Ordering::Acquire), captures + 1);
    rig.custody.current_console.store(true, Ordering::Release);
    rig.custody.scoped_fresh.store(false, Ordering::Release);
    assert_eq!(keyboard(&rig).len(), 1);
    assert_eq!(published(&rig).await, confirmed);
    assert_eq!(owner(&rig), (false, false, false, false));
    step(&rig).await;
    assert_eq!((sets(&rig), mode(&rig)), (1, DRIVER));
    rig.stop().await;
}

/// No acquire or maintain write while the low-power gate is set, and none
/// until Windows reports the system active again.
#[tokio::test]
async fn no_acquire_while_the_low_power_gate_is_set() {
    let (rig, _) = owner_rig(NORMAL).await;
    rig.custody.lifecycle.ingress(POWER, SUSPEND);
    assert!(rig.custody.lifecycle.low_power_or_unknown());
    let captures = rig.factory_calls.load(Ordering::Acquire);
    let before = published(&rig).await;
    for _ in 0..3 {
        step(&rig).await;
    }
    assert!(keyboard(&rig).is_empty());
    assert_eq!(rig.factory_calls.load(Ordering::Acquire), captures);
    assert_eq!(published(&rig).await, before);
    rig.custody.lifecycle.ingress(POWER, RESUME);
    assert!(!rig.custody.lifecycle.low_power_or_unknown());
    step(&rig).await;
    assert_eq!((sets(&rig), mode(&rig)), (1, DRIVER));
    rig.stop().await;
}

/// Managed returns without publishing while a laptop-control journal row
/// is unsettled (`RequiresReconciliation`); the fan block is the same row
/// for this pair (`FanStopController.BlocksOrdinaryHardware`).
#[tokio::test]
async fn an_unsettled_journal_row_skips_the_step() {
    let (rig, _) = owner_rig(NORMAL).await;
    rig.port.state.lock().unwrap().fail_write = Some(1);
    assert_eq!(rig.set(1, value(1, 1)).await["result"]["status"], 2);
    assert!(rig.storage.pending());
    let before = published(&rig).await;
    step(&rig).await;
    assert!(keyboard(&rig).is_empty());
    assert_eq!(published(&rig).await, before);
}

/// A competing controller refuses the capture: nothing is read or written
/// and the last status stays.
#[tokio::test]
async fn a_competing_controller_skips_the_step_with_the_mode_unchanged() {
    let (rig, _) = owner_rig(NORMAL).await;
    rig.custody.controller.store(true, Ordering::Release);
    let before = published(&rig).await;
    step(&rig).await;
    assert!(keyboard(&rig).is_empty());
    assert_eq!(published(&rig).await, before);
    assert_eq!(owner(&rig), (false, false, false, false));
    rig.custody.controller.store(false, Ordering::Release);
    rig.stop().await;
}

/// Without exact-device verification the step publishes null, writes
/// nothing and resets the recovery latch (managed `TryGetVerifiedDevice`).
#[tokio::test]
async fn an_unverified_device_publishes_null_and_resets_the_latch() {
    let (rig, _) = owner_rig(NORMAL).await;
    rig.custody.fresh_refuse.store(true, Ordering::Release);
    tick(&rig).await;
    assert!(owner(&rig).3);
    rig.custody.fresh_refuse.store(false, Ordering::Release);
    rig.reader.expire_for_tests();
    let captures = rig.factory_calls.load(Ordering::Acquire);
    for _ in 0..3 {
        step(&rig).await;
    }
    assert!(!owner(&rig).3);
    assert_eq!(rig.factory_calls.load(Ordering::Acquire), captures);
    assert!(keyboard(&rig).is_empty());
    assert_eq!(published(&rig).await, None);
    // A fresh exact-device read makes the next tick eligible.
    observe(&rig).await;
    tick(&rig).await;
    assert_eq!((sets(&rig), mode(&rig)), (1, DRIVER));
    assert_eq!(published(&rig).await, Some(true));
    rig.stop().await;
}

/// The step is due at the first eligible tick, the managed startup call,
/// then every 15 s; an unchanged status is not republished.
#[tokio::test]
async fn the_step_runs_at_the_first_tick_then_every_interval_without_republishing() {
    let (rig, _) = owner_rig(DRIVER).await;
    tick(&rig).await;
    assert_eq!(keyboard(&rig).len(), 1);
    assert_eq!(published(&rig).await, Some(true));
    let first = revision(&rig).await;
    tick(&rig).await;
    rig.runtime
        .driver_mode_age_for_tests(INTERVAL - std::time::Duration::from_secs(2));
    tick(&rig).await;
    assert_eq!(keyboard(&rig).len(), 1);
    rig.runtime
        .driver_mode_age_for_tests(std::time::Duration::from_secs(2));
    tick(&rig).await;
    assert_eq!(keyboard(&rig).len(), 2);
    assert_eq!(revision(&rig).await, first, "the owner never republishes");
    assert_eq!(published(&rig).await, Some(true));
    rig.stop().await;
}

/// No ID is pinned and every sent one is answered, so a long run of
/// acquisitions never drains the transaction pool.
#[tokio::test]
async fn long_runs_of_acquisitions_never_drain_the_pool() {
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    let free = rig.port.free_transactions();
    for _ in 0..200 {
        rig.port.state.lock().unwrap().device_mode = NORMAL;
        step(&rig).await;
        assert_eq!(mode(&rig), DRIVER);
    }
    assert_eq!(sets(&rig), 201);
    assert!(rig.port.free_transactions() >= free);
    assert_eq!(published(&rig).await, Some(true));
    rig.stop().await;
}

/// Shutdown restores Normal from an owned Driver mode before the fan
/// handback (managed `ReleaseDriverModeCoreAsync("shutdown")`, then
/// `StopFans`), confirmed by an independent readback.
#[tokio::test]
async fn shutdown_restores_normal_before_the_fan_handback() {
    let rig = fan_rig_with(0, 0, 2);
    rig.get().await;
    let outcome = rig.request(fan_request(101, Some(1), 3700)).await;
    assert_eq!(outcome["result"]["status"], 1, "{outcome}");
    rig.runtime.read_console({
        let custody = rig.custody.clone();
        move || {
            Some((
                custody.current_sid.lock().unwrap().clone(),
                custody.current_session.load(Ordering::Acquire) as u32,
            ))
        }
    });
    tick(&rig).await;
    assert_eq!(owner(&rig), (true, false, false, false));
    let before = rig.port.state.lock().unwrap().sent.len();
    shutdown(&rig).await.unwrap();
    let state = rig.port.state.lock().unwrap();
    let after: Vec<_> = state.sent[before..]
        .iter()
        .map(|(class, command, payload, _)| (*class, *command, payload.clone()))
        .collect();
    let release = after
        .iter()
        .position(|sent| *sent == (0, 0x04, vec![0, 0]))
        .unwrap();
    let handback = after
        .iter()
        .position(|(class, command, _)| *class == 13 && *command == 2)
        .unwrap();
    assert!(release < handback, "{after:?}");
    assert_eq!(after[release + 1].1, 0x84, "independent readback");
    assert_eq!((state.device_mode, state.fans), (NORMAL, [0, 0]));
    drop(state);
    assert_eq!(owner(&rig), (false, false, false, false));
}

/// A keyboard release that fails never skips the fan handback; its error is
/// kept, and the uncertain release blocks any later restore write.
#[tokio::test]
async fn a_failed_keyboard_release_does_not_skip_the_fan_handback() {
    let rig = fan_rig_with(0, 0, 2);
    rig.get().await;
    let outcome = rig.request(fan_request(101, Some(1), 3700)).await;
    assert_eq!(outcome["result"]["status"], 1, "{outcome}");
    rig.runtime.read_console({
        let custody = rig.custody.clone();
        move || {
            Some((
                custody.current_sid.lock().unwrap().clone(),
                custody.current_session.load(Ordering::Acquire) as u32,
            ))
        }
    });
    tick(&rig).await;
    rig.port.state.lock().unwrap().device_mode_bad_ack = true;
    assert!(shutdown(&rig).await.is_err());
    let (sets, fans) = {
        let state = rig.port.state.lock().unwrap();
        (state.device_mode_sets, state.fans)
    };
    assert_eq!(sets, 2);
    assert_eq!(fans, [0, 0], "the fan handback still ran");
    assert_eq!(owner(&rig), (true, true, false, false));
    let error = rig
        .runtime
        .release_driver_mode(&mailbox(&rig))
        .await
        .unwrap_err();
    assert!(error.to_string().contains("unresolved"), "{error}");
    assert_eq!(rig.port.state.lock().unwrap().device_mode_sets, 2);
}

/// Each release skip leaves the mode unchanged: not owned, a foreign change
/// to Normal, a competing controller, an unsettled journal row, an
/// uncertain earlier release and origin loss.
#[tokio::test]
async fn release_skips_leave_the_mode_unchanged() {
    // Not owned: OpenBlade found Driver mode and claimed nothing.
    let (rig, _) = owner_rig(DRIVER).await;
    tick(&rig).await;
    rig.runtime
        .release_driver_mode(&mailbox(&rig))
        .await
        .unwrap();
    assert_eq!((sets(&rig), mode(&rig)), (0, DRIVER));
    rig.stop().await;

    // A foreign change: Normal now, so ownership ends with no write.
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    rig.port.state.lock().unwrap().device_mode = NORMAL;
    rig.runtime
        .release_driver_mode(&mailbox(&rig))
        .await
        .unwrap();
    assert_eq!((sets(&rig), mode(&rig)), (1, NORMAL));
    assert_eq!(owner(&rig), (false, false, false, false));
    rig.stop().await;

    // A competing controller: no owned-Stop admission.
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    rig.custody.controller.store(true, Ordering::Release);
    let reads = keyboard(&rig).len();
    assert!(
        rig.runtime
            .release_driver_mode(&mailbox(&rig))
            .await
            .is_err()
    );
    assert_eq!((keyboard(&rig).len(), mode(&rig)), (reads, DRIVER));
    assert_eq!(owner(&rig), (true, false, false, false));
    rig.custody.controller.store(false, Ordering::Release);

    // An unsettled journal row (managed `RequiresReconciliation`).
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    rig.port.state.lock().unwrap().fail_write = Some(1);
    rig.set(1, value(1, 1)).await;
    let reads = keyboard(&rig).len();
    assert!(
        rig.runtime
            .release_driver_mode(&mailbox(&rig))
            .await
            .is_err()
    );
    assert_eq!((keyboard(&rig).len(), mode(&rig)), (reads, DRIVER));

    // Origin loss cannot authorize owned cleanup.
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    assert!(rig.runtime.refuse_release_driver_mode().is_err());
    let (other, _) = owner_rig(DRIVER).await;
    tick(&other).await;
    other.runtime.refuse_release_driver_mode().unwrap();
    rig.stop().await;
    other.stop().await;
}

/// Shutdown with a release refused for want of owned-Stop admission still
/// runs the fan step and reports the keyboard refusal.
#[tokio::test]
async fn a_refused_release_still_drains_and_reports() {
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    rig.custody.lifecycle.ingress(POWER, SUSPEND);
    let error = shutdown(&rig).await.unwrap_err();
    assert!(error.to_string().contains("left unchanged"), "{error}");
    assert_eq!((sets(&rig), mode(&rig)), (1, DRIVER));
}

/// The ownership build's compiled identity carries its own marker, which
/// this reader refuses by name.
#[test]
fn the_compiled_identity_carries_the_refused_owner_marker() {
    let identity = std::str::from_utf8(&crate::NATIVE_ARTIFACT_IDENTITY).unwrap();
    let fields: Vec<&str> = identity.split('\n').collect();
    assert_eq!(fields[2], "service");
    assert!(
        fields[5]
            .split(',')
            .any(|feature| feature == "service-keyboard-driver-mode-v1")
    );
    assert!(
        fields[8]
            .split(',')
            .any(|capability| capability == "service-keyboard-driver-mode-v1"),
        "{}",
        fields[8]
    );
    assert!(
        crate::native_package::startup_policy_for_tests(&crate::NATIVE_ARTIFACT_IDENTITY).is_err()
    );
}
