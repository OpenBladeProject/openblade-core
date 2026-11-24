//! A fake fan controller behind the real Reader, and the Reader-level
//! properties of the 0D/88 pair: its order, transaction pair, settle,
//! checkpoints, budget and sealing.
#[cfg(feature = "service-live-readings-v1")]
use super::worker::Queries;
use super::{
    FanPairSends, FanRpmRead,
    fan_rpm::{EXCHANGE, QUERY_RESERVE},
    worker::Worker,
    *,
};
use std::sync::{
    Arc, Mutex,
    atomic::{AtomicU8, AtomicUsize, Ordering},
};

/// A scripted (status, payload) reply to one selector.
type Reply = Option<(u8, Vec<u8>)>;
/// The report checksum's offset in the 91-byte frame: report byte 88.
pub(super) const CHECKSUM: usize = 1 + 88;

/// Answers the paired thermal getters (the `thermal` mode byte, Balanced by
/// default, with Automatic fans) and each 0D/88
/// query with a whole managed report: status 2 and `[1, selector, units]`,
/// or the selector's entry in `replies` when present. `stall` never completes
/// a fan query's GET, `first_get_delay` delays the first one's reply, and
/// `hold_query` blocks the worker thread inside the next fan query's SET
/// until its sender sends or is dropped.
/// Verification fails at the 1-based `fail_verify` call
/// (0 never). Every submitted report is recorded as (class, ID, payload),
/// each fan query's transaction ID in `query_ids` and the time from its send
/// to its GET in `settles`.
///
/// `slow_verify` sleeps the worker thread for its duration at its 1-based
/// verify call, on the wall clock.
///
/// The 00/87 MCU version query is answered with `mcu` (status, payload),
/// then the `mcu_edit` (offset into the 91-byte frame, byte) with the report
/// checksum recomputed unless the edit is the checksum itself, and with no
/// reply when `mcu` is None. `stall_mcu` never completes its GET, and
/// `hold_mcu` blocks the worker thread inside its SET like `hold_query`.
pub(crate) struct FanIo {
    transactions: Transactions,
    pub(crate) units: [u8; 2],
    pub(crate) thermal: AtomicU8,
    pub(crate) replies: Mutex<[Reply; 2]>,
    pub(crate) stall: AtomicBool,
    pub(crate) first_get_delay: Mutex<Option<Duration>>,
    pub(crate) hold_query: Mutex<Option<std::sync::mpsc::Receiver<()>>>,
    pub(crate) fail_verify: AtomicUsize,
    pub(crate) verifies: AtomicUsize,
    pub(crate) slow_verify: Mutex<Option<(usize, Duration)>>,
    pub(crate) sent: Mutex<Vec<(u8, u8, Vec<u8>)>>,
    pub(crate) query_ids: Mutex<Vec<u8>>,
    pub(crate) settles: Mutex<Vec<Duration>>,
    pub(crate) mcu: Mutex<Reply>,
    pub(crate) mcu_edit: Mutex<Option<(usize, u8)>>,
    pub(crate) stall_mcu: AtomicBool,
    pub(crate) hold_mcu: Mutex<Option<std::sync::mpsc::Receiver<()>>>,
}
impl FanIo {
    pub(crate) fn new(units: [u8; 2]) -> Arc<Self> {
        Arc::new(Self {
            transactions: Transactions::new(),
            units,
            thermal: AtomicU8::new(0),
            replies: Mutex::default(),
            stall: AtomicBool::new(false),
            first_get_delay: Mutex::default(),
            hold_query: Mutex::default(),
            fail_verify: AtomicUsize::new(0),
            verifies: AtomicUsize::new(0),
            slow_verify: Mutex::default(),
            sent: Mutex::default(),
            query_ids: Mutex::default(),
            settles: Mutex::default(),
            mcu: Mutex::default(),
            mcu_edit: Mutex::default(),
            stall_mcu: AtomicBool::new(false),
            hold_mcu: Mutex::default(),
        })
    }
    pub(crate) fn queries(&self) -> Vec<Vec<u8>> {
        let sent = self.sent.lock().unwrap();
        sent.iter()
            .filter(|(class, id, _)| (*class, *id) == (CLASS, 0x88))
            .map(|(.., payload)| payload.clone())
            .collect()
    }
    /// How many 00/87 queries were submitted.
    pub(crate) fn mcu_queries(&self) -> usize {
        let sent = self.sent.lock().unwrap();
        sent.iter()
            .filter(|(class, id, _)| (*class, *id) == (0, 0x87))
            .count()
    }
}
pub(super) struct FanPort {
    mcu_sent: bool,
    io: Arc<FanIo>,
    reply: Option<(usize, Frame)>,
    pending: Option<io::Result<(usize, Frame)>>,
    query_sent: Option<Instant>,
    ready_at: Option<Instant>,
}
impl Port for FanPort {
    fn transactions(&self) -> &Transactions {
        &self.io.transactions
    }
    fn verify(&mut self, limit: Limit<'_>) -> io::Result<()> {
        limit.check()?;
        let call = self.io.verifies.fetch_add(1, Ordering::AcqRel) + 1;
        if let Some((slow, pause)) = *self.io.slow_verify.lock().unwrap()
            && call == slow
        {
            std::thread::sleep(pause);
        }
        if call == self.io.fail_verify.load(Ordering::Acquire) {
            return Err(blocked("the opened HID identity no longer verifies"));
        }
        Ok(())
    }
    fn submit(&mut self, transfer: Transfer, limit: Limit<'_>) -> io::Result<()> {
        limit.check()?;
        assert!(self.pending.is_none());
        self.pending = match transfer {
            Transfer::SendGetter(frame) => {
                let (transaction, class, id) = (frame[2], frame[7], frame[8]);
                let payload = frame[9..9 + usize::from(frame[6])].to_vec();
                self.io
                    .sent
                    .lock()
                    .unwrap()
                    .push((class, id, payload.clone()));
                self.query_sent = None;
                self.mcu_sent = false;
                self.reply = match (class, id) {
                    (CLASS, GET_THERMAL) => Some((
                        FRAME_LENGTH,
                        super::tests::reply(
                            transaction,
                            &[1, payload[1], self.io.thermal.load(Ordering::Acquire), 0],
                        ),
                    )),
                    (CLASS, 0x88) => {
                        let hold = self.io.hold_query.lock().unwrap().take();
                        if let Some(hold) = hold {
                            let _ = hold.recv();
                        }
                        self.io.query_ids.lock().unwrap().push(transaction);
                        self.query_sent = Some(Instant::now());
                        let index = usize::from(payload[1]) - 1;
                        let (status, body) = self.io.replies.lock().unwrap()[index]
                            .clone()
                            .unwrap_or((2, vec![1, payload[1], self.io.units[index]]));
                        let mut reply = [0; FRAME_LENGTH];
                        let report = RazerReport::create(transaction, CLASS, 0x88, &body).unwrap();
                        reply[1..].copy_from_slice(&report.encode());
                        reply[1] = status;
                        Some((FRAME_LENGTH, reply))
                    }
                    (0, 0x87) => {
                        let hold = self.io.hold_mcu.lock().unwrap().take();
                        if let Some(hold) = hold {
                            let _ = hold.recv();
                        }
                        self.mcu_sent = true;
                        self.io.mcu.lock().unwrap().clone().map(|(status, body)| {
                            let mut reply = [0; FRAME_LENGTH];
                            let report = RazerReport::create(transaction, 0, 0x87, &body).unwrap();
                            reply[1..].copy_from_slice(&report.encode());
                            reply[1] = status;
                            if let Some((offset, byte)) = *self.io.mcu_edit.lock().unwrap() {
                                reply[offset] = byte;
                                if offset != CHECKSUM {
                                    reply[CHECKSUM] =
                                        openblade_protocol::calculate_checksum(&reply[1..])
                                            .unwrap();
                                }
                            }
                            (FRAME_LENGTH, reply)
                        })
                    }
                    // Normal mode, for a job that also asks the Driver mode.
                    (0, 0x84) => {
                        let mut reply = [0; FRAME_LENGTH];
                        let report = RazerReport::create(transaction, 0, 0x84, &[0, 0]).unwrap();
                        reply[1..].copy_from_slice(&report.encode());
                        reply[1] = 2;
                        Some((FRAME_LENGTH, reply))
                    }
                    _ => None,
                };
                Some(Ok((0, frame)))
            }
            #[cfg(feature = "service-thermal-mutation-v1")]
            Transfer::SendMutation(_) => panic!("read-only fixture received a mutation"),
            Transfer::ReceiveReply => {
                if let Some(sent) = self.query_sent {
                    self.io.settles.lock().unwrap().push(sent.elapsed());
                }
                if self.query_sent.is_some()
                    && let Some(delay) = self.io.first_get_delay.lock().unwrap().take()
                {
                    self.ready_at = Some(Instant::now() + delay);
                }
                if self.query_sent.is_some() && self.io.stall.load(Ordering::Acquire)
                    || self.mcu_sent && self.io.stall_mcu.load(Ordering::Acquire)
                {
                    // Outstanding until the exchange's deadline ends the wait.
                    None
                } else {
                    Some(
                        self.reply
                            .take()
                            .ok_or_else(|| io::ErrorKind::UnexpectedEof.into()),
                    )
                }
            }
        };
        Ok(())
    }
    fn poll(&mut self) -> Option<io::Result<(usize, Frame)>> {
        if self.ready_at.is_some_and(|ready| Instant::now() < ready) {
            return None;
        }
        self.ready_at = None;
        self.pending.take()
    }
    fn cancel(&mut self) {}
    fn poll_retirement(&mut self) -> io::Result<bool> {
        self.pending.take();
        Ok(true)
    }
}
pub(super) fn port(io: &Arc<FanIo>) -> FanPort {
    FanPort {
        io: io.clone(),
        mcu_sent: false,
        reply: None,
        pending: None,
        query_sent: None,
        ready_at: None,
    }
}

