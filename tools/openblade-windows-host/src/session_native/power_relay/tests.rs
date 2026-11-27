use std::{
    cell::RefCell,
    future::pending,
    rc::Rc,
    sync::{
        Arc,
        atomic::{AtomicI64, Ordering::SeqCst},
    },
    thread,
};

use super::*;

const STARTED: i64 = 100;
const FREQUENCY: i64 = 1000;

/// A relay whose QPC reads `clock`, counting milliseconds from `STARTED`.
pub(super) fn relay(clock: &Arc<AtomicI64>) -> Relay {
    let clock = Arc::clone(clock);
    Relay::new(STARTED, FREQUENCY, Box::new(move || clock.load(SeqCst))).unwrap()
}

fn clock() -> Arc<AtomicI64> {
    Arc::new(AtomicI64::new(STARTED))
}

fn transition(sequence: i64, suspending: bool) -> Transition {
    Transition {
        sequence,
        timestamp: STARTED,
        suspending,
    }
}

#[test]
fn only_suspend_and_resume_count_and_a_repeated_direction_is_ignored() {
    let relay = relay(&clock());
    for kind in [0, 1, 2, 3, 5, 6, 8, 9, 10, 0x8013] {
        assert_eq!(relay.notify(kind), None);
    }
    assert_eq!(relay.notify(SUSPEND), Some(transition(1, true)));
    assert_eq!(relay.notify(SUSPEND), None);
    assert_eq!(relay.notify(RESUME_AUTOMATIC), Some(transition(2, false)));
    assert_eq!(relay.notify(RESUME_SUSPEND), None);
}

#[test]
fn a_newer_transition_cancels_the_active_report_and_replaces_the_pending_one() {
    let relay = relay(&clock());
    let first = relay.notify(SUSPEND).unwrap();
    assert_eq!(relay.next(), Next::Send(first, BUDGET));
    let second = relay.notify(RESUME_AUTOMATIC).unwrap();
    assert!(relay.canceled(first));
    let third = relay.notify(SUSPEND).unwrap();
    // The replaced transition completes unsent; the active one has not ended.
    assert!(relay.lock().complete(second.sequence));
    assert!(!relay.lock().complete(first.sequence));
    relay.finish(first);
    assert!(relay.lock().complete(first.sequence));
    assert_eq!(relay.next(), Next::Send(third, BUDGET));
    relay.finish(third);
    assert_eq!(relay.next(), Next::Idle);
}

#[test]
fn the_budget_runs_from_the_transition_timestamp_on_the_qpc_clock() {
    let clock = clock();
    let relay = relay(&clock);
    let first = relay.notify(SUSPEND).unwrap();
    clock.store(STARTED + 500, SeqCst);
    assert_eq!(relay.remaining(first), Duration::from_millis(1000));
    clock.store(STARTED + 1500, SeqCst);
    assert_eq!(relay.remaining(first), Duration::ZERO);
    clock.store(STARTED + 1_000_000, SeqCst);
    assert_eq!(relay.remaining(first), Duration::ZERO);
    // A clock behind the timestamp, or a timestamp before the instance
    // started, leaves no budget.
    clock.store(STARTED - 1, SeqCst);
    assert_eq!(relay.remaining(first), Duration::ZERO);
    let early = Transition {
        timestamp: STARTED - 1,
        ..first
    };
    clock.store(STARTED, SeqCst);
    assert_eq!(relay.remaining(early), Duration::ZERO);
}

#[test]
fn a_relay_needs_a_positive_start_and_frequency() {
    for (started, frequency) in [(0, FREQUENCY), (-1, FREQUENCY), (STARTED, 0), (STARTED, -1)] {
        assert!(Relay::new(started, frequency, Box::new(|| STARTED)).is_err());
    }
}

