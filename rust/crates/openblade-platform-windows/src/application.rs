//! Application reporter identity binding for a caller that supplies ordered
//! foreground notifications. The opt-in watcher owns notification delivery.

mod native;
mod path;
#[cfg(feature = "foreground-watcher")]
pub mod watcher;

use std::num::{NonZeroU32, NonZeroU64};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Duration;

use openblade_application_reporter::{
    ApplicationProfile, ApplicationProfileReporter, Foreground, Guid, ProcessHandle,
    ProcessIdentity, ReportApplicationContextRequest, ReportOutcome,
};

/// A single owner feeds observed HWNDs and drives time and transport. No native
/// observation occurs until the owner explicitly supplies an observed HWND.
pub struct WindowsApplicationReporter(Binding<native::Windows>);

impl WindowsApplicationReporter {
    pub fn new(version: i32, instance: Guid, cancellation: Arc<AtomicBool>) -> Self {
        Self(Binding::new(
            native::Windows,
            version,
            instance,
            cancellation,
        ))
    }

    /// Zero means an explicit unknown observation. Never samples a new window
    /// in place of a stale, absent, or rejected notification.
    pub fn observe_window(&mut self, observed_hwnd: usize) {
        self.0.observe_window(observed_hwnd);
    }

    /// The caller supplies validated profiles (including managed normalization).
    pub fn observe_profiles(&mut self, profiles: Vec<ApplicationProfile>) {
        if self.0.running() {
            self.0.policy.observe_profiles(profiles);
        }
    }

    pub fn poll(
        &mut self,
        elapsed: Duration,
        request: Guid,
    ) -> Option<ReportApplicationContextRequest> {
        self.0.poll(elapsed, request)
    }

    pub fn complete(&mut self, sequence: i64, outcome: ReportOutcome) {
        if self.0.running() {
            self.0.policy.complete(sequence, outcome);
        }
    }

    pub fn disconnected(&mut self) {
        if self.0.running() {
            self.0.policy.disconnected();
        }
    }

    pub fn reconnected(&mut self) {
        if self.0.running() {
            self.0.policy.reconnected();
        }
    }

