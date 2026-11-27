//! The lane's gates over a fake collection: each refusal sends nothing, the
//! matrix I/O is held from issue to completion, and a late completion
//! releases it without a further request.
use super::*;

/// The fake collection's state, shared with the test.
#[derive(Default)]
struct Device {
    open: bool,
    opens: usize,
    closes: usize,
    refuse_open: bool,
    /// The handle describes another collection.
    foreign: bool,
    refuse_issue: bool,
    issued: Vec<MatrixReport>,
    outstanding: bool,
    /// The outstanding report's result, once the test completes it.
    completion: Option<bool>,
    cancels: usize,
    /// Advances this token while the report is prepared, as a retire racing
    /// the frame does.
    recall: Option<Arc<AtomicU64>>,
    /// Advances this token while the collection opens.
    recall_on_open: Option<Arc<AtomicU64>>,
    /// An open waits, after setting `opening`, until this is released or the
    /// lane cancels it, as a slow selection would.
    open_gate: Option<Arc<AtomicBool>>,
    opening: bool,
    /// Whether an open saw the lane's cancel flag.
    open_cancelled: bool,
    /// The issue moves the lane's clock past every deadline.
    expire_on_issue: bool,
    /// How far the lane's clock is ahead of the real one.
    clock: Duration,
}

#[derive(Clone, Default)]
struct Fake(Arc<Mutex<Device>>);

impl Fake {
    fn with(&self, change: impl FnOnce(&mut Device)) {
        change(&mut self.0.lock().unwrap());
    }
    fn read<T>(&self, read: impl FnOnce(&Device) -> T) -> T {
        read(&self.0.lock().unwrap())
    }
    fn complete(&self, completed: bool) {
        self.with(|d| d.completion = Some(completed));
    }
}

impl MatrixIo for Fake {
    fn open(&mut self, cancelled: &AtomicBool) -> bool {
        if let Some(gate) = self.read(|d| d.open_gate.clone()) {
            self.with(|d| d.opening = true);
            while !gate.load(Ordering::Acquire) {
                if cancelled.load(Ordering::SeqCst) {
                    self.with(|d| d.open_cancelled = true);
                    return false;
                }
                std::thread::sleep(Duration::from_millis(1));
            }
        }
        let mut d = self.0.lock().unwrap();
        d.opens += 1;
        d.open = !d.refuse_open;
        if let Some(recall) = &d.recall_on_open {
            recall.fetch_add(1, Ordering::SeqCst);
        }
        d.open
    }
    fn is_open(&self) -> bool {
        self.read(|d| d.open)
    }
    fn describes(&self) -> bool {
        self.read(|d| !d.foreign)
    }
    fn prepare(&mut self, _: &MatrixReport) -> bool {
        if let Some(recall) = self.read(|d| d.recall.clone()) {
            recall.fetch_add(1, Ordering::SeqCst);
        }
        true
    }
    fn issue(&mut self) -> bool {
        let mut d = self.0.lock().unwrap();
        if d.refuse_issue {
            return false;
        }
        d.issued
            .push([7; openblade_protocol::lighting::matrix::FEATURE_REPORT_LENGTH]);
        d.outstanding = true;
        if d.expire_on_issue {
            d.clock += Duration::from_secs(3600);
        }
        true
    }
    fn poll(&mut self) -> Option<bool> {
        let mut d = self.0.lock().unwrap();
        if !d.outstanding {
            return None;
        }
        let completed = d.completion.take()?;
        d.outstanding = false;
        Some(completed)
    }
    fn outstanding(&self) -> bool {
        self.read(|d| d.outstanding)
    }
    fn cancel(&mut self) {
        self.with(|d| d.cancels += 1);
    }
    fn close(&mut self) {
        self.with(|d| {
            assert!(!d.outstanding, "closed with a report outstanding");
            d.open = false;
            d.closes += 1;
        });
    }
    fn now(&self) -> Instant {
        Instant::now() + self.read(|d| d.clock)
    }
}

const REPORT: MatrixReport = [0; openblade_protocol::lighting::matrix::FEATURE_REPORT_LENGTH];