/// The fan controller behind the real Worker and the shared `Reader::job`, on
/// a paused job clock: the same reads, retirement and fresh admission check
/// as the native worker.
#[cfg_attr(not(feature = "service-live-readings-v1"), allow(dead_code))]
pub(crate) fn fan_io_worker(
    io: Arc<FanIo>,
    authorize: impl Fn() -> Option<ReadAdmission> + Send + Sync + 'static,
) -> Worker<ReadAdmission> {
    Worker::new(
        move |job| {
            let Some(admission) = authorize() else {
                return Ok(None);
            };
            let mut reader = Reader::new(port(&io), admission);
            tokio::runtime::Builder::new_current_thread()
                .enable_time()
                .start_paused(true)
                .build()?
                .block_on(reader.job(&mut || authorize(), &job, true))
        },
        None,
    )
}

pub(super) const ADMISSION: ReadAdmission = ReadAdmission {
    lifecycle: 1,
    generation: [7; 32],
};

struct Outcome {
    thermal: Option<ObservedPerformance>,
    fans: FanRpmRead,
    /// Pairs counted as sent (`FanPairSends`).
    pairs: u64,
    sealed: bool,
    /// Job time left when the queries ended.
    left: Duration,
    /// A second thermal observation on the same Reader, and what it sent.
    again: Option<ObservedPerformance>,
    again_sent: usize,
}

