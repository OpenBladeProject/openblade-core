//! Managed parity for the display-brightness HID input: the control buffer,
//! the transport's path cache, the controller's replies, the lane's
//! submission outcomes and the runtime's held-usage state machine. Managed's
//! own replies are replayed from `tests/data/display-brightness-hid-vectors.json`.
//! No Windows call is reached.

use std::{
    cell::RefCell,
    collections::VecDeque,
    io,
    sync::{Arc, Mutex},
    time::Duration,
};

use serde_json::Value;
use tokio::time::Instant;

use super::fake::{self, Shared};
use super::*;

const VECTORS: &str = include_str!("../../../../tests/data/display-brightness-hid-vectors.json");

fn later() -> Instant {
    Instant::now() + Duration::from_secs(5)
}

fn applied(message: &str) -> Outcome {
    Outcome {
        status: 1,
        message: message.to_owned(),
    }
}

fn failed(message: &str) -> Outcome {
    Outcome {
        status: 3,
        message: message.to_owned(),
    }
}

fn verified() -> io::Result<Option<fn() -> bool>> {
    Ok(Some(|| true))
}

fn unverified() -> io::Result<Option<fn() -> bool>> {
    Ok(None)
}

fn usages(script: &Shared) -> Vec<u16> {
    script.lock().unwrap().usages.clone()
}

fn push(script: &Shared, write: WriteResult) {
    script.lock().unwrap().writes.push_back(write);
}

/// Waits, without holding the runtime, until the lane has taken `count`
/// submissions.
async fn taken(script: &Shared, count: usize) {
    let deadline = std::time::Instant::now() + Duration::from_secs(5);
    while script.lock().unwrap().usages.len() < count {
        assert!(std::time::Instant::now() < deadline, "the lane takes it");
        tokio::time::sleep(Duration::from_millis(1)).await;
    }
}

#[test]
fn control_buffer_is_managed_type_ten_consumer_control() {
    for usage in [RELEASED, INCREMENT, DECREMENT] {
        let buffer = control_buffer(usage);
        let mut expected = [0_u8; 32];
        expected[4] = 10;
        expected[8..10].copy_from_slice(&usage.to_le_bytes());
        assert_eq!(buffer, expected);
    }
    assert_eq!(SUBMIT_INPUT_IOCTL, 0x8888_3020);
}

#[derive(Default)]
struct Scripted {
    resolutions: VecDeque<io::Result<(Option<String>, WriteResult)>>,
    writes: VecDeque<io::Result<WriteResult>>,
    calls: Arc<Mutex<Vec<String>>>,
}

impl Device for Scripted {
    fn resolve(&mut self) -> io::Result<(Option<String>, WriteResult)> {
        self.calls.lock().unwrap().push("resolve".into());
        self.resolutions
            .pop_front()
            .unwrap_or_else(|| Ok((Some("path-a".into()), WriteResult::applied())))
    }

    fn write(&mut self, path: &str, usage: u16) -> io::Result<WriteResult> {
        self.calls
            .lock()
            .unwrap()
            .push(format!("write {path} {usage}"));
        self.writes
            .pop_front()
            .unwrap_or_else(|| Ok(WriteResult::applied()))
    }
}

fn transport(script: impl FnOnce(&mut Scripted)) -> (Transport<Scripted>, Arc<Mutex<Vec<String>>>) {
    let mut device = Scripted::default();
    script(&mut device);
    let calls = device.calls.clone();
    (Transport::new(device), calls)
}

fn calls(log: &Arc<Mutex<Vec<String>>>) -> Vec<String> {
    std::mem::take(&mut *log.lock().unwrap())
}

#[test]
fn transport_caches_a_written_path_until_it_reports_unavailable() {
    let (mut transport, log) = transport(|device| {
        device.writes = VecDeque::from([
            Ok(WriteResult::applied()),
            Ok(WriteResult::applied()),
            Ok(WriteResult::unavailable(Some("gone".into()))),
            Ok(WriteResult::applied()),
        ]);
        device.resolutions = VecDeque::from([
            Ok((Some("path-a".into()), WriteResult::applied())),
            Ok((Some("path-b".into()), WriteResult::applied())),
        ]);
    });
    assert_eq!(transport.set_usage(INCREMENT), WriteResult::applied());
    assert_eq!(calls(&log), ["resolve", "write path-a 111"]);
    assert_eq!(transport.set_usage(RELEASED), WriteResult::applied());
    assert_eq!(calls(&log), ["write path-a 0"]);
    // Unavailable comes before an IOCTL, so the press is resolved once more.
    assert_eq!(transport.set_usage(DECREMENT), WriteResult::applied());
    assert_eq!(
        calls(&log),
        ["write path-a 112", "resolve", "write path-b 112"]
    );
}

