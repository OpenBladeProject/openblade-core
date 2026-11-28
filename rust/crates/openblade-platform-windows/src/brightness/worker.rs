//! Single-owner worker for COM and the Phase 6g brightness state machine.

use super::*;
use std::io;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU8, Ordering::SeqCst};
use std::sync::mpsc::{self, Receiver, RecvTimeoutError, SyncSender};

const OPEN: u8 = 0;
const CANCELLED: u8 = 1;
const STARTED: u8 = 2;
const CANCELLATION_POLL: Duration = Duration::from_millis(20);
// Allow delivery overhead between native phase budgets. This does not extend
// the worker's admission, completion, or readback budget.
const WRITE_REPLY_LIMIT: Duration = CALL_LIMIT
    .saturating_mul(3)
    .saturating_add(Duration::from_millis(100));

#[derive(Default)]
pub(super) struct RequestGate(AtomicU8);

impl RequestGate {
    pub(super) fn begin(&self) -> Result<(), NativeFailure> {
        self.0
            .compare_exchange(OPEN, STARTED, SeqCst, SeqCst)
            .map(|_| ())
            .map_err(|state| match state {
                CANCELLED => NativeFailure::Cancelled,
                _ => NativeFailure::SubmissionAlreadyStarted,
            })
    }

    // Atomically exclude a future submission or observe a possible one.
    fn cancel(&self) -> bool {
        self.0.compare_exchange(OPEN, CANCELLED, SeqCst, SeqCst) == Err(STARTED)
    }

    fn cancelled(&self) -> bool {
        self.0.load(SeqCst) == CANCELLED
    }
}

type ReadResult = Result<BrightnessSnapshot, BrightnessError>;
type ApplyResult = Result<BrightnessOutcome, BrightnessFailure>;

enum Operation {
    Read(Option<BrightnessTarget>, SyncSender<ReadResult>),
    Query(BrightnessQueryTarget, SyncSender<ReadResult>),
    Apply(
        BrightnessRequest,
        Option<BrightnessTarget>,
        SyncSender<ApplyResult>,
    ),
}

struct Request {
    operation: Operation,
    admission_deadline: Instant,
    gate: Arc<RequestGate>,
    finished: Arc<AtomicBool>,
    fence: Option<Arc<dyn SubmissionFence>>,
}

struct Completion(Arc<AtomicBool>);

impl Drop for Completion {
    fn drop(&mut self) {
        self.0.store(true, SeqCst);
    }
}

struct ReplyGuard<'a> {
    gate: &'a RequestGate,
    unresolved: &'a mut bool,
    settled: bool,
}

impl ReplyGuard<'_> {
    fn abandon(&mut self) -> bool {
        let started = self.gate.cancel();
        *self.unresolved |= started;
        self.settled = true;
        started
    }
}

impl Drop for ReplyGuard<'_> {
    fn drop(&mut self) {
        if !self.settled {
            self.abandon();
        }
    }
}

/// Keep one worker for the lifetime of the current-user brightness owner.
/// Read and pre-submission waits have a two-second budget. A submitted write
/// gets separate completion and readback windows. A caller can time out while
/// native connection, initiation, or cleanup is still running on the worker.
/// Shutdown (including Drop) closes admission and joins native cleanup. The host
/// must retire the worker on a blocking thread when a provider may still be busy.
pub struct WindowsBrightnessWorker {
    requests: Option<SyncSender<Request>>,
    thread: Option<std::thread::JoinHandle<()>>,
    pending: Option<Arc<AtomicBool>>,
    submission_unresolved: bool,
}

impl WindowsBrightnessWorker {
    /// Spawn a worker. COM initialization and connection are lazy on its thread.
    /// The host must establish its process-wide COM policy before COM use.
    pub fn start() -> io::Result<Self> {
        Self::spawn(super::windows::WindowsWmi::default)
    }

