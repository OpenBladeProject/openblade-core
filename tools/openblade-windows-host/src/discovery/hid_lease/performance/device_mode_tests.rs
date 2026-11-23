//! A fake keyboard behind the real Reader, and the Reader-level properties of
//! the 00/84 query: its checkpoints, settle, transaction pair, budget and
//! sealing.
use super::{worker::Worker, *};
use openblade_protocol::device_mode::DeviceMode;
use std::sync::{
    Arc, Mutex,
    atomic::{AtomicUsize, Ordering},
};

/// A keyboard answering the paired thermal getters (Balanced, Automatic) and
/// the 00/84 query. Each query reply is a whole managed report with the row's
/// status and payload, in the (length, report ID) `envelope`; `None` returns
/// no reply and `stall` never completes the query's GET. Verification fails
/// at the 1-based `fail_verify` call (0 never). Every submitted report is
/// recorded as (class, ID, payload), each query's transaction ID in
/// `query_ids` and the time from its send to its GET in `settles`.
/// Retirement completes `retirement` after it starts, on the job clock, and
/// then sets `retired`.
pub(crate) struct KeyboardIo {
    transactions: Transactions,
    pub(crate) mode: Mutex<Option<(u8, Vec<u8>)>>,
    pub(crate) envelope: Mutex<(usize, u8)>,
    pub(crate) stall: AtomicBool,
    pub(crate) fail_verify: AtomicUsize,
    pub(crate) verifies: AtomicUsize,
    pub(crate) sent: Mutex<Vec<(u8, u8, Vec<u8>)>>,
    pub(crate) query_ids: Mutex<Vec<u8>>,
    pub(crate) settles: Mutex<Vec<Duration>>,
    pub(crate) retirement: Mutex<Duration>,
    pub(crate) retired: AtomicBool,
}
impl KeyboardIo {
    pub(crate) fn new(mode: Option<(u8, Vec<u8>)>) -> Arc<Self> {
        Arc::new(Self {
            transactions: Transactions::new(),
            mode: Mutex::new(mode),
            envelope: Mutex::new((FRAME_LENGTH, 0)),
            stall: AtomicBool::new(false),
            fail_verify: AtomicUsize::new(0),
            verifies: AtomicUsize::new(0),
            sent: Mutex::default(),
            query_ids: Mutex::default(),
            settles: Mutex::default(),
            retirement: Mutex::default(),
            retired: AtomicBool::new(false),
        })
    }
    pub(crate) fn queries(&self) -> usize {
        let sent = self.sent.lock().unwrap();
        sent.iter().filter(|(class, ..)| *class == 0).count()
    }
}
pub(super) struct KeyboardPort {
    io: Arc<KeyboardIo>,
    reply: Option<(usize, Frame)>,
    pending: Option<io::Result<(usize, Frame)>>,
    query_sent: Option<Instant>,
    retiring: Option<Instant>,
}
impl KeyboardPort {
    pub(super) fn new(io: Arc<KeyboardIo>) -> Self {
        Self {
            io,
            reply: None,
            pending: None,
            query_sent: None,
            retiring: None,
        }
    }
}
impl Port for KeyboardPort {
    fn transactions(&self) -> &Transactions {
        &self.io.transactions
    }
    fn verify(&mut self, limit: Limit<'_>) -> io::Result<()> {
        limit.check()?;
        let call = self.io.verifies.fetch_add(1, Ordering::AcqRel) + 1;
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
                self.reply = match (class, id) {
                    (CLASS, GET_THERMAL) => Some((
                        FRAME_LENGTH,
                        super::tests::reply(transaction, &[1, payload[1], 0, 0]),
                    )),
                    (0, 0x84) => {
                        self.io.query_ids.lock().unwrap().push(transaction);
                        self.query_sent = Some(Instant::now());
                        self.io.mode.lock().unwrap().clone().map(|(status, body)| {
                            let mut reply = [0; FRAME_LENGTH];
                            let report = RazerReport::create(transaction, 0, 0x84, &body).unwrap();
                            reply[1..].copy_from_slice(&report.encode());
                            reply[1] = status;
                            let (length, id) = *self.io.envelope.lock().unwrap();
                            reply[0] = id;
                            (length, reply)
                        })
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
                if self.query_sent.is_some() && self.io.stall.load(Ordering::Acquire) {
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
        self.pending.take()
    }
    fn cancel(&mut self) {}
    fn poll_retirement(&mut self) -> io::Result<bool> {
        let started = *self.retiring.get_or_insert_with(Instant::now);
        if Instant::now() < started + *self.io.retirement.lock().unwrap() {
            return Ok(false);
        }
        self.pending.take();
        self.io.retired.store(true, Ordering::Release);
        Ok(true)
    }
}

/// The keyboard behind the real Worker and the shared `Reader::job`, on a
/// paused job clock: the same reads, retirement and fresh admission check as
/// the native worker.
pub(crate) fn keyboard_io_worker(
    io: Arc<KeyboardIo>,
    authorize: impl Fn() -> Option<ReadAdmission> + Send + Sync + 'static,
) -> Worker<ReadAdmission> {
    Worker::new(
        move |job| {
            let Some(admission) = authorize() else {
                return Ok(None);
            };
            let mut reader = Reader::new(KeyboardPort::new(io.clone()), admission);
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
const DRIVER: (u8, [u8; 2]) = (2, [3, 0]);
// A correlated FAILED status: a terminal reply that confirms no mode.
const FAILED: (u8, [u8; 2]) = (3, [3, 0]);

struct Outcome {
    result: Option<(ObservedPerformance, DeviceModeRead)>,
    sealed: bool,
    /// Job time left when the job ended.
    left: Duration,
    /// A second thermal observation on the same Reader, and what it sent.
    again: Option<ObservedPerformance>,
    again_sent: usize,
}

/// One `observe_job` on a fresh Reader with a two-second limit, then a second
/// thermal observation on that Reader.
fn reader_job(io: &Arc<KeyboardIo>, device_mode: bool) -> Outcome {
    reader_job_within(io, device_mode, Duration::from_secs(2))
}

fn reader_job_within(io: &Arc<KeyboardIo>, device_mode: bool, budget: Duration) -> Outcome {
    let cancelled = AtomicBool::new(false);
    let mut reader = Reader::new(KeyboardPort::new(io.clone()), ADMISSION);
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
            let result = reader
                .observe_job(&mut || Some(ADMISSION), limit, device_mode)
                .await;
            let sealed = reader.poisoned;
            let left = limit.deadline.saturating_duration_since(Instant::now());
            let before = io.sent.lock().unwrap().len();
            io.fail_verify.store(0, Ordering::Release);
            let again = reader.observe(&mut || Some(ADMISSION), limit).await;
            let again_sent = io.sent.lock().unwrap().len() - before;
            Outcome {
                result,
                sealed,
                left,
                again,
                again_sent,
            }
        })
}

fn mode(job: &Outcome) -> Option<DeviceModeRead> {
    job.result.map(|(_, mode)| mode)
}

fn keyboard(reply: (u8, [u8; 2])) -> Arc<KeyboardIo> {
    KeyboardIo::new(Some((reply.0, reply.1.to_vec())))
}

#[test]
fn a_confirmed_query_leaves_the_reader_open_and_a_failed_one_seals_it() {
    let confirmed = reader_job(&keyboard(DRIVER), true);
    assert_eq!(
        mode(&confirmed),
        Some(DeviceModeRead::Confirmed(DeviceMode::Driver))
    );
    assert!(!confirmed.sealed);
    assert!(confirmed.again.is_some());
    assert_eq!(confirmed.again_sent, 2);
    // A failed query keeps the thermal read, seals the Reader, and the same
    // Reader sends nothing more.
    let failed = reader_job(&keyboard(FAILED), true);
    assert!(failed.result.is_some());
    assert_eq!(mode(&failed), Some(DeviceModeRead::Unconfirmed));
    assert!(failed.sealed);
    assert!(failed.again.is_none());
    assert_eq!(failed.again_sent, 0);
}

/// The query's entry, pre-GET and post-decode checkpoints each stop on a lost
/// identity: no query, no GET, then no confirmed mode.
#[test]
fn each_query_checkpoint_stops_on_a_lost_identity() {
    let thermal = keyboard(DRIVER);
    assert_eq!(
        mode(&reader_job(&thermal, false)),
        Some(DeviceModeRead::NotRequested)
    );
    // Both thermal observations of that job made the same checks.
    let before = thermal.verifies.load(Ordering::Acquire) / 2;
    assert!(before > 0);
    for (check, queries, gets) in [(1, 0, 0), (2, 1, 0), (3, 1, 1)] {
        let io = keyboard(DRIVER);
        io.fail_verify.store(before + check, Ordering::Release);
        let job = reader_job(&io, true);
        assert_eq!(mode(&job), Some(DeviceModeRead::Unconfirmed), "{check}");
        assert!(job.result.is_some(), "{check}");
        assert!(job.sealed, "{check}");
        assert_eq!(io.queries(), queries, "{check}");
        assert_eq!(io.settles.lock().unwrap().len(), gets, "{check}");
    }
}

/// The GET waits the managed 30 ms settle, and the query reserves a whole pair
/// and sends only its first ID.
#[test]
fn the_query_settles_and_sends_only_the_first_reserved_id() {
    let io = keyboard(DRIVER);
    let job = reader_job(&io, true);
    assert_eq!(
        mode(&job),
        Some(DeviceModeRead::Confirmed(DeviceMode::Driver))
    );
    assert!(io.settles.lock().unwrap()[0] >= SETTLE);
    // The thermal pair was [1, 2]; the query reserved [3, 4] and sent 3.
    assert_eq!(*io.query_ids.lock().unwrap(), [3]);
    assert_eq!(io.transactions.reserve_pair(), Some([7, 8]));
}

/// A query whose GET never completes ends at its own budget, keeping the
/// reserve for retirement and the final checks, and keeps the thermal read.
#[test]
fn a_stalled_query_ends_at_its_own_budget_and_keeps_the_thermal_read() {
    let io = keyboard(DRIVER);
    io.stall.store(true, Ordering::Release);
    let job = reader_job(&io, true);
    assert!(job.result.is_some());
    assert_eq!(mode(&job), Some(DeviceModeRead::Unconfirmed));
    assert_eq!(io.settles.lock().unwrap().len(), 1);
    assert!(job.left >= Duration::from_millis(500), "{:?}", job.left);
}

/// The thermal read alone, and no query, when asked for none.
#[test]
fn a_job_that_is_not_asked_sends_no_query() {
    let io = keyboard(DRIVER);
    let job = reader_job(&io, false);
    assert_eq!(mode(&job), Some(DeviceModeRead::NotRequested));
    assert_eq!(io.queries(), 0);
    assert_eq!(io.sent.lock().unwrap().len(), 4);
}

/// With time beyond it, a stalled query waits exactly its own one-second
/// budget after the thermal read, not the rest of the job.
#[test]
fn a_stalled_query_waits_exactly_its_one_second_budget() {
    let limit = Duration::from_secs(5);
    let thermal = reader_job_within(&keyboard(DRIVER), false, limit);
    let io = keyboard(DRIVER);
    io.stall.store(true, Ordering::Release);
    let stalled = reader_job_within(&io, true, limit);
    assert_eq!(mode(&stalled), Some(DeviceModeRead::Unconfirmed));
    let spent = thermal.left - stalled.left;
    assert!(spent >= Duration::from_secs(1), "{spent:?}");
    assert!(spent < Duration::from_secs(1) + 2 * POLL, "{spent:?}");
}

/// A job with no more than the query's reserve left after its thermal read
/// keeps that read and sends no query, so the mode stays not requested and
/// the cadence keeps it due.
#[test]
fn a_job_without_time_for_the_query_sends_none() {
    let io = keyboard(DRIVER);
    let job = reader_job_within(&io, true, Duration::from_millis(500));
    assert!(job.result.is_some());
    assert_eq!(mode(&job), Some(DeviceModeRead::NotRequested));
    assert_eq!(io.queries(), 0);
}

/// The setter's pace check charges the baseline again and the readback's
/// trailing checkpoint at the baseline's own, from its reply to now, which no
/// reply-to-reply exchange includes.
#[cfg(feature = "service-keyboard-driver-mode-v1")]
#[test]
fn the_setter_charges_the_trailing_checkpoint() {
    let replied = tokio::time::Instant::now();
    let exchanges = super::Exchanges {
        last: Some(replied),
        slowest: Duration::from_millis(100),
        second: Duration::ZERO,
    };
    let fixed = super::device_mode::setter_fixed(&exchanges, replied + Duration::from_millis(40));
    assert_eq!(fixed, Duration::from_millis(140));
}