/// The thermal read, the fan pair and a second thermal read on one fresh
/// Reader within `budget`.
fn reader_job(io: &Arc<FanIo>, budget: Duration) -> Outcome {
    let cancelled = AtomicBool::new(false);
    let sends = FanPairSends::default();
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
            let fans = reader
                .read_fan_rpm(&mut || Some(ADMISSION), limit, &sends)
                .await;
            let sealed = reader.poisoned;
            let left = limit.deadline.saturating_duration_since(Instant::now());
            let before = io.sent.lock().unwrap().len();
            io.fail_verify.store(0, Ordering::Release);
            let again = reader.observe(&mut || Some(ADMISSION), limit).await;
            let again_sent = io.sent.lock().unwrap().len() - before;
            Outcome {
                thermal,
                fans,
                pairs: sends.count(),
                sealed,
                left,
                again,
                again_sent,
            }
        })
}

fn two_seconds(io: &Arc<FanIo>) -> Outcome {
    reader_job(io, Duration::from_secs(2))
}

#[test]
fn a_confirmed_pair_reads_both_selectors_in_order_and_leaves_the_reader_open() {
    let io = FanIo::new([0x20, 0x1e]);
    let job = two_seconds(&io);
    assert_eq!(job.fans, FanRpmRead::Confirmed([3200, 3000]));
    assert_eq!(job.pairs, 1);
    assert!(job.thermal.is_some());
    assert!(!job.sealed);
    assert!(job.again.is_some());
    assert_eq!(job.again_sent, 2);
    assert_eq!(io.queries(), [vec![1, 1, 0], vec![1, 2, 0]]);
    // The thermal pair was [1, 2]; the fan pair reserved and sent [3, 4].
    assert_eq!(*io.query_ids.lock().unwrap(), [3, 4]);
    assert!(io.settles.lock().unwrap().iter().all(|s| *s >= SETTLE));
}

