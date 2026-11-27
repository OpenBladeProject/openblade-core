//! The Reader-level properties of the 00/87 MCU version query over the fake
//! controller: the exact command, its transaction, settle, checkpoints,
//! budget, sealing and every way a reply can fail to confirm a version.
#[cfg(feature = "service-live-readings-v1")]
use super::fan_rpm_tests::fan_io_worker;
#[cfg(feature = "service-live-readings-v1")]
use super::worker::Queries;
use super::{
    McuQuerySends, McuVersionRead,
    fan_rpm::{EXCHANGE, QUERY_RESERVE},
    fan_rpm_tests::{ADMISSION, CHECKSUM, FanIo, port},
    *,
};
use openblade_protocol::mcu_version::McuVersion;
use std::sync::{Arc, atomic::Ordering};

const VERSION: McuVersion = McuVersion([1, 5, 0, 0]);
/// An (offset into the 91-byte frame, byte) edit of the scripted reply.
type Edit = Option<(usize, u8)>;
/// A scripted (status, payload) reply, or none.
type Scripted = Option<(u8, Vec<u8>)>;

fn controller() -> Arc<FanIo> {
    let io = FanIo::new([0x20, 0x1e]);
    *io.mcu.lock().unwrap() = Some((2, vec![1, 5, 0, 0]));
    io
}

struct Outcome {
    thermal: Option<ObservedPerformance>,
    read: McuVersionRead,
    /// Queries counted as sent (`McuQuerySends`).
    sends: u64,
    sealed: bool,
    /// Job time left when the query ended.
    left: Duration,
    /// What a second thermal observation on the same Reader sent.
    again_sent: usize,
}

/// The thermal read, the MCU query and a second thermal read on one fresh
/// Reader within `budget`.
fn reader_job(io: &Arc<FanIo>, budget: Duration) -> Outcome {
    let cancelled = AtomicBool::new(false);
    let sends = McuQuerySends::default();
    let mut reader = Reader::new(port(io), ADMISSION);
    tokio::runtime::Builder::new_current_thread()
        .enable_time()
        .start_paused(true)
        .build()
        .unwrap()
        .block_on(async {
            let limit = Limit {
                deadline: Instant::now() + budget,
                cancelled: &cancelled,
            };
            let thermal = reader.observe(&mut || Some(ADMISSION), limit).await;
            let read = reader
                .read_mcu_version(&mut || Some(ADMISSION), limit, &sends)
                .await;
            let sealed = reader.poisoned;
            let left = limit.deadline.saturating_duration_since(Instant::now());
            let before = io.sent.lock().unwrap().len();
            io.fail_verify.store(0, Ordering::Release);
            let _ = reader.observe(&mut || Some(ADMISSION), limit).await;
            Outcome {
                thermal,
                read,
                sends: sends.count(),
                sealed,
                left,
                again_sent: io.sent.lock().unwrap().len() - before,
            }
        })
}

fn two_seconds(io: &Arc<FanIo>) -> Outcome {
    reader_job(io, Duration::from_secs(2))
}

/// The captured exchange: one 00/87 SET with four zero bytes and the first
/// ID of a fresh pair, its settle, then the GET that confirms the version and
/// leaves the Reader open for its next read.
#[test]
fn a_confirmed_reply_is_the_version_and_leaves_the_reader_open() {
    let io = controller();
    let job = two_seconds(&io);
    assert!(job.thermal.is_some());
    assert_eq!(job.read, McuVersionRead::Confirmed(VERSION));
    assert_eq!(job.sends, 1);
    assert!(!job.sealed);
    assert!(job.again_sent > 0);
    let sent = io.sent.lock().unwrap().clone();
    // Two thermal getters, the query, two more thermal getters.
    assert_eq!(sent[2], (0, 0x87, vec![0; 4]));
    assert_eq!(io.mcu_queries(), 1);
}

