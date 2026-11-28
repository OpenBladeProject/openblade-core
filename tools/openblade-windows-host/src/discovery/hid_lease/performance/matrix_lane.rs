//! The animated matrix stream (`MatrixStream`) apart from its Win32 calls
//! (`MatrixIo`): one thread owns one lease on the exact reviewed matrix
//! collection and writes one 03/0B feature report at a time. Before each
//! report it checks, in order, that the request was not recalled, that the
//! collection is open and its handle still describes the selected
//! collection, that the frame's deadline has not passed, and that it holds
//! the process's matrix I/O (`MATRIX_IO`, which the render jobs' lease takes
//! too). The recall token (`epoch`) is checked once more immediately before
//! the report is issued: `MatrixStream::retire` advances it, so a frame the
//! runtime gave up on is never written late. A retire also cancels an open
//! in progress (`MatrixIo::open`'s flag), and an open it recalled refuses.
//!
//! A report holds the matrix I/O from its issue until its terminal
//! completion. One still outstanding at its frame's deadline is cancelled
//! and replied in doubt; the lane keeps polling it, between requests, until
//! it completes, and only that completion releases the matrix I/O and lets a
//! retired lease close. A report that never completes keeps both.
use super::{MatrixReport, MatrixStream, Pending, Unwritten};
use std::{
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, AtomicU64, Ordering},
        mpsc,
    },
    time::{Duration, Instant},
};

/// How often a frame polls its own report for completion.
const POLL: Duration = Duration::from_millis(1);
/// How often the lane polls a report left outstanding past its deadline,
/// for its first `SETTLE_BACKOFF`, and then at `SETTLE_POLL_SLOW`: a report
/// that has not completed by then is unlikely to complete soon.
const SETTLE_POLL: Duration = Duration::from_millis(10);
const SETTLE_BACKOFF: Duration = Duration::from_secs(1);
const SETTLE_POLL_SLOW: Duration = Duration::from_millis(100);
/// The lane's request queue. The runtime awaits each frame, but an open it
/// gave up on stays queued, and so does each retire, so open and retire pairs
/// accumulate while the lane is stuck in a slow open or a report. A full
/// queue refuses: an open or a frame is unsent, a settle is not known to be
/// settled, and a retire that does not fit has still recalled every frame
/// (`LaneStream::retire`).
const QUEUE: usize = 8;

/// The matrix collection's I/O for one lane: the Windows handle and its
/// overlapped 03/0B report, or a test's fake.
pub(crate) trait MatrixIo {
    /// Selects, opens and verifies the exact reviewed collection; `false`
    /// opened nothing. Setting `cancelled` abandons it.
    fn open(&mut self, cancelled: &AtomicBool) -> bool;
    fn is_open(&self) -> bool;
    /// Whether the open handle still describes the selected collection.
    fn describes(&self) -> bool;
    /// Builds `report`'s operation; `false` sent nothing.
    fn prepare(&mut self, report: &MatrixReport) -> bool;
    /// Issues the prepared report; `false` is the driver's refusal of a call
    /// that reached it.
    fn issue(&mut self) -> bool;
    /// The outstanding report's terminal result, once: `Some(true)` when it
    /// completed. `None` while it is pending or when none is outstanding.
    fn poll(&mut self) -> Option<bool>;
    fn outstanding(&self) -> bool;
    /// Requests cancellation of the outstanding report.
    fn cancel(&mut self);
    /// Closes the collection. Never called with a report outstanding.
    fn close(&mut self);
    /// The clock the lane's deadlines are compared on.
    fn now(&self) -> Instant {
        Instant::now()
    }
}

/// One lane's state over its I/O.
pub(crate) struct Lane<I: MatrixIo> {
    io: I,
    /// The process's matrix I/O (`MATRIX_IO`).
    matrix_io: &'static AtomicBool,
    /// The recall token `MatrixStream::retire` advances.
    epoch: Arc<AtomicU64>,
    /// The flag `MatrixStream::retire` sets to cancel an open in progress.
    cancel: Arc<AtomicBool>,
    /// Whether this lane holds `matrix_io` for its outstanding report.
    holds: bool,
    /// A retire waits for the outstanding report before the lease closes.
    closing: bool,
}

impl<I: MatrixIo> Lane<I> {
    pub(crate) fn new(
        io: I,
        matrix_io: &'static AtomicBool,
        epoch: Arc<AtomicU64>,
        cancel: Arc<AtomicBool>,
    ) -> Self {
        Self {
            io,
            matrix_io,
            epoch,
            cancel,
            holds: false,
            closing: false,
        }
    }

    fn current(&self, epoch: u64) -> bool {
        self.epoch.load(Ordering::SeqCst) == epoch
    }

    fn release(&mut self) {
        if self.holds {
            self.matrix_io.store(false, Ordering::Release);
            self.holds = false;
        }
    }

