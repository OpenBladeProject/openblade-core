use super::*;
use crate::discovery::{CandidateHint, pnp};
use serde_json::Value;
use std::{collections::VecDeque, sync::atomic::Ordering, task::Poll};

pub(super) const ADMITTED: Admission = Admission {
    epoch: 10,
    session: 1,
    full_adapter: true,
    controller_clear: true,
    awake: true,
};
const EXACT: Descriptor = Descriptor {
    device: DEVICE,
    usage: USAGE,
    feature_bytes: 91,
    version: 1,
};

fn fixture() -> Value {
    serde_json::from_str(include_str!(
        "../../../../../../evidence/legacy-captures/decoded/rz09-0581-pid-02e0-bios-3.01-thermal-write-validation-fixture.json"
    )).unwrap()
}

fn reply(transaction: u8, payload: &[u8]) -> Frame {
    let mut frame = [0; FRAME_LENGTH];
    frame[1..].copy_from_slice(
        &RazerReport::create(transaction, CLASS, GET_THERMAL, payload)
            .unwrap()
            .encode(),
    );
    frame[1] = 2;
    frame
}

pub(super) struct Script {
    identity: Descriptor,
    transactions: Transactions,
    replies: VecDeque<(usize, Frame)>,
    sent: Vec<Frame>,
    received: usize,
    pending: Option<(usize, Frame)>,
    stall: bool,
    cancels: usize,
    retired: bool,
}

impl Script {
    pub(super) fn pair(selectors: [u8; 2]) -> Self {
        Self {
            identity: EXACT,
            transactions: Transactions::new(),
            replies: VecDeque::from([
                (90, reply(1, &[1, 1, selectors[0], 0])),
                (91, reply(2, &[1, 2, selectors[1], 0])),
            ]),
            sent: Vec::new(),
            received: 0,
            pending: None,
            stall: false,
            cancels: 0,
            retired: false,
        }
    }
}

impl Port for Script {
    fn reserve_pair(&mut self) -> Option<[u8; 2]> {
        self.transactions.reserve_pair()
    }
    fn verify(&mut self, _: Limit<'_>) -> io::Result<()> {
        reviewed_descriptor(self.identity)
            .then_some(())
            .ok_or_else(|| blocked("wrong descriptor"))
    }

    fn submit(&mut self, transfer: Transfer, _: Limit<'_>) -> io::Result<()> {
        assert!(!self.retired && self.pending.is_none());
        self.pending = Some(match transfer {
            Transfer::SendGetter(frame) => {
                self.sent.push(frame);
                (0, frame)
            }
            Transfer::ReceiveReply => {
                self.received += 1;
                self.replies
                    .pop_front()
                    .ok_or(io::ErrorKind::UnexpectedEof)?
            }
        });
        Ok(())
    }

    fn poll(&mut self) -> Option<io::Result<(usize, Frame)>> {
        if self.stall {
            None
        } else {
            self.pending.take().map(Ok)
        }
    }

    fn cancel(&mut self) {
        self.cancels += 1;
    }

    fn poll_retirement(&mut self) -> io::Result<bool> {
        let _ = self.poll();
        self.retired = self.pending.is_none();
        Ok(self.retired)
    }
}

fn limit(cancelled: &AtomicBool) -> Limit<'_> {
    Limit {
        deadline: Instant::now() + Duration::from_millis(250),
        cancelled,
    }
}

