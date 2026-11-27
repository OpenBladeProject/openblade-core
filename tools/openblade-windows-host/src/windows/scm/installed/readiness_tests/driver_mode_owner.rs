//! Keyboard Driver-mode ownership (`service-keyboard-driver-mode-v1`): the
//! origin tick's 15 s acquire and maintain step and the shutdown release,
//! over the rig's injected custody, lifecycle, console probe and in-memory
//! port. No hardware or operating-system supplier is reached.
use super::*;
use crate::discovery::performance::DeviceModeRead;
use crate::installed::readiness::thermal_mutation::{Boundary, INTERVAL, Release};
use openblade_protocol::device_mode::DeviceMode;
use std::cell::Cell;
use std::rc::Rc;
use std::time::Duration;
use windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT as POWER;

const SUSPEND: u32 = 4;
const RESUME: u32 = 18;
/// PBT_APMPOWERSTATUSCHANGE: a power notice that advances the lifecycle epoch.
const SETTING: u32 = 10;
const SESSION_CHANGE: u32 = 14;
const WTS_SESSION_LOCK: u32 = 7;
const NORMAL: u8 = 0;
const DRIVER: u8 = 3;
/// The release's owned-Stop fresh authorization at the job's checkpoint
/// after its baseline reply, counted from the release's own admission: the
/// lane's and the worker's checks and the baseline's earlier checkpoints
/// come before it.
const TRAILING_CHECKPOINT: usize = 9;