    /// Polls an outstanding report; `true` once none is. Its completion
    /// releases the matrix I/O and closes a retired lease.
    fn settle(&mut self) -> bool {
        if self.io.outstanding() && self.io.poll().is_none() {
            return false;
        }
        self.release();
        if self.closing {
            self.closing = false;
            if self.io.is_open() {
                self.io.close();
            }
        }
        true
    }

    /// Cancels an outstanding report and closes the lease once none is.
    fn retire(&mut self) {
        if self.io.outstanding() {
            self.io.cancel();
        }
        self.closing = true;
        self.settle();
    }

    /// Opens the collection, or confirms that the open one still describes
    /// the selected collection, for the request at `epoch`. A report still
    /// outstanding opens nothing; a handle that no longer describes it is
    /// closed; an open recalled meanwhile refuses.
    fn open(&mut self, epoch: u64) -> Result<(), Unwritten> {
        // Cleared before the token is read: `MatrixStream::retire` advances
        // the token before it sets the flag, so a retire this check misses
        // still cancels the open below.
        self.cancel.store(false, Ordering::SeqCst);
        if !self.current(epoch) || !self.settle() {
            return Err(Unwritten::Unsent);
        }
        if !self.io.is_open() && !self.io.open(&self.cancel) {
            return Err(Unwritten::Unsent);
        }
        // Checked on every open, a reused one included.
        if !self.io.describes() {
            self.retire();
            return Err(Unwritten::Unsent);
        }
        // Recalled while it opened: the retire that recalled it closes it.
        if self.current(epoch) {
            Ok(())
        } else {
            Err(Unwritten::Unsent)
        }
    }

    /// Writes `report` and waits for its completion until `deadline`.
    fn frame(
        &mut self,
        report: &MatrixReport,
        deadline: Instant,
        epoch: u64,
    ) -> Result<(), Unwritten> {
        self.open(epoch)?;
        if self.io.now() >= deadline || !self.io.prepare(report) {
            return Err(Unwritten::Unsent);
        }
        if self
            .matrix_io
            .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
            .is_err()
        {
            return Err(Unwritten::Unsent);
        }
        self.holds = true;
        // The recall token, immediately before the SET_FEATURE.
        if !self.current(epoch) {
            self.release();
            return Err(Unwritten::Unsent);
        }
        if !self.io.issue() {
            // A call the driver refused may still have reached the keyboard.
            self.release();
            return Err(Unwritten::InDoubt);
        }
        loop {
            if let Some(completed) = self.io.poll() {
                self.release();
                return if completed {
                    Ok(())
                } else {
                    Err(Unwritten::InDoubt)
                };
            }
            if self.io.now() >= deadline {
                // Still outstanding: it keeps the matrix I/O until it ends.
                self.io.cancel();
                return Err(Unwritten::InDoubt);
            }
            std::thread::sleep(POLL);
        }
    }

    /// Waits until no report is outstanding, or `deadline`: `false` when
    /// one still is.
    fn settle_by(&mut self, deadline: Instant) -> bool {
        loop {
            if self.settle() {
                return true;
            }
            if self.io.now() >= deadline {
                return false;
            }
            std::thread::sleep(POLL);
        }
    }
}

enum Request {
    Open {
        epoch: u64,
        reply: tokio::sync::oneshot::Sender<Result<(), Unwritten>>,
    },
    Frame {
        report: Box<MatrixReport>,
        deadline: Instant,
        epoch: u64,
        reply: tokio::sync::oneshot::Sender<Result<(), Unwritten>>,
    },
    Retire,
    Settle {
        deadline: Instant,
        reply: tokio::sync::oneshot::Sender<bool>,
    },
}

/// The lane's thread: one request at a time, and while a report is
/// outstanding, a poll between requests, so a late completion releases the
/// matrix I/O with no further request.
fn run<I: MatrixIo>(mut lane: Lane<I>, requests: &mpsc::Receiver<Request>) {
    // Since when the lane has polled an outstanding report between requests.
    let mut polling: Option<Instant> = None;
    loop {
        let request = if lane.io.outstanding() {
            let since = *polling.get_or_insert_with(|| lane.io.now());
            let poll = if lane.io.now().saturating_duration_since(since) < SETTLE_BACKOFF {
                SETTLE_POLL
            } else {
                SETTLE_POLL_SLOW
            };
            match requests.recv_timeout(poll) {
                Ok(request) => Some(request),
                Err(mpsc::RecvTimeoutError::Timeout) => None,
                Err(mpsc::RecvTimeoutError::Disconnected) => break,
            }
        } else {
            polling = None;
            match requests.recv() {
                Ok(request) => Some(request),
                Err(_) => break,
            }
        };
        lane.settle();
        match request {
            Some(Request::Open { epoch, reply }) => {
                let _ = reply.send(lane.open(epoch));
            }
            Some(Request::Frame {
                report,
                deadline,
                epoch,
                reply,
            }) => {
                let _ = reply.send(lane.frame(&report, deadline, epoch));
            }
            Some(Request::Retire) => lane.retire(),
            Some(Request::Settle { deadline, reply }) => {
                let _ = reply.send(lane.settle_by(deadline));
            }
            None => {}
        }
    }
    lane.retire();
}