#[test]
fn transport_never_retries_a_failed_write_or_caches_an_unwritten_path() {
    let (mut transport, log) = transport(|device| {
        device.writes = VecDeque::from([
            Ok(WriteResult::failed(Some("IOCTL failed".into()))),
            Ok(WriteResult::applied()),
            Ok(WriteResult::failed(None)),
        ]);
    });
    // A failed first write may have submitted: it is reported, not retried,
    // and its path is not cached.
    assert_eq!(
        transport.set_usage(INCREMENT),
        WriteResult::failed(Some("IOCTL failed".into()))
    );
    assert_eq!(calls(&log), ["resolve", "write path-a 111"]);
    assert_eq!(transport.set_usage(RELEASED), WriteResult::applied());
    assert_eq!(calls(&log), ["resolve", "write path-a 0"]);
    // A failed cached write is not retried through a new resolution.
    assert_eq!(transport.set_usage(INCREMENT), WriteResult::failed(None));
    assert_eq!(calls(&log), ["write path-a 111"]);
}

#[test]
fn transport_reports_resolution_refusals_and_errors_as_managed_does() {
    let (mut transport, log) = transport(|device| {
        device.resolutions = VecDeque::from([
            Ok((None, WriteResult::unavailable(Some("absent".into())))),
            Ok((None, WriteResult::failed(Some("untrusted control".into())))),
            Err(io::Error::other("enumeration failed")),
        ]);
        device.writes = VecDeque::from([Err(io::Error::other("open failed"))]);
    });
    assert_eq!(
        transport.set_usage(INCREMENT),
        WriteResult::unavailable(Some("absent".into()))
    );
    assert_eq!(
        transport.set_usage(INCREMENT),
        WriteResult::failed(Some("untrusted control".into()))
    );
    assert_eq!(
        transport.set_usage(INCREMENT),
        WriteResult::failed(Some("enumeration failed".into()))
    );
    assert_eq!(
        transport.set_usage(INCREMENT),
        WriteResult::failed(Some("open failed".into()))
    );
    assert_eq!(
        calls(&log),
        [
            "resolve",
            "resolve",
            "resolve",
            "resolve",
            "write path-a 111"
        ]
    );
}

#[test]
fn transport_availability_resolves_without_seeding_the_cache() {
    let (mut transport, log) = transport(|device| {
        device.resolutions = VecDeque::from([
            Ok((Some("path-a".into()), WriteResult::applied())),
            Ok((None, WriteResult::unavailable(None))),
            Ok((None, WriteResult::failed(Some("untrusted".into())))),
            Err(io::Error::other("enumeration failed")),
        ]);
    });
    assert_eq!(transport.availability(), AVAILABILITY_AVAILABLE);
    assert_eq!(transport.availability(), AVAILABILITY_UNAVAILABLE);
    assert_eq!(transport.availability(), AVAILABILITY_UNKNOWN);
    assert_eq!(transport.availability(), AVAILABILITY_UNKNOWN);
    assert_eq!(calls(&log), ["resolve"; 4]);
    transport.set_usage(RELEASED);
    assert_eq!(calls(&log), ["resolve", "write path-a 0"]);
    // A panicked call leaves nothing cached.
    Hid::recover_from_panic(&mut transport);
    transport.set_usage(RELEASED);
    assert_eq!(calls(&log), ["resolve", "write path-a 0"]);
}

#[test]
fn reply_is_managed_controller_mapping() {
    assert_eq!(reply(&WriteResult::applied()), applied(SUBMITTED));
    assert_eq!(
        reply(&WriteResult::unavailable(Some("absent".into()))),
        Outcome::blocked(PATH_UNAVAILABLE)
    );
    assert_eq!(reply(&WriteResult::failed(None)), failed(UNCONFIRMED));
    assert_eq!(
        reply(&WriteResult::failed(Some(" \t".into()))),
        failed(UNCONFIRMED)
    );
    assert_eq!(
        reply(&WriteResult::failed(Some("IOCTL failed".into()))),
        failed("The display-brightness HID state could not be confirmed: IOCTL failed")
    );
}