/// Every reply that does not confirm a version leaves it unknown and seals
/// the Reader, which sends nothing more; the thermal read it followed stands.
/// Each was sent once and counts.
#[test]
fn every_unconfirmed_reply_seals_the_reader_and_confirms_no_version() {
    // (name, scripted reply, frame edit, stalled GET)
    let cases: [(&str, Scripted, Edit, bool); 10] = [
        ("no reply", None, None, false),
        ("timeout", Some((2, vec![1, 5, 0, 0])), None, true),
        ("failed status", Some((3, vec![1, 5, 0, 0])), None, false),
        ("busy status", Some((1, vec![1, 5, 0, 0])), None, false),
        ("short payload", Some((2, vec![1, 5, 0])), None, false),
        ("long payload", Some((2, vec![1, 5, 0, 0, 0])), None, false),
        // Offsets are into the 91-byte frame: the report starts at 1. The fake
        // recomputes the checksum after any other edit, so each case fails on
        // its own field.
        (
            "transaction mismatch",
            Some((2, vec![1, 5, 0, 0])),
            Some((2, 0xfe)),
            false,
        ),
        (
            "device-mode reply",
            Some((2, vec![1, 5, 0, 0])),
            Some((8, 0x84)),
            false,
        ),
        (
            "report ID",
            Some((2, vec![1, 5, 0, 0])),
            Some((0, 1)),
            false,
        ),
        // The correct checksum of this reply is 0x87.
        (
            "checksum",
            Some((2, vec![1, 5, 0, 0])),
            Some((CHECKSUM, 0x5a)),
            false,
        ),
    ];
    for (name, reply, edit, stall) in cases {
        let io = FanIo::new([0x20, 0x1e]);
        *io.mcu.lock().unwrap() = reply;
        *io.mcu_edit.lock().unwrap() = edit;
        io.stall_mcu.store(stall, Ordering::Release);
        let job = two_seconds(&io);
        assert!(job.thermal.is_some(), "{name}");
        assert_eq!(job.read, McuVersionRead::Unconfirmed, "{name}");
        assert_eq!(job.sends, 1, "{name}");
        assert!(job.sealed, "{name}");
        assert_eq!(job.again_sent, 0, "{name}");
        assert_eq!(io.mcu_queries(), 1, "{name}");
    }
}

/// Each checkpoint stops on a lost identity: the query is not sent, then its
/// GET is not read, then its decoded version is not confirmed. It counts as
/// sent only once its SET went out.
#[test]
fn each_checkpoint_stops_on_a_lost_identity() {
    let baseline = controller();
    let cancelled = AtomicBool::new(false);
    let limit = Limit {
        deadline: Instant::now() + Duration::from_secs(2),
        cancelled: &cancelled,
    };
    let mut reader = Reader::new(port(&baseline), ADMISSION);
    tokio::runtime::Builder::new_current_thread()
        .enable_time()
        .start_paused(true)
        .build()
        .unwrap()
        .block_on(reader.observe(&mut || Some(ADMISSION), limit))
        .unwrap();
    let before = baseline.verifies.load(Ordering::Acquire);
    assert!(before > 0);
    // Entry, before the GET, after the decode.
    for (check, sent) in [(1, 0), (2, 1), (3, 1)] {
        let io = controller();
        io.fail_verify.store(before + check, Ordering::Release);
        let job = two_seconds(&io);
        assert_eq!(job.read, McuVersionRead::Unconfirmed, "{check}");
        assert_eq!(job.sends, sent as u64, "{check}");
        assert_eq!(io.mcu_queries(), sent, "{check}");
        assert!(job.thermal.is_some(), "{check}");
        assert!(job.sealed, "{check}");
    }
    // No checkpoint fails past those three.
    let io = controller();
    io.fail_verify.store(before + 4, Ordering::Release);
    assert_eq!(
        reader_job(&io, Duration::from_secs(2)).read,
        McuVersionRead::Confirmed(VERSION)
    );
}