#[tokio::test(start_paused = true)]
async fn reviewed_pair_returns_typed_performance_and_only_submits_the_pinned_getters() {
    let capture = fixture();
    assert_eq!(capture["device"]["model"], MODEL);
    let run = &capture["runs"][1];
    assert_eq!(run["target"], "performance");
    assert_eq!(run["gates"]["candidateConfirmed"], true);
    let cancelled = AtomicBool::new(false);
    for (selectors, state, key) in [
        ([0, 0], ObservedPerformance::BalancedAutomatic, "baseline"),
        (
            [2, 2],
            ObservedPerformance::PerformanceAutomatic,
            "candidate",
        ),
    ] {
        let mut reader = Reader::new(Script::pair(selectors), ADMITTED);
        for (index, field) in ["thermal1Hex", "thermal2Hex"].into_iter().enumerate() {
            let (_, frame) = &reader.port.replies[index];
            assert_eq!(hex(&frame[9..13]), run[key][field]);
        }
        assert_eq!(
            reader
                .observe(&mut || Some(ADMITTED), limit(&cancelled))
                .await,
            Some(state)
        );
        assert_eq!(reader.port.received, 2);
        assert_eq!(reader.port.sent.len(), 2);
        for (i, frame) in reader.port.sent.iter().enumerate() {
            let row = &capture["readbackCommands"]["thermalPair"][i];
            assert_eq!(&frame[..3], &[0, 0, (i + 1) as u8]);
            assert_eq!(hex(&frame[7..8]), row["classHex"]);
            assert_eq!(hex(&frame[8..9]), row["idHex"]);
            assert_eq!(hex(&frame[9..13]), row["requestPayloadHex"]);
        }
    }
}

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|byte| format!("{byte:02X}")).collect()
}

#[tokio::test(start_paused = true)]
async fn wrong_model_hid_role_or_report_geometry_cannot_submit() {
    let mut selection = Selection {
        model: MODEL.to_owned(),
        device: DEVICE,
        usage: Some(USAGE),
        hint: CandidateHint {
            path: "synthetic".into(),
            facts: pnp::facts(&[r"HID\VID_1532&PID_02E0&MI_00&COL01".into()]).unwrap(),
        },
    };
    assert!(reviewed_selection(&selection));
    selection.model = "RZ09-0528".into();
    assert!(!reviewed_selection(&selection));
    selection.model = MODEL.into();
    selection.usage = None;
    assert!(!reviewed_selection(&selection));
    let cancelled = AtomicBool::new(false);
    for identity in [
        Descriptor {
            device: (0x1532, 0x02c6),
            ..EXACT
        },
        Descriptor {
            usage: (1, 6),
            ..EXACT
        },
        Descriptor {
            feature_bytes: 90,
            ..EXACT
        },
    ] {
        let mut script = Script::pair([2, 2]);
        script.identity = identity;
        let mut reader = Reader::new(script, ADMITTED);
        assert_eq!(
            reader
                .observe(&mut || Some(ADMITTED), limit(&cancelled))
                .await,
            None
        );
        assert!(reader.port.sent.is_empty());
    }
}

#[tokio::test(start_paused = true)]
async fn partial_malformed_and_uncorrelated_replies_are_unavailable() {
    let cancelled = AtomicBool::new(false);
    for corrupt in 0..10 {
        let mut script = Script::pair([2, 2]);
        let (length, frame) = &mut script.replies[1];
        match corrupt {
            0 => *length = 89,
            1 => frame[0] = 1,
            2 => frame[89] ^= 1,
            3 => *frame = reply(1, &[1, 2, 2, 0]),
            4 => *frame = reply(2, &[1, 1, 2, 0]),
            5 => *frame = reply(2, &[1, 2, 2]),
            6 => frame[1] = 3,
            7 => *frame = reply(2, &[1, 2, 0, 0]),
            8 => *frame = reply(2, &[1, 2, 2, 1]),
            9 => {
                script.replies.pop_back();
            }
            _ => unreachable!(),
        }
        let mut reader = Reader::new(script, ADMITTED);
        assert_eq!(
            reader
                .observe(&mut || Some(ADMITTED), limit(&cancelled))
                .await,
            None
        );
        assert_eq!(reader.port.sent.len(), 2);
        assert_eq!(
            reader
                .observe(&mut || Some(ADMITTED), limit(&cancelled))
                .await,
            None
        );
        assert_eq!(
            reader.port.sent.len(),
            2,
            "a failed lease cannot replay a getter"
        );
    }
}