#[tokio::test]
async fn lane_submits_on_its_own_thread_and_answers() {
    let (lane, script) = fake::lane();
    push(&script, WriteResult::failed(Some("x".into())));
    assert_eq!(
        lane.submit(INCREMENT, Duration::from_secs(1)).await,
        Called::Done(WriteResult::failed(Some("x".into())))
    );
    assert_eq!(
        lane.submit(RELEASED, Duration::from_secs(1)).await,
        Called::Done(WriteResult::applied())
    );
    assert_eq!(usages(&script), [INCREMENT, RELEASED]);
}

#[tokio::test]
async fn lane_abandons_a_started_call_and_never_starts_a_withdrawn_one() {
    let (lane, script) = fake::lane();
    let release = fake::hold(&script);
    assert_eq!(
        lane.submit(INCREMENT, Duration::from_millis(50)).await,
        Called::Abandoned
    );
    // Queued behind the held call, it times out before starting.
    assert_eq!(
        lane.submit(DECREMENT, Duration::from_millis(50)).await,
        Called::NotStarted
    );
    script.lock().unwrap().hold = None;
    release();
    assert_eq!(
        lane.submit(RELEASED, Duration::from_secs(1)).await,
        Called::Done(WriteResult::applied())
    );
    assert_eq!(usages(&script), [INCREMENT, RELEASED]);
}

#[tokio::test]
async fn lane_refuses_a_submission_beyond_its_queue_at_once() {
    let (lane, script) = fake::lane();
    let release = fake::hold(&script);
    let first = tokio::spawn({
        let lane = lane.clone();
        async move { lane.submit(INCREMENT, Duration::from_secs(5)).await }
    });
    taken(&script, 1).await;
    let queued: Vec<_> = [RELEASED, DECREMENT]
        .into_iter()
        .map(|usage| {
            let lane = lane.clone();
            tokio::spawn(async move { lane.submit(usage, Duration::from_secs(5)).await })
        })
        .collect();
    tokio::time::sleep(Duration::from_millis(20)).await;
    let started = std::time::Instant::now();
    assert_eq!(
        lane.submit(INCREMENT, Duration::from_secs(5)).await,
        Called::NotStarted
    );
    assert!(started.elapsed() < Duration::from_secs(1));
    script.lock().unwrap().hold = None;
    release();
    assert_eq!(first.await.unwrap(), Called::Done(WriteResult::applied()));
    for queued in queued {
        assert_eq!(queued.await.unwrap(), Called::Done(WriteResult::applied()));
    }
    assert_eq!(usages(&script), [INCREMENT, RELEASED, DECREMENT]);
}

#[tokio::test]
async fn lane_survives_a_panicking_call_as_abandoned() {
    let (lane, script) = fake::lane();
    script.lock().unwrap().panic_next = true;
    assert_eq!(
        lane.submit(INCREMENT, Duration::from_secs(1)).await,
        Called::Abandoned
    );
    assert_eq!(
        lane.submit(RELEASED, Duration::from_secs(1)).await,
        Called::Done(WriteResult::applied())
    );
    assert_eq!(usages(&script), [INCREMENT, RELEASED]);
}

#[tokio::test]
async fn lane_publishes_availability_without_waiting_for_windows() {
    let (lane, script) = fake::lane();
    script.lock().unwrap().availability = AVAILABILITY_AVAILABLE;
    assert_eq!(lane.availability(), AVAILABILITY_UNKNOWN);
    let deadline = std::time::Instant::now() + Duration::from_secs(5);
    while lane.availability() != AVAILABILITY_AVAILABLE {
        assert!(std::time::Instant::now() < deadline, "the probe publishes");
        tokio::time::sleep(Duration::from_millis(1)).await;
    }
    // A fresh resolution stands for its lifetime: no further probe.
    script.lock().unwrap().availability = AVAILABILITY_UNAVAILABLE;
    for _ in 0..10 {
        assert_eq!(lane.availability(), AVAILABILITY_AVAILABLE);
    }
    tokio::time::sleep(Duration::from_millis(20)).await;
    assert_eq!(script.lock().unwrap().probes, 1);
    assert!(usages(&script).is_empty());
}

#[tokio::test]
async fn held_usage_is_submitted_once_then_released() {
    let (lane, script) = fake::lane();
    let held = RefCell::default();
    let lane = Some(&*lane);
    for (usage, expected) in [
        (DECREMENT, applied(SUBMITTED)),
        (DECREMENT, applied(REMAINS_ASSERTED)),
        (RELEASED, applied(SUBMITTED)),
    ] {
        assert_eq!(
            set_usage(&held, lane, usage, later(), verified)
                .await
                .unwrap(),
            expected
        );
    }
    assert_eq!(usages(&script), [RELEASED, DECREMENT, RELEASED]);
    assert_eq!(
        *held.borrow(),
        Held {
            asserted: RELEASED,
            release_required: false,
            neutralization_pending: false
        }
    );
}

