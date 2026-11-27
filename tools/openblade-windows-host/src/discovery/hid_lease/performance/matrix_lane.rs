//! The animated matrix stream (`MatrixStream`) apart from its Win32 calls
//! (`MatrixIo`): one thread owns one lease on the exact reviewed matrix
//! collection and writes one 03/0B feature report at a time. Before each
//! report it checks, in order, that the request was not recalled, that the
//! collection is open and its handle still describes the selected
//! collection, that the frame's deadline has not passed, and that it holds
//! the process's matrix I/O (`MATRIX_IO`, which the render jobs' lease takes
//! too). The recall token (`epoch`) is checked once more immediately before
//! the report is issued: `MatrixStream::retire` advances it, so a frame the
//! runtime gave up on is never written late.
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
/// How often the lane polls a report left outstanding past its deadline.
const SETTLE_POLL: Duration = Duration::from_millis(10);

/// The matrix collection's I/O for one lane: the Windows handle and its
/// overlapped 03/0B report, or a test's fake.
pub(crate) trait MatrixIo {
    /// Selects, opens and verifies the exact reviewed collection; `false`
    /// opened nothing.
    fn open(&mut self) -> bool;
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
}

/// One lane's state over its I/O.
pub(crate) struct Lane<I: MatrixIo> {
    io: I,
    /// The process's matrix I/O (`MATRIX_IO`).
    matrix_io: &'static AtomicBool,
    /// The recall token `MatrixStream::retire` advances.
    epoch: Arc<AtomicU64>,
    /// Whether this lane holds `matrix_io` for its outstanding report.
    holds: bool,
    /// A retire waits for the outstanding report before the lease closes.
    closing: bool,
}

impl<I: MatrixIo> Lane<I> {
    pub(crate) fn new(io: I, matrix_io: &'static AtomicBool, epoch: Arc<AtomicU64>) -> Self {
        Self {
            io,
            matrix_io,
            epoch,
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

    /// Opens the collection, or confirms it is open, for the request at
    /// `epoch`. A report still outstanding opens nothing.
    fn open(&mut self, epoch: u64) -> Result<(), Unwritten> {
        if !self.current(epoch) || !self.settle() {
            return Err(Unwritten::Unsent);
        }
        if self.io.is_open() || self.io.open() {
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
        if !self.io.describes() {
            self.retire();
            return Err(Unwritten::Unsent);
        }
        if Instant::now() >= deadline || !self.io.prepare(report) {
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
            if Instant::now() >= deadline {
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
            if Instant::now() >= deadline {
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
    loop {
        let request = if lane.io.outstanding() {
            match requests.recv_timeout(SETTLE_POLL) {
                Ok(request) => Some(request),
                Err(mpsc::RecvTimeoutError::Timeout) => None,
                Err(mpsc::RecvTimeoutError::Disconnected) => break,
            }
        } else {
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
    requests: Mutex<Option<mpsc::Sender<Request>>>,
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
            let (sender, receiver) = mpsc::channel();
            let (make, matrix_io, epoch) = (self.make.clone(), self.matrix_io, self.epoch.clone());
            let started = std::thread::Builder::new()
                .name("openblade-matrix-stream".into())
                .spawn(move || run(Lane::new(make(), matrix_io, epoch), &receiver));
            if started.is_err() {
                return false;
            }
            *requests = Some(sender);
        }
        requests
            .as_ref()
            .is_some_and(|sender| sender.send(request).is_ok())
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
        self.epoch.fetch_add(1, Ordering::SeqCst);
        let _ = self.send(Request::Retire, false);
    }

    fn settle(&self, deadline: tokio::time::Instant) -> Pending<bool> {
        let (reply, result) = tokio::sync::oneshot::channel();
        let request = Request::Settle {
            deadline: deadline.into_std(),
            reply,
        };
        // A lane never started has nothing outstanding.
        let started = self
            .requests
            .lock()
            .is_ok_and(|requests| requests.is_some());
        let sent = started && self.send(request, false);
        Box::pin(async move { !started || sent && result.await.unwrap_or(false) })
    }
}

#[cfg(test)]
#[path = "matrix_lane_tests.rs"]
mod tests;
