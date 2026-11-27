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
    fn open(&mut self) -> bool {
        let mut d = self.0.lock().unwrap();
        d.opens += 1;
        d.open = !d.refuse_open;
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
}

const REPORT: MatrixReport = [0; openblade_protocol::lighting::matrix::FEATURE_REPORT_LENGTH];

fn flag() -> &'static AtomicBool {
    Box::leak(Box::new(AtomicBool::new(false)))
}
fn lane(fake: &Fake, matrix_io: &'static AtomicBool) -> (Lane<Fake>, Arc<AtomicU64>) {
    let epoch = Arc::new(AtomicU64::new(0));
    (Lane::new(fake.clone(), matrix_io, epoch.clone()), epoch)
}
fn soon() -> Instant {
    Instant::now() + Duration::from_secs(30)
}
fn issued(fake: &Fake) -> usize {
    fake.read(|d| d.issued.len())
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
    let deadline = Instant::now() + Duration::from_millis(20);
    assert!(matches!(
        lane.frame(&REPORT, deadline, 0),
        Err(Unwritten::InDoubt)
    ));
    assert_eq!(fake.read(|d| d.cancels), 1);
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
/// doubt until then.
#[test]
fn the_thread_releases_a_late_completion_without_a_request() {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_time()
        .build()
        .unwrap();
    let fake = Fake::default();
    let matrix_io = flag();
    let made = fake.clone();
    let stream = LaneStream::new(move || made.clone(), matrix_io);
    let within = |ms| tokio::time::Instant::now() + Duration::from_millis(ms);
    runtime.block_on(async {
        assert!(stream.settle(within(1)).await, "never started: settled");
        assert!(stream.open().await.is_ok());
        assert!(matches!(
            stream.frame(REPORT, within(20)).await,
            Err(Unwritten::InDoubt)
        ));
        assert!(matrix_io.load(Ordering::Acquire));
        stream.retire();
        assert!(!stream.settle(within(30)).await);
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