/// Builds a lane's I/O on its own thread.
type Make<I> = Arc<dyn Fn() -> I + Send + Sync>;

/// A `MatrixStream` over a lane whose thread starts with the first request
/// and ends when the stream is dropped, retiring the lease.
pub(crate) struct LaneStream<I: MatrixIo + 'static> {
    make: Make<I>,
    matrix_io: &'static AtomicBool,
    epoch: Arc<AtomicU64>,
    cancel: Arc<AtomicBool>,
    requests: Mutex<Option<mpsc::SyncSender<Request>>>,
}

impl<I: MatrixIo + 'static> LaneStream<I> {
    pub(crate) fn new(
        make: impl Fn() -> I + Send + Sync + 'static,
        matrix_io: &'static AtomicBool,
    ) -> Self {
        Self {
            make: Arc::new(make),
            matrix_io,
            epoch: Arc::new(AtomicU64::new(0)),
            cancel: Arc::new(AtomicBool::new(false)),
            requests: Mutex::new(None),
        }
    }

    fn epoch(&self) -> u64 {
        self.epoch.load(Ordering::SeqCst)
    }

    /// Sends `request`, starting the thread first when `start` asks for it;
    /// `false` when it was not sent.
    fn send(&self, request: Request, start: bool) -> bool {
        let Ok(mut requests) = self.requests.lock() else {
            return false;
        };
        if requests.is_none() && start {
            let (sender, receiver) = mpsc::sync_channel(QUEUE);
            let lane = (
                self.make.clone(),
                self.matrix_io,
                self.epoch.clone(),
                self.cancel.clone(),
            );
            let started = std::thread::Builder::new()
                .name("openblade-matrix-stream".into())
                .spawn(move || {
                    let (make, matrix_io, epoch, cancel) = lane;
                    run(Lane::new(make(), matrix_io, epoch, cancel), &receiver);
                });
            if started.is_err() {
                return false;
            }
            *requests = Some(sender);
        }
        requests
            .as_ref()
            .is_some_and(|sender| sender.try_send(request).is_ok())
    }
}

impl<I: MatrixIo + 'static> MatrixStream for LaneStream<I> {
    fn open(&self) -> Pending<Result<(), Unwritten>> {
        let (reply, result) = tokio::sync::oneshot::channel();
        let sent = self.send(
            Request::Open {
                epoch: self.epoch(),
                reply,
            },
            true,
        );
        Box::pin(async move {
            if !sent {
                return Err(Unwritten::Unsent);
            }
            // Opening writes nothing.
            result.await.unwrap_or(Err(Unwritten::Unsent))
        })
    }

    fn frame(
        &self,
        report: MatrixReport,
        deadline: tokio::time::Instant,
    ) -> Pending<Result<(), Unwritten>> {
        let (reply, result) = tokio::sync::oneshot::channel();
        let sent = self.send(
            Request::Frame {
                report: Box::new(report),
                deadline: deadline.into_std(),
                epoch: self.epoch(),
                reply,
            },
            true,
        );
        Box::pin(async move {
            if !sent {
                return Err(Unwritten::Unsent);
            }
            // A thread that ended without replying may have submitted it.
            result.await.unwrap_or(Err(Unwritten::InDoubt))
        })
    }

    fn retire(&self) {
        // The token first, then the flag (`Lane::open`).
        self.epoch.fetch_add(1, Ordering::SeqCst);
        self.cancel.store(true, Ordering::SeqCst);
        // A queue too full for the retire still recalled every frame; the
        // lease closes when the lane ends.
        let _ = self.send(Request::Retire, false);
    }

    fn settle(&self, deadline: tokio::time::Instant) -> Pending<bool> {
        let (reply, result) = tokio::sync::oneshot::channel();
        let request = Request::Settle {
            deadline: deadline.into_std(),
            reply,
        };
        // A lane never started has nothing outstanding; one whose state is
        // unreadable is not known to be settled.
        let started = match self.requests.lock() {
            Ok(requests) => requests.is_some(),
            Err(_) => return Box::pin(std::future::ready(false)),
        };
        let sent = started && self.send(request, false);
        Box::pin(async move { !started || sent && result.await.unwrap_or(false) })
    }
}

#[cfg(test)]
#[path = "matrix_lane_tests.rs"]
mod tests;
