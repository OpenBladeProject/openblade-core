use crate::{
    io::{
        CancelResult, Completion, Direction, FeatureIo, Frame, Limit, Pending, Stop, Transfer,
        finish_pending,
    },
    session::{AdapterPowerLevels, Failure, ReplyFailure, Session},
};
use openblade_types::ProtocolValidationFailure;
use serde_json::Value;
use std::{
    collections::VecDeque,
    sync::{
        Arc,
        atomic::{AtomicBool, AtomicUsize, Ordering},
        mpsc::{self, Receiver, SyncSender},
    },
    thread,
    time::{Duration, Instant},
};

const CAPTURE: &str = include_str!(
    "../../../../evidence/legacy-captures/decoded/rz09-0581-pid-02e0-bios-3.01-adapter-power-readback-fixture.json"
);

struct Step {
    direction: Direction,
    frame: Frame,
    reply: Option<Frame>,
    result: Transfer,
}

struct Replay {
    steps: VecDeque<Step>,
    calls: Arc<AtomicUsize>,
    retired: Arc<AtomicBool>,
}

impl FeatureIo for Replay {
    fn transfer(&mut self, direction: Direction, frame: &mut Frame, limit: Limit<'_>) -> Transfer {
        if let Some(reason) = limit.stop() {
            return Transfer::BeforeSubmission(reason);
        }
        let step = self.steps.pop_front().expect("unexpected I/O or replay");
        self.calls.fetch_add(1, Ordering::Relaxed);
        assert_eq!(step.direction, direction);
        assert_eq!(&step.frame, frame);
        if let Some(reply) = step.reply {
            *frame = reply;
        }
        step.result
    }
}

impl Drop for Replay {
    fn drop(&mut self) {
        self.retired.store(true, Ordering::Release);
    }
}

fn replay(
    steps: impl IntoIterator<Item = Step>,
) -> (Session<Replay>, Arc<AtomicUsize>, Arc<AtomicBool>) {
    let calls = Arc::new(AtomicUsize::new(0));
    let retired = Arc::new(AtomicBool::new(false));
    let session = Session::new(Replay {
        steps: steps.into_iter().collect(),
        calls: calls.clone(),
        retired: retired.clone(),
    });
    (session, calls, retired)
}

// The capture freezes the typed command and observed payloads. Windows
// framing and transferred-byte cases come from the managed transport tests.
fn frame(status: u8, transaction: u8, payload: [u8; 2]) -> Frame {
    let mut frame = [0; crate::io::FRAME_LENGTH];
    frame[1..11].copy_from_slice(&[
        status,
        transaction,
        0,
        0,
        0,
        2,
        0x07,
        0x8C,
        payload[0],
        payload[1],
    ]);
    frame[89] = 2 ^ 0x07 ^ 0x8C ^ payload[0] ^ payload[1];
    frame
}

fn set(transaction: u8, result: Transfer) -> Step {
    Step {
        direction: Direction::Set,
        frame: frame(0, transaction, [0, 0]),
        reply: None,
        result,
    }
}

fn get(reply: Frame, result: Transfer) -> Step {
    Step {
        direction: Direction::Get,
        frame: [0; crate::io::FRAME_LENGTH],
        reply: Some(reply),
        result,
    }
}

fn limit(canceled: &AtomicBool) -> Limit<'_> {
    Limit {
        canceled,
        deadline: Instant::now() + Duration::from_secs(10),
    }
}

