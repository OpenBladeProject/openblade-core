//! Current-user application profile reporter policy, driven by a single owner.
//! The caller supplies normalized executable paths, process evidence, monotonic
//! time and transport completion. This crate performs no native or IPC calls.

use std::collections::{BTreeMap, VecDeque};
use std::num::{NonZeroU32, NonZeroU64};
use std::time::Duration;

pub const LEASE_INTERVAL: Duration = Duration::from_secs(5);
const OBSERVATION_CAPACITY: usize = 16;

/// GUID value supplied by the host. Wire formatting belongs to the IPC adapter.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Guid(pub [u8; 16]);

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ActivationMode {
    Foreground,
    WhileRunningAfterActivation,
}

/// The selection fields of a validated ApplicationProfileDefinition. Comparable
/// so a host can skip a snapshot whose selection fields did not change.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ApplicationProfile {
    pub id: Guid,
    pub executable_path: String,
    pub mode: ActivationMode,
    pub enabled: bool,
}

/// The generation must be independently observed for this process instance
/// (for example from its creation time), and shared by the foreground and
/// retained-handle observations. A PID or executable path alone is insufficient.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ProcessIdentity {
    pub pid: NonZeroU32,
    pub generation: NonZeroU64,
}

/// The latest current-user foreground sample. Paths use the same normalization
/// as the validated profiles. Samples must be supplied in observation order.
#[derive(Default)]
pub enum Foreground {
    #[default]
    Unknown,
    Executable {
        path: String,
        process: Option<ProcessIdentity>,
    },
}

impl Foreground {
    fn path(&self) -> Option<&str> {
        match self {
            Self::Unknown => None,
            Self::Executable { path, .. } => Some(path),
        }
    }

    fn process(&self) -> Option<ProcessIdentity> {
        match self {
            Self::Unknown => None,
            Self::Executable { process, .. } => *process,
        }
    }
}

/// An owned observation of the exact process lifetime. Dropping it must release
/// the handle and any exit subscription. Exit events carry the observed identity.
pub trait ProcessHandle {
    fn identity(&self) -> ProcessIdentity;
    /// The normalized full executable path observed from this handle.
    fn executable_path(&self) -> &str;
    /// True only while the retained handle still confirms the process is alive.
    fn is_alive(&self) -> bool;
}

/// Typed counterpart of the managed ReportApplicationContextRequest.
/// A missing executable path is an explicit clear.
#[derive(Debug, Eq, PartialEq)]
pub struct ReportApplicationContextRequest {
    pub version: i32,
    pub request_id: Guid,
    pub instance_id: Guid,
    pub sequence: i64,
    pub executable_path: Option<String>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ReportOutcome {
    Applied,
    Stored,
    Rejected,
    TransportFailed,
}

enum Observation {
    Foreground(Foreground),
    Exited(ProcessIdentity),
}

struct Tracked<H> {
    identity: ProcessIdentity,
    profile_id: Guid,
    path: String,
    activation: u64,
    handle: H,
}

pub struct ApplicationProfileReporter<H> {
    paths_equal: fn(&str, &str) -> bool,
    version: i32,
    instance_id: Guid,
    profiles: Vec<ApplicationProfile>,
    latest_profiles: Option<Vec<ApplicationProfile>>,
    observations: VecDeque<Observation>,
    foreground: Foreground,
    tracked: BTreeMap<NonZeroU32, Tracked<H>>,
    activation: u64,
    submitted_path: Option<String>,
    pending: bool,
    sequence: i64,
    in_flight: Option<i64>,
    lease_tick: u64,
    connected: bool,
    reconnect_pending: bool,
    cancelled: bool,
}

impl<H: ProcessHandle> ApplicationProfileReporter<H> {
    /// Use the current protocol version, a fresh instance GUID and an exact
    /// full-path comparator with the managed OrdinalIgnoreCase semantics.
    pub fn new(version: i32, instance_id: Guid, paths_equal: fn(&str, &str) -> bool) -> Self {
        Self {
            paths_equal,
            version,
            instance_id,
            profiles: Vec::new(),
            latest_profiles: None,
            observations: VecDeque::with_capacity(OBSERVATION_CAPACITY),
            foreground: Foreground::Unknown,
            tracked: BTreeMap::new(),
            activation: 0,
            submitted_path: None,
            pending: false,
            sequence: 0,
            in_flight: None,
            lease_tick: 0,
            connected: true,
            reconnect_pending: false,
            cancelled: false,
        }
    }