fn flag() -> &'static AtomicBool {
    Box::leak(Box::new(AtomicBool::new(false)))
}
fn lane(fake: &Fake, matrix_io: &'static AtomicBool) -> (Lane<Fake>, Arc<AtomicU64>) {
    let epoch = Arc::new(AtomicU64::new(0));
    let cancel = Arc::new(AtomicBool::new(false));
    (
        Lane::new(fake.clone(), matrix_io, epoch.clone(), cancel),
        epoch,
    )
}
fn soon() -> Instant {
    Instant::now() + Duration::from_secs(30)
}
fn issued(fake: &Fake) -> usize {
    fake.read(|d| d.issued.len())
}
/// Releases a slow open's gate when dropped, so a failed test ends the
/// lane's thread too.
struct Gate(Arc<AtomicBool>);
impl Drop for Gate {
    fn drop(&mut self) {
        self.0.store(true, Ordering::Release);
    }
}
/// Waits, for at most 30 s, until the lane is inside a slow open.
fn until_opening(fake: &Fake) {
    let until = Instant::now() + Duration::from_secs(30);
    while !fake.read(|d| d.opening) && Instant::now() < until {
        std::thread::sleep(Duration::from_millis(1));
    }
    assert!(fake.read(|d| d.opening), "the lane opens");
}

#[test]
fn a_completed_frame_holds_the_matrix_io_only_while_outstanding() {
    let fake = Fake::default();
    let matrix_io = flag();
    let (mut lane, _) = lane(&fake, matrix_io);
    fake.complete(true);
    assert!(lane.frame(&REPORT, soon(), 0).is_ok());
    assert_eq!((issued(&fake), fake.read(|d| d.opens)), (1, 1));
    assert!(!matrix_io.load(Ordering::Acquire));
    // A failed completion may have reached the keyboard.
    fake.complete(false);
    assert!(matches!(
        lane.frame(&REPORT, soon(), 0),
        Err(Unwritten::InDoubt)
    ));
    assert!(!matrix_io.load(Ordering::Acquire));
    assert_eq!(fake.read(|d| d.opens), 1, "the lease is kept");
}

#[test]
fn a_handle_describing_another_collection_sends_nothing_and_closes() {
    let fake = Fake::default();
    let (mut lane, _) = lane(&fake, flag());
    fake.with(|d| d.foreign = true);
    assert!(matches!(
        lane.frame(&REPORT, soon(), 0),
        Err(Unwritten::Unsent)
    ));
    assert_eq!(issued(&fake), 0);
    assert_eq!(fake.read(|d| (d.open, d.closes)), (false, 1));
}

#[test]
fn matrix_io_held_elsewhere_sends_nothing() {
    let fake = Fake::default();
    let matrix_io = flag();
    let (mut lane, _) = lane(&fake, matrix_io);
    matrix_io.store(true, Ordering::Release);
    assert!(matches!(
        lane.frame(&REPORT, soon(), 0),
        Err(Unwritten::Unsent)
    ));
    assert_eq!(issued(&fake), 0);
    assert!(
        matrix_io.load(Ordering::Acquire),
        "the other holder keeps it"
    );
}

#[test]
fn a_recalled_frame_sends_nothing_even_at_the_last_check() {
    let fake = Fake::default();
    let matrix_io = flag();
    let (mut lane, epoch) = lane(&fake, matrix_io);
    // Recalled before it reached the lane: nothing is opened either.
    epoch.store(1, Ordering::SeqCst);
    assert!(matches!(
        lane.frame(&REPORT, soon(), 0),
        Err(Unwritten::Unsent)
    ));
    assert_eq!(fake.read(|d| d.opens), 0);
    // Recalled after every other check passed: the token immediately before
    // the issue refuses it, and the matrix I/O is released.
    fake.with(|d| d.recall = Some(epoch.clone()));
    assert!(matches!(
        lane.frame(&REPORT, soon(), 1),
        Err(Unwritten::Unsent)
    ));
    assert_eq!(issued(&fake), 0);
    assert!(!matrix_io.load(Ordering::Acquire));
}

#[test]
fn an_expired_deadline_or_a_closed_collection_sends_nothing() {
    let fake = Fake::default();
    let (mut lane, _) = lane(&fake, flag());
    assert!(matches!(
        lane.frame(&REPORT, Instant::now(), 0),
        Err(Unwritten::Unsent)
    ));
    fake.with(|d| {
        d.open = false;
        d.refuse_open = true;
    });
    assert!(matches!(
        lane.frame(&REPORT, soon(), 0),
        Err(Unwritten::Unsent)
    ));
    assert_eq!(issued(&fake), 0);
}