#[test]
fn replays_the_observed_adapter_query_with_both_managed_transfer_counts() {
    let capture: Value = serde_json::from_str(CAPTURE).unwrap();
    assert_eq!(capture["device"]["model"], "RZ09-0581");
    assert_eq!(capture["device"]["productId"], "02E0");
    assert_eq!(capture["query"]["commandClass"], "07");
    assert_eq!(capture["query"]["commandId"], "8C");
    assert_eq!(capture["query"]["requestPayloadHex"], "0000");
    assert_eq!(capture["query"]["expectedResponsePayloadLength"], 2);

    let steps = capture["mappings"]
        .as_array()
        .unwrap()
        .iter()
        .enumerate()
        .flat_map(|(index, row)| {
            let hex = row["responsePayloadHex"].as_str().unwrap();
            let payload = [
                u8::from_str_radix(&hex[..2], 16).unwrap(),
                u8::from_str_radix(&hex[2..], 16).unwrap(),
            ];
            [
                set((index + 1) as u8, Transfer::Completed(Ok(0))),
                get(
                    frame(2, (index + 1) as u8, payload),
                    Transfer::Completed(Ok([90, 91][index])),
                ),
            ]
        });
    let (session, calls, retired) = replay(steps);
    let canceled = AtomicBool::new(false);
    assert_eq!(
        session.query_adapter_power(limit(&canceled)),
        Ok(AdapterPowerLevels {
            actual: 0x07,
            recommended: 0x11
        })
    );
    assert_eq!(
        session.query_adapter_power(limit(&canceled)),
        Ok(AdapterPowerLevels {
            actual: 0x11,
            recommended: 0x11
        })
    );
    assert_eq!(calls.load(Ordering::Relaxed), 4);
    assert!(!retired.load(Ordering::Acquire));
}

#[test]
fn synchronous_set_rejection_has_its_own_provenance_and_retires_the_transport() {
    let (session, calls, retired) = replay([set(1, Transfer::Rejected(5))]);
    let canceled = AtomicBool::new(false);
    assert_eq!(
        session.query_adapter_power(limit(&canceled)),
        Err(Failure::SynchronousSetRejection(5))
    );
    assert_eq!(
        session.query_adapter_power(limit(&canceled)),
        Err(Failure::ReconciliationRequired)
    );
    assert_eq!(calls.load(Ordering::Relaxed), 1);
    assert!(retired.load(Ordering::Acquire));
}

#[test]
fn cancellation_before_native_set_does_not_poison_the_handle() {
    let (session, calls, retired) = replay([
        set(1, Transfer::BeforeSubmission(Stop::Caller)),
        set(2, Transfer::Completed(Ok(0))),
        get(frame(2, 2, [7, 17]), Transfer::Completed(Ok(91))),
    ]);
    let canceled = AtomicBool::new(false);
    assert_eq!(
        session.query_adapter_power(limit(&canceled)),
        Err(Failure::BeforeSubmission(Transfer::BeforeSubmission(
            Stop::Caller
        )))
    );
    assert!(session.query_adapter_power(limit(&canceled)).is_ok());
    assert_eq!(calls.load(Ordering::Relaxed), 3);
    assert!(!retired.load(Ordering::Acquire));
}

#[test]
fn malformed_get_never_replays_and_keeps_the_validation_stage() {
    let mut wrong_id = frame(2, 1, [7, 17]);
    wrong_id[0] = 1;
    let mut bad_checksum = frame(2, 1, [7, 17]);
    bad_checksum[89] ^= 1;
    let cases = [
        (frame(2, 1, [7, 17]), 89, ReplyFailure::Count),
        (wrong_id, 91, ReplyFailure::ReportId),
        (wrong_id, 90, ReplyFailure::Count),
        (
            bad_checksum,
            91,
            ReplyFailure::Protocol(ProtocolValidationFailure::Checksum),
        ),
        (
            frame(2, 2, [7, 17]),
            91,
            ReplyFailure::Protocol(ProtocolValidationFailure::TransactionMismatch),
        ),
    ];
    let canceled = AtomicBool::new(false);
    for (reply, count, failure) in cases {
        let (session, calls, retired) = replay([
            set(1, Transfer::Completed(Ok(0))),
            get(reply, Transfer::Completed(Ok(count))),
        ]);
        assert_eq!(
            session.query_adapter_power(limit(&canceled)),
            Err(Failure::UncertainProtocol { count, failure })
        );
        assert_eq!(
            session.query_adapter_power(limit(&canceled)),
            Err(Failure::ReconciliationRequired)
        );
        assert_eq!(calls.load(Ordering::Relaxed), 2);
        assert!(retired.load(Ordering::Acquire));
    }
}