#[test]
fn a_suspend_without_budget_expires_its_report_and_a_pending_one_is_dropped() {
    // Each QPC read is a full budget after the one before, so every callback
    // finds its budget spent the moment it would wait.
    let ticks = Arc::new(AtomicI64::new(STARTED));
    let relay = {
        let ticks = Arc::clone(&ticks);
        Relay::new(
            STARTED,
            FREQUENCY,
            Box::new(move || ticks.fetch_add(1500, SeqCst)),
        )
        .unwrap()
    };
    // The active report is canceled before it starts.
    assert_eq!(relay.on_notification(SUSPEND), None);
    let Next::Skip(first) = relay.next() else {
        panic!("an expired report must not be sent");
    };
    assert_eq!((first.sequence, first.suspending), (1, true));
    relay.finish(first);
    // A suspend pending behind a resume is dropped unsent, and the resume
    // report is canceled by it.
    assert_eq!(relay.on_notification(RESUME_AUTOMATIC), None);
    let Next::Skip(resume) = relay.next() else {
        panic!("an expired report must not be sent");
    };
    let expired = relay.on_notification(SUSPEND).unwrap();
    assert_eq!((expired.sequence, expired.suspending), (3, true));
    assert!(relay.lock().pending.is_none());
    assert!(relay.canceled(resume));
}

#[test]
fn a_resume_callback_returns_without_waiting_for_its_report() {
    let relay = relay(&clock());
    assert_eq!(relay.on_notification(RESUME_AUTOMATIC), None);
    // Its report has not even started.
    assert_eq!(relay.next(), Next::Send(transition(1, false), BUDGET));
}

#[test]
fn a_waiting_suspend_callback_returns_when_its_report_ends() {
    let relay = Arc::new(relay(&clock()));
    let suspend = relay.notify(SUSPEND).unwrap();
    let waiter = {
        let relay = Arc::clone(&relay);
        // A generous bound: the wait ends on completion, not on time.
        thread::spawn(move || relay.wait(suspend, Duration::from_secs(3600)))
    };
    assert_eq!(relay.next(), Next::Send(suspend, BUDGET));
    relay.finish(suspend);
    assert!(waiter.join().unwrap());
    // Dispose releases a waiting callback too.
    let resume = relay.notify(RESUME_AUTOMATIC).unwrap();
    let waiter = {
        let relay = Arc::clone(&relay);
        thread::spawn(move || relay.wait(resume, Duration::from_secs(3600)))
    };
    relay.dispose();
    assert!(waiter.join().unwrap());
    assert_eq!(relay.notify(SUSPEND), None);
    assert_eq!(relay.next(), Next::Disposed);
}

/// The transport against a scripted sink. `calls` records each write: a send
/// whose gate admitted it.
struct Rig {
    relay: Relay,
    clock: Arc<AtomicI64>,
    calls: Rc<RefCell<Vec<Transition>>>,
    notices: Rc<RefCell<Vec<Notice>>>,
}

impl Rig {
    fn new() -> Self {
        let clock = clock();
        Self {
            relay: relay(&clock),
            clock,
            calls: Rc::default(),
            notices: Rc::default(),
        }
    }

    async fn serve(
        &self,
        reply: impl AsyncFn(Transition) -> io::Result<Option<u8>>,
        stop: &mut watch::Receiver<bool>,
        verify: impl Fn() -> io::Result<()>,
    ) -> io::Result<()> {
        serve(
            &self.relay,
            async |gate: Gate<'_>| {
                gate.admit()?;
                self.calls.borrow_mut().push(gate.transition);
                reply(gate.transition).await
            },
            stop,
            verify,
            |notice| self.notices.borrow_mut().push(notice),
        )
        .await
    }
}

fn answered(sequence: i64, suspending: bool, status: Option<u8>) -> Notice {
    Notice::Answered {
        sequence,
        suspending,
        status,
    }
}

fn abandoned(sequence: i64, suspending: bool) -> Notice {
    Notice::Abandoned {
        sequence,
        suspending,
    }
}

/// Let the transport run until it waits.
async fn settle() {
    for _ in 0..16 {
        tokio::task::yield_now().await;
    }
}