    /// Coalesce validated service profile snapshots separately from the
    /// lossy observation queue so a burst cannot evict authoritative changes.
    pub fn observe_profiles(&mut self, profiles: Vec<ApplicationProfile>) {
        if !self.cancelled {
            self.latest_profiles = Some(profiles);
        }
    }

    pub fn observe_foreground(&mut self, foreground: Foreground) {
        self.enqueue(Observation::Foreground(foreground));
    }

    /// A sampled source can retain just the newest observation and its handle
    /// while a report is in flight. Exit notifications still keep their order.
    pub fn observe_latest_foreground(&mut self, foreground: Foreground) {
        self.observations
            .retain(|event| !matches!(event, Observation::Foreground(_)));
        self.observe_foreground(foreground);
    }

    pub fn observe_exit(&mut self, identity: ProcessIdentity) {
        self.enqueue(Observation::Exited(identity));
    }

    fn enqueue(&mut self, observation: Observation) {
        if self.cancelled {
            return;
        }
        if self.observations.len() == OBSERVATION_CAPACITY {
            self.observations.pop_front();
        }
        self.observations.push_back(observation);
    }

    /// Drain one batch and, if due, emit one request. The caller supplies
    /// monotonic elapsed time since startup and a fresh GUID for each send.
    /// While a request is in flight, only the bounded inputs are updated.
    pub fn poll(
        &mut self,
        elapsed: Duration,
        request_id: Guid,
        mut open: impl FnMut(ProcessIdentity) -> Option<H>,
    ) -> Option<ReportApplicationContextRequest> {
        if self.cancelled || self.in_flight.is_some() {
            return None;
        }

        if let Some(profiles) = self.latest_profiles.take() {
            self.profiles = profiles;
            self.tracked.retain(|_, tracked| {
                self.profiles.iter().any(|profile| {
                    profile.enabled
                        && profile.id == tracked.profile_id
                        && profile.mode == ActivationMode::WhileRunningAfterActivation
                        && (self.paths_equal)(&profile.executable_path, &tracked.path)
                })
            });
            self.track_foreground(false, &mut open);
        }
        while let Some(observation) = self.observations.pop_front() {
            match observation {
                Observation::Foreground(foreground) => {
                    let changed = foreground.process() != self.foreground.process()
                        || !self.same_path(foreground.path(), self.foreground.path());
                    self.foreground = foreground;
                    if let Some(identity) = self.foreground.process()
                        && self
                            .tracked
                            .get(&identity.pid)
                            .is_some_and(|old| old.identity != identity)
                    {
                        self.tracked.remove(&identity.pid);
                    }
                    self.track_foreground(changed, &mut open);
                }
                Observation::Exited(identity) => self.retire(identity),
            }
        }

        let tick = elapsed.as_secs() / LEASE_INTERVAL.as_secs();
        let lease_due = tick > self.lease_tick;
        if lease_due || self.reconnect_pending {
            self.lease_tick = self.lease_tick.max(tick);
            self.prune_exited_processes();
        }
        if !self.connected {
            return None;
        }

        let next = self.active_path();
        let changed = !self.same_path(next, self.submitted_path.as_deref());
        let refresh = lease_due && (next.is_some() || self.pending);
        let reconnect = self.reconnect_pending && self.sequence != 0;
        if !(changed || refresh || reconnect) {
            self.reconnect_pending = false;
            return None;
        }
        let executable_path = next.map(str::to_owned);
        self.sequence = self
            .sequence
            .checked_add(1)
            .expect("report sequence exhausted");
        self.submitted_path.clone_from(&executable_path);
        self.pending = true;
        self.reconnect_pending = false;
        self.in_flight = Some(self.sequence);
        Some(ReportApplicationContextRequest {
            version: self.version,
            request_id,
            instance_id: self.instance_id,
            sequence: self.sequence,
            executable_path,
        })
    }

    /// Release dead process handles independently of transport progress.
    pub fn prune_exited_processes(&mut self) {
        self.tracked.retain(|_, tracked| {
            if tracked.handle.is_alive() {
                return true;
            }
            if self.foreground.process() == Some(tracked.identity) {
                self.foreground = Foreground::Unknown;
            }
            false
        });
    }

    /// The foreground is unknown (locked console, secure desktop or another
    /// desktop) but the user's processes are not affected: retained handles,
    /// activation order and the submission watermark all stay.
    pub fn forget_foreground(&mut self) {
        self.observations
            .retain(|observation| matches!(observation, Observation::Exited(_)));
        self.foreground = Foreground::Unknown;
    }