/// Managed returns no reading unless both selectors decode. A refused first
/// reply sends no second query; either refusal seals the Reader and keeps
/// the thermal read.
#[test]
fn an_unconfirmed_selector_seals_the_reader_and_confirms_neither_speed() {
    for (index, reply, queries) in [
        (0, (2, vec![1, 2, 0x20]), 1),
        (1, (2, vec![0, 2, 0x1e]), 2),
        (1, (3, vec![1, 2, 0x1e]), 2),
        (0, (2, vec![1, 1]), 1),
    ] {
        let io = FanIo::new([0x20, 0x1e]);
        io.replies.lock().unwrap()[index] = Some(reply.clone());
        let job = two_seconds(&io);
        assert_eq!(job.fans, FanRpmRead::Unconfirmed, "{reply:?}");
        assert_eq!(job.pairs, 1, "{reply:?}");
        assert!(job.thermal.is_some());
        assert!(job.sealed);
        assert!(job.again.is_none());
        assert_eq!(job.again_sent, 0);
        assert_eq!(io.queries().len(), queries, "{reply:?}");
    }
}

/// Each checkpoint of each query stops on a lost identity: the first query is
/// not sent, then each GET is not read, and no speed is confirmed. The pair
/// counts as sent only once its first SET went out.
#[test]
fn each_query_checkpoint_stops_on_a_lost_identity() {
    let thermal = FanIo::new([0x20, 0x1e]);
    let cancelled = AtomicBool::new(false);
    let limit = Limit {
        deadline: Instant::now() + Duration::from_secs(2),
        cancelled: &cancelled,
    };
    let mut reader = Reader::new(port(&thermal), ADMISSION);
    tokio::runtime::Builder::new_current_thread()
        .enable_time()
        .start_paused(true)
        .build()
        .unwrap()
        .block_on(reader.observe(&mut || Some(ADMISSION), limit))
        .unwrap();
    let before = thermal.verifies.load(Ordering::Acquire);
    assert!(before > 0);
    // Entry, GET 1, after decode 1, GET 2, after decode 2.
    for (check, queries, gets) in [(1, 0, 0), (2, 1, 0), (3, 1, 1), (4, 2, 1), (5, 2, 2)] {
        let io = FanIo::new([0x20, 0x1e]);
        io.fail_verify.store(before + check, Ordering::Release);
        let job = two_seconds(&io);
        assert_eq!(job.fans, FanRpmRead::Unconfirmed, "{check}");
        assert_eq!(job.pairs, u64::from(queries > 0), "{check}");
        assert!(job.thermal.is_some(), "{check}");
        assert!(job.sealed, "{check}");
        assert_eq!(io.queries().len(), queries, "{check}");
        assert_eq!(io.settles.lock().unwrap().len(), gets, "{check}");
    }
}

/// A query whose GET never completes ends at the pair's own one-second
/// budget, keeping the reserve for retirement and the final checks.
#[test]
fn a_stalled_query_ends_at_its_own_budget() {
    let limit = Duration::from_secs(5);
    let io = FanIo::new([0x20, 0x1e]);
    let confirmed = reader_job(&io, limit);
    let stalled_io = FanIo::new([0x20, 0x1e]);
    stalled_io.stall.store(true, Ordering::Release);
    let stalled = reader_job(&stalled_io, limit);
    assert_eq!(stalled.fans, FanRpmRead::Unconfirmed);
    assert!(stalled.thermal.is_some());
    assert_eq!(stalled_io.settles.lock().unwrap().len(), 1);
    let spent = confirmed.left - stalled.left;
    assert!(spent < Duration::from_secs(1), "{spent:?}");
    assert!(stalled.left >= Duration::from_secs(3), "{:?}", stalled.left);
    let short = FanIo::new([0x20, 0x1e]);
    short.stall.store(true, Ordering::Release);
    let job = reader_job(&short, Duration::from_millis(1200));
    assert_eq!(job.fans, FanRpmRead::Unconfirmed);
    assert!(job.left >= Duration::from_millis(500), "{:?}", job.left);
}