#[tokio::test(start_paused = true)]
async fn each_transition_is_reported_in_order_and_the_pending_one_follows() {
    let rig = Rig::new();
    let (stop_tx, mut stop) = watch::channel(false);
    let driver = async {
        rig.relay.notify(SUSPEND).unwrap();
        settle().await;
        rig.relay.notify(RESUME_AUTOMATIC).unwrap();
        settle().await;
        stop_tx.send(true).unwrap();
    };
    let (result, ()) = tokio::join!(
        rig.serve(
            async |transition| Ok(Some(transition.sequence as u8)),
            &mut stop,
            || Ok(())
        ),
        driver
    );
    result.unwrap();
    assert_eq!(
        *rig.calls.borrow(),
        [transition(1, true), transition(2, false)]
    );
    assert_eq!(
        *rig.notices.borrow(),
        [answered(1, true, Some(1)), answered(2, false, Some(2))]
    );
    // Stop disposed the relay.
    assert_eq!(rig.relay.notify(SUSPEND), None);
}

#[tokio::test(start_paused = true)]
async fn a_newer_transition_abandons_the_report_in_flight_and_only_the_latest_follows() {
    let rig = Rig::new();
    let (stop_tx, mut stop) = watch::channel(false);
    let driver = async {
        rig.relay.notify(SUSPEND).unwrap();
        settle().await;
        // The first report hangs; two more transitions arrive behind it.
        rig.relay.notify(RESUME_AUTOMATIC).unwrap();
        rig.relay.notify(SUSPEND).unwrap();
        settle().await;
        stop_tx.send(true).unwrap();
    };
    let (result, ()) = tokio::join!(
        rig.serve(
            async |transition| {
                if transition.sequence == 1 {
                    pending::<()>().await;
                }
                Ok(Some(1))
            },
            &mut stop,
            || Ok(())
        ),
        driver
    );
    result.unwrap();
    assert_eq!(
        *rig.calls.borrow(),
        [transition(1, true), transition(3, true)]
    );
    assert_eq!(
        *rig.notices.borrow(),
        [abandoned(1, true), answered(3, true, Some(1))]
    );
}

#[tokio::test(start_paused = true)]
async fn a_report_in_flight_is_abandoned_at_its_budget_before_any_stop() {
    let rig = Rig::new();
    let (stop_tx, mut stop) = watch::channel(false);
    let driver = async {
        rig.relay.notify(RESUME_AUTOMATIC).unwrap();
        settle().await;
        // The send hangs; nothing ends it until the budget does.
        tokio::time::sleep(BUDGET - Duration::from_millis(1)).await;
        settle().await;
        assert!(rig.notices.borrow().is_empty());
        tokio::time::sleep(Duration::from_millis(1)).await;
        settle().await;
        assert_eq!(*rig.notices.borrow(), [abandoned(1, false)]);
        stop_tx.send(true).unwrap();
    };
    let (result, ()) = tokio::join!(
        rig.serve(
            async |_| {
                pending::<()>().await;
                Ok(None)
            },
            &mut stop,
            || Ok(())
        ),
        driver
    );
    result.unwrap();
    assert_eq!(rig.calls.borrow().len(), 1);
    assert_eq!(*rig.notices.borrow(), [abandoned(1, false)]);
}

#[tokio::test(start_paused = true)]
async fn an_expired_suspend_cancels_its_report_in_flight_at_once() {
    let rig = Rig::new();
    let (stop_tx, mut stop) = watch::channel(false);
    let driver = async {
        let suspend = rig.relay.notify(SUSPEND).unwrap();
        settle().await;
        let started = tokio::time::Instant::now();
        // The suspend callback's own wait ran out, so it expires the report
        // that is still in flight.
        assert!(!rig.relay.expire(suspend));
        settle().await;
        assert_eq!(*rig.notices.borrow(), [abandoned(1, true)]);
        assert!(started.elapsed() < BUDGET);
        stop_tx.send(true).unwrap();
    };
    let (result, ()) = tokio::join!(
        rig.serve(
            async |_| {
                pending::<()>().await;
                Ok(None)
            },
            &mut stop,
            || Ok(())
        ),
        driver
    );
    result.unwrap();
    assert_eq!(*rig.notices.borrow(), [abandoned(1, true)]);
}