#[test]
fn a_refused_issue_is_in_doubt_and_releases_the_matrix_io() {
    let fake = Fake::default();
    let matrix_io = flag();
    let (mut lane, _) = lane(&fake, matrix_io);
    fake.with(|d| d.refuse_issue = true);
    assert!(matches!(
        lane.frame(&REPORT, soon(), 0),
        Err(Unwritten::InDoubt)
    ));
    assert!(!matrix_io.load(Ordering::Acquire));
}

/// A report outstanding at its deadline is cancelled and in doubt, and
/// keeps the matrix I/O; nothing new is sent behind it, a retire waits for
/// it, and its completion releases the matrix I/O and closes the lease. One
/// that never completes keeps both.
#[test]
fn an_outstanding_report_keeps_the_matrix_io_until_it_completes() {
    let fake = Fake::default();
    let matrix_io = flag();
    let (mut lane, _) = lane(&fake, matrix_io);
    // The lane's clock passes the deadline at the issue.
    fake.with(|d| d.expire_on_issue = true);
    assert!(matches!(
        lane.frame(&REPORT, soon(), 0),
        Err(Unwritten::InDoubt)
    ));
    assert_eq!(fake.read(|d| d.cancels), 1);
    fake.with(|d| {
        d.expire_on_issue = false;
        d.clock = Duration::ZERO;
    });
    assert!(matrix_io.load(Ordering::Acquire));
    assert!(matches!(
        lane.frame(&REPORT, soon(), 0),
        Err(Unwritten::Unsent)
    ));
    assert_eq!(issued(&fake), 1, "nothing behind an outstanding report");
    assert!(!lane.settle_by(Instant::now() + Duration::from_millis(20)));
    lane.retire();
    assert_eq!(fake.read(|d| (d.open, d.closes)), (true, 0));
    assert!(matrix_io.load(Ordering::Acquire), "never completed: held");
    fake.complete(false);
    assert!(lane.settle());
    assert!(!matrix_io.load(Ordering::Acquire));
    assert_eq!(fake.read(|d| (d.open, d.closes)), (false, 1));
}

/// The lane's thread polls an outstanding report between requests: its late
/// completion releases the matrix I/O with no further request, and a retire
/// sent meanwhile closes the lease once it completes. Settle answers in
/// doubt until then. The lane's clock passes the frame's deadline at the
/// issue, so the report is outstanding at its deadline on any host.
#[test]
fn the_thread_releases_a_late_completion_without_a_request() {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_time()
        .build()
        .unwrap();
    let fake = Fake::default();
    fake.with(|d| d.expire_on_issue = true);
    let matrix_io = flag();
    let made = fake.clone();
    let stream = LaneStream::new(move || made.clone(), matrix_io);
    let within = |ms| tokio::time::Instant::now() + Duration::from_millis(ms);
    runtime.block_on(async {
        assert!(stream.settle(within(1)).await, "never started: settled");
        assert!(stream.open().await.is_ok());
        assert!(matches!(
            stream.frame(REPORT, within(30_000)).await,
            Err(Unwritten::InDoubt)
        ));
        assert_eq!(fake.read(|d| d.cancels), 1);
        assert!(matrix_io.load(Ordering::Acquire));
        stream.retire();
        // Past every deadline on the lane's clock: answered at once.
        assert!(!stream.settle(within(30_000)).await);
        fake.with(|d| d.clock = Duration::ZERO);
        fake.complete(true);
        let until = Instant::now() + Duration::from_secs(30);
        while matrix_io.load(Ordering::Acquire) && Instant::now() < until {
            std::thread::sleep(Duration::from_millis(1));
        }
        assert!(
            !matrix_io.load(Ordering::Acquire),
            "released without a request"
        );
        assert!(stream.settle(within(30_000)).await);
        assert_eq!(fake.read(|d| (d.open, d.closes)), (false, 1));
        // The next request opens the lease anew.
        assert!(stream.open().await.is_ok());
        assert_eq!(fake.read(|d| d.opens), 2);
    });
}