/// Without more than the reserve left, or on a sealed Reader, nothing is sent.
#[test]
fn a_job_without_time_or_on_a_sealed_reader_sends_no_query() {
    let io = FanIo::new([0x20, 0x1e]);
    let job = reader_job(&io, Duration::from_millis(500));
    assert!(job.thermal.is_some());
    assert_eq!(job.fans, FanRpmRead::NotRequested);
    assert_eq!(job.pairs, 0);
    assert!(io.queries().is_empty());
    let cancelled = AtomicBool::new(false);
    let sealed = FanIo::new([0x20, 0x1e]);
    let mut reader = Reader::new(port(&sealed), ADMISSION);
    reader.seal();
    let fans = tokio::runtime::Builder::new_current_thread()
        .enable_time()
        .start_paused(true)
        .build()
        .unwrap()
        .block_on(reader.read_fan_rpm(
            &mut || Some(ADMISSION),
            Limit {
                deadline: Instant::now() + Duration::from_secs(2),
                cancelled: &cancelled,
            },
            &FanPairSends::default(),
        ));
    assert_eq!(fans, FanRpmRead::NotRequested);
    assert!(sealed.sent.lock().unwrap().is_empty());
}

/// The pair alone on a fresh Reader with `budget` beyond the reserve, and
/// the pairs counted as sent.
fn fan_pair(io: &Arc<FanIo>, budget: Duration) -> (FanRpmRead, u64) {
    let cancelled = AtomicBool::new(false);
    let sends = FanPairSends::default();
    let mut reader = Reader::new(port(io), ADMISSION);
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
                .read_fan_rpm(&mut || Some(ADMISSION), limit, &sends)
                .await;
            (read, sends.count())
        })
}

/// No SET goes out unless the pair's budget covers both exchanges, each a
/// SET, the settle and a GET: a SET without that time would likely time out
/// and quarantine its ID for the process.
#[test]
fn the_pair_is_sent_only_when_its_budget_covers_both_exchanges() {
    let pair = EXCHANGE.saturating_mul(2);
    let io = FanIo::new([0x20, 0x1e]);
    assert_eq!(
        fan_pair(&io, pair - Duration::from_millis(1)),
        (FanRpmRead::NotRequested, 0)
    );
    assert!(io.sent.lock().unwrap().is_empty());
    let io = FanIo::new([0x20, 0x1e]);
    assert_eq!(
        fan_pair(&io, pair),
        (FanRpmRead::Confirmed([3200, 3000]), 1)
    );
    assert_eq!(io.queries().len(), 2);
}

/// A first exchange that leaves less than one exchange of budget leaves the
/// second selector unsent and its ID free; the pair confirms nothing.
#[test]
fn a_long_first_exchange_leaves_the_second_query_unsent() {
    let pair = EXCHANGE.saturating_mul(2);
    // The first exchange spends the settle and this delay.
    let spare = pair - EXCHANGE - SETTLE;
    for (delay, expected, queries) in [
        (spare, FanRpmRead::Confirmed([3200, 3000]), 2),
        (spare + Duration::from_millis(5), FanRpmRead::Unconfirmed, 1),
    ] {
        let io = FanIo::new([0x20, 0x1e]);
        *io.first_get_delay.lock().unwrap() = Some(delay);
        assert_eq!(fan_pair(&io, pair), (expected, 1), "{delay:?}");
        assert_eq!(io.queries().len(), queries, "{delay:?}");
        assert_eq!(io.queries()[0][1], 1);
    }
}

/// Through the shared job body: only a job that asks sends the pair, after
/// the thermal getters.
#[cfg(feature = "service-live-readings-v1")]
#[test]
fn the_shared_job_sends_the_pair_only_when_asked() {
    for (fan_rpm, expected, sent) in [
        (false, FanRpmRead::NotRequested, 2),
        (true, FanRpmRead::Confirmed([3200, 0]), 4),
    ] {
        let io = FanIo::new([0x20, 0]);
        let worker = fan_io_worker(io.clone(), || Some(ADMISSION));
        let queries = Queries {
            fan_rpm: fan_rpm.then(FanPairSends::default),
            mcu_version: None,
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
        assert_eq!(reading.fan_rpm, expected);
        assert_eq!(io.sent.lock().unwrap().len(), sent);
        assert!(
            io.sent.lock().unwrap()[..2]
                .iter()
                .all(|(_, id, _)| *id == GET_THERMAL)
        );
    }
}