/// A query whose GET never completes ends at its own one-second budget,
/// keeping the reserve for retirement and the final checks.
#[test]
fn a_stalled_query_ends_at_its_own_budget() {
    let limit = Duration::from_secs(5);
    let confirmed = reader_job(&controller(), limit);
    let stalled_io = controller();
    stalled_io.stall_mcu.store(true, Ordering::Release);
    let stalled = reader_job(&stalled_io, limit);
    assert_eq!(stalled.read, McuVersionRead::Unconfirmed);
    let spent = confirmed.left - stalled.left;
    assert!(spent < Duration::from_secs(1), "{spent:?}");
    assert!(stalled.left >= Duration::from_secs(3), "{:?}", stalled.left);
    let short = controller();
    short.stall_mcu.store(true, Ordering::Release);
    let job = reader_job(&short, Duration::from_millis(1200));
    assert_eq!(job.read, McuVersionRead::Unconfirmed);
    assert!(job.left >= QUERY_RESERVE, "{:?}", job.left);
}

/// The query alone on a fresh Reader with `budget` beyond the reserve.
fn query(io: &Arc<FanIo>, budget: Duration, sealed: bool) -> (McuVersionRead, u64) {
    let cancelled = AtomicBool::new(false);
    let sends = McuQuerySends::default();
    let mut reader = Reader::new(port(io), ADMISSION);
    if sealed {
        reader.seal();
    }
    tokio::runtime::Builder::new_current_thread()
        .enable_time()
        .start_paused(true)
        .build()
        .unwrap()
        .block_on(async {
            let limit = Limit {
                deadline: Instant::now() + QUERY_RESERVE + budget,
                cancelled: &cancelled,
            };
            let read = reader
                .read_mcu_version(&mut || Some(ADMISSION), limit, &sends)
                .await;
            (read, sends.count())
        })
}

/// No SET goes out unless the budget beyond the reserve covers one exchange,
/// or on a sealed Reader: a SET without that time would likely time out and
/// quarantine its ID for the process.
#[test]
fn the_query_is_sent_only_with_an_exchange_of_budget_on_an_open_reader() {
    let io = controller();
    assert_eq!(
        query(&io, EXCHANGE - Duration::from_millis(1), false),
        (McuVersionRead::NotRequested, 0)
    );
    assert!(io.sent.lock().unwrap().is_empty());
    let io = controller();
    assert_eq!(
        query(&io, EXCHANGE, true),
        (McuVersionRead::NotRequested, 0)
    );
    assert!(io.sent.lock().unwrap().is_empty());
    let io = controller();
    assert_eq!(
        query(&io, EXCHANGE, false),
        (McuVersionRead::Confirmed(VERSION), 1)
    );
    assert_eq!(io.mcu_queries(), 1);
}

/// The budget is checked again after the pair is reserved: an entry
/// checkpoint slow enough to leave less than one exchange sends no SET, so
/// its ID stays free instead of being quarantined by a timeout. This runs on
/// the wall clock, since the fake's checkpoint is slow on the worker thread.
#[test]
fn a_slow_entry_checkpoint_that_spends_the_exchange_sends_no_set() {
    let io = controller();
    // The query alone on a fresh Reader: its entry checkpoint is the first
    // verify. It leaves 100 ms of the query's 1 s, below one exchange, so
    // that checkpoint itself still passes.
    *io.slow_verify.lock().unwrap() = Some((1, Duration::from_millis(900)));
    assert!(EXCHANGE > Duration::from_millis(100));
    let cancelled = AtomicBool::new(false);
    let sends = McuQuerySends::default();
    let mut reader = Reader::new(port(&io), ADMISSION);
    let read = tokio::runtime::Builder::new_current_thread()
        .enable_time()
        .build()
        .unwrap()
        .block_on(async {
            let limit = Limit {
                deadline: Instant::now() + QUERY_RESERVE + Duration::from_secs(2),
                cancelled: &cancelled,
            };
            reader
                .read_mcu_version(&mut || Some(ADMISSION), limit, &sends)
                .await
        });
    assert_eq!(io.verifies.load(Ordering::Acquire), 1);
    assert_eq!(read, McuVersionRead::Unconfirmed);
    assert_eq!(sends.count(), 0);
    assert!(io.sent.lock().unwrap().is_empty());
}

