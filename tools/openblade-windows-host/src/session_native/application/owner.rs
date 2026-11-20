//! The reporter owner: one dedicated thread that drives the foreground watcher.
//!
//! The watcher resolves windows with blocking Win32 calls, so it never runs on
//! the Agent's single-threaded runtime. This thread is its only user: it
//! applies the latest profile snapshot, polls at a bounded cadence even when no
//! foreground callback arrives, hands at most one request at a time to the
//! transport, and applies that request's completion. Nothing here performs IPC.
use std::{
    io,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
        mpsc::{Receiver, RecvTimeoutError, SyncSender},
    },
    time::{Duration, Instant},
};

use openblade_application_reporter::{
    ApplicationProfile, Guid, ReportApplicationContextRequest as Request, ReportOutcome,
};
use openblade_platform_windows::application::watcher::{Wake, WindowsForegroundWatcher};
use tokio::sync::{mpsc, oneshot, watch};

use super::profiles::Profiles;

/// The watcher surface the owner drives. `WindowsForegroundWatcher` is the
/// production implementation; tests supply the scripted platform watcher.
pub(super) trait Watcher {
    /// Wait for the registration thread. A failure has already retired it.
    fn wait_ready(&mut self, timeout: Duration) -> io::Result<()>;
    /// Call `wake` (from any thread, without blocking) after each foreground or
    /// desktop notice and when the registration thread ends, so the owner waits
    /// for notices instead of polling for them.
    fn set_notice(&mut self, wake: Wake);
    fn observe_profiles(&mut self, profiles: Vec<ApplicationProfile>);
    fn poll(&mut self, elapsed: Duration, request_id: Guid) -> Option<Request>;
    fn complete(&mut self, sequence: i64, outcome: ReportOutcome);
    /// Retire local process evidence and join the registration thread.
    fn shutdown(self) -> io::Result<()>;
}

impl Watcher for WindowsForegroundWatcher {
    fn wait_ready(&mut self, timeout: Duration) -> io::Result<()> {
        WindowsForegroundWatcher::wait_ready(self, timeout)
    }

    fn set_notice(&mut self, wake: Wake) {
        WindowsForegroundWatcher::set_notice(self, wake);
    }

    fn observe_profiles(&mut self, profiles: Vec<ApplicationProfile>) {
        WindowsForegroundWatcher::observe_profiles(self, profiles);
    }

    fn poll(&mut self, elapsed: Duration, request_id: Guid) -> Option<Request> {
        WindowsForegroundWatcher::poll(self, elapsed, request_id)
    }

    fn complete(&mut self, sequence: i64, outcome: ReportOutcome) {
        WindowsForegroundWatcher::complete(self, sequence, outcome);
    }

    fn shutdown(self) -> io::Result<()> {
        WindowsForegroundWatcher::shutdown(self)
    }
}

/// Stop requests shared by the transport and the owner. `requested` is stored
/// before `cancel`, so a canceled watcher with no request means the watcher
/// retired itself (lost hook thread or startup failure), which is a failure.
pub(super) struct Signals {
    requested: AtomicBool,
    cancel: Arc<AtomicBool>,
}

impl Signals {
    pub(super) fn new() -> Self {
        Self {
            requested: AtomicBool::new(false),
            cancel: Arc::default(),
        }
    }

    /// The flag the watcher observes for cancellation.
    pub(super) fn cancellation(&self) -> Arc<AtomicBool> {
        Arc::clone(&self.cancel)
    }

    pub(super) fn retire(&self) {
        self.requested.store(true, Ordering::Release);
        self.cancel.store(true, Ordering::Release);
    }

    fn requested(&self) -> bool {
        self.requested.load(Ordering::Acquire)
    }

    fn canceled(&self) -> bool {
        self.cancel.load(Ordering::Acquire)
    }
}

/// Sets both flags on every exit of the transport, including a dropped future.
pub(super) struct Retire(pub(super) Arc<Signals>);

impl Drop for Retire {
    fn drop(&mut self) {
        self.0.retire();
    }
}

/// The outcome of the one request in flight. It has its own slot, never the
/// wake channel, because a wake can fill that channel and a completion must not
/// be lost; at most one request is in flight, so one slot is enough.
pub(super) type Completion = Arc<Mutex<Option<(i64, ReportOutcome)>>>;

/// A wake carries nothing: the owner reads the slots it waits on. The channel
/// holds one entry because one pending wake is as good as many.
pub(super) type Wakes = SyncSender<()>;

#[derive(Clone, Copy)]
pub(super) struct Timing {
    /// Housekeeping tick. Foreground and desktop notices, profile changes,
    /// completions and stop all wake the owner at once. The tick only bounds
    /// what has no notice: a process exit, a lock without a desktop event and
    /// the five-second renewal, which is checked against the elapsed time.
    pub(super) poll: Duration,
    /// How long the registration thread may take to install its hooks.
    pub(super) ready: Duration,
}