/// A rig with a verified published status, the lifecycle wired in as the
/// low-power gate and a console probe naming the rig's user while the
/// returned flag is set, as `Runtime::native` wires them.
async fn owner_rig(mode: u8) -> (Rig, Rc<Cell<bool>>) {
    let files = MemoryFiles::default();
    files.profiles(1);
    wired(Rig::new(files), mode).await
}
/// `rig` wired as `owner_rig` wires it, its keyboard in `mode`.
async fn wired(rig: Rig, mode: u8) -> (Rig, Rc<Cell<bool>>) {
    rig.port.state.lock().unwrap().device_mode = mode;
    observe(&rig).await;
    let custody = rig.custody.clone();
    rig.runtime
        .read_low_power(move || custody.lifecycle.low_power());
    cleanup(&rig);
    let named = Rc::new(Cell::new(true));
    console(&rig, named.clone());
    (rig, named)
}
/// The Service cleanup admission over the rig custody, as
/// `Runtime::native` wires `CleanupGuard`.
fn cleanup(rig: &Rig) {
    let custody = rig.custody.clone();
    rig.runtime
        .read_cleanup(move |passed, scope| CleanupFresh::capture(custody.clone(), passed, scope));
}
/// The console probe: the rig's user while `named` is set.
fn console(rig: &Rig, named: Rc<Cell<bool>>) {
    let custody = rig.custody.clone();
    rig.runtime.read_console(move || {
        named.get().then(|| {
            (
                custody.current_sid.lock().unwrap().clone(),
                custody.current_session.load(Ordering::Acquire) as u32,
            )
        })
    });
}
/// A rig whose fans OpenBlade owns and whose keyboard it then claimed.
pub(super) async fn fan_owner_rig() -> Rig {
    let rig = fan_rig_with(0, 0, 2);
    rig.get().await;
    let outcome = rig.request(fan_request(101, Some(1), 3700)).await;
    assert_eq!(outcome["result"]["status"], 1, "{outcome}");
    cleanup(&rig);
    let custody = rig.custody.clone();
    rig.runtime
        .read_low_power(move || custody.lifecycle.low_power());
    console(&rig, Rc::new(Cell::new(true)));
    tick(&rig).await;
    assert_eq!(owner(&rig), (true, false, false));
    rig
}
/// An observeState request: a fresh exact-device mailbox read.
async fn observe(rig: &Rig) {
    assert_eq!(rig.get().await["success"], true);
    let observed = rig
        .request(json!({"operation":"observeState","version":91,"requestId":"dddddddd-bbbb-cccc-dddd-000000000071","includeLiveReadings":false,"requestedLiveReadings":0,"effectiveLiveReadings":0}))
        .await;
    assert_eq!(observed["success"], true, "{observed}");
    assert!(rig.reader.driver_mode_verified());
}
/// The serving loop's step without forcing it due.
async fn tick(rig: &Rig) {
    try_tick(rig).await.unwrap();
}
/// One maintenance step, run as a request's exchange is (`Rig::held`): its job
/// takes its budget from the step's start and a loaded host spends none of it.
async fn try_tick(rig: &Rig) -> io::Result<()> {
    rig.held(
        HeldClock::CatchUp,
        Box::pin(async {
            let maintain = rig.source.lock().await.maintain_driver_mode();
            match maintain {
                Some(maintain) => maintain.await,
                None => Ok(()),
            }
        }),
    )
    .await
}
/// A due step while the recorded mode is still current, so a step after a
/// confirmed Driver finds it and starts no job (managed reads and finds it).
async fn step(rig: &Rig) {
    rig.runtime.driver_mode_due_for_tests();
    tick(rig).await;
}
/// One interval later: the step is due and the recorded mode is stale, as
/// the owner's record is at its next due step.
async fn later(rig: &Rig) {
    rig.runtime.driver_mode_age_for_tests(INTERVAL);
    rig.reader.driver_mode_age_for_tests(INTERVAL);
    tick(rig).await;
}
/// The mode a fresh observeState status carries: what the read cadence holds
/// (`ReadSource::record_driver_mode`). The fixture read job sends no 00/84
/// query of its own, so only the owner records a mode here.
async fn published(rig: &Rig) -> Option<bool> {
    let observed = rig
        .request(json!({"operation":"observeState","version":91,"requestId":"dddddddd-bbbb-cccc-dddd-000000000072","includeLiveReadings":false,"requestedLiveReadings":0,"effectiveLiveReadings":0}))
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
fn captures(rig: &Rig) -> usize {
    rig.factory_calls.load(Ordering::Acquire)
}
/// (owns, indeterminate, recovery latch).
fn owner(rig: &Rig) -> (bool, bool, bool) {
    rig.runtime.driver_mode_state_for_tests()
}
fn mailbox(rig: &Rig) -> MailboxSource {
    MailboxSource::Reader(rig.reader.clone())
}
/// Records a read job's result under the current read admission.
fn record(rig: &Rig, mode: DeviceMode) {
    let admission = ReadCustody::authorize(rig.custody.as_ref()).unwrap();
    rig.reader
        .record_driver_mode(admission, DeviceModeRead::Confirmed(mode), Instant::now());
}
/// The shutdown release on its own, and its outcome.
async fn release(rig: &Rig) -> Option<Release> {
    rig.held(
        HeldClock::CatchUp,
        Box::pin(async {
            rig.runtime
                .release_driver_mode(&mailbox(rig))
                .await
                .unwrap();
        }),
    )
    .await;
    rig.runtime.driver_mode_release()
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
/// SCM STOP, then the drain on a held test clock (`Rig::drain`), so a loaded
/// host spends none of the drain's deadline.
async fn shutdown(rig: &Rig) -> io::Result<()> {
    rig.origin.stop();
    let drained = rig.drain().await;
    rig.source.lock().await.confirm_drained().unwrap();
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
    assert_eq!(owner(&rig), (true, false, false));
    assert_eq!(published(&rig).await, Some(true));
    assert_eq!(revision(&rig).await, feed);
    rig.stop().await;
}

/// Managed returns true for a keyboard already in Driver mode, with no
/// write and no claim.
#[tokio::test]
async fn already_driver_reports_true_without_a_write_or_a_claim() {
    let (rig, _) = owner_rig(DRIVER).await;
    tick(&rig).await;
    assert_eq!(keyboard(&rig).len(), 1);
    assert_eq!(sets(&rig), 0);
    assert_eq!(owner(&rig), (false, false, false));
    assert_eq!(rig.runtime.driver_mode_claims_for_tests(), 0);
    assert_eq!(published(&rig).await, Some(true));
    rig.stop().await;
}

/// After the startup call, a step whose read job holds Driver for the
/// current admission starts no job; a Normal or stale result is read again.
#[tokio::test]
async fn a_current_driver_result_starts_no_job_after_the_startup_call() {
    let (rig, _) = owner_rig(DRIVER).await;
    tick(&rig).await;
    rig.port.state.lock().unwrap().device_mode = NORMAL;
    for _ in 0..3 {
        step(&rig).await;
    }
    assert_eq!((keyboard(&rig).len(), sets(&rig)), (1, 0));
    // A read job's Normal result under the same admission is read again.
    record(&rig, DeviceMode::Normal);
    step(&rig).await;
    assert_eq!(
        (keyboard(&rig).len(), sets(&rig), mode(&rig)),
        (4, 1, DRIVER)
    );
    // A stale Driver result is read again, and finds Driver.
    later(&rig).await;
    assert_eq!((keyboard(&rig).len(), sets(&rig)), (5, 1));
    rig.stop().await;
}

/// The startup call reads even when a read job already holds Driver.
#[tokio::test]
async fn the_startup_call_always_reads() {
    let (rig, _) = owner_rig(NORMAL).await;
    record(&rig, DeviceMode::Driver);
    tick(&rig).await;
    assert_eq!((sets(&rig), mode(&rig)), (1, DRIVER));
    rig.stop().await;
}

/// Ownership is claimed before the setter can be submitted: a setter whose
/// submission fails, so it may have reached the firmware, leaves the claim
/// and makes the owner Indeterminate with null published and no recovery.
/// Before a later step resolves it, the shutdown release still restores
/// Normal after a fresh read of Driver.
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
    let sent = keyboard(&rig).len();
    later(&rig).await;
    assert_eq!((keyboard(&rig).len(), sets(&rig)), (sent + 2, 1));
    assert_eq!(owner(&rig), (true, true, false));
    assert_eq!(published(&rig).await, None);
    rig.port.state.lock().unwrap().device_mode_fail_set = false;
    rig.port.state.lock().unwrap().device_mode = DRIVER;
    shutdown(&rig).await.unwrap();
    assert_eq!((sets(&rig), mode(&rig)), (2, NORMAL));
    assert_eq!(rig.runtime.driver_mode_release(), Some(Release::Restored));
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
    assert_eq!(owner(&rig), (false, false, true));
    assert_eq!(published(&rig).await, None);
    assert_eq!(mode(&rig), NORMAL);
}

/// The checkpoint before the first send: refused there, the job sends
/// nothing (`DeviceModeStep::Pending`), so the step records nothing, spends
/// no recovery and claims nothing.
#[tokio::test]
async fn the_checkpoint_before_the_first_send_refuses_with_nothing_sent() {
    let (rig, _) = owner_rig(NORMAL).await;
    // The first two fresh authorizations are the step.s admission and the
    // lane.s; the job.s first checkpoint authorizes twice more.
    rig.custody.refuse_at.store(5, Ordering::Release);
    let start = captures(&rig);
    tick(&rig).await;
    assert!(keyboard(&rig).is_empty());
    assert_eq!(rig.runtime.driver_mode_claims_for_tests(), 0);
    assert_eq!(captures(&rig), start + 1, "no recovery");
    assert!(!rig.reader.driver_mode_recorded_for_tests());
    assert_eq!(owner(&rig), (false, false, false));
    rig.custody.fresh_refuse.store(false, Ordering::Release);
    rig.stop().await;
}

/// The checkpoints after the setter's submission and before the readback:
/// refused at either, the setter may have been applied, so the step is
/// Indeterminate with the claim kept and nothing replayed. The next due
/// step's read of Driver resolves it and keeps that claim.
#[tokio::test]
async fn a_checkpoint_refused_after_submission_is_indeterminate() {
    // (authorization after the first query's send, exchanges sent)
    for (nth, sent) in [(7, 2), (9, 2)] {
        let (rig, _) = owner_rig(NORMAL).await;
        refuse_after_first_query(&rig, nth);
        tick(&rig).await;
        assert_eq!((keyboard(&rig).len(), sets(&rig)), (sent, 1), "{nth}");
        assert_eq!(owner(&rig), (true, true, false), "{nth}");
        assert_eq!(published(&rig).await, None, "{nth}");
        rig.custody.fresh_refuse.store(false, Ordering::Release);
        later(&rig).await;
        assert_eq!((keyboard(&rig).len(), sets(&rig)), (sent + 1, 1), "{nth}");
        assert_eq!(owner(&rig), (true, false, false), "{nth}");
        assert_eq!(published(&rig).await, Some(true), "{nth}");
        rig.stop().await;
        assert_eq!(mode(&rig), NORMAL, "{nth}");
    }
}

/// An acknowledgement that does not echo the setter, and a readback that
/// does not confirm Driver or times out, are Indeterminate: null is
/// published, the claim is kept and nothing is ever replayed. The next due
/// step's read resolves each from the mode the keyboard is in: Driver keeps
/// the claim, and Normal (the ignored setter) ends it.
#[tokio::test]
async fn an_ambiguous_acknowledgement_or_readback_publishes_null_until_a_read_resolves_it() {
    for (case, driver) in [
        ("bad ack", true),
        ("ignored setter", false),
        ("readback timeout", true),
    ] {
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
        later(&rig).await;
        assert_eq!(sets(&rig), 1, "{case}");
        assert_eq!(owner(&rig), (true, true, false), "{case}");
        assert_eq!(published(&rig).await, None, "{case}");
        let sent = keyboard(&rig).len();
        {
            let mut port = rig.port.state.lock().unwrap();
            port.device_mode_bad_ack = false;
            port.device_mode_ignore_set = false;
            port.device_mode_timeout = None;
        }
        later(&rig).await;
        assert_eq!((keyboard(&rig).len(), sets(&rig)), (sent + 1, 1), "{case}");
        assert_eq!(owner(&rig), (driver, false, false), "{case}");
        assert_eq!(published(&rig).await, Some(driver), "{case}");
        rig.stop().await;
    }
}

/// The keyboard exchanges sent from the `from`th on: (command, payload).
fn commands(rig: &Rig, from: usize) -> Vec<(u8, Vec<u8>)> {
    keyboard(rig)[from..]
        .iter()
        .map(|(command, payload, _)| (*command, payload.clone()))
        .collect()
}
/// The 00/84 query.
fn query() -> (u8, Vec<u8>) {
    (0x84, vec![0, 0])
}
/// A power-status notice, which moves the lifecycle epoch, while the next
/// job's `nth` 00/84 query is sent.
fn notice_at_query(rig: &Rig, nth: usize) {
    let custody = Arc::downgrade(&rig.custody);
    let query = rig.port.state.lock().unwrap().device_mode_queries + nth;
    rig.port.state.lock().unwrap().device_mode_on_query = Some(Arc::new(move |sent| {
        if sent == query {
            custody.upgrade().unwrap().lifecycle.ingress(POWER, SETTING);
        }
    }));
}
/// That notice delivered inside the `nth` fresh authorization after the
/// next job's first 00/84 query is sent, once the authorization has read
/// the epoch it checks.
fn notice_after_first_query(rig: &Rig, nth: usize) {
    let custody = Arc::downgrade(&rig.custody);
    let start = rig.port.state.lock().unwrap().device_mode_queries + 1;
    rig.port.state.lock().unwrap().device_mode_on_query = Some(Arc::new(move |query| {
        if query == start {
            let custody = custody.upgrade().unwrap();
            custody.notice_skip.store(nth - 1, Ordering::Release);
            *custody.notice_during_authorize.lock().unwrap() = Some((POWER, SETTING, None));
        }
    }));
}
/// The queued defect: an acquire whose setter was applied and exactly
/// acknowledged, with a power notice moving the epoch its capture pins while
/// the independent readback is sent. The readback is fenced, so the owner is
/// Indeterminate with the claim kept and null published.
async fn fenced_readback_rig() -> (Rig, Rc<Cell<bool>>) {
    let (rig, named) = owner_rig(NORMAL).await;
    rig.custody.pinned_fresh.store(true, Ordering::Release);
    notice_at_query(&rig, 2);
    tick(&rig).await;
    rig.port.state.lock().unwrap().device_mode_on_query = None;
    assert_eq!(commands(&rig, 0), [query(), (0x04, vec![3, 0]), query()]);
    assert_eq!((sets(&rig), mode(&rig)), (1, DRIVER));
    assert_eq!(owner(&rig), (true, true, false));
    assert_eq!(published(&rig).await, None);
    (rig, named)
}

/// A later due step resolves a fenced readback read-only: its own 00/84
/// read under a fresh capture finds Driver, so the acquire took effect. The
/// claim the acquire made is kept and no new one is made, true is published,
/// and maintenance resumes on the ordinary cadence with no further write.
#[tokio::test]
async fn a_later_read_of_driver_resolves_a_fenced_readback() {
    let (rig, _) = fenced_readback_rig().await;
    let sent = keyboard(&rig).len();
    let start = captures(&rig);
    later(&rig).await;
    assert_eq!(commands(&rig, sent), [query()]);
    assert_eq!(captures(&rig), start + 1);
    assert_eq!(owner(&rig), (true, false, false));
    assert_eq!(rig.runtime.driver_mode_claims_for_tests(), 1);
    assert_eq!(published(&rig).await, Some(true));
    later(&rig).await;
    assert_eq!(commands(&rig, sent + 1), [query()]);
    assert_eq!(sets(&rig), 1);
    shutdown(&rig).await.unwrap();
    assert_eq!(rig.runtime.driver_mode_release(), Some(Release::Restored));
    assert_eq!(mode(&rig), NORMAL);
}

/// A later read of Normal says the acquire did not take effect (a failed
/// submission) or was undone (a fenced readback, then a foreign Normal). The
/// claim ends and false is published. The next tick acquires as a new write:
/// a fresh capture, read-before-write, a new claim and a setter on a new ID.
#[tokio::test]
async fn a_later_read_of_normal_ends_the_claim_and_the_next_tick_acquires_afresh() {
    for case in ["not applied", "undone"] {
        let rig = if case == "undone" {
            let (rig, _) = fenced_readback_rig().await;
            rig.port.state.lock().unwrap().device_mode = NORMAL;
            rig
        } else {
            let (rig, _) = owner_rig(NORMAL).await;
            rig.port.state.lock().unwrap().device_mode_fail_set = true;
            tick(&rig).await;
            rig.port.state.lock().unwrap().device_mode_fail_set = false;
            assert_eq!(owner(&rig), (true, true, false), "{case}");
            rig
        };
        let first = keyboard(&rig)[1].clone();
        assert_eq!(
            (first.0, sets(&rig), mode(&rig)),
            (0x04, 1, NORMAL),
            "{case}"
        );
        let sent = keyboard(&rig).len();
        later(&rig).await;
        assert_eq!(commands(&rig, sent), [query()], "{case}");
        assert_eq!(owner(&rig), (false, false, false), "{case}");
        assert_eq!(rig.runtime.driver_mode_claims_for_tests(), 1, "{case}");
        assert_eq!(published(&rig).await, Some(false), "{case}");
        let start = captures(&rig);
        tick(&rig).await;
        assert_eq!(captures(&rig), start + 1, "{case}");
        assert_eq!(
            commands(&rig, sent + 1),
            [query(), (0x04, vec![3, 0]), query()],
            "{case}"
        );
        assert_ne!(keyboard(&rig)[sent + 2].2, first.2, "{case}: a new ID");
        assert_eq!((sets(&rig), mode(&rig)), (2, DRIVER), "{case}");
        assert_eq!(owner(&rig), (true, false, false), "{case}");
        assert_eq!(rig.runtime.driver_mode_claims_for_tests(), 2, "{case}");
        assert_eq!(published(&rig).await, Some(true), "{case}");
        rig.stop().await;
    }
}

/// A resolution read that is unreadable stays Indeterminate and publishes
/// null, as the Indeterminate step did, with no recovery and no write. One
/// refused before any exchange, at the capture or by the lane, records
/// nothing. Each later due step reads again until one confirms the mode.
#[tokio::test]
async fn an_unreadable_resolution_read_stays_indeterminate() {
    let (rig, named) = fenced_readback_rig().await;
    rig.port.state.lock().unwrap().device_mode_mute = true;
    let sent = keyboard(&rig).len();
    let start = captures(&rig);
    later(&rig).await;
    assert_eq!(commands(&rig, sent), [query()]);
    assert_eq!(captures(&rig), start + 1, "no recovery");
    assert_eq!(owner(&rig), (true, true, false));
    assert_eq!(published(&rig).await, None);
    later(&rig).await;
    assert_eq!(commands(&rig, sent), [query(), query()]);
    assert_eq!(owner(&rig), (true, true, false));
    rig.port.state.lock().unwrap().device_mode_mute = false;
    // No console user, then an admission the lane refuses: nothing is sent.
    named.set(false);
    later(&rig).await;
    named.set(true);
    rig.custody.fresh_refuse.store(true, Ordering::Release);
    let start = captures(&rig);
    later(&rig).await;
    assert_eq!(captures(&rig), start + 1);
    rig.custody.fresh_refuse.store(false, Ordering::Release);
    assert_eq!(keyboard(&rig).len(), sent + 2);
    assert_eq!(owner(&rig), (true, true, false));
    later(&rig).await;
    assert_eq!(commands(&rig, sent + 2), [query()]);
    assert_eq!(sets(&rig), 1);
    assert_eq!(owner(&rig), (true, false, false));
    assert_eq!(published(&rig).await, Some(true));
    rig.stop().await;
}

/// The resolution read is fenced by the epoch its fresh capture pins: a
/// notice while its query is sent, or inside any of the four authorizations
/// of the checkpoints after its settle and after its reply, leaves it
/// unconfirmed, so the owner stays Indeterminate with null published. The
/// next due step resolves it.
#[tokio::test]
async fn a_notice_during_the_resolution_read_leaves_it_indeterminate() {
    // `None`: at the send; otherwise the nth authorization after it.
    for during in [None, Some(1), Some(2), Some(3), Some(4)] {
        let (rig, _) = fenced_readback_rig().await;
        match during {
            None => notice_at_query(&rig, 1),
            Some(nth) => notice_after_first_query(&rig, nth),
        }
        let sent = keyboard(&rig).len();
        later(&rig).await;
        rig.port.state.lock().unwrap().device_mode_on_query = None;
        assert!(
            rig.custody
                .notice_during_authorize
                .lock()
                .unwrap()
                .is_none(),
            "{during:?}: the notice was delivered"
        );
        assert_eq!(commands(&rig, sent), [query()], "{during:?}");
        assert_eq!(owner(&rig), (true, true, false), "{during:?}");
        assert_eq!(published(&rig).await, None, "{during:?}");
        later(&rig).await;
        assert_eq!(commands(&rig, sent + 1), [query()], "{during:?}");
        assert_eq!(sets(&rig), 1, "{during:?}");
        assert_eq!(owner(&rig), (true, false, false), "{during:?}");
        rig.stop().await;
    }
}

/// A release while Indeterminate still restores Normal and ends the claim.
/// Its confirmed Normal also settles the acquire's doubt: the record is
/// `Clear`, so nothing stays Indeterminate (with only memory, the doubt
/// outlived the release). A later read of Driver, set by someone else, keeps
/// the keyboard unclaimed: a read never claims, so a Stop that finds it
/// Normal again reports `NotDriver`, not a foreign change.
#[tokio::test]
async fn a_resolution_read_of_driver_never_claims_the_keyboard() {
    let (rig, _) = fenced_readback_rig().await;
    suspend(&rig);
    tick(&rig).await;
    assert_eq!(prepared(&rig), Some(Release::Restored));
    assert_eq!((sets(&rig), mode(&rig)), (2, NORMAL));
    assert_eq!(owner(&rig), (false, false, false));
    rig.custody.lifecycle.ingress(POWER, RESUME);
    rig.port.state.lock().unwrap().device_mode = DRIVER;
    later(&rig).await;
    assert_eq!(owner(&rig), (false, false, false));
    assert_eq!(rig.runtime.driver_mode_claims_for_tests(), 1);
    assert_eq!(sets(&rig), 2);
    assert_eq!(published(&rig).await, Some(true));
    rig.port.state.lock().unwrap().device_mode = NORMAL;
    shutdown(&rig).await.unwrap();
    assert_eq!(rig.runtime.driver_mode_release(), Some(Release::NotDriver));
}

/// The resolution keeps the step's skip conditions (managed
/// `RequiresReconciliation`): while a laptop-control journal row is
/// unsettled it reads nothing and the owner stays Indeterminate.
#[tokio::test]
async fn an_unsettled_journal_row_defers_the_resolution() {
    let (rig, _) = fenced_readback_rig().await;
    rig.custody.pinned_fresh.store(false, Ordering::Release);
    let writes = rig.port.state.lock().unwrap().writes;
    rig.port.state.lock().unwrap().fail_write = Some(writes + 1);
    assert_eq!(rig.set(1, value(1, 1)).await["result"]["status"], 2);
    assert!(rig.storage.pending());
    let sent = keyboard(&rig).len();
    later(&rig).await;
    assert_eq!(keyboard(&rig).len(), sent);
    assert_eq!(owner(&rig), (true, true, false));
}

/// The resolving step's read is its first exchange and its only one: it
/// writes nothing before it, and nothing after it in the same tick, whatever
/// it reads, with the recovery latch unspent.
#[tokio::test]
async fn the_resolving_step_reads_before_any_write_and_writes_nothing() {
    for (keyboard_mode, muted) in [(DRIVER, false), (NORMAL, false), (DRIVER, true)] {
        let (rig, _) = fenced_readback_rig().await;
        {
            let mut port = rig.port.state.lock().unwrap();
            port.device_mode = keyboard_mode;
            port.device_mode_mute = muted;
        }
        assert!(!owner(&rig).2, "the recovery latch is unspent");
        let sent = keyboard(&rig).len();
        let case = (keyboard_mode, muted);
        later(&rig).await;
        assert_eq!(commands(&rig, sent), [query()], "{case:?}");
        assert_eq!((sets(&rig), mode(&rig)), (1, keyboard_mode), "{case:?}");
        rig.port.state.lock().unwrap().device_mode_mute = false;
        rig.stop().await;
    }
}

/// One recovery per failure streak: a failed baseline read is followed in
/// the same step by one fresh capture and read-before-write.
#[tokio::test]
async fn a_failed_step_recovers_once_with_a_fresh_read_before_write() {
    let (rig, _) = owner_rig(NORMAL).await;
    rig.port.state.lock().unwrap().device_mode_timeout = Some(1);
    let start = captures(&rig);
    tick(&rig).await;
    assert_eq!(captures(&rig), start + 2);
    assert_eq!(rig.port.state.lock().unwrap().device_mode_queries, 3);
    assert_eq!((sets(&rig), mode(&rig)), (1, DRIVER));
    assert_eq!(owner(&rig), (true, false, false));
    assert_eq!(published(&rig).await, Some(true));
    rig.stop().await;
}

/// The recovery latch holds through a failure streak and resets on success.
#[tokio::test]
async fn the_recovery_latch_holds_for_a_failure_streak_and_resets_on_success() {
    let (rig, _) = owner_rig(NORMAL).await;
    let mute = |muted| rig.port.state.lock().unwrap().device_mode_mute = muted;
    mute(true);
    let start = captures(&rig);
    tick(&rig).await;
    assert_eq!(captures(&rig), start + 2, "a failure and its one recovery");
    assert!(owner(&rig).2);
    later(&rig).await;
    assert_eq!(
        captures(&rig),
        start + 3,
        "the streak has no second recovery"
    );
    mute(false);
    later(&rig).await;
    assert_eq!(owner(&rig), (true, false, false));
    assert_eq!(published(&rig).await, Some(true));
    mute(true);
    let start = captures(&rig);
    later(&rig).await;
    assert_eq!(
        captures(&rig),
        start + 2,
        "a new streak recovers once again"
    );
    assert_eq!(published(&rig).await, None);
    assert_eq!(sets(&rig), 1);
    mute(false);
    rig.stop().await;
}

/// A step refused before any firmware command (`DeviceModeStep::Pending`)
/// records nothing, so the read job still queries and a recorded mode is
/// not replaced with null, and spends no recovery.
#[tokio::test]
async fn a_step_refused_before_any_exchange_records_nothing() {
    let (rig, _) = owner_rig(NORMAL).await;
    rig.custody.fresh_refuse.store(true, Ordering::Release);
    let start = captures(&rig);
    tick(&rig).await;
    assert_eq!(captures(&rig), start + 1, "no recovery");
    assert!(!rig.reader.driver_mode_recorded_for_tests());
    assert!(keyboard(&rig).is_empty());
    assert_eq!(owner(&rig), (false, false, false));
    rig.custody.fresh_refuse.store(false, Ordering::Release);
    // A read job's Normal result stays published through a write port that
    // failed to open, which is known not applied too.
    record(&rig, DeviceMode::Normal);
    rig.port.state.lock().unwrap().mutation_open_fails = 1;
    step(&rig).await;
    assert!(keyboard(&rig).is_empty());
    assert_eq!(published(&rig).await, Some(false));
    assert!(!owner(&rig).2);
    step(&rig).await;
    assert_eq!((sets(&rig), mode(&rig)), (1, DRIVER));
    rig.stop().await;
}

/// A refused lease activation is known not applied: nothing is sent or
/// recorded, and a later step activates and writes.
#[tokio::test]
async fn a_refused_activation_records_nothing_and_a_later_step_writes() {
    let (rig, _) = owner_rig(NORMAL).await;
    rig.custody.activation_refusal.store(1, Ordering::Release);
    tick(&rig).await;
    assert!(keyboard(&rig).is_empty());
    assert!(!rig.reader.driver_mode_recorded_for_tests());
    assert_eq!(owner(&rig), (false, false, false));
    rig.custody.activation_refusal.store(0, Ordering::Release);
    observe(&rig).await;
    step(&rig).await;
    assert_eq!((sets(&rig), mode(&rig)), (1, DRIVER));
    rig.stop().await;
}

/// A recovery-only lease never activates a write, so the step neither
/// captures nor queues a job, reads keep working, and with no capture the
/// shutdown release cannot write either.
#[tokio::test]
async fn a_recovery_only_lease_skips_the_step_and_cannot_release() {
    let (rig, _) = owner_rig(DRIVER).await;
    rig.custody.activation_refusal.store(3, Ordering::Release);
    let start = captures(&rig);
    for _ in 0..3 {
        step(&rig).await;
    }
    assert_eq!(captures(&rig), start);
    assert!(keyboard(&rig).is_empty());
    observe(&rig).await;
    shutdown(&rig).await.unwrap();
    assert_eq!(rig.runtime.driver_mode_release(), Some(Release::Unadmitted));
    assert_eq!((sets(&rig), mode(&rig)), (0, DRIVER));
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
    let start = captures(&rig);
    step(&rig).await;
    assert_eq!(captures(&rig), start);
    named.set(true);
    // A locked or switched console refuses the scoped capture.
    rig.custody.scoped_fresh.store(true, Ordering::Release);
    rig.custody.current_console.store(false, Ordering::Release);
    step(&rig).await;
    assert_eq!(captures(&rig), start + 1);
    rig.custody.current_console.store(true, Ordering::Release);
    rig.custody.scoped_fresh.store(false, Ordering::Release);
    assert_eq!(keyboard(&rig).len(), 1);
    assert_eq!(published(&rig).await, confirmed);
    assert_eq!(owner(&rig), (false, false, false));
    later(&rig).await;
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
    let start = captures(&rig);
    let before = published(&rig).await;
    for _ in 0..3 {
        step(&rig).await;
    }
    assert!(keyboard(&rig).is_empty());
    assert_eq!(captures(&rig), start);
    assert_eq!(published(&rig).await, before);
    rig.custody.lifecycle.ingress(POWER, RESUME);
    assert!(!rig.custody.lifecycle.low_power_or_unknown());
    step(&rig).await;
    assert_eq!((sets(&rig), mode(&rig)), (1, DRIVER));
    rig.stop().await;
}

/// Managed returns without publishing while a laptop-control journal row
/// is unsettled (`RequiresReconciliation`).
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
    assert!(!owner(&rig).2);
}