#[tokio::test(start_paused = true)]
async fn a_report_whose_budget_runs_out_during_verification_is_never_written() {
    let rig = Rig::new();
    let (stop_tx, mut stop) = watch::channel(false);
    let driver = async {
        rig.relay.notify(RESUME_AUTOMATIC).unwrap();
        settle().await;
        // Abandoned before any stop, without a write.
        assert_eq!(*rig.notices.borrow(), [abandoned(1, false)]);
        stop_tx.send(true).unwrap();
    };
    let (result, ()) = tokio::join!(
        rig.serve(
            async |_| Ok(Some(1)),
            &mut stop,
            || {
                // Verifying the installed origin outlasts the whole budget.
                rig.clock.store(STARTED + 1500, SeqCst);
                Ok(())
            }
        ),
        driver
    );
    result.unwrap();
    assert!(rig.calls.borrow().is_empty());
    assert_eq!(*rig.notices.borrow(), [abandoned(1, false)]);
}

#[tokio::test(start_paused = true)]
async fn a_report_superseded_while_the_channel_admits_is_never_written() {
    let rig = Rig::new();
    let (stop_tx, mut stop) = watch::channel(false);
    rig.relay.notify(RESUME_AUTOMATIC).unwrap();
    let driver = async {
        settle().await;
        stop_tx.send(true).unwrap();
    };
    let (result, ()) = tokio::join!(
        serve(
            &rig.relay,
            async |gate: Gate<'_>| {
                // A suspend arrives while the channel verifies the Service,
                // before the write; the repeat for its own report is ignored.
                rig.relay.notify(SUSPEND);
                gate.admit()?;
                rig.calls.borrow_mut().push(gate.transition);
                Ok(Some(1))
            },
            &mut stop,
            || Ok(()),
            |notice| rig.notices.borrow_mut().push(notice),
        ),
        driver
    );
    result.unwrap();
    assert_eq!(*rig.calls.borrow(), [transition(2, true)]);
    assert_eq!(
        *rig.notices.borrow(),
        [abandoned(1, false), answered(2, true, Some(1))]
    );
}

#[tokio::test(start_paused = true)]
async fn a_transition_out_of_budget_is_never_sent() {
    let rig = Rig::new();
    let (stop_tx, mut stop) = watch::channel(false);
    rig.relay.notify(SUSPEND).unwrap();
    rig.clock.store(STARTED + 1500, SeqCst);
    let driver = async {
        settle().await;
        stop_tx.send(true).unwrap();
    };
    let (result, ()) = tokio::join!(
        rig.serve(async |_| Ok(Some(1)), &mut stop, || Ok(())),
        driver
    );
    result.unwrap();
    assert!(rig.calls.borrow().is_empty());
    assert_eq!(*rig.notices.borrow(), [abandoned(1, true)]);
}

#[tokio::test(start_paused = true)]
async fn stop_drops_the_report_in_flight_and_disposes_the_relay() {
    let rig = Rig::new();
    let (stop_tx, mut stop) = watch::channel(false);
    let driver = async {
        rig.relay.notify(SUSPEND).unwrap();
        settle().await;
        stop_tx.send(true).unwrap();
    };
    let (result, ()) = tokio::join!(
        rig.serve(
            async |_| {
                pending::<()>().await;
                Ok(None)
            },
            &mut stop,
            || Ok(())
        ),
        driver
    );
    result.unwrap();
    assert_eq!(*rig.notices.borrow(), [abandoned(1, true)]);
    assert!(rig.relay.lock().complete(1));
    assert_eq!(rig.relay.notify(RESUME_AUTOMATIC), None);
}