#[test]
fn failed_get_is_uncertain_even_if_get_itself_was_rejected_before_submission() {
    let (session, calls, retired) = replay([
        set(1, Transfer::Completed(Ok(0))),
        get([0; crate::io::FRAME_LENGTH], Transfer::Rejected(5)),
    ]);
    let canceled = AtomicBool::new(false);
    assert_eq!(
        session.query_adapter_power(limit(&canceled)),
        Err(Failure::UncertainIo(Direction::Get, Transfer::Rejected(5)))
    );
    assert_eq!(calls.load(Ordering::Relaxed), 2);
    assert!(retired.load(Ordering::Acquire));
}

#[test]
fn bounded_transaction_allocator_never_wraps_to_zero_or_reuses_an_id() {
    let (session, calls, _) = replay((1..=255).flat_map(|transaction| {
        [
            set(transaction, Transfer::Completed(Ok(0))),
            get(frame(2, transaction, [7, 17]), Transfer::Completed(Ok(91))),
        ]
    }));
    let canceled = AtomicBool::new(false);
    for _ in 1..=255 {
        assert!(session.query_adapter_power(limit(&canceled)).is_ok());
    }
    assert_eq!(
        session.query_adapter_power(limit(&canceled)),
        Err(Failure::Exhausted)
    );
    assert_eq!(calls.load(Ordering::Relaxed), 510);
}

struct PendingSet<'a> {
    entered: SyncSender<()>,
    cancel_called: SyncSender<()>,
    completion: Receiver<Completion>,
    request_result: CancelResult,
    retired: &'a AtomicBool,
}

impl FeatureIo for PendingSet<'_> {
    fn transfer(&mut self, direction: Direction, frame: &mut Frame, limit: Limit<'_>) -> Transfer {
        assert_eq!(
            direction,
            Direction::Set,
            "GET must not run after canceled SET"
        );
        assert_eq!(*frame, self::frame(0, 1, [0, 0]));
        self.entered.send(()).unwrap();
        finish_pending(self, limit)
    }
}

impl Pending for PendingSet<'_> {
    fn poll(&mut self) -> Option<Completion> {
        self.completion.try_recv().ok()
    }

    fn cancel(&mut self) -> CancelResult {
        self.cancel_called.send(()).unwrap();
        self.request_result
    }
}

impl Drop for PendingSet<'_> {
    fn drop(&mut self) {
        self.retired.store(true, Ordering::Release);
    }
}