/// A fan job that has not joined blocks the step on its own
/// (`FanStopController.BlocksOrdinaryHardware`), without waiting for the
/// lane, though no journal row is pending.
#[tokio::test]
async fn an_unjoined_fan_job_skips_the_step_without_waiting() {
    let rig = fan_rig_with(0, 0, 2);
    rig.get().await;
    console(&rig, Rc::new(Cell::new(true)));
    observe(&rig).await;
    let held = Arc::new(AtomicBool::new(false));
    rig.port.state.lock().unwrap().held_read = Some(held.clone());
    let holding = rig.port.state.lock().unwrap().holding.clone();
    {
        let request = rig.request(fan_request(101, Some(1), 3700));
        tokio::pin!(request);
        tokio::select! {
            _ = &mut request => panic!("the held fan job completed"),
            _ = holding.notified() => {}
        }
    }
    assert!(!rig.storage.pending(), "only the fan job is unsettled");
    let start = captures(&rig);
    tokio::time::timeout(Duration::from_secs(1), tick(&rig))
        .await
        .expect("the step does not wait for the lane");
    assert_eq!(captures(&rig), start + 1);
    assert!(keyboard(&rig).is_empty());
    assert!(!owner(&rig).2);
    held.store(true, Ordering::Release);
    rig.port.state.lock().unwrap().held_read = None;
    rig.stop().await;
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
    assert_eq!(owner(&rig), (false, false, false));
    rig.custody.controller.store(false, Ordering::Release);
    rig.stop().await;
}

/// A request holding the mutation gate defers the step to the next tick,
/// which runs it without waiting for the interval.
#[tokio::test]
async fn a_held_gate_defers_the_step_to_the_next_tick() {
    let (rig, _) = owner_rig(NORMAL).await;
    let gate = rig.runtime.driver_mode_gate_for_tests();
    let held = gate.lock().await;
    let start = captures(&rig);
    tick(&rig).await;
    assert_eq!((captures(&rig), keyboard(&rig).len()), (start, 0));
    drop(held);
    tick(&rig).await;
    assert_eq!((sets(&rig), mode(&rig)), (1, DRIVER));
    rig.stop().await;
}

/// Without exact-device verification the step publishes null, writes
/// nothing and resets the recovery latch (managed `TryGetVerifiedDevice`).
#[tokio::test]
async fn an_unverified_device_publishes_null_and_resets_the_latch() {
    let (rig, _) = owner_rig(NORMAL).await;
    rig.port.state.lock().unwrap().device_mode_mute = true;
    tick(&rig).await;
    assert!(owner(&rig).2);
    rig.port.state.lock().unwrap().device_mode_mute = false;
    rig.reader.expire_for_tests();
    let (start, sent) = (captures(&rig), keyboard(&rig).len());
    for _ in 0..3 {
        step(&rig).await;
    }
    assert!(!owner(&rig).2);
    assert_eq!((captures(&rig), keyboard(&rig).len()), (start, sent));
    assert_eq!(published(&rig).await, None);
    // A fresh exact-device read makes the next tick eligible.
    observe(&rig).await;
    tick(&rig).await;
    assert_eq!((sets(&rig), mode(&rig)), (1, DRIVER));
    assert_eq!(published(&rig).await, Some(true));
    rig.stop().await;
}

/// The step is due at the first eligible tick, the managed startup call,
/// then every 15 s, as managed's; an unchanged status is not republished.
#[tokio::test]
async fn the_step_runs_at_the_first_tick_then_every_interval_without_republishing() {
    let (rig, _) = owner_rig(DRIVER).await;
    tick(&rig).await;
    assert_eq!(keyboard(&rig).len(), 1);
    assert_eq!(published(&rig).await, Some(true));
    let first = revision(&rig).await;
    tick(&rig).await;
    let early = INTERVAL - Duration::from_secs(2);
    rig.runtime.driver_mode_age_for_tests(early);
    rig.reader.driver_mode_age_for_tests(early);
    tick(&rig).await;
    assert_eq!(keyboard(&rig).len(), 1);
    rig.runtime
        .driver_mode_age_for_tests(Duration::from_secs(2));
    rig.reader.driver_mode_age_for_tests(Duration::from_secs(2));
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
        // Each step levels the test clock with real time, so on a loaded
        // host the run outlasts the retained read (`RETENTION_WINDOW`) and
        // an unverified device runs no step. A fresh read keeps each step
        // verified; its read job sends no 00/84, so every step acquires.
        observe(&rig).await;
        later(&rig).await;
        assert_eq!(mode(&rig), DRIVER);
    }
    assert_eq!(sets(&rig), 201);
    assert!(rig.port.free_transactions() >= free);
    assert_eq!(published(&rig).await, Some(true));
    rig.stop().await;
}

/// Every ID the job may send is reserved before the first send: with fewer
/// than four free, nothing is sent and the reservation is returned.
#[tokio::test]
async fn every_id_is_reserved_before_the_first_send() {
    let (rig, _) = owner_rig(NORMAL).await;
    let free = rig.port.free_transactions();
    rig.port.reserve_for_tests((free - 2) / 2);
    let left = rig.port.free_transactions();
    assert!((2..4).contains(&left), "{left}");
    tick(&rig).await;
    assert!(keyboard(&rig).is_empty());
    assert_eq!(rig.runtime.driver_mode_claims_for_tests(), 0);
    assert_eq!(rig.port.free_transactions(), left);
    // Nothing was sent: no record and no recovery.
    assert!(!rig.reader.driver_mode_recorded_for_tests());
    assert_eq!(owner(&rig), (false, false, false));
    rig.stop().await;
}

/// The setter is sent only when the job can still read its acknowledgement
/// and the readback (`Pace::covers`); too little time is known not applied,
/// before any claim, and leaves nothing in doubt (`DeviceModeStep::Pending`):
/// nothing is recorded and no recovery is spent.
#[tokio::test]
async fn the_setter_needs_time_for_its_acknowledgement_and_readback() {
    for (latency, applied) in [(800, false), (100, true)] {
        let (rig, _) = owner_rig(NORMAL).await;
        rig.port.state.lock().unwrap().device_mode_latency = Some(Duration::from_millis(latency));
        tick(&rig).await;
        let claims = rig.runtime.driver_mode_claims_for_tests();
        assert_eq!(
            (sets(&rig) == 1, claims == 1),
            (applied, applied),
            "{latency}"
        );
        assert!(!owner(&rig).1, "{latency}");
        assert_eq!(
            rig.reader.driver_mode_recorded_for_tests(),
            applied,
            "{latency}"
        );
        assert!(!owner(&rig).2, "{latency}: no recovery");
        rig.port.state.lock().unwrap().device_mode_latency = None;
        rig.stop().await;
    }
}

/// Fatal admission evidence fails the step and the release.
#[tokio::test]
async fn fatal_admission_evidence_fails_the_step_and_the_release() {
    let (rig, _) = owner_rig(NORMAL).await;
    rig.custody.integrity_lost.store(true, Ordering::Release);
    assert!(try_tick(&rig).await.is_err());
    assert!(keyboard(&rig).is_empty());
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    assert_eq!(sets(&rig), 1);
    rig.custody.fatal_health.store(true, Ordering::Release);
    assert!(
        rig.runtime
            .release_driver_mode(&mailbox(&rig))
            .await
            .is_err()
    );
    assert_eq!((sets(&rig), mode(&rig)), (1, DRIVER));
    rig.custody.fatal_health.store(false, Ordering::Release);
}

/// A fatal error a worker reported is retained: it fails the step that ran
/// the job and every later one.
#[tokio::test]
async fn a_fatal_worker_error_is_retained_by_later_steps() {
    let (rig, _) = owner_rig(NORMAL).await;
    rig.port.state.lock().unwrap().mutation_worker_error = Some(Arc::new(|| {
        crate::installed::readiness::thermal_mutation::fatal_admission("injected")
    }));
    assert!(try_tick(&rig).await.is_err());
    rig.port.state.lock().unwrap().mutation_worker_error = None;
    rig.runtime.driver_mode_due_for_tests();
    assert!(try_tick(&rig).await.is_err());
}

/// A worker that fails after its job leaves the lane fenced: no later
/// keyboard exchange and no release write.
#[tokio::test]
async fn a_worker_error_fences_later_keyboard_writes() {
    let (rig, _) = owner_rig(NORMAL).await;
    rig.port.state.lock().unwrap().mutation_worker_error =
        Some(Arc::new(|| io::Error::other("injected worker failure")));
    tick(&rig).await;
    rig.port.state.lock().unwrap().mutation_worker_error = None;
    let sent = keyboard(&rig).len();
    later(&rig).await;
    assert_eq!(keyboard(&rig).len(), sent);
    assert_eq!(release(&rig).await, Some(Release::Unsettled));
    assert_eq!((keyboard(&rig).len(), mode(&rig)), (sent, DRIVER));
}

/// Shutdown restores Normal before the fan handback (managed
/// `ReleaseDriverModeCoreAsync("shutdown")`, then `StopFans`), confirmed by
/// an independent readback.
#[tokio::test]
async fn shutdown_restores_normal_before_the_fan_handback() {
    let rig = fan_owner_rig().await;
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
    assert_eq!(owner(&rig), (false, false, false));
    assert_eq!(rig.runtime.driver_mode_release(), Some(Release::Restored));
}

/// Managed releases a Driver mode it did not set: a fresh read of Driver
/// under owned-Stop admission writes Normal once. A read of Normal ends an
/// owned claim as a foreign change, or finds nothing to do.
#[tokio::test]
async fn the_release_restores_any_driver_mode_and_reports_a_foreign_change() {
    let (rig, _) = owner_rig(DRIVER).await;
    tick(&rig).await;
    shutdown(&rig).await.unwrap();
    assert_eq!((sets(&rig), mode(&rig)), (1, NORMAL));
    assert_eq!(rig.runtime.driver_mode_release(), Some(Release::Restored));

    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    rig.port.state.lock().unwrap().device_mode = NORMAL;
    assert_eq!(release(&rig).await, Some(Release::ForeignChange));
    assert_eq!((sets(&rig), owner(&rig)), (1, (false, false, false)));
    rig.stop().await;

    let (rig, _) = owner_rig(DRIVER).await;
    tick(&rig).await;
    rig.port.state.lock().unwrap().device_mode = NORMAL;
    assert_eq!(release(&rig).await, Some(Release::NotDriver));
    assert_eq!(sets(&rig), 0);
    // The release job runs once.
    rig.port.state.lock().unwrap().device_mode = DRIVER;
    assert_eq!(release(&rig).await, Some(Release::Attempted));
    assert_eq!(sets(&rig), 0);
    rig.stop().await;
}

