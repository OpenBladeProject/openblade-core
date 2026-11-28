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

// Without a release or an answer nothing is freed: unsettled reservations
// stop after exactly 127 pairs of the IDs 1 to 254.
#[test]
fn unsettled_reservations_stop_after_exactly_127_pairs() {
    let transactions = Transactions::new();
    for pair in 0u8..127 {
        assert_eq!(
            transactions.reserve_pair(),
            Some([pair * 2 + 1, pair * 2 + 2])
        );
    }
    assert_eq!(transactions.reserve_pair(), None);
    assert_eq!(transactions.reserve_pair(), None);
}

fn fixture() -> Value {
    serde_json::from_str(include_str!(
        "../../../../../../evidence/legacy-captures/decoded/rz09-0581-pid-02e0-bios-3.01-thermal-write-validation-fixture.json"
    )).unwrap()
}

pub(super) fn reply(transaction: u8, payload: &[u8]) -> Frame {
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
    fn transactions(&self) -> &Transactions {
        &self.transactions
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
            #[cfg(feature = "service-thermal-mutation-v1")]
            Transfer::SendMutation(_) => panic!("read-only fixture received a mutation"),
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
async fn paired_observation_fixture_uses_typed_read_custody_without_power_or_ui_session() {
    let fixture: Value = serde_json::from_str(include_str!(
        "../../../../../../tests/Fixtures/Parity/rz09-0581-thermal-pair-observation.json"
    ))
    .unwrap();
    let admitted = ReadAdmission {
        lifecycle: 1,
        generation: [9; 32],
    };
    assert!(admitted.readable());
    assert!(admitted.matches_session(Some(0)));
    let cancelled = AtomicBool::new(false);
    for row in fixture["cases"].as_array().unwrap() {
        let mode = row["mode"].as_u64().unwrap() as u8;
        let fan = row["fan"].as_u64().unwrap() as u8;
        let mut script = Script::pair([mode, mode]);
        script.replies = VecDeque::from([
            (90, reply(1, &[1, 1, mode, fan])),
            (91, reply(2, &[1, 2, mode, fan])),
        ]);
        let mut reader = Reader::new(script, admitted);
        let state = reader
            .observe(&mut || Some(admitted), limit(&cancelled))
            .await
            .unwrap();
        let (preset, fans, hyper) = state.projection();
        assert_eq!(serde_json::json!(preset), row["performance"]);
        assert_eq!(serde_json::json!(fans), row["fans"]);
        assert_eq!(serde_json::json!(hyper), row["hyperBoostActive"]);
        assert_eq!(reader.port.sent.len(), 2);
        assert!(reader.port.sent.iter().all(|frame| frame[8] == GET_THERMAL));
        reader.retire(None).await.unwrap();
    }
    for row in fixture["incomplete"].as_array().unwrap() {
        let mode = row["mode"].as_u64().unwrap() as u8;
        let fan = row["fan"].as_u64().unwrap() as u8;
        let mut script = Script::pair([mode, mode]);
        script.replies = VecDeque::from([
            (90, reply(1, &[1, 1, mode, fan])),
            (91, reply(2, &[1, 2, mode, fan])),
        ]);
        let mut reader = Reader::new(script, admitted);
        assert!(
            reader
                .observe(&mut || Some(admitted), limit(&cancelled))
                .await
                .is_none()
        );
        assert!(reader.poisoned);
        assert!(
            reader
                .observe(&mut || Some(admitted), limit(&cancelled))
                .await
                .is_none()
        );
        assert_eq!(reader.port.sent.len(), 3);
    }
}

#[tokio::test(start_paused = true)]
async fn typed_read_generation_loss_never_adopts_the_same_visible_value_from_a_new_epoch() {
    let original = ReadAdmission {
        lifecycle: 1,
        generation: [1; 32],
    };
    let replacement = ReadAdmission {
        lifecycle: 1,
        generation: [2; 32],
    };
    let cancelled = AtomicBool::new(false);
    let mut reader = Reader::new(Script::pair([0, 0]), original);
    assert_eq!(
        reader
            .observe(&mut || Some(original), limit(&cancelled))
            .await,
        Some(ObservedPerformance::BalancedAutomatic)
    );
    assert!(
        reader
            .observe(&mut || Some(replacement), limit(&cancelled))
            .await
            .is_none()
    );
    assert!(reader.poisoned);
    assert!(
        reader
            .observe(&mut || Some(original), limit(&cancelled))
            .await
            .is_none()
    );
    assert_eq!(reader.port.sent.len(), 2);
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
            facts: pnp::facts(&[r"HID\VID_1532&PID_02E0&MI_00&COL01".into()], &[0x1532]).unwrap(),
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
            5,
            Some(Admission {
                controller_clear: false,
                ..ADMITTED
            }),
            1,
            0,
        ), // Controller unknown or present after sending the getter.
        (
            7,
            Some(Admission {
                full_adapter: false,
                ..ADMITTED
            }),
            1,
            1,
        ), // Power is no longer confirmed before channel 2.
        (
            9,
            Some(Admission {
                epoch: 11,
                ..ADMITTED
            }),
            2,
            1,
        ), // Power/lifecycle cycle.
        (
            11,
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
    let mut unsupported = Reader::new(Script::pair([8, 8]), ADMITTED);
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
    while reader.port.transactions.reserve_pair().is_some() {}
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

#[derive(serde::Deserialize)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
struct CustomFixture {
    schema_version: u32,
    model: String,
    cases: Vec<CustomCase>,
}
#[derive(serde::Deserialize)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
struct CustomCase {
    id: String,
    fan: u8,
    cpu_payload: Vec<u8>,
    gpu_payload: Vec<u8>,
    #[serde(deserialize_with = "required_nullable")]
    expected: Option<CustomExpected>,
    query_count: usize,
    port_healthy: bool,
}

#[derive(serde::Deserialize, serde::Serialize)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
struct CustomExpected {
    performance: u8,
    fans: u8,
    hyper_boost_active: bool,
    #[serde(deserialize_with = "required_nullable")]
    custom_performance: Option<CustomLevelsExpected>,
}
#[derive(serde::Deserialize, serde::Serialize)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
struct CustomLevelsExpected {
    cpu: u8,
    gpu: u8,
    maximum_fans: bool,
}
fn required_nullable<'de, D: serde::Deserializer<'de>, T: serde::Deserialize<'de>>(
    deserializer: D,
) -> Result<Option<T>, D::Error> {
    serde::Deserialize::deserialize(deserializer)
}

pub(super) fn custom_reply(transaction: u8, payload: &[u8]) -> Frame {
    let mut frame = [0; FRAME_LENGTH];
    frame[1..].copy_from_slice(
        &RazerReport::create(transaction, CLASS, 0x87, payload)
            .unwrap()
            .encode(),
    );
    frame[1] = 2;
    frame
}
impl Script {
    pub(super) fn custom(fan: u8, cpu: &[u8], gpu: &[u8], first: u8) -> Self {
        let mut script = Self::pair([4, 4]);
        script.transactions = Transactions::starting_at(first);
        script.replies = VecDeque::from([
            (90, reply(first, &[1, 1, 4, fan])),
            (91, reply(first + 1, &[1, 2, 4, fan])),
            (90, custom_reply(first + 2, cpu)),
            (91, custom_reply(first + 3, gpu)),
        ]);
        script
    }
}
#[tokio::test(start_paused = true)]
async fn custom_shared_parity_is_complete_ordered_and_preserves_manual_unknown_health() {
    let text =
        include_str!("../../../../../../tests/Fixtures/Parity/rz09-0581-custom-observation.json");
    let fixture: CustomFixture = serde_json::from_str(text).unwrap();
    assert_eq!(fixture.schema_version, 1);
    assert_eq!(fixture.model, MODEL);
    let proof = ReadAdmission {
        lifecycle: 1,
        generation: [8; 32],
    };
    let cancelled = AtomicBool::new(false);
    for row in fixture.cases {
        let mut reader = Reader::new(
            Script::custom(row.fan, &row.cpu_payload, &row.gpu_payload, 1),
            proof,
        );
        let result = reader.observe(&mut || Some(proof), limit(&cancelled)).await;
        let actual = result.map(|state| {
            let (performance,fans,hyper) = state.projection();
            serde_json::json!({"performance":performance,"fans":fans,"hyperBoostActive":hyper,
                "customPerformance":state.custom_levels().map(|v| serde_json::json!({"cpu":v.cpu as u8,"gpu":v.gpu as u8,"maximumFans":false}))})
        });
        assert_eq!(
            serde_json::json!(actual),
            serde_json::to_value(&row.expected).unwrap(),
            "{}",
            row.id
        );
        assert_eq!(!reader.poisoned, row.port_healthy, "{}", row.id);
        assert_eq!(reader.port.sent.len(), row.query_count, "{}", row.id);
        for (index, frame) in reader.port.sent.iter().enumerate() {
            assert_eq!(frame[2], index as u8 + 1);
            assert_eq!(frame[8], if index < 2 { 0x82 } else { 0x87 });
            if index >= 2 {
                assert_eq!(&frame[9..12], &[1, index as u8 - 1, 0]);
            }
        }
        reader.port.replies =
            VecDeque::from([(91, reply(5, &[1, 1, 0, 0])), (91, reply(6, &[1, 2, 0, 0]))]);
        assert_eq!(
            reader.observe(&mut || Some(proof), limit(&cancelled)).await,
            row.port_healthy
                .then_some(ObservedPerformance::BalancedAutomatic)
        );
    }
}

#[tokio::test(start_paused = true)]
async fn custom_component_faults_and_lost_custody_never_publish_partial_levels() {
    let cancelled = AtomicBool::new(false);
    for corrupt in 0..8 {
        let mut script = Script::custom(0, &[1, 1, 1], &[1, 2, 2], 1);
        let (length, frame) = &mut script.replies[3];
        match corrupt {
            0 => *length = 89,
            1 => frame[0] = 1,
            2 => frame[89] ^= 1,
            3 => frame[2] = 3,
            4 => frame[1] = 3,
            5 => {
                frame[7] = 0;
                frame[89] = openblade_protocol::calculate_checksum(&frame[1..]).unwrap();
            }
            6 => {
                frame[8] = 0x82;
                frame[89] = openblade_protocol::calculate_checksum(&frame[1..]).unwrap();
            }
            7 => {
                script.replies.pop_back();
            }
            _ => unreachable!(),
        }
        let mut reader = Reader::new(script, ADMITTED);
        assert!(
            reader
                .observe(&mut || Some(ADMITTED), limit(&cancelled))
                .await
                .is_none()
        );
        assert!(reader.poisoned);
        assert_eq!(reader.port.sent.len(), 4);
        assert!(
            reader
                .observe(&mut || Some(ADMITTED), limit(&cancelled))
                .await
                .is_none()
        );
        assert_eq!(reader.port.sent.len(), 4);
    }
    for revoke in [13, 15, 17, 19] {
        let mut reader = Reader::new(Script::custom(0, &[1, 1, 1], &[1, 2, 2], 1), ADMITTED);
        let mut calls = 0;
        assert!(
            reader
                .observe(
                    &mut || {
                        calls += 1;
                        (calls < revoke).then_some(ADMITTED)
                    },
                    limit(&cancelled)
                )
                .await
                .is_none()
        );
        assert!(reader.poisoned);
    }
}

#[tokio::test(start_paused = true)]
async fn custom_cpu_success_then_claimed_gpu_admission_cancel_submits_no_gpu() {
    let cancelled = AtomicBool::new(false);
    let proof = ReadAdmission {
        lifecycle: 1,
        generation: [9; 32],
    };
    let mut calls = 0;
    let mut reader = Reader::new(Script::custom(0, &[1, 1, 1], &[1, 2, 2], 1), proof);
    let observed = reader
        .observe(
            &mut || {
                calls += 1;
                // The read_pair entry checkpoint consumes two calls, then the reserved
                // pair's checkpoints consume ten; CPU entry/read consume four. CPU has
                // decoded successfully before GPU invokes admission.
                if calls == 17 {
                    cancelled.store(true, Ordering::Release);
                }
                Some(proof)
            },
            limit(&cancelled),
        )
        .await;
    assert!(observed.is_none());
    assert!(reader.poisoned);
    assert_eq!(reader.port.received, 3);
    assert_eq!(reader.port.sent.len(), 3);
    assert!(reader.port.pending.is_none());
    assert_eq!(reader.port.cancels, 1);
    reader.retire(None).await.unwrap();
    assert!(reader.port.retired);
}

#[test]
fn custom_fixture_rejects_missing_nullable_unknown_duplicate_and_non_numeric_fields() {
    let json =
        include_str!("../../../../../../tests/Fixtures/Parity/rz09-0581-custom-observation.json");
    for replacement in [
        r#""cpu": 1, "cpu": 2"#,
        r#""cpu": "1""#,
        r#""unexpected": 1"#,
    ] {
        let changed = json.replacen(r#""cpu": 1"#, replacement, 1);
        assert_ne!(changed, json);
        assert!(serde_json::from_str::<CustomFixture>(&changed).is_err());
    }
    let mut value: serde_json::Value = serde_json::from_str(json).unwrap();
    value["cases"][4]["expected"]
        .as_object_mut()
        .unwrap()
        .remove("customPerformance");
    assert!(serde_json::from_value::<CustomFixture>(value).is_err());
}

#[tokio::test(start_paused = true)]
async fn correction_refused_initial_checkpoint_consumes_no_pair_but_reserved_refusal_burns_ids() {
    let cancelled = AtomicBool::new(false);
    let proof = ReadAdmission {
        generation: [7; 32],
        lifecycle: 1,
    };
    let mut reader = Reader::new(Script::pair([0, 0]), proof);
    assert!(
        reader
            .observe(&mut || None, limit(&cancelled))
            .await
            .is_none()
    );
    assert!(reader.port.sent.is_empty());
    assert_eq!(reader.port.transactions.reserve_pair(), Some([1, 2]));
    // An unretired reader keeps the IDs of an admitted reservation held.
    let mut reader = Reader::new(Script::pair([0, 0]), proof);
    let mut calls = 0;
    assert!(
        reader
            .observe(
                &mut || {
                    calls += 1;
                    if calls <= 2 { Some(proof) } else { None }
                },
                limit(&cancelled)
            )
            .await
            .is_none()
    );
    assert!(reader.port.sent.is_empty());
    assert_eq!(reader.port.transactions.reserve_pair(), Some([3, 4]));
}