impl Timing {
    pub(super) const INSTALLED: Self = Self {
        poll: Duration::from_millis(500),
        ready: Duration::from_secs(5),
    };
}

/// The synchronous body of the owner loop.
pub(super) struct Owner<W, G> {
    watcher: W,
    profiles: watch::Receiver<Profiles>,
    applied: Profiles,
    next_id: Guid,
    new_id: G,
}

impl<W: Watcher, G: FnMut() -> io::Result<Guid>> Owner<W, G> {
    pub(super) fn new(
        watcher: W,
        profiles: watch::Receiver<Profiles>,
        mut new_id: G,
    ) -> io::Result<Self> {
        Ok(Self {
            watcher,
            profiles,
            applied: None,
            next_id: new_id()?,
            new_id,
        })
    }

    /// Apply the newest profile snapshot, then poll. A snapshot is applied
    /// before the poll it precedes, as managed applies the latest status before
    /// reconciling. A closed feed keeps its last snapshot.
    pub(super) fn step(&mut self, elapsed: Duration) -> io::Result<Option<Request>> {
        let latest = self.profiles.borrow().clone();
        let changed = match (&self.applied, &latest) {
            (Some(applied), Some(latest)) => !Arc::ptr_eq(applied, latest),
            (None, Some(_)) => true,
            (_, None) => false,
        };
        if changed && let Some(latest) = latest {
            self.watcher.observe_profiles(latest.to_vec());
            self.applied = Some(latest);
        }
        let request = self.watcher.poll(elapsed, self.next_id);
        if request.is_some() {
            // Every attempt carries a fresh request id.
            self.next_id = (self.new_id)()?;
        }
        Ok(request)
    }

    pub(super) fn complete(&mut self, sequence: i64, outcome: ReportOutcome) {
        self.watcher.complete(sequence, outcome);
    }

    pub(super) fn shutdown(self) -> io::Result<()> {
        self.watcher.shutdown()
    }
}

/// Everything the owner thread shares with the transport.
pub(super) struct Link {
    pub(super) signals: Arc<Signals>,
    pub(super) requests: mpsc::Sender<Request>,
    pub(super) wakes: Receiver<()>,
    /// Lets a watcher notice wake the owner.
    pub(super) wake: Wakes,
    pub(super) completion: Completion,
    pub(super) ready: oneshot::Sender<io::Result<()>>,
}

/// The owner thread. `ready` carries the startup outcome; the returned result
/// is the thread's own, read after a stop or an unexpected exit.
pub(super) fn run<W: Watcher>(
    make: impl FnOnce() -> io::Result<W>,
    profiles: watch::Receiver<Profiles>,
    new_id: impl FnMut() -> io::Result<Guid>,
    timing: &Timing,
    link: Link,
) -> io::Result<()> {
    let Link {
        signals,
        requests,
        wakes,
        wake,
        completion,
        ready,
    } = link;
    let mut watcher = match make() {
        Ok(watcher) => watcher,
        Err(error) => {
            let _ = ready.send(Err(error));
            return Ok(());
        }
    };
    watcher.set_notice(Arc::new(move || {
        // A full channel already holds a wake.
        let _ = wake.try_send(());
    }));
    if let Err(error) = watcher.wait_ready(timing.ready) {
        let _ = ready.send(Err(error));
        // The watcher retired its own startup; join its thread off the caller's path.
        return watcher.shutdown();
    }
    let mut owner = match Owner::new(watcher, profiles, new_id) {
        Ok(owner) => owner,
        Err(error) => {
            let _ = ready.send(Err(io::Error::new(error.kind(), "request id generation")));
            return Err(error);
        }
    };
    let _ = ready.send(Ok(()));
    let started = Instant::now();
    let mut failure = None;
    while !signals.canceled() {
        match owner.step(started.elapsed()) {
            Ok(Some(request)) => {
                let sequence = request.sequence;
                // Capacity one with at most one request in flight, so Full is
                // unreachable. Treat it as a failed send to stay consistent.
                match requests.try_send(request) {
                    Ok(()) => {}
                    Err(mpsc::error::TrySendError::Full(_)) => {
                        owner.complete(sequence, ReportOutcome::TransportFailed);
                    }
                    Err(mpsc::error::TrySendError::Closed(_)) => break,
                }
            }
            Ok(None) => {}
            Err(error) => {
                failure = Some(error);
                break;
            }
        }
        match wakes.recv_timeout(timing.poll) {
            Ok(()) | Err(RecvTimeoutError::Timeout) => {}
            Err(RecvTimeoutError::Disconnected) => break,
        }
        let finished = completion
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .take();
        if let Some((sequence, outcome)) = finished {
            owner.complete(sequence, outcome);
        }
    }
    let retired = owner.shutdown();
    if let Some(error) = failure {
        return Err(error);
    }
    // Canceled without a request: the watcher retired itself.
    if !signals.requested() {
        return Err(io::Error::new(
            io::ErrorKind::BrokenPipe,
            "foreground watcher stopped unexpectedly",
        ));
    }
    retired
}