/// The suspend notice (PBT_APMSUSPEND), as `Lifecycle::notice` delivers it.
fn suspend(rig: &Rig) {
    rig.custody.lifecycle.ingress(POWER, SUSPEND);
}
/// The last prepared suspend generation's keyboard outcome.
fn prepared(rig: &Rig) -> Option<Release> {
    rig.runtime.low_power_outcome_for_tests()
}
/// The progress of every fan journal row.
fn progress(rig: &Rig) -> Vec<u64> {
    fan_rows(rig)
        .iter()
        .filter_map(|row| row["progress"].as_u64())
        .collect()
}

/// A release holds the Service's cleanup admission, never a console
/// capture: with nobody signed in, the console switched and the write epoch
/// moved since the last step, the release still restores Normal, while the
/// same conditions refuse every acquire.
#[tokio::test]
async fn the_release_needs_no_console_user() {
    let (rig, named) = owner_rig(NORMAL).await;
    rig.custody.pinned_fresh.store(true, Ordering::Release);
    tick(&rig).await;
    assert_eq!(owner(&rig), (true, false, false));
    named.set(false);
    rig.custody.current_console.store(false, Ordering::Release);
    rig.custody.current_session.store(8, Ordering::Release);
    rig.custody.lifecycle.ingress(POWER, SETTING);
    let captured = captures(&rig);
    later(&rig).await;
    assert_eq!(
        captures(&rig),
        captured,
        "no console user, no acquire capture"
    );
    assert_eq!(release(&rig).await, Some(Release::Restored));
    assert_eq!((sets(&rig), mode(&rig)), (2, NORMAL));
    assert_eq!(
        captures(&rig),
        captured,
        "the release captured no console user"
    );
    rig.custody.current_session.store(7, Ordering::Release);
    rig.custody.current_console.store(true, Ordering::Release);
    rig.stop().await;
}

/// Each cleanup refusal sends nothing and leaves the mode unchanged: a
/// competing controller, unknown Windows power, a revoked custody (the
/// device's exact custody), or no cleanup admission composed at all.
#[tokio::test]
async fn the_cleanup_admission_refuses_with_nothing_sent() {
    for refusal in ["controller", "power", "custody", "uncomposed"] {
        let (rig, _) = if refusal == "uncomposed" {
            let files = MemoryFiles::default();
            files.profiles(1);
            let rig = Rig::new(files);
            rig.port.state.lock().unwrap().device_mode = DRIVER;
            observe(&rig).await;
            (rig, Rc::new(Cell::new(true)))
        } else {
            owner_rig(NORMAL).await
        };
        tick(&rig).await;
        let sent = keyboard(&rig).len();
        match refusal {
            "controller" => rig.custody.controller.store(true, Ordering::Release),
            "power" => rig.port.state.lock().unwrap().windows_on_battery = Some(None),
            "custody" => rig.custody.live.store(false, Ordering::Release),
            _ => {}
        }
        assert_eq!(release(&rig).await, Some(Release::Unadmitted), "{refusal}");
        assert_eq!(keyboard(&rig).len(), sent, "{refusal}");
        assert_eq!(mode(&rig), DRIVER, "{refusal}");
        rig.custody.controller.store(false, Ordering::Release);
        rig.port.state.lock().unwrap().windows_on_battery = None;
        rig.custody.live.store(true, Ordering::Release);
    }
}

/// A confirmed suspend releases the keyboard at once and once per
/// generation, under the cleanup admission for that generation with nobody
/// signed in, and leaves owned manual fans as they are: rule 1 names only the
/// keyboard at a low-power boundary, and native has no Fan Stop to restore.
/// Owned Stop still hands them back, and the saved fan override is unchanged.
#[tokio::test]
async fn a_suspend_releases_the_keyboard_and_leaves_the_fans() {
    let rig = fan_owner_rig().await;
    let saved = rig.files.read("manual-overrides.json").unwrap();
    assert!(saved.is_some());
    let rows = progress(&rig);
    let before = rig.port.state.lock().unwrap().sent.len();
    rig.custody.current_console.store(false, Ordering::Release);
    suspend(&rig);
    tick(&rig).await;
    assert_eq!(prepared(&rig), Some(Release::Restored));
    {
        let state = rig.port.state.lock().unwrap();
        let fans_sent = state.sent[before..]
            .iter()
            .filter(|(class, ..)| *class != 0)
            .count();
        assert_eq!(fans_sent, 0);
        assert_eq!((state.device_mode, state.fans), (NORMAL, [1, 1]));
    }
    assert_eq!(progress(&rig), rows);
    assert_eq!(owner(&rig), (false, false, false));
    // The same generation is prepared once.
    let sent = rig.port.state.lock().unwrap().sent.len();
    tick(&rig).await;
    tick(&rig).await;
    assert_eq!(rig.port.state.lock().unwrap().sent.len(), sent);
    assert_eq!(prepared(&rig), Some(Release::Restored));
    // A second release of that generation is its one attempt again, and
    // sends nothing, not even the baseline query.
    let generation = rig.custody.lifecycle.low_power().unwrap().generation;
    let again = rig
        .runtime
        .release_driver_mode_for(
            &mailbox(&rig),
            Boundary::LowPower(generation),
            Duration::from_millis(900),
        )
        .await
        .unwrap();
    assert_eq!(again, Release::Attempted);
    assert_eq!(rig.port.state.lock().unwrap().sent.len(), sent);
    rig.custody.current_console.store(true, Ordering::Release);
    assert_eq!(rig.files.read("manual-overrides.json").unwrap(), saved);
    rig.custody.lifecycle.ingress(POWER, RESUME);
    shutdown(&rig).await.unwrap();
    assert_eq!(rig.port.state.lock().unwrap().fans, [0, 0]);
}

/// A suspend from a late notice on a slow device leaves the fan owner whole
/// (reviewer R1): no row, so after the resume a manual request and a return
/// to Automatic both apply, and the Stop that follows succeeds.
#[tokio::test]
async fn a_late_suspend_on_a_slow_device_leaves_the_fan_owner_whole() {
    let rig = fan_owner_rig().await;
    rig.runtime
        .read_notice_age(|_| Some(Duration::from_millis(1250)));
    rig.port.state.lock().unwrap().latency = Some(Duration::from_millis(150));
    let rows = progress(&rig);
    suspend(&rig);
    tick(&rig).await;
    assert_eq!(progress(&rig), rows);
    assert_eq!(rig.port.state.lock().unwrap().fans, [1, 1]);
    rig.port.state.lock().unwrap().latency = None;
    rig.custody.lifecycle.ingress(POWER, RESUME);
    let manual = rig.request(fan_request(102, Some(1), 3600)).await;
    assert_eq!(manual["result"]["status"], 1, "{manual}");
    let automatic = rig.request(fan_request(103, None, 3700)).await;
    assert_eq!(automatic["result"]["status"], 1, "{automatic}");
    assert_eq!(rig.port.state.lock().unwrap().fans, [0, 0]);
    shutdown(&rig).await.unwrap();
}

/// The suspend notice as the SCM dispatcher thread delivers it, after the
/// rig's preparation armed the hold on this, the runtime's, thread: the
/// notice's hold, which only another thread can wait on.
fn held_suspend(rig: &Rig) -> crate::origin_lifetime::PendingHold {
    rig.custody
        .lifecycle
        .notice(POWER, SUSPEND, None)
        .expect("a new suspend generation is held")
}
/// Waits on `hold` from a thread that is not the runtime's, as the SCM
/// dispatcher does: whether the preparation recorded its generation, and
/// how long the wait took.
fn wait_elsewhere(hold: crate::origin_lifetime::PendingHold) -> (bool, Duration) {
    std::thread::spawn(move || {
        let at = Instant::now();
        (hold.wait(), at.elapsed())
    })
    .join()
    .unwrap()
}

/// The SCM callback that delivered a suspend is held, on its own thread,
/// until the preparation running on the runtime's thread has released the
/// keyboard and recorded the generation, well within `MAXIMUM_AGE`. The
/// hold sends nothing and changes no admission: the release is the same
/// three exchanges under the same cleanup scope.
#[tokio::test]
async fn a_held_suspend_callback_ends_when_the_preparation_records_it() {
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    rig.runtime.hold_suspend_for_tests(&rig.custody.lifecycle);
    let sent = keyboard(&rig).len();
    let runtime_thread = std::thread::current().id();
    let custody = rig.custody.clone();
    let (accepted, delivered) = std::sync::mpsc::channel();
    let dispatcher = std::thread::spawn(move || {
        assert_ne!(std::thread::current().id(), runtime_thread);
        let hold = custody.lifecycle.notice(POWER, SUSPEND, None).unwrap();
        accepted.send(()).unwrap();
        let at = Instant::now();
        (hold.wait(), at.elapsed())
    });
    delivered.recv().unwrap();
    std::thread::sleep(Duration::from_millis(50));
    assert!(!dispatcher.is_finished(), "held until the preparation runs");
    tick(&rig).await;
    let (recorded, waited) = dispatcher.join().unwrap();
    assert!(recorded);
    assert!(
        waited >= Duration::from_millis(50)
            && waited < crate::installed::readiness::thermal_mutation::LOW_POWER_MAXIMUM_AGE,
        "{waited:?}"
    );
    assert_eq!(prepared(&rig), Some(Release::Restored));
    assert_eq!(
        keyboard(&rig)[sent..]
            .iter()
            .map(|(command, ..)| *command)
            .collect::<Vec<_>>(),
        [0x84, 0x04, 0x84]
    );
    assert_eq!(mode(&rig), NORMAL);
    rig.custody.lifecycle.ingress(POWER, RESUME);
    rig.stop().await;
}

/// Every exit of the preparation records its generation, so the held
/// callback returns at once: a notice too old to prepare, a refused
/// release, fatal admission evidence and a preparation dropped mid-release.
#[tokio::test]
async fn every_exit_of_the_preparation_ends_the_hold() {
    let fast =
        |(recorded, waited): (bool, Duration)| recorded && waited < Duration::from_millis(100);
    // Aged out.
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    rig.runtime.read_notice_age(|_| {
        Some(crate::installed::readiness::thermal_mutation::LOW_POWER_MAXIMUM_AGE)
    });
    rig.runtime.hold_suspend_for_tests(&rig.custody.lifecycle);
    let hold = held_suspend(&rig);
    tick(&rig).await;
    assert_eq!(prepared(&rig), Some(Release::Exhausted));
    assert!(fast(wait_elsewhere(hold)), "aged");
    // Refused by the cleanup admission.
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    rig.runtime.hold_suspend_for_tests(&rig.custody.lifecycle);
    rig.custody.controller.store(true, Ordering::Release);
    let hold = held_suspend(&rig);
    tick(&rig).await;
    assert_eq!(prepared(&rig), Some(Release::Unadmitted));
    assert!(fast(wait_elsewhere(hold)), "unadmitted");
    assert_eq!(mode(&rig), DRIVER);
    rig.custody.controller.store(false, Ordering::Release);
    // Fatal admission evidence fails the step after recording.
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    rig.runtime.hold_suspend_for_tests(&rig.custody.lifecycle);
    rig.port.state.lock().unwrap().mutation_worker_error = Some(Arc::new(|| {
        crate::installed::readiness::thermal_mutation::fatal_admission("injected")
    }));
    let hold = held_suspend(&rig);
    assert!(try_tick(&rig).await.is_err());
    assert_eq!(prepared(&rig), None, "the error records no outcome");
    assert!(fast(wait_elsewhere(hold)), "error");
    rig.port.state.lock().unwrap().mutation_worker_error = None;
    // A preparation dropped while its release runs.
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    rig.runtime.hold_suspend_for_tests(&rig.custody.lifecycle);
    rig.port.state.lock().unwrap().device_mode_retire_delay = Some(Duration::from_millis(300));
    let hold = held_suspend(&rig);
    let dropped = tokio::time::timeout(Duration::from_millis(100), tick(&rig)).await;
    assert!(dropped.is_err(), "the release was still running");
    assert!(fast(wait_elsewhere(hold)), "dropped");
    rig.port.state.lock().unwrap().device_mode_retire_delay = None;
}

/// WTSSESSION_NOTIFICATION codes Windows sends as it logs every user off
/// before a shutdown or a Fast Startup hibernate.
const WTS_CONSOLE_DISCONNECT: u32 = 2;
const WTS_SESSION_LOGOFF: u32 = 6;
/// Windows logs every user off: the logoff and console-disconnect notices,
/// then nobody is signed in and the console has no session.
fn log_everyone_off(rig: &Rig, named: &Cell<bool>) {
    rig.custody
        .lifecycle
        .ingress(SESSION_CHANGE, WTS_SESSION_LOGOFF);
    rig.custody
        .lifecycle
        .ingress(SESSION_CHANGE, WTS_CONSOLE_DISCONNECT);
    named.set(false);
    rig.custody.current_console.store(false, Ordering::Release);
}

/// A full shutdown logs every user off before SHUTDOWN reaches the
/// Service; the Owned Stop release is captured after those notices, holds
/// the Service's cleanup admission and restores Normal with nobody signed
/// in and no console capture.
#[tokio::test]
async fn a_shutdown_after_logoff_releases() {
    let (rig, named) = owner_rig(NORMAL).await;
    tick(&rig).await;
    assert_eq!((owner(&rig), mode(&rig)), ((true, false, false), DRIVER));
    log_everyone_off(&rig, &named);
    let captured = captures(&rig);
    shutdown(&rig).await.unwrap();
    assert_eq!(rig.runtime.driver_mode_release(), Some(Release::Restored));
    assert_eq!((sets(&rig), mode(&rig)), (2, NORMAL));
    assert_eq!(captures(&rig), captured, "no console capture");
}