#[tokio::test(start_paused = true)]
async fn power_controller_session_and_owner_loss_block_remaining_traffic_or_publication() {
    let cancelled = AtomicBool::new(false);
    for (revoked_on, evidence, sent, received) in [
        (1, None, 0, 0), // No owner at entry.
        (
            3,
            Some(Admission {
                controller_clear: false,
                ..ADMITTED
            }),
            1,
            0,
        ), // Controller unknown or present after sending the getter.
        (
            5,
            Some(Admission {
                full_adapter: false,
                ..ADMITTED
            }),
            1,
            1,
        ), // Power is no longer confirmed before channel 2.
        (
            7,
            Some(Admission {
                epoch: 11,
                ..ADMITTED
            }),
            2,
            1,
        ), // Power/lifecycle cycle.
        (
            9,
            Some(Admission {
                session: 2,
                ..ADMITTED
            }),
            2,
            2,
        ), // Caller changed before publish.
    ] {
        let mut calls = 0;
        let mut authority = || {
            calls += 1;
            if calls >= revoked_on {
                evidence
            } else {
                Some(ADMITTED)
            }
        };
        let mut reader = Reader::new(Script::pair([2, 2]), ADMITTED);
        assert_eq!(
            reader.observe(&mut authority, limit(&cancelled)).await,
            None
        );
        assert_eq!(reader.port.sent.len(), sent);
        assert_eq!(reader.port.received, received);
    }
}

#[tokio::test(start_paused = true)]
async fn unsupported_state_is_unavailable_and_transactions_are_not_reused() {
    let cancelled = AtomicBool::new(false);
    let mut unsupported = Reader::new(Script::pair([5, 5]), ADMITTED);
    assert_eq!(
        unsupported
            .observe(&mut || Some(ADMITTED), limit(&cancelled))
            .await,
        None
    );

    let mut reader = Reader::new(Script::pair([0, 0]), ADMITTED);
    assert!(
        reader
            .observe(&mut || Some(ADMITTED), limit(&cancelled))
            .await
            .is_some()
    );
    reader.port.replies =
        VecDeque::from([(91, reply(3, &[1, 1, 2, 0])), (91, reply(4, &[1, 2, 2, 0]))]);
    assert!(
        reader
            .observe(&mut || Some(ADMITTED), limit(&cancelled))
            .await
            .is_some()
    );
    let transactions: Vec<_> = reader.port.sent.iter().map(|frame| frame[2]).collect();
    assert_eq!(transactions, [1, 2, 3, 4]);
    reader.port.transactions.0.store(254, Ordering::Relaxed);
    assert_eq!(
        reader
            .observe(&mut || Some(ADMITTED), limit(&cancelled))
            .await,
        None
    );
    assert_eq!(reader.port.sent.len(), 4);
}

#[tokio::test(start_paused = true)]
async fn timeout_and_explicit_cancellation_fence_the_unretired_operation() {
    for cancel in [false, true] {
        let cancelled = AtomicBool::new(false);
        let mut script = Script::pair([2, 2]);
        script.stall = true;
        let mut reader = Reader::new(script, ADMITTED);
        let mut authority = || Some(ADMITTED);
        let observation = reader.observe(&mut authority, limit(&cancelled));
        let signal = async {
            if cancel {
                tokio::time::sleep(POLL).await;
                cancelled.store(true, Ordering::Release);
            }
        };
        let (result, ()) = tokio::join!(observation, signal);
        assert_eq!(result, None);
        assert_eq!(reader.port.cancels, 1);
        assert_eq!(reader.port.sent.len(), 1);
        assert_eq!(reader.port.received, 0);
        assert_eq!(
            reader
                .retire(Some(Instant::now() + POLL))
                .await
                .unwrap_err()
                .kind(),
            io::ErrorKind::TimedOut
        );
        assert!(reader.port.pending.is_some());
        reader.port.stall = false; // A late successful completion is discarded.
        reader.retire(Some(Instant::now() + POLL)).await.unwrap();
        assert!(reader.port.retired);
        assert_eq!(
            reader
                .observe(&mut || Some(ADMITTED), limit(&cancelled))
                .await,
            None
        );
    }
}

#[tokio::test(start_paused = true)]
async fn dropping_observation_cancels_and_keeps_its_pending_storage() {
    let cancelled = AtomicBool::new(false);
    let mut script = Script::pair([0, 0]);
    script.stall = true;
    let mut reader = Reader::new(script, ADMITTED);
    let mut authority = || Some(ADMITTED);
    let mut observation = Box::pin(reader.observe(&mut authority, limit(&cancelled)));
    assert_eq!(
        std::future::poll_fn(|cx| Poll::Ready(observation.as_mut().poll(cx))).await,
        Poll::Pending
    );
    drop(observation);
    assert_eq!(reader.port.cancels, 1);
    assert!(reader.port.pending.is_some());
}
