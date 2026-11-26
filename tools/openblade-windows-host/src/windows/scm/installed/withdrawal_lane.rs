//! A thread that owns one piece of driver state and runs calls on it one at
//! a time, with a budget per call: the protocol the GPU clock-offset worker
//! (`gpu_clock_offsets::Worker`) and the display-brightness lane
//! (`display_brightness_hid::Lane`) share.
//!
//! A call is queued with a claim. The thread starts it only by moving the
//! claim from queued to started, so a caller that stops waiting can withdraw
//! a call that has not started and know it never will. A call that started
//! and did not answer within its budget is abandoned: it finishes on the
//! thread, which keeps what it did. A call that panics is abandoned too; the
//! thread recovers its state (`Recover`) before the next call and keeps
//! serving.
//!
//! A call that must not be withdrawn, such as a release owed at Stop, is
//! queued with `Waiting::Stays`: neither its budget's end nor its caller's
//! drop withdraws it, so it runs after the call in progress however long its
//! caller waits.

use std::{
    io,
    panic::{self, AssertUnwindSafe},
    sync::{
        Arc,
        atomic::{AtomicBool, AtomicU8, Ordering},
        mpsc::{Receiver, SyncSender, sync_channel},
    },
    thread,
    time::Duration,
};

use tokio::sync::oneshot;

/// How a call ended.
#[derive(Debug, PartialEq, Eq)]
pub(super) enum Called<R> {
    Done(R),
    /// The call never started and never will: the thread was busy with a
    /// call and its queue was full, it had stopped, or the budget ran out
    /// first and the call was withdrawn. Nothing was submitted.
    NotStarted,
    /// The call returned no result within its budget: it started and
    /// finishes on the thread, which records what the driver did, or it
    /// panicked and the thread recovered its state. A call queued with
    /// `Waiting::Stays` that had not started yet is abandoned too: it still
    /// runs. Either way a write is indeterminate to its caller.
    Abandoned,
}

/// What a queued call's budget end or its caller's drop does to it.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum Waiting {
    /// It is withdrawn, so a call nobody observes never starts later.
    Withdraws,
    /// It stays queued and runs after the call in progress. Only the
    /// display-brightness lane queues one.
    #[cfg_attr(
        not(any(test, feature = "service-brightness-hid-usage-v1")),
        allow(dead_code)
    )]
    Stays,
}

const QUEUED: u8 = 0;
const STARTED: u8 = 1;
const WITHDRAWN: u8 = 2;

/// A call's place in the queue. Its caller may keep a clone to learn, after
/// the call or after dropping it, whether it ever started.
#[derive(Clone)]
pub(super) struct Claim(Arc<AtomicU8>);

impl Default for Claim {
    fn default() -> Self {
        Self(Arc::new(AtomicU8::new(QUEUED)))
    }
}

impl Claim {
    /// Withdraws the call if it has not started; whether it was withdrawn
    /// now or before.
    pub(super) fn withdraw(&self) -> bool {
        match self
            .0
            .compare_exchange(QUEUED, WITHDRAWN, Ordering::AcqRel, Ordering::Acquire)
        {
            Ok(_) => true,
            Err(state) => state == WITHDRAWN,
        }
    }

    pub(super) fn started(&self) -> bool {
        self.0.load(Ordering::Acquire) == STARTED
    }

    fn start(&self) -> bool {
        self.0
            .compare_exchange(QUEUED, STARTED, Ordering::AcqRel, Ordering::Acquire)
            .is_ok()
    }
}

/// Withdraws a `Waiting::Withdraws` call when its caller stops waiting, by
/// returning or by being dropped.
struct Withdrawing<'a>(Option<&'a Claim>);

impl Drop for Withdrawing<'_> {
    fn drop(&mut self) {
        if let Some(claim) = self.0 {
            claim.withdraw();
        }
    }
}

/// The state's own repair after a call on it panicked, before the next.
pub(super) trait Recover {
    fn recover_from_panic(&mut self);
}

type Run<S> = Box<dyn FnOnce(&mut S) + Send>;
type Job<S> = (Claim, Run<S>);

/// The thread that owns one `S`. At most one call runs and `waiting` more
/// wait; a further call is refused at once. Dropping the lane stops it after
/// the call in progress, without waiting; a call still queued then never
/// starts.
pub(super) struct Lane<S: 'static> {
    jobs: SyncSender<Job<S>>,
    stopped: Arc<AtomicBool>,
}

impl<S: 'static> Drop for Lane<S> {
    fn drop(&mut self) {
        // A queued call must not start once its owner has gone.
        self.stopped.store(true, Ordering::Release);
    }
}