/// A shutdown while the low-power gate is still set, a missed resume,
/// refuses the Owned Stop release with nothing sent, as managed blocks its
/// Stop cleanup in low power: Owned Stop pins the ordinary epoch, which the
/// gate refuses. Here the suspend's own preparation came too late to run,
/// so the keyboard is still in Driver mode.
#[tokio::test]
async fn a_shutdown_while_suspended_refuses_the_release() {
    use crate::installed::readiness::thermal_mutation::LOW_POWER_MAXIMUM_AGE;
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    rig.runtime.read_notice_age(|_| Some(LOW_POWER_MAXIMUM_AGE));
    suspend(&rig);
    tick(&rig).await;
    assert_eq!(prepared(&rig), Some(Release::Exhausted));
    let sent = keyboard(&rig).len();
    shutdown(&rig).await.unwrap();
    assert_eq!(rig.runtime.driver_mode_release(), Some(Release::Unadmitted));
    assert_eq!(keyboard(&rig).len(), sent, "nothing sent");
    assert_eq!((sets(&rig), mode(&rig)), (1, DRIVER));
}

/// An observeState request with request number `n`, as a client's status
/// read; its reply.
#[cfg(feature = "service-profile-triggers-v1")]
async fn observed(rig: &Rig, n: u32) -> Value {
    rig.request(json!({"operation":"observeState","version":91,"requestId":format!("dddddddd-bbbb-cccc-dddd-{n:012}"),"includeLiveReadings":false,"requestedLiveReadings":0,"effectiveLiveReadings":0}))
        .await
}
/// One lifecycle tick and the reconciliation it starts.
#[cfg(feature = "service-profile-triggers-v1")]
async fn lifecycle_tick(rig: &Rig) {
    let reconcile = rig.source.lock().await.reconcile_lifecycle();
    if let Some(reconcile) = reconcile {
        reconcile.await.unwrap();
    }
}
/// Wires the lifecycle tick as `Runtime::native` does and runs its first
/// pass.
#[cfg(feature = "service-profile-triggers-v1")]
async fn wire_lifecycle(rig: &Rig) {
    let epoch = rig.custody.clone();
    rig.runtime
        .read_admission_epoch(move || epoch.lifecycle.active());
    let custody = rig.custody.clone();
    rig.runtime
        .read_power_generation(move || custody.lifecycle.observe_power_kind(None));
    Fresh::power_generation(rig.custody.as_ref());
    lifecycle_tick(rig).await;
}

/// With the lifecycle triggers, a suspend and resume latch no fan drift
/// (reviewer R4): the suspend leaves owned manual fans as OpenBlade applied
/// them, so no reading after the resume is an external change.
#[cfg(feature = "service-profile-triggers-v1")]
#[tokio::test]
async fn a_suspend_and_resume_latch_no_fan_drift() {
    let rig = fan_owner_rig().await;
    wire_lifecycle(&rig).await;
    let before = observed(&rig, 90).await;
    assert_eq!(before["status"]["externalChanges"], json!([]), "{before}");
    suspend(&rig);
    tick(&rig).await;
    lifecycle_tick(&rig).await;
    assert_eq!(prepared(&rig), Some(Release::Restored));
    rig.custody.lifecycle.ingress(POWER, RESUME);
    for round in 0..4u32 {
        lifecycle_tick(&rig).await;
        let status = observed(&rig, 91 + round).await;
        later(&rig).await;
        assert_eq!(
            status["status"]["externalChanges"],
            json!([]),
            "{round}: {status}"
        );
        assert_eq!(rig.port.state.lock().unwrap().fans, [1, 1], "{round}");
    }
}

/// In low power the step acquires nothing; once Windows reports the system
/// active again (PBT_APMRESUMEAUTOMATIC or PBT_APMRESUMESUSPEND) the 15 s
/// cadence reacquires, and the next suspend generation releases again.
#[tokio::test]
async fn the_release_blocks_reacquire_until_resume() {
    for resume in [RESUME, 7] {
        let (rig, _) = owner_rig(NORMAL).await;
        tick(&rig).await;
        suspend(&rig);
        tick(&rig).await;
        assert_eq!(rig.runtime.driver_mode_release(), Some(Release::Restored));
        assert_eq!((sets(&rig), mode(&rig)), (2, NORMAL), "{resume}");
        for _ in 0..3 {
            later(&rig).await;
        }
        assert_eq!(sets(&rig), 2, "{resume}: no acquire in low power");
        rig.custody.lifecycle.ingress(POWER, resume);
        later(&rig).await;
        assert_eq!((sets(&rig), mode(&rig)), (3, DRIVER), "{resume}");
        suspend(&rig);
        tick(&rig).await;
        assert_eq!((sets(&rig), mode(&rig)), (4, NORMAL), "{resume}");
        rig.stop().await;
    }
}

/// Only a suspend prepares: a power notice that keeps the system active,
/// or a resume, sends nothing.
#[tokio::test]
async fn only_a_suspend_notice_prepares() {
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    let sent = keyboard(&rig).len();
    for notice in [SETTING, RESUME, 7] {
        rig.custody.lifecycle.ingress(POWER, notice);
        tick(&rig).await;
        assert_eq!(prepared(&rig), None, "{notice}");
    }
    assert_eq!(keyboard(&rig).len(), sent);
    assert_eq!(mode(&rig), DRIVER);
    rig.stop().await;
}

/// A cleanup captured for one suspend generation admits nothing once that
/// generation ends: a resume refuses the low-power scope, and the low-power
/// scope never admits an ordinary epoch.
#[tokio::test]
async fn the_low_power_scope_ends_with_its_generation() {
    let (rig, _) = owner_rig(NORMAL).await;
    suspend(&rig);
    let generation = rig.custody.lifecycle.low_power().unwrap().generation;
    let scope = CleanupScope::LowPower { generation };
    let custody: Arc<dyn ReadCustody> = rig.custody.clone();
    let cleanup = CleanupFresh::capture(rig.custody.clone(), custody.clone(), scope).unwrap();
    assert!(cleanup.authorize().is_some());
    assert!(
        CleanupFresh::capture(
            rig.custody.clone(),
            custody.clone(),
            CleanupScope::OwnedStop
        )
        .is_err()
    );
    rig.custody.lifecycle.ingress(POWER, RESUME);
    assert_eq!(cleanup.authorize(), None);
    assert!(CleanupFresh::capture(rig.custody.clone(), custody.clone(), scope).is_err());
    suspend(&rig);
    assert_eq!(
        cleanup.authorize(),
        None,
        "a later generation is not this one"
    );
    rig.custody.lifecycle.ingress(POWER, RESUME);
    rig.stop().await;
}

/// A notice older than managed's `MaximumAge` prepares nothing.
#[tokio::test]
async fn an_expired_suspend_notice_prepares_nothing() {
    use crate::installed::readiness::thermal_mutation::LOW_POWER_MAXIMUM_AGE;
    let (rig, _) = owner_rig(NORMAL).await;
    rig.runtime.read_notice_age(|_| Some(LOW_POWER_MAXIMUM_AGE));
    tick(&rig).await;
    let sent = keyboard(&rig).len();
    suspend(&rig);
    tick(&rig).await;
    assert_eq!(prepared(&rig), Some(Release::Exhausted));
    assert_eq!((keyboard(&rig).len(), mode(&rig)), (sent, DRIVER));
    rig.custody.lifecycle.ingress(POWER, RESUME);
    rig.stop().await;
}

/// A late notice still gives the keyboard the rest of the window, up to its
/// shutdown share, and its gate wait is that whole share (reviewer R5): with
/// 550 or 300 ms left it restores Normal.
#[tokio::test]
async fn a_late_suspend_notice_still_releases_the_keyboard() {
    for left in [550u64, 300] {
        let (rig, _) = owner_rig(NORMAL).await;
        tick(&rig).await;
        assert_eq!((sets(&rig), mode(&rig)), (1, DRIVER));
        rig.runtime
            .read_notice_age(move |_| Some(Duration::from_millis(1500 - left)));
        suspend(&rig);
        tick(&rig).await;
        assert_eq!(prepared(&rig), Some(Release::Restored), "{left}");
        assert_eq!((sets(&rig), mode(&rig)), (2, NORMAL), "{left}");
        rig.custody.lifecycle.ingress(POWER, RESUME);
        rig.stop().await;
    }
}

/// The low-power preparation refuses as the shutdown release does: a
/// competing controller or unknown Windows power sends nothing. Battery
/// admits the keyboard release, which needs no AC. No case sends anything to
/// the fans, and owned Stop still hands them back.
#[tokio::test]
async fn a_suspend_under_a_refused_admission_sends_nothing() {
    for refusal in ["controller", "power", "battery"] {
        let rig = fan_owner_rig().await;
        let sent = rig.port.state.lock().unwrap().sent.len();
        match refusal {
            "controller" => rig.custody.controller.store(true, Ordering::Release),
            "power" => rig.port.state.lock().unwrap().windows_on_battery = Some(None),
            _ => rig.port.state.lock().unwrap().windows_on_battery = Some(Some(true)),
        }
        suspend(&rig);
        tick(&rig).await;
        let keyboard = if refusal == "battery" {
            Release::Restored
        } else {
            Release::Unadmitted
        };
        assert_eq!(prepared(&rig), Some(keyboard), "{refusal}");
        let fans_sent = rig.port.state.lock().unwrap().sent[sent..]
            .iter()
            .filter(|(class, ..)| *class != 0)
            .count();
        assert_eq!(fans_sent, 0, "{refusal}");
        rig.custody.controller.store(false, Ordering::Release);
        rig.port.state.lock().unwrap().windows_on_battery = None;
        // The fan owner is untouched, so Stop hands the fans back.
        rig.custody.lifecycle.ingress(POWER, RESUME);
        shutdown(&rig).await.unwrap();
        assert_eq!(rig.port.state.lock().unwrap().fans, [0, 0], "{refusal}");
    }
}

/// A custody reopened since the owner's write cannot hand its record back:
/// the cleanup proof's generation must be the owner's.
#[tokio::test]
async fn the_handback_needs_the_owner_custody_generation() {
    let rig = fan_owner_rig().await;
    rig.origin.stop();
    rig.custody.generation.store(8, Ordering::Release);
    assert!(
        rig.runtime
            .handback_permit_for_tests(CleanupScope::OwnedStop)
            .is_none()
    );
    rig.custody.generation.store(7, Ordering::Release);
    assert!(
        rig.runtime
            .handback_permit_for_tests(CleanupScope::OwnedStop)
            .is_some()
    );
}

/// A keyboard release that may have submitted Normal unconfirmed ends every
/// later release and acquire write: the uncertain write is never replayed.
#[tokio::test]
async fn an_ambiguous_low_power_release_stops_later_keyboard_writes() {
    let rig = fan_owner_rig().await;
    rig.port.state.lock().unwrap().device_mode_bad_ack = true;
    suspend(&rig);
    tick(&rig).await;
    assert_eq!(prepared(&rig), Some(Release::Unconfirmed));
    assert!(rig.runtime.driver_mode_uncertain_for_tests());
    rig.port.state.lock().unwrap().device_mode_bad_ack = false;
    let sets = sets(&rig);
    rig.custody.lifecycle.ingress(POWER, RESUME);
    later(&rig).await;
    shutdown(&rig).await.unwrap();
    assert_eq!(rig.port.state.lock().unwrap().device_mode_sets, sets);
    assert_eq!(rig.runtime.driver_mode_release(), Some(Release::Uncertain));
}

/// A release whose Normal may have applied unconfirmed is resolved by a later
/// confirmed read, never replayed: a read from a job that started before the
/// release leaves it in doubt, one after the resume resolves it, and the next
/// due step acquires Driver through the normal cadence.
#[tokio::test]
async fn a_later_read_resolves_an_ambiguous_release() {
    let rig = fan_owner_rig().await;
    let before = Instant::now();
    rig.port.state.lock().unwrap().device_mode_bad_ack = true;
    suspend(&rig);
    tick(&rig).await;
    assert_eq!(prepared(&rig), Some(Release::Unconfirmed));
    assert!(rig.runtime.driver_mode_uncertain_for_tests());
    rig.port.state.lock().unwrap().device_mode_bad_ack = false;
    assert_eq!(mode(&rig), NORMAL);
    let sets = sets(&rig);
    rig.custody.lifecycle.ingress(POWER, RESUME);
    let admission = ReadCustody::authorize(rig.custody.as_ref()).unwrap();
    rig.reader.record_driver_mode(
        admission,
        DeviceModeRead::Confirmed(DeviceMode::Normal),
        before,
    );
    step(&rig).await;
    assert!(rig.runtime.driver_mode_uncertain_for_tests());
    assert_eq!(self::sets(&rig), sets);
    record(&rig, DeviceMode::Normal);
    step(&rig).await;
    assert!(!rig.runtime.driver_mode_uncertain_for_tests());
    assert_eq!((self::sets(&rig), mode(&rig)), (sets + 1, DRIVER));
    assert!(owner(&rig).0);
    shutdown(&rig).await.unwrap();
    assert_eq!(rig.runtime.driver_mode_release(), Some(Release::Restored));
}

/// A read that resolves an ambiguous release as Driver keeps the claim it
/// finds and never makes one: a keyboard OpenBlade never claimed, put in
/// Driver by someone else, is still unclaimed after the read, so a Stop that
/// finds it Normal again reports `NotDriver`, not a foreign change.
#[tokio::test]
async fn a_read_of_driver_never_claims_the_keyboard() {
    let (rig, _) = owner_rig(DRIVER).await;
    tick(&rig).await;
    assert_eq!(owner(&rig), (false, false, false));
    rig.port.state.lock().unwrap().device_mode_bad_ack = true;
    suspend(&rig);
    tick(&rig).await;
    assert_eq!(prepared(&rig), Some(Release::Unconfirmed));
    assert!(rig.runtime.driver_mode_uncertain_for_tests());
    rig.port.state.lock().unwrap().device_mode_bad_ack = false;
    rig.custody.lifecycle.ingress(POWER, RESUME);
    rig.port.state.lock().unwrap().device_mode = DRIVER;
    record(&rig, DeviceMode::Driver);
    step(&rig).await;
    assert!(!rig.runtime.driver_mode_uncertain_for_tests());
    assert_eq!(owner(&rig), (false, false, false));
    assert_eq!(rig.runtime.driver_mode_claims_for_tests(), 0);
    rig.port.state.lock().unwrap().device_mode = NORMAL;
    shutdown(&rig).await.unwrap();
    assert_eq!(rig.runtime.driver_mode_release(), Some(Release::NotDriver));
}