    /// The development host supplies a synthetic backend and owns its
    /// script-only COM policy. This uses the same queue and submission gate.
    #[cfg(feature = "scripted-display")]
    pub fn start_scripted<N: WmiBrightnessNative + 'static>(
        native: impl FnOnce() -> N + Send + 'static,
    ) -> io::Result<Self> {
        Self::spawn(native)
    }

    fn spawn<N: WmiBrightnessNative + 'static>(
        native: impl FnOnce() -> N + Send + 'static,
    ) -> io::Result<Self> {
        let (requests, receiver) = mpsc::sync_channel::<Request>(1);
        let thread = std::thread::Builder::new()
            .name("openblade-brightness".into())
            .spawn(move || {
                let mut brightness = WmiBrightness::new(native());
                for request in receiver {
                    let completion = Completion(request.finished);
                    let cancelled = || request.gate.cancelled();
                    let budget = CallBudget {
                        deadline: request.admission_deadline,
                        cancelled: &cancelled,
                        submission_gate: Some(&request.gate),
                        fence: request.fence.as_deref(),
                    };
                    match request.operation {
                        Operation::Read(target, reply) => {
                            let result = brightness.read_with_budget(target.as_ref(), &budget);
                            drop(completion);
                            let _ = reply.send(result);
                        }
                        Operation::Query(target, reply) => {
                            let result = brightness.query(&target, &budget);
                            drop(completion);
                            let _ = reply.send(result);
                        }
                        Operation::Apply(intent, target, reply) => {
                            let result =
                                brightness.apply_with_budget(intent, target.as_ref(), &budget);
                            drop(completion);
                            let _ = reply.send(result);
                        }
                    }
                }
            })?;
        Ok(Self {
            requests: Some(requests),
            thread: Some(thread),
            pending: None,
            submission_unresolved: false,
        })
    }

    /// Wait for the native owner to release its COM objects and exit its apartment.
    pub fn shutdown(mut self) -> io::Result<()> {
        self.join()
    }

    fn join(&mut self) -> io::Result<()> {
        // A live caller exclusively borrows self. Any abandoned request already
        // had its admission canceled by ReplyGuard before shutdown can begin.
        drop(self.requests.take());
        if let Some(thread) = self.thread.take() {
            thread
                .join()
                .map_err(|_| io::Error::other("brightness worker panicked"))?;
        }
        Ok(())
    }

    /// Observe an exact display for a query. Never resolves a pending write.
    pub fn read_query_target(
        &mut self,
        target: BrightnessQueryTarget,
        cancelled: impl Fn() -> bool,
    ) -> ReadResult {
        self.execute(CALL_LIMIT, &cancelled, None, |reply| {
            Operation::Query(target, reply)
        })
        .map_err(|(failure, _)| BrightnessError::Native(failure))?
    }

    pub fn read(&mut self, cancelled: impl Fn() -> bool) -> ReadResult {
        self.read_with_target(None, cancelled)
    }

    pub fn read_target(
        &mut self,
        target: BrightnessTarget,
        cancelled: impl Fn() -> bool,
    ) -> ReadResult {
        self.read_with_target(Some(target), cancelled)
    }

    fn read_with_target(
        &mut self,
        target: Option<BrightnessTarget>,
        cancelled: impl Fn() -> bool,
    ) -> ReadResult {
        self.execute(CALL_LIMIT, &cancelled, None, |reply| {
            Operation::Read(target, reply)
        })
        .map_err(|(failure, _)| BrightnessError::Native(failure))?
    }

    pub fn apply(
        &mut self,
        intent: BrightnessRequest,
        cancelled: impl Fn() -> bool,
    ) -> ApplyResult {
        self.apply_with_target(intent, None, None, cancelled)
    }

    pub fn apply_target(
        &mut self,
        intent: BrightnessRequest,
        target: BrightnessTarget,
        cancelled: impl Fn() -> bool,
    ) -> ApplyResult {
        self.apply_with_target(intent, Some(target), None, cancelled)
    }

    /// Apply to an exact display with host authority re-checked by the
    /// worker immediately before the one possible native invocation.
    pub fn apply_target_fenced(
        &mut self,
        intent: BrightnessRequest,
        target: BrightnessTarget,
        fence: Arc<dyn SubmissionFence>,
        cancelled: impl Fn() -> bool,
    ) -> ApplyResult {
        self.apply_with_target(intent, Some(target), Some(fence), cancelled)
    }

    fn apply_with_target(
        &mut self,
        intent: BrightnessRequest,
        target: Option<BrightnessTarget>,
        fence: Option<Arc<dyn SubmissionFence>>,
        cancelled: impl Fn() -> bool,
    ) -> ApplyResult {
        if self.submission_unresolved {
            return Err(BrightnessFailure {
                reason: BrightnessError::SubmissionUnresolved,
                submission_may_have_occurred: false,
                state: None,
            });
        }
        self.execute(WRITE_REPLY_LIMIT, &cancelled, fence, |reply| {
            Operation::Apply(intent, target, reply)
        })
        .map_err(|(failure, started)| BrightnessFailure {
            reason: BrightnessError::Native(failure),
            submission_may_have_occurred: started,
            state: None,
        })?
    }

    fn execute<T>(
        &mut self,
        limit: Duration,
        cancelled: &dyn Fn() -> bool,
        fence: Option<Arc<dyn SubmissionFence>>,
        operation: impl FnOnce(SyncSender<T>) -> Operation,
    ) -> Result<T, (NativeFailure, bool)> {
        if cancelled() {
            return Err((NativeFailure::Cancelled, false));
        }
        if self.pending.as_ref().is_some_and(|done| !done.load(SeqCst)) {
            return Err((NativeFailure::WorkerBusy, false));
        }
        let start = Instant::now();
        let gate = Arc::new(RequestGate::default());
        let finished = Arc::new(AtomicBool::new(false));
        let (reply, receiver) = mpsc::sync_channel(1);
        self.requests
            .as_ref()
            .expect("worker owns the request sender")
            .try_send(Request {
                operation: operation(reply),
                admission_deadline: start + CALL_LIMIT,
                gate: Arc::clone(&gate),
                finished: Arc::clone(&finished),
                fence,
            })
            .map_err(|error| {
                let failure = match error {
                    mpsc::TrySendError::Full(_) => NativeFailure::WorkerBusy,
                    mpsc::TrySendError::Disconnected(_) => NativeFailure::WorkerDisconnected,
                };
                (failure, false)
            })?;
        self.pending = Some(finished);
        let mut reply_guard = ReplyGuard {
            gate: &gate,
            unresolved: &mut self.submission_unresolved,
            settled: false,
        };
        match wait_for_reply(
            receiver,
            &gate,
            start + CALL_LIMIT,
            start + limit,
            cancelled,
        ) {
            Ok(result) => {
                reply_guard.settled = true;
                Ok(result)
            }
            Err(failure) => Err((failure, reply_guard.abandon())),
        }
    }
}

impl Drop for WindowsBrightnessWorker {
    fn drop(&mut self) {
        let _ = self.join();
    }
}

fn wait_for_reply<T>(
    receiver: Receiver<T>,
    gate: &RequestGate,
    admission_deadline: Instant,
    deadline: Instant,
    cancelled: &dyn Fn() -> bool,
) -> Result<T, NativeFailure> {
    loop {
        let remaining = if cancelled() && !gate.cancel() {
            Err(NativeFailure::Cancelled)
        } else if Instant::now() >= admission_deadline && !gate.cancel() {
            Err(NativeFailure::DeadlineExceeded)
        } else {
            time_left(deadline)
        };
        let wait = match remaining {
            Ok(time) => time.min(CANCELLATION_POLL),
            // Prefer a definitive reply already in the channel at the cutoff.
            Err(failure) => return receiver.try_recv().map_err(|_| failure),
        };
        match receiver.recv_timeout(wait) {
            Ok(reply) => return Ok(reply),
            Err(RecvTimeoutError::Timeout) => {}
            Err(RecvTimeoutError::Disconnected) => return Err(NativeFailure::WorkerDisconnected),
        }
    }
}

#[cfg(test)]
mod tests;
