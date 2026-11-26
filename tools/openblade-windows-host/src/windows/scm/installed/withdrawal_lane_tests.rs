//! The shared lane protocol: withdrawal, abandonment, the calls that stay
//! queued, the queue bound and panic recovery. The callers' own suites cover
//! their state (`gpu_clock_offsets_tests`, `display_brightness_hid_tests`).

use std::{
    sync::{Arc, Mutex, mpsc},
    time::Duration,
};

use super::*;

/// What the lane ran, in order, and how often it recovered.
#[derive(Default)]
struct Log {
    ran: Vec<&'static str>,
    recovered: usize,
}

struct State(Arc<Mutex<Log>>);

impl Recover for State {
    fn recover_from_panic(&mut self) {
        self.0.lock().unwrap().recovered += 1;
    }
}

const LONG: Duration = Duration::from_secs(5);

fn lane(waiting: usize) -> (Lane<State>, Arc<Mutex<Log>>) {
    let log = Arc::new(Mutex::new(Log::default()));
    let state = log.clone();
    let lane = Lane::spawn("openblade-test-lane", waiting, move || State(state)).unwrap();
    (lane, log)
}

fn ran(log: &Arc<Mutex<Log>>) -> Vec<&'static str> {
    log.lock().unwrap().ran.clone()
}

/// A call that records `name`, then waits for `release` when given.
fn step(
    name: &'static str,
    release: Option<mpsc::Receiver<()>>,
) -> impl FnOnce(&mut State) + Send + 'static {
    move |state| {
        state.0.lock().unwrap().ran.push(name);
        if let Some(release) = release {
            let _ = release.recv();
        }
    }
}

/// Waits on the real clock, without holding the runtime, until `count`
/// calls have started.
async fn started(log: &Arc<Mutex<Log>>, count: usize) {
    let deadline = std::time::Instant::now() + LONG;
    while log.lock().unwrap().ran.len() < count {
        assert!(std::time::Instant::now() < deadline, "the lane starts it");
        tokio::time::sleep(Duration::from_millis(1)).await;
    }
}

/// Holds the lane on one call until the returned sender sends or drops.
async fn busy(lane: &Arc<Lane<State>>, log: &Arc<Mutex<Log>>) -> mpsc::Sender<()> {
    let (release, hold) = mpsc::channel();
    let held = lane.clone();
    tokio::spawn(async move { held.call(LONG, step("busy", Some(hold))).await });
    started(log, 1).await;
    release
}

#[tokio::test]
async fn a_withdrawn_call_never_starts_and_a_staying_one_runs_after_the_busy_one() {
    let (lane, log) = lane(2);
    let lane = Arc::new(lane);
    let release = busy(&lane, &log).await;
    // The lane cannot start either before `release`, so both outcomes hold
    // whatever the budget's timing.
    let withdrawn = lane.call(Duration::from_millis(10), step("withdrawn", None));
    assert_eq!(withdrawn.await, Called::NotStarted);
    let claim = Claim::default();
    let staying = lane.call_claimed(
        &claim,
        Waiting::Stays,
        Duration::from_millis(10),
        step("stays", None),
    );
    assert_eq!(staying.await, Called::Abandoned);
    assert!(!claim.started());
    release.send(()).unwrap();
    started(&log, 2).await;
    assert_eq!(ran(&log), ["busy", "stays"]);
    assert!(claim.started());
}

#[tokio::test]
async fn a_dropped_caller_withdraws_only_a_withdrawing_call() {
    let (lane, log) = lane(2);
    let lane = Arc::new(lane);
    let release = busy(&lane, &log).await;
    let withdrawing = Claim::default();
    let staying = Claim::default();
    tokio::select! {
        _ = async {
            tokio::join!(
                lane.call_claimed(&withdrawing, Waiting::Withdraws, LONG, step("dropped", None)),
                lane.call_claimed(&staying, Waiting::Stays, LONG, step("kept", None)),
            )
        } => panic!("both wait behind the busy call"),
        () = tokio::time::sleep(Duration::from_millis(20)) => {}
    }
    // The dropped caller's claim tells it the call never started.
    assert!(withdrawing.withdraw());
    release.send(()).unwrap();
    started(&log, 2).await;
    assert!(staying.started());
    assert!(!withdrawing.started());
    assert_eq!(ran(&log), ["busy", "kept"]);
}

#[tokio::test]
async fn a_started_call_past_its_budget_is_abandoned_and_finishes_on_the_lane() {
    let (lane, log) = lane(1);
    let (release, hold) = mpsc::channel();
    let claim = Claim::default();
    let call = lane.call_claimed(
        &claim,
        Waiting::Withdraws,
        Duration::from_secs(1),
        step("slow", Some(hold)),
    );
    let (called, ()) = tokio::join!(call, async {
        started(&log, 1).await;
    });
    // Started before its budget ended, so it could not be withdrawn.
    assert_eq!(called, Called::Abandoned);
    assert!(claim.started());
    assert!(!claim.withdraw());
    release.send(()).unwrap();
    assert_eq!(lane.call(LONG, |_| 7).await, Called::Done(7));
}

#[tokio::test]
async fn a_call_beyond_the_queue_is_refused_at_once() {
    let (lane, log) = lane(1);
    let lane = Arc::new(lane);
    let release = busy(&lane, &log).await;
    let queued = tokio::spawn({
        let lane = lane.clone();
        async move { lane.call(LONG, step("queued", None)).await }
    });
    tokio::time::sleep(Duration::from_millis(20)).await;
    let refused = std::time::Instant::now();
    assert_eq!(
        lane.call(LONG, step("refused", None)).await,
        Called::NotStarted
    );
    assert!(refused.elapsed() < Duration::from_secs(1));
    release.send(()).unwrap();
    assert_eq!(queued.await.unwrap(), Called::Done(()));
    assert_eq!(ran(&log), ["busy", "queued"]);
}

#[tokio::test]
async fn a_panicking_call_is_abandoned_and_the_state_recovered() {
    let (lane, log) = lane(1);
    let called = lane
        .call(LONG, |_: &mut State| -> u8 {
            panic!("scripted lane panic")
        })
        .await;
    assert_eq!(called, Called::Abandoned);
    assert_eq!(lane.call(LONG, |_| 1).await, Called::Done(1));
    assert_eq!(log.lock().unwrap().recovered, 1);
}