/// A session notice during the low-power release, at its baseline read or
/// between the setter's acknowledgement and its readback (reviewer R2, R3),
/// moves the lifecycle epoch but not the suspend generation, so the release
/// still restores Normal and nothing is left in doubt. After the resume
/// requests apply, the keyboard is acquired again, and Stop succeeds.
#[tokio::test]
async fn a_lock_notice_during_the_low_power_release_keeps_its_admission() {
    for at in [1, 2] {
        let rig = fan_owner_rig().await;
        assert_eq!(mode(&rig), DRIVER);
        let custody = Arc::downgrade(&rig.custody);
        let query = rig.port.state.lock().unwrap().device_mode_queries + at;
        rig.port.state.lock().unwrap().device_mode_on_query = Some(Arc::new(move |sent| {
            if sent == query {
                custody
                    .upgrade()
                    .unwrap()
                    .lifecycle
                    .ingress(SESSION_CHANGE, WTS_SESSION_LOCK);
            }
        }));
        suspend(&rig);
        tick(&rig).await;
        rig.port.state.lock().unwrap().device_mode_on_query = None;
        assert_eq!(prepared(&rig), Some(Release::Restored), "{at}");
        assert!(!rig.runtime.driver_mode_uncertain_for_tests(), "{at}");
        assert_eq!(mode(&rig), NORMAL, "{at}");
        rig.custody.lifecycle.ingress(POWER, RESUME);
        let manual = rig.request(fan_request(102, Some(1), 3600)).await;
        assert_eq!(manual["result"]["status"], 1, "{at}: {manual}");
        let sets = sets(&rig);
        later(&rig).await;
        assert_eq!((self::sets(&rig), mode(&rig)), (sets + 1, DRIVER), "{at}");
        shutdown(&rig).await.unwrap();
        assert_eq!(mode(&rig), NORMAL, "{at}");
        assert_eq!(rig.port.state.lock().unwrap().fans, [0, 0], "{at}");
    }
}

/// The cleanup admission's order (`thermal_native::admit_cleanup`), which
/// `CleanupGuard` and its test model share: origin trust, the pinned fence,
/// origin commitment, power and controllers, origin trust and the fence
/// again, then the custody. Each refusal stops the checks after it, an
/// unreadable commitment refuses, and only a competing controller is typed.
#[test]
fn the_cleanup_admission_checks_in_order_and_stops_at_each_refusal() {
    use crate::installed::readiness::thermal_native::{Denial, admit_cleanup};
    const ORDER: [&str; 7] = [
        "verify",
        "fence",
        "committed",
        "power",
        "verify",
        "fence",
        "custody",
    ];
    let proof = ReadAdmission {
        lifecycle: 9,
        generation: [7; 32],
    };
    // Runs the admission with the call at `refuse` failing: a refused
    // commitment reads as `unreadable` or false, and refused power as a
    // competing controller or another refusal.
    let run = |refuse: Option<usize>, unreadable: bool, competing: bool| {
        let calls = std::cell::RefCell::new(Vec::new());
        let passes = |name: &'static str| {
            let mut calls = calls.borrow_mut();
            calls.push(name);
            refuse != Some(calls.len() - 1)
        };
        let result = admit_cleanup(
            || passes("verify"),
            || match passes("committed") {
                false if unreadable => None,
                passed => Some(passed),
            },
            || passes("fence"),
            || match passes("power") {
                true => Ok(()),
                false if competing => Err(Denial::CompetingController),
                false => Err(Denial::Admission),
            },
            || passes("custody").then_some(proof),
        );
        (result, calls.into_inner())
    };
    assert_eq!(run(None, false, false), (Ok(proof), ORDER.to_vec()));
    for at in 0..ORDER.len() {
        assert_eq!(
            run(Some(at), false, false),
            (Err(Denial::Admission), ORDER[..=at].to_vec()),
            "{at}"
        );
    }
    assert_eq!(
        run(Some(2), true, false),
        (Err(Denial::Admission), ORDER[..=2].to_vec())
    );
    assert_eq!(
        run(Some(3), false, true),
        (Err(Denial::CompetingController), ORDER[..=3].to_vec())
    );
}