    /// Invalidate every local process after a change of session or user. Retain
    /// the submission watermark so the next poll can clear old intent.
    pub fn forget_process_context(&mut self) {
        self.observations.clear();
        self.foreground = Foreground::Unknown;
        self.tracked.clear();
    }

    pub fn complete(&mut self, sequence: i64, outcome: ReportOutcome) {
        if self.in_flight == Some(sequence) {
            self.in_flight = None;
            self.pending = !matches!(outcome, ReportOutcome::Applied | ReportOutcome::Stored);
        }
    }

    /// The transport owner must cancel the retired send. Late completions cannot
    /// acknowledge a later request. The reporter retains the instance and sequence.
    pub fn disconnected(&mut self) {
        self.connected = false;
        self.in_flight = None;
    }

    /// Reconcile queued evidence and reassert the current context on a new
    /// connection, including a clear when a context was previously submitted.
    pub fn reconnected(&mut self) {
        if !self.cancelled && !self.connected {
            self.connected = true;
            self.reconnect_pending = true;
        }
    }

    /// Stop observation and release retained handles. The transport owner
    /// cancels any outstanding send and stops its timer.
    pub fn cancel(&mut self) {
        self.cancelled = true;
        self.in_flight = None;
        self.observations.clear();
        self.latest_profiles = None;
        self.profiles.clear();
        self.tracked.clear();
        self.foreground = Foreground::Unknown;
        self.submitted_path = None;
    }

    fn same_path(&self, first: Option<&str>, second: Option<&str>) -> bool {
        match (first, second) {
            (Some(first), Some(second)) => (self.paths_equal)(first, second),
            (None, None) => true,
            _ => false,
        }
    }

    fn match_profile<'a>(
        profiles: &'a [ApplicationProfile],
        path: Option<&str>,
        paths_equal: fn(&str, &str) -> bool,
    ) -> Option<&'a ApplicationProfile> {
        let path = path?;
        profiles
            .iter()
            .find(|profile| profile.enabled && paths_equal(&profile.executable_path, path))
    }

    fn active_path(&self) -> Option<&str> {
        Self::match_profile(&self.profiles, self.foreground.path(), self.paths_equal)
            .map(|profile| profile.executable_path.as_str())
            .or_else(|| {
                self.tracked
                    .values()
                    .max_by_key(|tracked| tracked.activation)
                    .map(|tracked| tracked.path.as_str())
            })
    }

    fn track_foreground(
        &mut self,
        refresh_activation: bool,
        open: &mut impl FnMut(ProcessIdentity) -> Option<H>,
    ) {
        let Some(identity) = self.foreground.process() else {
            return;
        };
        let Some(profile) =
            Self::match_profile(&self.profiles, self.foreground.path(), self.paths_equal)
                .filter(|profile| profile.mode == ActivationMode::WhileRunningAfterActivation)
        else {
            return;
        };
        let profile_id = profile.id;

        if let Some(current) = self.tracked.get_mut(&identity.pid)
            && current.identity == identity
            && current.profile_id == profile_id
            && (self.paths_equal)(&current.path, &profile.executable_path)
            && current.handle.is_alive()
        {
            if refresh_activation {
                current.activation = next_activation(&mut self.activation);
            }
            return;
        }

        let Some(handle) = open(identity) else { return };
        if handle.identity() != identity || !handle.is_alive() {
            // The foreground sample is stale or the lifetime could not be
            // confirmed between observation and opening the handle.
            self.foreground = Foreground::Unknown;
            return;
        }
        if !(self.paths_equal)(handle.executable_path(), &profile.executable_path) {
            return;
        }
        let path = profile.executable_path.clone();
        self.tracked.insert(
            identity.pid,
            Tracked {
                identity,
                profile_id,
                path,
                activation: next_activation(&mut self.activation),
                handle,
            },
        );
    }

    fn retire(&mut self, identity: ProcessIdentity) {
        if self
            .tracked
            .get(&identity.pid)
            .is_some_and(|tracked| tracked.identity == identity)
        {
            self.tracked.remove(&identity.pid);
            if self.foreground.process() == Some(identity) {
                self.foreground = Foreground::Unknown;
            }
        }
    }
}

fn next_activation(sequence: &mut u64) -> u64 {
    *sequence = sequence
        .checked_add(1)
        .expect("activation sequence exhausted");
    *sequence
}

#[cfg(test)]
mod tests;