    pub fn cancel(&mut self) {
        self.0.cancel();
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct Owner {
    thread: NonZeroU32,
    pid: NonZeroU32,
}

trait Desktop {
    type Process: Lifetime;

    fn active_local_session(&mut self) -> Option<NonZeroU32>;
    fn foreground(&mut self) -> usize;
    fn owner(&mut self, window: usize) -> Option<Owner>;
    fn process_session(&mut self, pid: NonZeroU32) -> Option<NonZeroU32>;
    fn open_query_and_synchronize(&mut self, pid: NonZeroU32) -> Option<Self::Process>;
    fn full_path(&mut self, input: &str) -> Option<String>;
    fn paths_equal(first: &str, second: &str) -> bool;
}

trait Lifetime {
    fn identity(&self) -> Option<ProcessIdentity>;
    fn image_path(&self) -> Option<String>;
    fn alive(&self) -> bool;
}

struct Sample<P> {
    identity: ProcessIdentity,
    path: String,
    process: P,
}

// The current sample and the policy can share ownership of one kernel handle.
// A PID is never used to reopen or stand in for a sampled lifetime.
struct Retained<P>(Arc<Sample<P>>);

impl<P: Lifetime> ProcessHandle for Retained<P> {
    fn identity(&self) -> ProcessIdentity {
        self.0.identity
    }

    fn executable_path(&self) -> &str {
        &self.0.path
    }

    fn is_alive(&self) -> bool {
        self.0.process.alive()
    }
}

struct Binding<A: Desktop> {
    api: A,
    policy: ApplicationProfileReporter<Retained<A::Process>>,
    foreground: Option<Arc<Sample<A::Process>>>,
    cancellation: Arc<AtomicBool>,
    stopped: bool,
}

impl<A: Desktop> Binding<A> {
    fn new(api: A, version: i32, instance: Guid, cancellation: Arc<AtomicBool>) -> Self {
        Self {
            api,
            policy: ApplicationProfileReporter::new(version, instance, A::paths_equal),
            foreground: None,
            cancellation,
            stopped: false,
        }
    }

    fn observe_window(&mut self, window: usize) {
        if !self.running() {
            return;
        }
        let sample = resolve(&mut self.api, window, &self.cancellation);
        if !self.running() {
            return;
        }
        self.accept_sample(sample);
    }

    fn accept_sample(&mut self, sample: Option<Sample<A::Process>>) {
        self.foreground = sample.map(Arc::new);
        let foreground = self
            .foreground
            .as_ref()
            .map_or(Foreground::Unknown, |sample| Foreground::Executable {
                path: sample.path.clone(),
                process: Some(sample.identity),
            });
        self.policy.observe_latest_foreground(foreground);
    }

    fn poll(
        &mut self,
        elapsed: Duration,
        request: Guid,
    ) -> Option<ReportApplicationContextRequest> {
        if !self.running() {
            return None;
        }
        self.prune_foreground();
        self.poll_policy(elapsed, request)
    }

    fn prune_foreground(&mut self) {
        if self
            .foreground
            .as_ref()
            .is_some_and(|sample| !sample.process.alive())
            && let Some(retired) = self.foreground.take()
        {
            self.policy.observe_exit(retired.identity);
            self.policy.observe_latest_foreground(Foreground::Unknown);
        }
    }

    fn poll_policy(
        &mut self,
        elapsed: Duration,
        request: Guid,
    ) -> Option<ReportApplicationContextRequest> {
        if !self.running() {
            return None;
        }
        let result = self.policy.poll(elapsed, request, |identity| {
            self.foreground
                .as_ref()
                .filter(|sample| sample.identity == identity)
                .map(|sample| Retained(Arc::clone(sample)))
        });
        if self.running() { result } else { None }
    }

    fn running(&mut self) -> bool {
        if self.cancellation.load(Ordering::Acquire) {
            self.cancel();
        }
        !self.stopped
    }

    fn cancel(&mut self) {
        self.stopped = true;
        self.policy.cancel();
        self.foreground = None;
    }
}

fn resolve<A: Desktop>(
    api: &mut A,
    window: usize,
    cancellation: &AtomicBool,
) -> Option<Sample<A::Process>> {
    let running = || !cancellation.load(Ordering::Acquire);
    if window == 0 || !running() {
        return None;
    }
    let session = api.active_local_session()?;
    if !running() || api.foreground() != window {
        return None;
    }
    if !running() {
        return None;
    }
    let owner = api.owner(window)?;
    if !running() || api.process_session(owner.pid) != Some(session) || !running() {
        return None;
    }
    let process = api.open_query_and_synchronize(owner.pid)?;
    if !running() {
        return None;
    }
    let identity = process.identity()?;
    if identity.pid != owner.pid || !running() {
        return None;
    }
    let image = process.image_path()?;
    if !running() {
        return None;
    }
    let path = path::normalize(&image, |input| api.full_path(input))?;
    let basename = path.rsplit(['\\', '/']).next()?;
    if A::paths_equal(basename, "ApplicationFrameHost.exe")
        || !running()
        || api.active_local_session() != Some(session)
        || !running()
        || api.owner(window) != Some(owner)
        || !running()
        || api.process_session(owner.pid) != Some(session)
        || !running()
        || api.foreground() != window
        || !running()
        || !process.alive()
        || !running()
    {
        return None;
    }
    Some(Sample {
        identity,
        path,
        process,
    })
}

fn process_identity(pid: u32, generation: u64) -> Option<ProcessIdentity> {
    Some(ProcessIdentity {
        pid: NonZeroU32::new(pid)?,
        generation: NonZeroU64::new(generation)?,
    })
}

#[cfg(test)]
mod tests;