/// A boundary while a request holds the gate: the keyboard waits on the gate
/// for the rest of its share, as managed's release does, the request's next
/// authorization refuses at the new epoch, and the keyboard then restores
/// Normal. The fans keep what OpenBlade last applied.
#[tokio::test]
async fn a_suspend_while_a_write_is_in_flight() {
    let rig = fan_owner_rig().await;
    let held = Arc::new(AtomicBool::new(false));
    let sent = rig.port.state.lock().unwrap().sent.len();
    let keys = keyboard(&rig).len();
    rig.port.state.lock().unwrap().held_read = Some(held.clone());
    let request = crate::service_status::StatusRequest::decode(
        &serde_json::to_vec(&fan_request(102, Some(1), 3600)).unwrap(),
    )
    .unwrap();
    let mutation = rig.source.lock().await.mutate(
        USER,
        SID.into(),
        request,
        Instant::now() + Duration::from_secs(3),
    );
    let preparing = async {
        while !rig.port.state.lock().unwrap().sent[sent..]
            .iter()
            .any(|(class, command, ..)| (*class, *command) == (13, 0x82))
        {
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
        suspend(&rig);
        let prepare = async {
            let maintain = rig.source.lock().await.maintain_driver_mode().unwrap();
            maintain.await.unwrap();
        };
        // The request is held only until the release has had time to reach
        // the gate, so the release keeps nearly its whole share for its job
        // however loaded the host is. A release that reached the gate later
        // would still find the request refused at the epoch the suspend moved.
        let unhold = async {
            tokio::time::sleep(Duration::from_millis(20)).await;
            held.store(true, Ordering::Release);
            rig.port.state.lock().unwrap().held_read = None;
        };
        tokio::join!(prepare, unhold);
    };
    let (outcome, ()) = tokio::join!(mutation, preparing);
    assert_ne!(
        outcome.unwrap().0.status,
        1,
        "the in-flight request is refused"
    );
    assert_eq!(prepared(&rig), Some(Release::Restored));
    assert_eq!(mode(&rig), NORMAL);
    assert!(keyboard(&rig).len() > keys);
    assert_eq!(rig.port.state.lock().unwrap().fans, [1, 1]);
}

/// A Stop during the low-power release joins the dropped release job and
/// reads how far it got. A job stopped before its setter leaves nothing in
/// doubt, so the Stop release restores Normal itself; a job stopped after
/// its setter may have applied it unconfirmed, so the Stop release sends
/// nothing more.
#[tokio::test]
async fn a_stop_during_the_low_power_release_reads_the_dropped_job() {
    for (held_query, ambiguous) in [(0, false), (1, true)] {
        let (rig, _) = owner_rig(NORMAL).await;
        tick(&rig).await;
        let gate = Arc::new(AtomicBool::new(false));
        let hold = gate.clone();
        let blocked = rig.port.state.lock().unwrap().device_mode_queries + 1 + held_query;
        rig.port.state.lock().unwrap().device_mode_on_query = Some(Arc::new(move |query| {
            while query == blocked && !hold.load(Ordering::Acquire) {
                std::thread::sleep(Duration::from_millis(1));
            }
        }));
        suspend(&rig);
        let dropped = tokio::time::timeout(Duration::from_millis(100), tick(&rig)).await;
        assert!(
            dropped.is_err(),
            "{ambiguous}: the release was still running"
        );
        // The generation ends before the held reply, so the dropped job
        // stops at its next authorization.
        rig.custody.lifecycle.ingress(POWER, RESUME);
        gate.store(true, Ordering::Release);
        shutdown(&rig).await.unwrap();
        assert_eq!(sets(&rig), 2, "{ambiguous}: one release setter");
        let expected = if ambiguous {
            Release::Uncertain
        } else {
            Release::Restored
        };
        assert_eq!(
            rig.runtime.driver_mode_release(),
            Some(expected),
            "{ambiguous}"
        );
        assert_eq!(mode(&rig), NORMAL, "{ambiguous}");
        assert_eq!(rig.runtime.driver_mode_uncertain_for_tests(), ambiguous);
    }
}

/// The handback holds no authority beyond the owner's cleanup recipe: it
/// journals only cleanup stages, and it prepares, resolves and confirms no
/// request intent.
#[tokio::test]
async fn the_handback_refuses_every_stage_beyond_its_recipe() {
    let rig = fan_owner_rig().await;
    let rows = fan_rows(&rig).len();
    rig.origin.stop();
    let permit = rig
        .runtime
        .handback_permit_for_tests(CleanupScope::OwnedStop)
        .unwrap();
    for stage in [0, 6, 10, 11, 12, 30, 44, 52] {
        assert!(permit.record_fan(stage).is_err(), "{stage}");
    }
    let baseline = crate::discovery::performance::ObservedPerformance::PerformanceManual;
    assert!(permit.resolve_fan(2, baseline).is_err());
    assert!(permit.confirm_fan_noop(2, baseline).is_err());
    assert!(permit.prepare_fan(permit.fan_record().unwrap()).is_err());
    assert!(
        permit
            .resolve(2, baseline, &AtomicBool::new(false))
            .is_err()
    );
    assert_eq!(fan_rows(&rig).len(), rows);
    assert!(permit.authorize().is_some());
}

/// The fan handback holds only the owned-Stop scope: a low-power cleanup
/// admission captures no handback permit, so the suspend path cannot reach
/// the fans.
#[tokio::test]
async fn the_handback_refuses_a_low_power_scope() {
    let rig = fan_owner_rig().await;
    suspend(&rig);
    let generation = rig.custody.lifecycle.low_power().unwrap().generation;
    assert!(
        rig.runtime
            .handback_permit_for_tests(CleanupScope::LowPower { generation })
            .is_none()
    );
    rig.custody.lifecycle.ingress(POWER, RESUME);
    rig.origin.stop();
    assert!(
        rig.runtime
            .handback_permit_for_tests(CleanupScope::OwnedStop)
            .is_some()
    );
}

/// A release that starts no job does not consume the one attempt: after a
/// refused activation it reports Unadmitted and a later release runs.
#[tokio::test]
async fn a_release_that_starts_no_job_keeps_its_attempt() {
    let (rig, _) = owner_rig(DRIVER).await;
    rig.custody.activation_refusal.store(1, Ordering::Release);
    tick(&rig).await;
    rig.custody.activation_refusal.store(0, Ordering::Release);
    assert_eq!(release(&rig).await, Some(Release::Unadmitted));
    assert_eq!(sets(&rig), 0);
    observe(&rig).await;
    step(&rig).await;
    assert_eq!(release(&rig).await, Some(Release::Restored));
    assert_eq!((sets(&rig), mode(&rig)), (1, NORMAL));
    rig.stop().await;
}

/// Each release skip leaves the mode unchanged and returns no error: a
/// competing controller, an unsettled journal row, a held gate and origin
/// loss.
#[tokio::test]
async fn release_skips_leave_the_mode_unchanged() {
    // A competing controller: no owned-Stop admission.
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    rig.custody.controller.store(true, Ordering::Release);
    let reads = keyboard(&rig).len();
    assert_eq!(release(&rig).await, Some(Release::Unadmitted));
    assert_eq!((keyboard(&rig).len(), mode(&rig)), (reads, DRIVER));
    assert_eq!(owner(&rig), (true, false, false));
    rig.custody.controller.store(false, Ordering::Release);

    // An unsettled journal row (managed `RequiresReconciliation`).
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    rig.port.state.lock().unwrap().fail_write = Some(1);
    rig.set(1, value(1, 1)).await;
    let reads = keyboard(&rig).len();
    assert_eq!(release(&rig).await, Some(Release::Unsettled));
    assert_eq!((keyboard(&rig).len(), mode(&rig)), (reads, DRIVER));

    // The same row written through another store than the owner's last
    // console capture, as a request of another console user writes it.
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    rig.custody.other_storage.store(true, Ordering::Release);
    rig.port.state.lock().unwrap().fail_write = Some(1);
    rig.set(1, value(1, 1)).await;
    let reads = keyboard(&rig).len();
    assert_eq!(release(&rig).await, Some(Release::Unsettled));
    assert_eq!((keyboard(&rig).len(), mode(&rig)), (reads, DRIVER));

    // A held gate exhausts the share without a write.
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    let gate = rig.runtime.driver_mode_gate_for_tests();
    let held = gate.lock().await;
    let outcome = tokio::time::timeout(Duration::from_secs(2), release(&rig))
        .await
        .expect("the release is bounded");
    assert_eq!(outcome, Some(Release::Exhausted));
    drop(held);
    assert_eq!(mode(&rig), DRIVER);
    rig.stop().await;

    // Origin loss cannot authorize owned cleanup.
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    rig.runtime.refuse_release_driver_mode();
    assert_eq!(rig.runtime.driver_mode_release(), Some(Release::OriginLost));
    assert_eq!(mode(&rig), DRIVER);
}

/// A refused keyboard release is a diagnostic: the Stop still succeeds,
/// and in low power the mode is left unchanged.
#[tokio::test]
async fn a_refused_release_does_not_fail_the_stop() {
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    rig.custody.lifecycle.ingress(POWER, SUSPEND);
    shutdown(&rig).await.unwrap();
    assert_eq!(rig.runtime.driver_mode_release(), Some(Release::Unadmitted));
    assert_eq!((sets(&rig), mode(&rig)), (1, DRIVER));
}

/// A keyboard release that fails after submission is a diagnostic too: it
/// never skips the fan handback or fails the Stop.
#[tokio::test]
async fn a_failed_keyboard_release_does_not_skip_the_fan_handback() {
    let rig = fan_owner_rig().await;
    rig.port.state.lock().unwrap().device_mode_bad_ack = true;
    shutdown(&rig).await.unwrap();
    let state = rig.port.state.lock().unwrap();
    assert_eq!((state.device_mode_sets, state.fans), (2, [0, 0]));
    drop(state);
    assert_eq!(
        rig.runtime.driver_mode_release(),
        Some(Release::Unconfirmed)
    );
    assert!(owner(&rig).0, "an unconfirmed release keeps the claim");
}

/// The keyboard's bounded share of the drain: a release stalled on its
/// first read still leaves the fans their own time, through the real
/// listener-then-source retirement (`Ports::retire`).
#[tokio::test]
async fn a_stalled_keyboard_release_leaves_the_fans_their_drain_time() {
    let rig = fan_owner_rig().await;
    rig.port.state.lock().unwrap().device_mode_latency = Some(Duration::from_secs(5));
    let events = Events::default();
    let (mut ports, _tx) = ports(Fault::None, &events);
    ports.source = rig.source.clone();
    rig.origin.stop();
    ports.retire().await.unwrap();
    let state = rig.port.state.lock().unwrap();
    // The acquire set it; the stalled release sent no setter.
    assert_eq!(
        (state.device_mode_sets, state.device_mode, state.fans),
        (1, DRIVER, [0, 0])
    );
    drop(state);
    assert_eq!(
        rig.runtime.driver_mode_release(),
        Some(Release::Unconfirmed)
    );
}

/// An unsettled fan row blocks the release before the journal check
/// (`FanStopController.BlocksOrdinaryHardware`), with nothing sent.
#[tokio::test]
async fn an_unsettled_fan_row_blocks_the_release() {
    let rig = fan_owner_rig().await;
    let writes = rig.port.state.lock().unwrap().writes;
    rig.port.state.lock().unwrap().fail_write = Some(writes + 1);
    let outcome = rig.request(fan_request(102, Some(1), 3800)).await;
    assert_ne!(outcome["result"]["status"], 1, "{outcome}");
    let sent = keyboard(&rig).len();
    assert_eq!(release(&rig).await, Some(Release::FanBlocked));
    assert_eq!((keyboard(&rig).len(), mode(&rig)), (sent, DRIVER));
}

/// Starts a fan request for the fans' current target, whose first 0D/82
/// read is held, drops the request once that read is sent, and returns the
/// hold: the fan worker stays registered and unjoined, as a request dropped
/// at its deadline leaves it. Released, it finds the target unchanged and
/// journals and writes nothing.
async fn drop_held_fan_request(rig: &Rig) -> Arc<AtomicBool> {
    let held = Arc::new(AtomicBool::new(false));
    let writes = rig.port.state.lock().unwrap().writes;
    let sent = rig.port.state.lock().unwrap().sent.len();
    rig.port.state.lock().unwrap().held_read = Some(held.clone());
    let request = crate::service_status::StatusRequest::decode(
        &serde_json::to_vec(&fan_request(102, Some(1), 3700)).unwrap(),
    )
    .unwrap();
    let mut mutation = rig.source.lock().await.mutate(
        USER,
        SID.into(),
        request,
        Instant::now() + Duration::from_secs(3),
    );
    let end = Instant::now() + Duration::from_secs(2);
    let read = |state: &crate::discovery::performance::mutation_fixture::State| {
        state.sent[sent..]
            .iter()
            .any(|(class, command, ..)| (*class, *command) == (13, 0x82))
    };
    while !read(&rig.port.state.lock().unwrap()) {
        assert!(Instant::now() < end);
        tokio::select! {
            result = &mut mutation => panic!("held fan request completed early: {}", result.is_ok()),
            () = tokio::time::sleep(Duration::from_millis(5)) => {}
        }
    }
    drop(mutation);
    assert_eq!(rig.port.state.lock().unwrap().writes, writes);
    held
}

/// The gate wait and the join of active work have their own bound inside
/// the share: a write job still holding the HID lane exhausts it, with
/// nothing sent, well before the share ends.
#[tokio::test]
async fn a_held_write_job_exhausts_the_release_entry() {
    use crate::installed::readiness::thermal_mutation::KEYBOARD_SHUTDOWN_BUDGET;
    let rig = fan_owner_rig().await;
    let held = drop_held_fan_request(&rig).await;
    let sent = keyboard(&rig).len();
    let started = Instant::now();
    assert_eq!(release(&rig).await, Some(Release::Exhausted));
    let spent = started.elapsed();
    assert!(spent < KEYBOARD_SHUTDOWN_BUDGET / 2, "{spent:?}");
    assert_eq!((keyboard(&rig).len(), mode(&rig)), (sent, DRIVER));
    held.store(true, Ordering::Release);
    rig.port.state.lock().unwrap().held_read = None;
    rig.stop().await;
}

/// The release joins active work before it checks the fans: a fan request
/// dropped mid-read whose worker then finished with nothing to write is
/// joined and settled, so the release restores Normal instead of finding the
/// fan job unjoined.
#[tokio::test]
async fn the_release_checks_the_fans_after_joining_active_work() {
    let rig = fan_owner_rig().await;
    let held = drop_held_fan_request(&rig).await;
    held.store(true, Ordering::Release);
    rig.port.state.lock().unwrap().held_read = None;
    rig.origin.stop();
    assert_eq!(release(&rig).await, Some(Release::Restored));
    assert_eq!(mode(&rig), NORMAL);
    rig.stop().await;
}

/// The share bounds the whole release, the lane's join of the job included:
/// a worker slow to retire ends the release at its share with the job's
/// confirmed result. With the share over, nothing is recorded after the
/// job: the record keeps the release's intent, owned before it, for a later
/// read to resolve.
#[tokio::test]
async fn a_worker_slow_to_retire_ends_the_release_at_its_share() {
    use crate::installed::readiness::thermal_mutation::KEYBOARD_SHUTDOWN_BUDGET;
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    rig.port.state.lock().unwrap().device_mode_retire_delay = Some(Duration::from_millis(2400));
    let started = Instant::now();
    assert_eq!(release(&rig).await, Some(Release::Restored));
    let spent = started.elapsed();
    assert!(spent >= KEYBOARD_SHUTDOWN_BUDGET, "{spent:?}");
    assert!(
        spent < KEYBOARD_SHUTDOWN_BUDGET + Duration::from_millis(400),
        "{spent:?}"
    );
    assert_eq!((sets(&rig), mode(&rig)), (2, NORMAL));
    assert_eq!(owner(&rig), (true, false, false));
    assert!(rig.runtime.driver_mode_uncertain_for_tests());
    rig.stop().await;
}

/// A keyboard worker that retires only after the keyboard's share has ended
/// still leaves the fans the rest of the drain: the fan handback joins it and
/// hands the fans back through the real listener-then-source retirement
/// (`Ports::retire`), past the 2 s a source drain without the keyboard's
/// share would allow. The handback is paced to what is left of the drain,
/// about 0.8 s after a 2.05 s join, which its fixture recipe needs about
/// 0.36 s of; a later join leaves it too little, and it then writes no row.
#[tokio::test]
async fn the_fans_join_a_keyboard_worker_slow_to_retire_within_their_drain_time() {
    let rig = fan_owner_rig().await;
    rig.port.state.lock().unwrap().device_mode_retire_delay = Some(Duration::from_millis(2050));
    let events = Events::default();
    let (mut ports, _tx) = ports(Fault::None, &events);
    ports.source = rig.source.clone();
    rig.origin.stop();
    ports.retire().await.unwrap();
    let state = rig.port.state.lock().unwrap();
    assert_eq!((state.device_mode, state.fans), (NORMAL, [0, 0]));
    drop(state);
    assert_eq!(rig.runtime.driver_mode_release(), Some(Release::Restored));
}

/// The release job has its own budget from its start, inside the share: a
/// baseline read that never replies ends at that budget, well before the
/// share would.
#[tokio::test]
async fn a_stalled_release_job_ends_at_its_own_budget() {
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    rig.port.state.lock().unwrap().device_mode_latency = Some(Duration::from_secs(5));
    let started = Instant::now();
    assert_eq!(release(&rig).await, Some(Release::Unconfirmed));
    let spent = started.elapsed();
    assert!(spent >= Duration::from_millis(700), "{spent:?}");
    assert!(spent < Duration::from_millis(800), "{spent:?}");
    assert_eq!((sets(&rig), mode(&rig)), (1, DRIVER));
    rig.port.state.lock().unwrap().device_mode_latency = None;
}

/// A release whose measured pace cannot fit the setter, its acknowledgement
/// and the readback sends no setter and reports Exhausted, not a setter that
/// may have been submitted; the claim stays.
#[tokio::test]
async fn a_release_without_time_for_the_setter_is_exhausted() {
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    rig.port.state.lock().unwrap().device_mode_latency = Some(Duration::from_millis(300));
    assert_eq!(release(&rig).await, Some(Release::Exhausted));
    assert_eq!((sets(&rig), mode(&rig)), (1, DRIVER));
    assert!(owner(&rig).0, "the claim stays");
    rig.port.state.lock().unwrap().device_mode_latency = None;
    rig.stop().await;
}

/// Fatal admission evidence fails the release, from the join of active work
/// or from the release job's own worker.
#[tokio::test]
async fn fatal_evidence_from_the_lane_fails_the_release() {
    let fatal = || -> Arc<dyn Fn() -> io::Error + Send + Sync> {
        Arc::new(|| crate::installed::readiness::thermal_mutation::fatal_admission("injected"))
    };
    let failed = async |rig: &Rig| {
        rig.runtime
            .release_driver_mode(&mailbox(rig))
            .await
            .is_err()
    };
    // The join of a dropped fan request's worker finds integrity lost.
    let rig = fan_owner_rig().await;
    let held = drop_held_fan_request(&rig).await;
    rig.custody.integrity_lost.store(true, Ordering::Release);
    held.store(true, Ordering::Release);
    rig.port.state.lock().unwrap().held_read = None;
    rig.origin.stop();
    assert!(failed(&rig).await);
    assert_eq!(mode(&rig), DRIVER);

    // The release job's own worker fails fatally after confirming Normal.
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    rig.port.state.lock().unwrap().mutation_worker_error = Some(fatal());
    assert!(failed(&rig).await);
    rig.port.state.lock().unwrap().mutation_worker_error = None;

    // Integrity lost at any of the 17 checks the release passes before its
    // job sends: the cleanup capture's health checks around its first
    // authorization, every later fresh authorization of the cleanup
    // admission (each checks health itself), the lane's integrity check, the
    // lane's authorizations of the job's admission and of its own, and the
    // job's checkpoints before its first query. The 18th follows that query.
    // An authorization refuses as `None`, so each is fatal, never
    // `Unadmitted`.
    for check in 1..=17 {
        let (rig, _) = owner_rig(NORMAL).await;
        tick(&rig).await;
        let sent = keyboard(&rig).len();
        rig.custody
            .integrity_lost_at
            .store(check, Ordering::Release);
        assert!(failed(&rig).await, "{check}");
        assert!(
            rig.custody.integrity_lost.load(Ordering::Acquire),
            "{check}"
        );
        assert_eq!(
            (keyboard(&rig).len(), mode(&rig)),
            (sent, DRIVER),
            "{check}"
        );
    }
}

/// A request holding the mutation gate exhausts the release's entry, with
/// nothing sent, well before the share ends.
#[tokio::test]
async fn a_request_holding_the_gate_exhausts_the_release_entry() {
    use crate::installed::readiness::thermal_mutation::KEYBOARD_SHUTDOWN_BUDGET;
    let rig = fan_owner_rig().await;
    let held = Arc::new(AtomicBool::new(false));
    let sent = rig.port.state.lock().unwrap().sent.len();
    let keys = keyboard(&rig).len();
    rig.port.state.lock().unwrap().held_read = Some(held.clone());
    let request = crate::service_status::StatusRequest::decode(
        &serde_json::to_vec(&fan_request(102, Some(1), 3700)).unwrap(),
    )
    .unwrap();
    let mutation = rig.source.lock().await.mutate(
        USER,
        SID.into(),
        request,
        Instant::now() + Duration::from_secs(3),
    );
    let releasing = async {
        // The request holds the gate once its first 0D/82 read is sent.
        while !rig.port.state.lock().unwrap().sent[sent..]
            .iter()
            .any(|(class, command, ..)| (*class, *command) == (13, 0x82))
        {
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
        let started = Instant::now();
        let outcome = release(&rig).await;
        let spent = started.elapsed();
        held.store(true, Ordering::Release);
        rig.port.state.lock().unwrap().held_read = None;
        (outcome, spent)
    };
    let (_, (outcome, spent)) = tokio::join!(mutation, releasing);
    assert_eq!(outcome, Some(Release::Exhausted));
    assert!(spent < KEYBOARD_SHUTDOWN_BUDGET / 2, "{spent:?}");
    assert_eq!((keyboard(&rig).len(), mode(&rig)), (keys, DRIVER));
    rig.stop().await;
}

/// The release job's deadline also ends inside the share after slow
/// synchronous admissions: after a 600 ms owned-Stop authorization only
/// what is left of the share remains, so 100 ms exchanges leave no time for
/// the setter.
#[tokio::test]
async fn slow_admissions_leave_the_release_job_only_the_rest_of_the_share() {
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    rig.port.state.lock().unwrap().device_mode_latency = Some(Duration::from_millis(100));
    *rig.custody.owned_stop_stall.lock().unwrap() = Some((1, Duration::from_millis(600)));
    assert_eq!(release(&rig).await, Some(Release::Exhausted));
    assert_eq!((sets(&rig), mode(&rig)), (1, DRIVER));
    rig.port.state.lock().unwrap().device_mode_latency = None;
    rig.stop().await;
}

/// The setter's pace charges the baseline's trailing checkpoint: a slow
/// checkpoint after the baseline reply leaves room for the two remaining
/// exchanges at the baseline's length, but not for that checkpoint again,
/// so no setter is sent.
///
/// The pace is the subject, so the job measures it on paused time: 100 ms
/// exchanges (50 ms each way) and the 220 ms stall are exact on its clock,
/// and the release's share runs on a held test clock, so a loaded host can
/// neither end the job inside an exchange nor change what it measures.
#[tokio::test]
async fn the_release_pace_charges_the_trailing_checkpoint() {
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    rig.port.state.lock().unwrap().latency = Some(Duration::from_millis(50));
    *rig.custody.owned_stop_stall.lock().unwrap() =
        Some((TRAILING_CHECKPOINT, Duration::from_millis(220)));
    assert_eq!(release(&rig).await, Some(Release::Exhausted));
    assert_eq!((sets(&rig), mode(&rig)), (1, DRIVER));
    rig.port.state.lock().unwrap().latency = None;
    rig.stop().await;
}

/// Each fatal-evidence backstop of the release, on its own.
#[tokio::test]
async fn each_release_fatal_backstop_holds_on_its_own() {
    let fatal = || -> Arc<dyn Fn() -> io::Error + Send + Sync> {
        Arc::new(|| crate::installed::readiness::thermal_mutation::fatal_admission("injected"))
    };
    // Controller admission poisoned once the readback is sent: the lane's
    // retirement reports it and nothing retains it, so only the release's
    // own fatal arm fails the release.
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    let custody = Arc::downgrade(&rig.custody);
    let readback = rig.port.state.lock().unwrap().device_mode_queries + 2;
    rig.port.state.lock().unwrap().device_mode_on_query = Some(Arc::new(move |query| {
        if query == readback {
            custody
                .upgrade()
                .unwrap()
                .fatal_health
                .store(true, Ordering::Release);
        }
    }));
    assert!(
        rig.runtime
            .release_driver_mode(&mailbox(&rig))
            .await
            .is_err()
    );
    rig.custody.fatal_health.store(false, Ordering::Release);

    // Fatal evidence another holder of the custody retained while the job
    // read back Normal, and a worker slow to retire: the share ends before
    // the lane can report it, so only the release's own check of retained
    // evidence fails it.
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    let retained = mailbox(&rig).fatal_evidence();
    let readback = rig.port.state.lock().unwrap().device_mode_queries + 2;
    rig.port.state.lock().unwrap().device_mode_on_query = Some(Arc::new(move |query| {
        if query == readback {
            *retained.lock().unwrap() = Some("injected retained evidence");
        }
    }));
    rig.port.state.lock().unwrap().device_mode_retire_delay = Some(Duration::from_millis(2400));
    assert!(
        rig.runtime
            .release_driver_mode(&mailbox(&rig))
            .await
            .is_err()
    );
    assert_eq!(mode(&rig), NORMAL);

    // A fatal worker error is retained, so later steps fail too.
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    rig.port.state.lock().unwrap().mutation_worker_error = Some(fatal());
    assert!(
        rig.runtime
            .release_driver_mode(&mailbox(&rig))
            .await
            .is_err()
    );
    rig.port.state.lock().unwrap().mutation_worker_error = None;
    rig.runtime.driver_mode_due_for_tests();
    assert!(try_tick(&rig).await.is_err());

    // The fan handback's cleanup returns the worker's fatal error, not the
    // unconfirmed stop that follows it.
    let rig = fan_owner_rig().await;
    rig.origin.stop();
    rig.port.state.lock().unwrap().mutation_worker_error = Some(fatal());
    let error = rig
        .runtime
        .stop_fans(&mailbox(&rig), Instant::now() + Duration::from_secs(3))
        .await
        .unwrap_err();
    assert!(
        crate::installed::readiness::thermal_mutation::is_fatal_admission(&error),
        "{error}"
    );
    rig.port.state.lock().unwrap().mutation_worker_error = None;
}

/// Without owned-Stop origin admission the drain refuses the keyboard
/// release as origin loss, with nothing sent.
#[tokio::test]
async fn origin_loss_refuses_the_release_in_the_drain() {
    let (rig, _) = owner_rig(NORMAL).await;
    tick(&rig).await;
    let sent = keyboard(&rig).len();
    rig.origin.lose_trust();
    rig.origin.stop();
    let _ = rig.source.lock().await.drain().await;
    assert_eq!(rig.runtime.driver_mode_release(), Some(Release::OriginLost));
    assert_eq!((keyboard(&rig).len(), mode(&rig)), (sent, DRIVER));
}

/// The keyboard's share is added to the source drain, so the fan handback
/// keeps its time, and the listener and source drains together stay inside
/// the 5 s Stop wait hint (`scm_host::status_for`).
#[test]
fn the_keyboard_share_fits_the_stop_wait_hint() {
    use crate::installed::readiness::thermal_mutation::KEYBOARD_SHUTDOWN_BUDGET;
    use crate::installed::readiness::{DRAIN_TIMEOUT, SOURCE_DRAIN_TIMEOUT};
    assert_eq!(
        SOURCE_DRAIN_TIMEOUT,
        DRAIN_TIMEOUT + KEYBOARD_SHUTDOWN_BUDGET
    );
    assert!(KEYBOARD_SHUTDOWN_BUDGET < DRAIN_TIMEOUT / 2);
    let hint = crate::scm_host::status_for(crate::scm_host::Phase::Stopping).dwWaitHint;
    assert!(DRAIN_TIMEOUT + SOURCE_DRAIN_TIMEOUT < Duration::from_millis(hint.into()));
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

#[path = "driver_mode_record_tests.rs"]
mod record;

/// A held display-brightness usage at a confirmed suspend and on the
/// maintenance step (`service-brightness-hid-usage-v1`), over the rig's
/// scripted lane.
#[cfg(feature = "service-brightness-hid-usage-v1")]
mod display_brightness {
    use super::*;
    use crate::installed::readiness::thermal_mutation::brightness_hid::{
        SUBMITTED, Usage, WriteResult, fake,
    };

    fn usages(rig: &Rig) -> Vec<Usage> {
        rig.brightness.lock().unwrap().usages.clone()
    }

    async fn press(rig: &Rig, n: u8, usage: u16) {
        let response = rig
            .request(json!({
                "operation": "setDisplayBrightnessHidUsage",
                "version": 91,
                "requestId": format!("aaaaaaaa-bbbb-cccc-eeee-{n:012}"),
                "usage": usage,
            }))
            .await;
        assert_eq!(
            response["result"],
            json!({"status": 1, "message": SUBMITTED}),
            "{response}"
        );
    }

    /// The keyboard's mode as each later usage reaches the lane.
    fn modes_at_each_usage(rig: &Rig) -> Arc<std::sync::Mutex<Vec<u8>>> {
        let modes = Arc::new(std::sync::Mutex::new(Vec::new()));
        let (seen, port) = (modes.clone(), rig.port.state.clone());
        rig.brightness.lock().unwrap().on_usage = Some(Arc::new(move |_| {
            seen.lock().unwrap().push(port.lock().unwrap().device_mode);
        }));
        modes
    }

    /// Managed `PrepareForLowPowerCoreAsync`: a usage still held is released
    /// first, while the keyboard is still in Driver mode, and the release
    /// settles the held state, so the Stop that follows owes nothing.
    #[tokio::test]
    async fn a_suspend_releases_a_held_usage_before_the_keyboard() {
        let (rig, _) = owner_rig(NORMAL).await;
        tick(&rig).await;
        assert_eq!(owner(&rig), (true, false, false));
        press(&rig, 1, 0x6F).await;
        let modes = modes_at_each_usage(&rig);
        suspend(&rig);
        tick(&rig).await;
        assert_eq!(prepared(&rig), Some(Release::Restored));
        assert_eq!(*modes.lock().unwrap(), [DRIVER]);
        assert_eq!(mode(&rig), NORMAL);
        assert_eq!(
            usages(&rig),
            [Usage::Released, Usage::Increment, Usage::Released]
        );
        // The same generation releases nothing twice.
        tick(&rig).await;
        rig.custody.lifecycle.ingress(POWER, RESUME);
        shutdown(&rig).await.unwrap();
        assert_eq!(usages(&rig).len(), 3);
    }

    /// The brightness release is a diagnostic: one that fails, or one the
    /// lane has not answered when its share ends, leaves the keyboard its
    /// whole share. After a failed one the Stop that follows still owes the
    /// release; an unfinished one stays owed, so Stop queues no other.
    #[tokio::test]
    async fn a_failed_or_unfinished_brightness_release_never_skips_the_keyboard() {
        for unfinished in [false, true] {
            let (rig, _) = owner_rig(NORMAL).await;
            tick(&rig).await;
            press(&rig, 1, 0x70).await;
            let release = unfinished.then(|| fake::hold(&rig.brightness));
            if !unfinished {
                rig.brightness
                    .lock()
                    .unwrap()
                    .writes
                    .push_back(WriteResult::failed(Some("IOCTL failed".into())));
            }
            suspend(&rig);
            tick(&rig).await;
            assert_eq!(prepared(&rig), Some(Release::Restored), "{unfinished}");
            assert_eq!(mode(&rig), NORMAL, "{unfinished}");
            assert_eq!(
                usages(&rig),
                [Usage::Released, Usage::Decrement, Usage::Released],
                "{unfinished}"
            );
            if let Some(release) = release {
                rig.brightness.lock().unwrap().hold = None;
                release();
            }
            rig.custody.lifecycle.ingress(POWER, RESUME);
            shutdown(&rig).await.unwrap();
            let sent = if unfinished { 3 } else { 4 };
            assert_eq!(usages(&rig).len(), sent, "{unfinished}");
        }
    }

    fn request(n: u8, usage: u16) -> Value {
        json!({
            "operation": "setDisplayBrightnessHidUsage",
            "version": 91,
            "requestId": format!("aaaaaaaa-bbbb-cccc-eeee-{n:012}"),
            "usage": usage,
        })
    }

    /// Waits on the real clock until the lane has taken `count` usages.
    async fn taken(rig: &Rig, count: usize) {
        let deadline = std::time::Instant::now() + Duration::from_secs(5);
        while usages(rig).len() < count {
            assert!(std::time::Instant::now() < deadline, "the lane takes it");
            tokio::time::sleep(Duration::from_millis(1)).await;
        }
    }

    /// A press still on the lane when a suspend is confirmed, which ends
    /// `after` into the preparation or, with none, outlasts it. The press's
    /// request stops waiting, so the keyboard takes the gate and keeps its
    /// whole share, and the release the press owes is queued behind it at
    /// once and runs after it, the gate notwithstanding.
    async fn a_press_running_at_suspend(after: Option<Duration>) {
        let (rig, _) = owner_rig(NORMAL).await;
        tick(&rig).await;
        assert_eq!(owner(&rig), (true, false, false));
        let release = fake::hold(&rig.brightness);
        let pressing = rig.request(request(2, 0x6F));
        tokio::pin!(pressing);
        tokio::select! {
            _ = &mut pressing => panic!("the press is held on the lane"),
            () = taken(&rig, 2) => {}
        }
        suspend(&rig);
        let release = Arc::new(release);
        let worker = after.map(|after| {
            let release = release.clone();
            std::thread::spawn(move || {
                std::thread::sleep(after);
                (*release)();
            })
        });
        let (reply, ()) = tokio::join!(&mut pressing, tick(&rig));
        assert_eq!(
            reply["result"],
            json!({
                "status": 3,
                "message": "The display-brightness HID state could not be confirmed: \
                            the laptop entered low power or is stopping",
            }),
            "{after:?}"
        );
        assert_eq!(prepared(&rig), Some(Release::Restored), "{after:?}");
        assert_eq!(mode(&rig), NORMAL, "{after:?}");
        if let Some(worker) = worker {
            worker.join().unwrap();
        } else {
            assert_eq!(usages(&rig), [Usage::Released, Usage::Increment]);
            (*release)();
        }
        taken(&rig, 3).await;
        assert_eq!(
            usages(&rig),
            [Usage::Released, Usage::Increment, Usage::Released],
            "{after:?}"
        );
        // The release that answered late is owed no longer.
        rig.custody.lifecycle.ingress(POWER, RESUME);
        shutdown(&rig).await.unwrap();
        assert_eq!(usages(&rig).len(), 3, "{after:?}");
    }

    #[tokio::test]
    async fn a_press_outlasting_the_suspend_window_never_starves_the_keyboard() {
        a_press_running_at_suspend(None).await;
    }

    #[tokio::test]
    async fn a_press_ending_inside_the_suspend_window_is_released_after_it() {
        a_press_running_at_suspend(Some(Duration::from_millis(450))).await;
    }

    /// A closed Driver-mode record returns the maintenance step before its
    /// own cadence; a pending neutralization is still tried only once per
    /// interval.
    #[tokio::test]
    async fn a_pending_neutralization_is_tried_once_per_interval() {
        use super::super::driver_mode_record_model::RECORD;
        let files = MemoryFiles::default();
        files.profiles(1);
        files
            .bytes
            .lock()
            .unwrap()
            .insert(RECORD.into(), b"not a record".to_vec());
        let rig = Rig::restarted(files);
        for _ in 0..4 {
            rig.brightness
                .lock()
                .unwrap()
                .writes
                .push_back(WriteResult::failed(Some("IOCTL failed".into())));
        }
        let (rig, _) = wired(rig, NORMAL).await;
        for _ in 0..6 {
            tick(&rig).await;
        }
        assert!(rig.runtime.driver_mode_closed_for_tests());
        assert_eq!(usages(&rig), [Usage::Released]);
        rig.runtime
            .driver_mode_age_for_tests(INTERVAL - Duration::from_secs(1));
        tick(&rig).await;
        assert_eq!(usages(&rig).len(), 1);
        rig.runtime
            .driver_mode_age_for_tests(Duration::from_secs(1));
        tick(&rig).await;
        assert_eq!(usages(&rig), [Usage::Released, Usage::Released]);
        rig.stop().await;
    }

    /// Managed `MaintainDriverModeAsync`: a started Service neutralizes on
    /// the first step that finds the device verified, before the keyboard
    /// step, and a release that fails leaves it pending for the next due
    /// step. A failed release never skips the keyboard step, and nothing is
    /// pressed.
    #[tokio::test]
    async fn the_maintenance_step_neutralizes_until_a_release_applies() {
        let files = MemoryFiles::default();
        files.profiles(1);
        let rig = Rig::new(files);
        rig.brightness
            .lock()
            .unwrap()
            .writes
            .push_back(WriteResult::failed(Some("IOCTL failed".into())));
        let modes = modes_at_each_usage(&rig);
        let (rig, _) = wired(rig, NORMAL).await;
        tick(&rig).await;
        assert_eq!(owner(&rig), (true, false, false));
        assert_eq!(mode(&rig), DRIVER);
        assert_eq!(usages(&rig), [Usage::Released]);
        // A step that is not yet due neutralizes nothing.
        tick(&rig).await;
        assert_eq!(usages(&rig).len(), 1);
        step(&rig).await;
        assert_eq!(usages(&rig), [Usage::Released, Usage::Released]);
        // Applied: no later step releases again.
        step(&rig).await;
        later(&rig).await;
        assert_eq!(usages(&rig).len(), 2);
        // The first release ran before the keyboard's acquire, the second
        // with the keyboard already in Driver mode.
        assert_eq!(*modes.lock().unwrap(), [NORMAL, DRIVER]);
        rig.stop().await;
        assert_eq!(usages(&rig).len(), 2, "nothing held, nothing owed");
    }
}