/// A retire while the collection opens cancels that open, and the frame
/// queued behind it was recalled: nothing is issued, even when the slow
/// selection would have succeeded.
#[test]
fn a_retire_cancels_an_open_in_progress_and_recalls_the_frame_behind_it() {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_time()
        .build()
        .unwrap();
    let fake = Fake::default();
    let gate = Gate(Arc::new(AtomicBool::new(false)));
    fake.with(|d| d.open_gate = Some(gate.0.clone()));
    let made = fake.clone();
    let stream = LaneStream::new(move || made.clone(), flag());
    runtime.block_on(async {
        let opening = stream.open();
        let frame = stream.frame(
            REPORT,
            tokio::time::Instant::now() + Duration::from_secs(30),
        );
        until_opening(&fake);
        stream.retire();
        let opened = tokio::time::timeout(Duration::from_secs(10), opening)
            .await
            .expect("the retire cancels the open");
        assert!(matches!(opened, Err(Unwritten::Unsent)));
        assert!(fake.read(|d| d.open_cancelled), "the open was cancelled");
        gate.0.store(true, Ordering::Release);
        assert!(matches!(frame.await, Err(Unwritten::Unsent)));
        assert_eq!(issued(&fake), 0);
        assert_eq!(fake.read(|d| d.opens), 0, "nothing was opened");
    });
}

/// An open recalled while the collection opened refuses, so a request the
/// runtime gave up on does not report the collection open.
#[test]
fn an_open_recalled_while_opening_refuses() {
    let fake = Fake::default();
    let (mut lane, epoch) = lane(&fake, flag());
    fake.with(|d| d.recall_on_open = Some(epoch.clone()));
    assert!(matches!(lane.open(0), Err(Unwritten::Unsent)));
    assert_eq!(fake.read(|d| d.opens), 1);
}

/// An open lease is confirmed only while its handle still describes the
/// selected collection; a stale one is closed and the open refuses.
#[test]
fn a_stale_open_lease_refuses_the_open_and_closes() {
    let fake = Fake::default();
    let (mut lane, _) = lane(&fake, flag());
    assert!(lane.open(0).is_ok());
    assert!(lane.open(0).is_ok(), "confirmed");
    fake.with(|d| d.foreign = true);
    assert!(matches!(lane.open(0), Err(Unwritten::Unsent)));
    assert_eq!(fake.read(|d| (d.open, d.opens, d.closes)), (false, 1, 1));
}

/// A stream whose request state is unreadable is not known to be settled.
#[test]
fn a_poisoned_stream_does_not_report_settled() {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_time()
        .build()
        .unwrap();
    let fake = Fake::default();
    let made = fake.clone();
    let stream = LaneStream::new(move || made.clone(), flag());
    let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let _requests = stream.requests.lock().unwrap();
        panic!("poisons the request state");
    }));
    let deadline = tokio::time::Instant::now() + Duration::from_secs(1);
    assert!(!runtime.block_on(stream.settle(deadline)));
}

/// A lane stuck in a slow open with its queue full refuses at once: a
/// further frame is unsent and never issued, and a settle reports that the
/// lane is not known to be settled. The retire that recalls the queued
/// frames still recalls them although it does not fit either.
#[test]
fn a_full_queue_refuses_a_frame_unsent_and_a_settle_unsettled() {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_time()
        .build()
        .unwrap();
    let fake = Fake::default();
    let gate = Gate(Arc::new(AtomicBool::new(false)));
    fake.with(|d| d.open_gate = Some(gate.0.clone()));
    let made = fake.clone();
    let stream = LaneStream::new(move || made.clone(), flag());
    let far = || tokio::time::Instant::now() + Duration::from_secs(30);
    runtime.block_on(async {
        let opening = stream.open();
        until_opening(&fake);
        let queued: Vec<_> = (0..QUEUE).map(|_| stream.frame(REPORT, far())).collect();
        let refused = tokio::time::timeout(Duration::from_secs(10), stream.frame(REPORT, far()))
            .await
            .expect("a full queue answers at once");
        assert!(matches!(refused, Err(Unwritten::Unsent)));
        assert!(!stream.settle(far()).await, "not known to be settled");
        stream.retire();
        let recalled = async {
            assert!(matches!(opening.await, Err(Unwritten::Unsent)));
            for frame in queued {
                assert!(matches!(frame.await, Err(Unwritten::Unsent)));
            }
        };
        tokio::time::timeout(Duration::from_secs(10), recalled)
            .await
            .expect("the retire cancels the open and recalls the queue");
        assert_eq!(issued(&fake), 0);
    });
}