/// The query is admitted only on the Reader's exact identity, and that
/// identity is the one the capture-backed fixture pins. RZ09-0528, whose
/// managed read rests on its own device coverage record, has no Reader.
#[test]
fn only_the_fixture_identity_has_a_reader() {
    let fixture: serde_json::Value = serde_json::from_str(include_str!(
        "../../../../../../evidence/legacy-captures/decoded/rz09-0581-pid-02e0-bios-3.01-mcu-version-fixture.json"
    ))
    .unwrap();
    let device = &fixture["device"];
    let hex = |value: &serde_json::Value| u16::from_str_radix(value.as_str().unwrap(), 16).unwrap();
    assert_eq!(device["model"], MODEL);
    assert_eq!(
        (hex(&device["vendorId"]), hex(&device["productId"])),
        DEVICE
    );
    assert_eq!(fixture["productionWired"], true);
    for (model, device, admitted) in [
        (MODEL, DEVICE, true),
        ("RZ09-0528", (0x1532, 0x02c6), false),
        ("RZ09-0528", DEVICE, false),
        (MODEL, (0x1532, 0x02c6), false),
    ] {
        let selection = Selection {
            model: model.to_owned(),
            device,
            usage: Some(USAGE),
            hint: crate::discovery::CandidateHint {
                path: "synthetic".into(),
                facts: crate::discovery::pnp::facts(
                    &[r"HID\VID_1532&PID_02E0&MI_00&COL01".into()],
                    &[0x1532],
                )
                .unwrap(),
            },
        };
        assert_eq!(
            reviewed_selection(&selection),
            admitted,
            "{model} {device:?}"
        );
    }
}

/// Through the shared job body: only a job that asks sends the query, after
/// the thermal getters and the fan pair, and an unconfirmed query costs the
/// fan pair of the same job nothing.
#[cfg(feature = "service-live-readings-v1")]
#[test]
fn the_shared_job_sends_the_query_last_and_only_when_asked() {
    for (asked, reply, expected, sent_total) in [
        (
            false,
            Some((2, vec![1, 5, 0, 0])),
            McuVersionRead::NotRequested,
            4,
        ),
        (
            true,
            Some((2, vec![1, 5, 0, 0])),
            McuVersionRead::Confirmed(VERSION),
            5,
        ),
        (true, None, McuVersionRead::Unconfirmed, 5),
    ] {
        let io = FanIo::new([0x20, 0x1e]);
        *io.mcu.lock().unwrap() = reply;
        let worker = fan_io_worker(io.clone(), || Some(ADMISSION));
        let sends = McuQuerySends::default();
        let queries = Queries {
            fan_rpm: Some(super::FanPairSends::default()),
            mcu_version: asked.then(|| sends.clone()),
            #[cfg(feature = "service-keyboard-driver-mode-read-v1")]
            device_mode: false,
        };
        let reading = tokio::runtime::Builder::new_current_thread()
            .enable_time()
            .build()
            .unwrap()
            .block_on(worker.observe_with_queries(Instant::now() + Duration::from_secs(3), queries))
            .unwrap()
            .unwrap();
        assert_eq!(reading.mcu_version, expected);
        assert_eq!(reading.fan_rpm, super::FanRpmRead::Confirmed([3200, 3000]));
        assert_eq!(sends.count(), u64::from(asked));
        let sent = io.sent.lock().unwrap().clone();
        let ids: Vec<_> = sent.iter().map(|(class, id, _)| (*class, *id)).collect();
        let mut order = vec![
            (CLASS, GET_THERMAL),
            (CLASS, GET_THERMAL),
            (CLASS, 0x88),
            (CLASS, 0x88),
        ];
        if asked {
            order.push((0, 0x87));
        }
        assert_eq!(ids, order, "{expected:?}");
        assert_eq!(sent.len(), sent_total, "{expected:?}");
    }
}