#[test]
fn cancellation_does_not_release_the_gate_or_handle_before_native_completion() {
    // ERROR_NOT_FOUND can race with a successful completion. Other cancel
    // errors also leave the request pending until the kernel reports completion.
    for (request_result, completion_result) in
        [(Ok(()), Err(995)), (Err(1168), Ok(0)), (Err(5), Err(31))]
    {
        let (entered, started) = mpsc::sync_channel(1);
        let (cancel_called, cancel_observed) = mpsc::sync_channel(1);
        let (complete, completion) = mpsc::sync_channel(1);
        let retired = AtomicBool::new(false);
        let canceled = AtomicBool::new(false);
        let queued_cancel = AtomicBool::new(false);
        let another = AtomicBool::new(false);
        let session = Session::new(PendingSet {
            entered,
            cancel_called,
            completion,
            request_result,
            retired: &retired,
        });
        thread::scope(|scope| {
            let _drain_on_unwind = DrainOnDrop(Some(complete.clone()));
            let active = scope.spawn(|| session.query_adapter_power(limit(&canceled)));
            started.recv_timeout(Duration::from_secs(2)).unwrap();
            let queued = scope.spawn(|| session.query_adapter_power(limit(&queued_cancel)));
            let until = Instant::now() + Duration::from_secs(2);
            while !session.waiter_present() {
                assert!(Instant::now() < until);
                thread::yield_now();
            }
            assert_eq!(
                session.query_adapter_power(limit(&another)),
                Err(Failure::QueueFull)
            );
            queued_cancel.store(true, Ordering::Release);
            assert_eq!(queued.join().unwrap(), Err(Failure::Queued(Stop::Caller)));
            canceled.store(true, Ordering::Release);
            cancel_observed
                .recv_timeout(Duration::from_secs(2))
                .unwrap();
            assert!(!active.is_finished());
            assert!(!retired.load(Ordering::Acquire));
            let expired_wait = Limit {
                canceled: &another,
                deadline: Instant::now() + Duration::from_millis(20),
            };
            assert_eq!(
                session.query_adapter_power(expired_wait),
                Err(Failure::Queued(Stop::Deadline))
            );
            complete.send(completion_result).unwrap();
            assert_eq!(
                active.join().unwrap(),
                Err(Failure::UncertainIo(
                    Direction::Set,
                    Transfer::Canceled {
                        reason: Stop::Caller,
                        request: request_result,
                        completion: completion_result,
                    }
                ))
            );
            assert!(retired.load(Ordering::Acquire));
            assert_eq!(
                session.query_adapter_power(limit(&another)),
                Err(Failure::ReconciliationRequired)
            );
        });
    }
}

struct DrainOnDrop(Option<SyncSender<Completion>>);

impl Drop for DrainOnDrop {
    fn drop(&mut self) {
        if let Some(sender) = self.0.take() {
            let _ = sender.try_send(Err(995));
        }
    }
}

#[test]
fn pending_completion_without_cancellation_is_published() {
    struct FinishesOnSecondPoll(bool);

    impl Pending for FinishesOnSecondPoll {
        fn poll(&mut self) -> Option<Completion> {
            let was_polled = self.0;
            self.0 = true;
            was_polled.then_some(Ok(91))
        }

        fn cancel(&mut self) -> CancelResult {
            panic!("unexpected cancel")
        }
    }

    let canceled = AtomicBool::new(false);
    assert_eq!(
        finish_pending(&mut FinishesOnSecondPoll(false), limit(&canceled)),
        Transfer::Completed(Ok(91))
    );
}

#[test]
fn completed_set_followed_by_cancellation_keeps_the_count_and_never_issues_get() {
    struct CancelsOnSet<'a>(&'a AtomicBool);

    impl FeatureIo for CancelsOnSet<'_> {
        fn transfer(&mut self, direction: Direction, _: &mut Frame, _: Limit<'_>) -> Transfer {
            assert_eq!(direction, Direction::Set);
            self.0.store(true, Ordering::Release);
            Transfer::Completed(Ok(0))
        }
    }

    let canceled = AtomicBool::new(false);
    let session = Session::new(CancelsOnSet(&canceled));
    assert_eq!(
        session.query_adapter_power(limit(&canceled)),
        Err(Failure::UncertainIo(
            Direction::Set,
            Transfer::CompletedAfterStop {
                reason: Stop::Caller,
                completion: Ok(0)
            },
        )),
    );
    canceled.store(false, Ordering::Release);
    assert_eq!(
        session.query_adapter_power(limit(&canceled)),
        Err(Failure::ReconciliationRequired)
    );
}

#[test]
fn native_preparation_failure_can_be_distinguished_from_submission() {
    let (session, calls, retired) = replay([set(1, Transfer::PreparationFailed(8))]);
    let canceled = AtomicBool::new(false);
    assert_eq!(
        session.query_adapter_power(limit(&canceled)),
        Err(Failure::BeforeSubmission(Transfer::PreparationFailed(8)))
    );
    assert_eq!(calls.load(Ordering::Relaxed), 1);
    assert!(!retired.load(Ordering::Acquire));
}