#[tokio::test]
async fn a_restarted_service_neutralizes_before_its_first_press() {
    let (lane, script) = fake::lane();
    // The state a restart starts from: nothing persisted, neutralization
    // pending.
    let held = RefCell::new(Held::default());
    assert!(held.borrow().neutralization_pending);
    set_usage(&held, Some(&lane), INCREMENT, later(), verified)
        .await
        .unwrap();
    assert_eq!(usages(&script), [RELEASED, INCREMENT]);
}

#[tokio::test]
async fn an_unverified_device_blocks_before_the_transport() {
    let (lane, script) = fake::lane();
    let held = RefCell::default();
    assert_eq!(
        set_usage(&held, Some(&lane), INCREMENT, later(), unverified)
            .await
            .unwrap(),
        Outcome::blocked(DEVICE_REQUIRED)
    );
    assert!(usages(&script).is_empty());
    assert_eq!(*held.borrow(), Held::default());
}

#[tokio::test]
async fn admission_refused_again_before_the_press_submits_nothing_more() {
    let (lane, script) = fake::lane();
    let held = RefCell::default();
    let refused = || -> io::Result<Option<fn() -> bool>> { Ok(Some(|| false)) };
    assert_eq!(
        set_usage(&held, Some(&lane), INCREMENT, later(), refused)
            .await
            .unwrap(),
        Outcome::blocked(DEVICE_REQUIRED)
    );
    // Only the neutralizing release reached the transport.
    assert_eq!(usages(&script), [RELEASED]);
    assert!(!held.borrow().release_required);
}

#[tokio::test]
async fn fatal_admission_fails_closed_without_submitting() {
    let (lane, script) = fake::lane();
    let held = RefCell::default();
    let fatal = || -> io::Result<Option<fn() -> bool>> { Err(io::Error::other("fatal")) };
    let error = set_usage(&held, Some(&lane), INCREMENT, later(), fatal)
        .await
        .unwrap_err();
    assert_eq!(error.to_string(), "fatal");
    assert!(usages(&script).is_empty());
}

#[tokio::test]
async fn an_absent_lane_refuses_as_the_unavailable_path() {
    let held = RefCell::default();
    assert_eq!(
        set_usage(&held, None, INCREMENT, later(), verified)
            .await
            .unwrap(),
        Outcome::blocked(PATH_UNAVAILABLE)
    );
    assert_eq!(
        release(&held, None, later()).await,
        Outcome::blocked(PATH_UNAVAILABLE)
    );
    assert_eq!(*held.borrow(), Held::default());
}

#[tokio::test]
async fn a_press_that_outlives_its_budget_is_held_and_never_replayed() {
    let (lane, script) = fake::lane();
    let held = RefCell::default();
    let lane = Some(&*lane);
    set_usage(&held, lane, RELEASED, later(), verified)
        .await
        .unwrap();
    let release_hold = fake::hold(&script);
    let deadline = Instant::now() + ANSWER_MARGIN + Duration::from_millis(50);
    assert_eq!(
        set_usage(&held, lane, INCREMENT, deadline, verified)
            .await
            .unwrap(),
        failed(
            "The display-brightness HID state could not be confirmed: \
             the submission did not finish within its budget"
        )
    );
    assert_eq!(
        *held.borrow(),
        Held {
            asserted: INCREMENT,
            release_required: true,
            neutralization_pending: false
        }
    );
    script.lock().unwrap().hold = None;
    release_hold();
    // The same press is already held; the next one owes a release first.
    assert_eq!(
        set_usage(&held, lane, INCREMENT, later(), verified)
            .await
            .unwrap(),
        applied(REMAINS_ASSERTED)
    );
    set_usage(&held, lane, DECREMENT, later(), verified)
        .await
        .unwrap();
    assert_eq!(usages(&script), [RELEASED, INCREMENT, RELEASED, DECREMENT]);
}

#[tokio::test]
async fn a_dropped_request_after_the_press_started_still_owes_its_release() {
    let (lane, script) = fake::lane();
    let held = RefCell::default();
    let lane = Some(&*lane);
    set_usage(&held, lane, RELEASED, later(), verified)
        .await
        .unwrap();
    let release_hold = fake::hold(&script);
    tokio::select! {
        _ = set_usage(&held, lane, DECREMENT, later(), verified) => {
            panic!("the press is held on the lane");
        }
        () = taken(&script, 2) => {}
    }
    assert!(held.borrow().release_required);
    assert_eq!(held.borrow().asserted, DECREMENT);
    script.lock().unwrap().hold = None;
    release_hold();
    assert_eq!(release(&held, lane, later()).await, applied(SUBMITTED));
    assert_eq!(usages(&script), [RELEASED, DECREMENT, RELEASED]);
    assert!(!held.borrow().release_required);
}