#[tokio::test(start_paused = true)]
async fn a_failed_exchange_is_reported_and_not_replayed() {
    let rig = Rig::new();
    let (stop_tx, mut stop) = watch::channel(false);
    let driver = async {
        rig.relay.notify(SUSPEND).unwrap();
        settle().await;
        stop_tx.send(true).unwrap();
    };
    let (result, ()) = tokio::join!(
        rig.serve(
            async |_| Err(io::ErrorKind::BrokenPipe.into()),
            &mut stop,
            || Ok(())
        ),
        driver
    );
    result.unwrap();
    assert_eq!(rig.calls.borrow().len(), 1);
    assert_eq!(
        *rig.notices.borrow(),
        [Notice::Failed {
            sequence: 1,
            suspending: true,
            kind: io::ErrorKind::BrokenPipe,
        }]
    );
}

#[tokio::test(start_paused = true)]
async fn an_origin_failure_ends_the_role_before_any_send() {
    let rig = Rig::new();
    let (_stop_tx, mut stop) = watch::channel(false);
    rig.relay.notify(SUSPEND).unwrap();
    // Bounded, so a role that kept serving fails here instead of hanging.
    let error = tokio::time::timeout(
        Duration::from_secs(60),
        rig.serve(
            async |_| Ok(Some(1)),
            &mut stop,
            || Err(io::ErrorKind::PermissionDenied.into()),
        ),
    )
    .await
    .expect("the origin failure must end the role")
    .unwrap_err();
    assert_eq!(error.kind(), io::ErrorKind::PermissionDenied);
    assert!(rig.calls.borrow().is_empty());
    assert_eq!(rig.relay.next(), Next::Disposed);
}

#[test]
fn the_request_refuses_what_the_managed_codec_refuses() {
    let request = wire::Request {
        request_id: [0x11; 16],
        instance: [0x22; 16],
        started: STARTED,
        transition: transition(1, true),
    };
    assert!(request.encode().is_ok());
    for refused in [
        wire::Request {
            request_id: [0; 16],
            ..request
        },
        wire::Request {
            instance: [0; 16],
            ..request
        },
        wire::Request {
            started: 0,
            ..request
        },
        wire::Request {
            transition: transition(0, true),
            ..request
        },
        wire::Request {
            transition: Transition {
                timestamp: STARTED - 1,
                ..request.transition
            },
            ..request
        },
    ] {
        assert_eq!(
            refused.encode().unwrap_err().kind(),
            io::ErrorKind::InvalidData
        );
    }
}

fn reply(edit: impl FnOnce(&mut serde_json::Value)) -> Vec<u8> {
    let mut frame = serde_json::json!({
        "frame": "response",
        "version": 91,
        "requestId": REQUEST_ID,
        "success": true,
        "error": null,
        "status": null,
        "result": {"status": 1, "message": "Applied"},
        "archive": null,
        "errorCode": null,
    });
    edit(&mut frame);
    serde_json::to_vec(&frame).unwrap()
}

const REQUEST_ID: &str = "11111111-1111-1111-1111-111111111111";

#[test]
fn a_reply_answers_with_its_status_or_a_refusal_and_nothing_else() {
    for status in 0..=3u8 {
        let reply = reply(|frame| frame["result"]["status"] = status.into());
        assert_eq!(
            wire::decode_reply(&reply, REQUEST_ID).unwrap(),
            Some(status)
        );
    }
    let refused = reply(|frame| {
        frame["success"] = false.into();
        frame["result"] = serde_json::Value::Null;
        frame["error"] = "Refused.".into();
    });
    assert_eq!(wire::decode_reply(&refused, REQUEST_ID).unwrap(), None);
    for invalid in [
        reply(|frame| frame["result"]["status"] = 4.into()),
        reply(|frame| frame["requestId"] = "22222222-2222-2222-2222-222222222222".into()),
        reply(|frame| frame["status"] = serde_json::json!({})),
        reply(|frame| frame["errorCode"] = 1.into()),
        reply(|frame| {
            frame["success"] = false.into();
            frame["result"] = serde_json::Value::Null;
            frame["error"] = " ".into();
        }),
        reply(|frame| frame["unexpected"] = true.into()),
    ] {
        assert_eq!(
            wire::decode_reply(&invalid, REQUEST_ID).unwrap_err().kind(),
            io::ErrorKind::InvalidData
        );
    }
}