impl<S: Recover + 'static> Lane<S> {
    /// Starts the thread `name` and builds the state on it, so a library
    /// handle the state holds lives where it is called.
    pub(super) fn spawn(
        name: &str,
        waiting: usize,
        build: impl FnOnce() -> S + Send + 'static,
    ) -> io::Result<Self> {
        let (jobs, queue) = sync_channel::<Job<S>>(waiting);
        let stopped = Arc::new(AtomicBool::new(false));
        let running = stopped.clone();
        thread::Builder::new()
            .name(name.into())
            .spawn(move || run(build(), &queue, &running))?;
        Ok(Self { jobs, stopped })
    }

    /// Runs `job` within `budget`, withdrawn if it has not started when the
    /// budget ends or the future is dropped.
    pub(super) async fn call<R: Send + 'static>(
        &self,
        budget: Duration,
        job: impl FnOnce(&mut S) -> R + Send + 'static,
    ) -> Called<R> {
        self.call_claimed(&Claim::default(), Waiting::Withdraws, budget, job)
            .await
    }

    /// Runs `job` under `claim` within `budget`. Dropping the future before
    /// the call starts withdraws it only when it `Waiting::Withdraws`; after
    /// it starts, the call finishes on the thread, as when the budget runs
    /// out.
    pub(super) async fn call_claimed<R: Send + 'static>(
        &self,
        claim: &Claim,
        waiting: Waiting,
        budget: Duration,
        job: impl FnOnce(&mut S) -> R + Send + 'static,
    ) -> Called<R> {
        let (reply, mut result) = oneshot::channel();
        let job: Run<S> = Box::new(move |state| {
            let _ = reply.send(job(state));
        });
        if self.jobs.try_send((claim.clone(), job)).is_err() {
            return Called::NotStarted;
        }
        let _withdrawing = Withdrawing((waiting == Waiting::Withdraws).then_some(claim));
        match tokio::time::timeout(budget, &mut result).await {
            Ok(Ok(value)) => Called::Done(value),
            // A started call that sent nothing panicked; one never started
            // was dropped unrun by a stopped lane.
            Ok(Err(_)) if claim.started() => Called::Abandoned,
            Ok(Err(_)) => Called::NotStarted,
            Err(_) if waiting == Waiting::Withdraws && claim.withdraw() => Called::NotStarted,
            // It started, or stays queued to start; it may have just finished.
            Err(_) => match result.try_recv() {
                Ok(value) => Called::Done(value),
                Err(_) => Called::Abandoned,
            },
        }
    }

    /// [`call`](Self::call) for a caller off the async runtime, or one that
    /// must measure `budget` on the real clock: it blocks this thread until
    /// the call ends or `budget` runs out, with the same withdrawal,
    /// abandonment and panic rules.
    #[cfg(feature = "service-gpu-clock-tuning-v1")]
    pub(super) fn call_blocking<R: Send + 'static>(
        &self,
        budget: Duration,
        job: impl FnOnce(&mut S) -> R + Send + 'static,
    ) -> Called<R> {
        use std::sync::mpsc::RecvTimeoutError;
        let claim = Claim::default();
        let (reply, result) = sync_channel(1);
        let job: Run<S> = Box::new(move |state| {
            let _ = reply.send(job(state));
        });
        if self.jobs.try_send((claim.clone(), job)).is_err() {
            return Called::NotStarted;
        }
        let _withdrawing = Withdrawing(Some(&claim));
        match result.recv_timeout(budget) {
            Ok(value) => Called::Done(value),
            // As in `call`: a started call that sent nothing panicked.
            Err(RecvTimeoutError::Disconnected) if claim.started() => Called::Abandoned,
            Err(RecvTimeoutError::Disconnected) => Called::NotStarted,
            Err(RecvTimeoutError::Timeout) if claim.withdraw() => Called::NotStarted,
            Err(RecvTimeoutError::Timeout) => match result.try_recv() {
                Ok(value) => Called::Done(value),
                Err(_) => Called::Abandoned,
            },
        }
    }
}

#[cfg(test)]
impl<S: 'static> Lane<S> {
    /// Queues `job` with no caller waiting, as a call whose caller is gone.
    pub(super) fn enqueue_for_tests(&self, job: impl FnOnce(&mut S) + Send + 'static) {
        assert!(
            self.jobs
                .try_send((Claim::default(), Box::new(job)))
                .is_ok()
        );
    }
}

fn run<S: Recover>(mut state: S, queue: &Receiver<Job<S>>, stopped: &AtomicBool) {
    // The queue disconnects when the lane is dropped; a call still queued
    // then is dropped unrun.
    while let Ok((claim, job)) = queue.recv() {
        if stopped.load(Ordering::Acquire) {
            break;
        }
        if !claim.start() {
            continue;
        }
        // A panicking call must not end the thread: its in-memory record of
        // an uncertain submission may be the only one. The state is
        // recovered before the next call, so a broken invariant is not
        // observed.
        if panic::catch_unwind(AssertUnwindSafe(|| job(&mut state))).is_err() {
            state.recover_from_panic();
        }
    }
}

#[cfg(test)]
#[path = "withdrawal_lane_tests.rs"]
mod tests;