#[tokio::test]
async fn a_request_dropped_before_its_press_started_submits_nothing() {
    let (lane, script) = fake::lane();
    let held = RefCell::default();
    let lane_ref = Some(&*lane);
    set_usage(&held, lane_ref, RELEASED, later(), verified)
        .await
        .unwrap();
    // Hold the lane on another call, so the press stays queued.
    let release_hold = fake::hold(&script);
    let busy = tokio::spawn({
        let lane = lane.clone();
        async move { lane.submit(RELEASED, Duration::from_secs(5)).await }
    });
    taken(&script, 2).await;
    tokio::select! {
        _ = set_usage(&held, lane_ref, INCREMENT, later(), verified) => {
            panic!("the press waits behind the busy lane");
        }
        () = tokio::time::sleep(Duration::from_millis(50)) => {}
    }
    script.lock().unwrap().hold = None;
    release_hold();
    busy.await.unwrap();
    // The withdrawn press never reached the transport, though the dropped
    // request left it recorded as held: the next press releases first.
    assert_eq!(
        lane.submit(RELEASED, Duration::from_secs(1)).await,
        Called::Done(WriteResult::applied())
    );
    assert_eq!(usages(&script), [RELEASED, RELEASED, RELEASED]);
    assert!(held.borrow().release_required);
}

fn write_result(value: &Value) -> WriteResult {
    let error = value["error"].as_str().map(str::to_owned);
    match value["status"].as_str() {
        Some("Applied") => WriteResult::applied(),
        Some("Unavailable") => WriteResult::unavailable(error),
        Some("Failed") => WriteResult::failed(error),
        other => panic!("unknown write status {other:?}"),
    }
}

fn number(value: &Value) -> u64 {
    value
        .as_u64()
        .or_else(|| value.as_str().and_then(|text| text.parse().ok()))
        .unwrap_or_else(|| panic!("{value} is a number"))
}

#[test]
fn managed_buffers_replay() {
    let root: Value = serde_json::from_str(VECTORS).unwrap();
    assert_eq!(number(&root["ioctl"]), u64::from(SUBMIT_INPUT_IOCTL));
    let buffers = root["buffers"].as_array().unwrap();
    assert_eq!(buffers.len(), 3);
    for row in buffers {
        let usage = u16::try_from(number(&row["usage"])).unwrap();
        let hex: String = control_buffer(usage)
            .iter()
            .map(|byte| format!("{byte:02x}"))
            .collect();
        assert_eq!(row["buffer"].as_str().unwrap(), hex, "usage {usage}");
    }
}

#[tokio::test]
async fn managed_sequences_replay() {
    let root: Value = serde_json::from_str(VECTORS).unwrap();
    let sequences = root["sequences"].as_array().unwrap();
    assert!(sequences.len() >= 10);
    for row in sequences {
        let name = row["name"].as_str().unwrap();
        let (lane, script) = fake::lane();
        for write in row["writes"].as_array().unwrap() {
            push(&script, write_result(write));
        }
        let admitted = row["verified"].as_bool().unwrap();
        let held = RefCell::default();
        let steps = row["steps"].as_array().unwrap();
        let requests = row["requests"].as_array().unwrap();
        assert_eq!(steps.len(), requests.len(), "{name}");
        for (request, step) in requests.iter().zip(steps) {
            let usage = u16::try_from(number(request)).unwrap();
            let admit = || -> io::Result<Option<fn() -> bool>> {
                Ok(admitted.then_some((|| true) as fn() -> bool))
            };
            let outcome = set_usage(&held, Some(&lane), usage, later(), admit)
                .await
                .unwrap();
            let expected = Outcome {
                status: u8::try_from(number(&step["status"])).unwrap(),
                message: step["message"].as_str().unwrap().to_owned(),
            };
            assert_eq!(outcome, expected, "{name} usage {usage}");
        }
        let expected: Vec<u16> = row["usages"]
            .as_array()
            .unwrap()
            .iter()
            .map(|usage| u16::try_from(number(usage)).unwrap())
            .collect();
        assert_eq!(usages(&script), expected, "{name}");
    }
}
