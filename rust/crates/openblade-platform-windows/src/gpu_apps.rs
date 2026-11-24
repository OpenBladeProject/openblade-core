//! QueryGpuApps projection from Windows observations.
//!
//! The Windows reader binds each process record to the counter
//! observation without reopening a bare PID for its metadata. Two matching
//! identities are required before residency becomes an app row.
//!
//! The separately selected `gpu-close` feature adds the managed
//! `WindowsGpuAppManager` close and force-close actions over a fresh,
//! uncapped scan (`close_windows_gpu_apps`). `action::native` holds the
//! retained-handle backend and lists its deliberate deviations from managed.

use std::{
    cmp::Ordering,
    collections::{BTreeMap, BTreeSet},
    time::Duration,
};

mod sampling;

#[cfg(any(test, feature = "scripted-gpu-close", feature = "gpu-close"))]
mod action;

#[cfg(feature = "gpu-close")]
pub use action::{ActionResult, Kind, Report, Target};
pub use sampling::activity::NvidiaActivityQuery;
#[cfg(feature = "gpu-close")]
pub use sampling::close_windows_gpu_apps;
pub use sampling::sample_windows_gpu_apps;

#[cfg(feature = "scripted-gpu")]
pub use sampling::scripted;

#[cfg(test)]
mod tests;

pub const MAXIMUM_APPS: usize = 128;
const SAMPLE_ATTEMPTS: usize = 20;
const SAMPLE_INTERVAL: Duration = Duration::from_millis(150);

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct NvidiaAdapterLuid {
    pub high: i32,
    pub low: u32,
}

pub struct CurrentUser<'a> {
    /// Canonical binary SID from the current user's token.
    pub sid: &'a [u8],
    pub session_id: u32,
}

pub struct EngineCounter {
    pub instance: String,
    pub status: u32,
    pub utilization_percent: f64,
}

#[derive(Clone, Copy)]
pub struct ProcessIdentity<'a> {
    pub start_time_utc_ticks: i64,
    /// Canonical binary SID from the same process identity as the start time.
    pub user_sid: &'a [u8],
    pub session_id: u32,
    pub name: &'a str,
}

#[derive(Clone, Copy)]
pub enum ProcessObservation<'a> {
    Identified {
        identity: ProcessIdentity<'a>,
        parent_pid: Option<i32>,
    },
    /// The reader detected an exit or conflicting observations for this PID.
    Stale,
}

pub struct Sample<'a> {
    pub counters: &'a [EngineCounter],
    /// Missing keys mean process provenance could not be established.
    pub processes: BTreeMap<i32, ProcessObservation<'a>>,
}

/// Supplies observations from one refreshed NVIDIA counter query. An available
/// sample may contain no counters. A failed read breaks the stability window.
pub trait GpuAppSource {
    fn refresh(&mut self) -> Result<NvidiaAdapterLuid, QueryUnavailable>;
    fn read(&mut self) -> Result<Sample<'_>, QueryUnavailable>;
    fn wait(&mut self, interval: Duration) -> Result<(), QueryUnavailable>;
    /// The host supplies the managed OrdinalIgnoreCase semantics for names,
    /// including non-ASCII executable names. Comparisons must be consistent.
    fn compare_names(&self, left: &str, right: &str) -> Ordering;
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum BlockReason {
    OtherSession,
    OtherAccount,
    Protected,
}

impl BlockReason {
    pub fn message(self) -> &'static str {
        match self {
            Self::OtherSession => "The process belongs to another Windows session.",
            Self::OtherAccount => "The process belongs to another Windows account.",
            Self::Protected => "OpenBlade does not close Windows or OpenBlade processes.",
        }
    }
}

#[derive(Debug, PartialEq)]
pub struct GpuApp {
    pub process_id: i32,
    pub start_time_utc_ticks: i64,
    pub name: String,
    pub utilization_percent: f64,
    pub block_reason: Option<BlockReason>,
}

impl GpuApp {
    /// Inventory eligibility. Any future action requires fresh exact identity admission.
    pub fn can_close(&self) -> bool {
        self.block_reason.is_none()
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum OmissionReason {
    ActivityOnly,
    StaleIdentity,
    InvalidUtilization,
}

#[derive(Debug, PartialEq)]
pub struct GpuAppInventory {
    pub apps: Vec<GpuApp>,
    pub truncated: bool,
    /// Local diagnostics ordered by PID. These do not assert an app identity.
    pub omissions: BTreeMap<i32, OmissionReason>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum QueryUnavailable {
    CurrentUser,
    Counters,
    UnsupportedAdapter,
    AdapterDiscovery(i32),
    AdapterChanged,
    MissingData,
    MalformedCounters,
    NativeCounter(u32),
    Cancelled,
    NameComparison,
}

/// Projects QueryGpuApps rows and the list cap. The source owns cancellation and
/// collection. The Session request/response envelope stays with the host.
pub fn query_gpu_apps(
    source: &mut impl GpuAppSource,
    current_user: &CurrentUser<'_>,
) -> Result<GpuAppInventory, QueryUnavailable> {
    query_gpu_apps_with_limit(source, current_user, MAXIMUM_APPS)
}

fn query_gpu_apps_with_limit(
    source: &mut impl GpuAppSource,
    current_user: &CurrentUser<'_>,
    app_limit: usize,
) -> Result<GpuAppInventory, QueryUnavailable> {
    if current_user.sid.is_empty() {
        return Err(QueryUnavailable::CurrentUser);
    }
    let adapter = source.refresh()?;
    let token = adapter.counter_token();
    let mut primed = false;
    let mut previous = None;
    let mut unavailable = QueryUnavailable::Counters;
    for attempt in 0..SAMPLE_ATTEMPTS {
        match source.read() {
            Ok(_) if !primed => primed = true,
            Ok(sample) => {
                let current = Snapshot::from_sample(sample, &token);
                if let Some(previous) = previous {
                    return Ok(project(
                        previous,
                        current,
                        current_user,
                        app_limit,
                        |left, right| source.compare_names(left, right),
                    ));
                }
                previous = Some(current);
            }
            Err(
                error @ (QueryUnavailable::Counters
                | QueryUnavailable::MissingData
                | QueryUnavailable::MalformedCounters
                | QueryUnavailable::NativeCounter(_)),
            ) => {
                unavailable = error;
                primed = false;
                previous = None;
            }
            Err(error) => return Err(error),
        }
        if attempt + 1 < SAMPLE_ATTEMPTS {
            source.wait(SAMPLE_INTERVAL)?;
        }
    }
    Err(unavailable)
}

struct Process {
    start_time_utc_ticks: i64,
    user_sid: Vec<u8>,
    session_id: u32,
    name: String,
    parent_pid: Option<i32>,
}

struct Snapshot {
    utilization: BTreeMap<i32, f64>,
    processes: BTreeMap<i32, Result<Process, OmissionReason>>,
}

impl Snapshot {
    fn from_sample(sample: Sample<'_>, token: &str) -> Self {
        let mut utilization = BTreeMap::new();
        for counter in sample.counters {
            if let Some(pid) = counter_pid(counter, token) {
                *utilization.entry(pid).or_insert(0.0) += counter.utilization_percent.max(0.0);
            }
        }
        let processes = sample
            .processes
            .iter()
            .map(|(&pid, observation)| {
                let process = match observation {
                    ProcessObservation::Identified {
                        identity,
                        parent_pid,
                    } if pid > 0
                        && identity.start_time_utc_ticks > 0
                        && !identity.user_sid.is_empty()
                        && valid_name(identity.name) =>
                    {
                        Ok(Process {
                            start_time_utc_ticks: identity.start_time_utc_ticks,
                            user_sid: identity.user_sid.to_vec(),
                            session_id: identity.session_id,
                            name: identity.name.to_owned(),
                            parent_pid: *parent_pid,
                        })
                    }
                    ProcessObservation::Identified { .. } => Err(OmissionReason::ActivityOnly),
                    ProcessObservation::Stale => Err(OmissionReason::StaleIdentity),
                };
                (pid, process)
            })
            .collect();
        Self {
            utilization,
            processes,
        }
    }

    fn process(&self, pid: i32) -> Result<&Process, OmissionReason> {
        self.processes
            .get(&pid)
            .ok_or(OmissionReason::ActivityOnly)?
            .as_ref()
            .map_err(|reason| *reason)
    }
}

impl NvidiaAdapterLuid {
    fn counter_token(self) -> String {
        format!("luid_0x{:08x}_0x{:08x}", self.high as u32, self.low)
    }
}

fn counter_pid(counter: &EngineCounter, token: &str) -> Option<i32> {
    if !matches!(counter.status, 0 | 1) || !counter.utilization_percent.is_finite() {
        return None;
    }
    nvidia_pid(&counter.instance, token)
}

fn nvidia_pid(instance: &str, token: &str) -> Option<i32> {
    let instance = instance.to_ascii_lowercase();
    if !instance.contains(token) {
        return None;
    }
    let (digits, _) = instance.strip_prefix("pid_")?.split_once('_')?;
    if digits.is_empty() || !digits.bytes().all(|digit| digit.is_ascii_digit()) {
        return None;
    }
    digits.parse().ok().filter(|pid| *pid > 0)
}

fn valid_name(name: &str) -> bool {
    !name.is_empty()
        && name.trim() == name
        && name.encode_utf16().count() <= 260
        && !name.chars().any(char::is_control)
}

fn project(
    previous: Snapshot,
    current: Snapshot,
    user: &CurrentUser<'_>,
    app_limit: usize,
    compare: impl Fn(&str, &str) -> Ordering,
) -> GpuAppInventory {
    let mut activity: Vec<_> = current
        .utilization
        .iter()
        .filter(|(pid, _)| previous.utilization.contains_key(pid))
        .collect();
    // The managed intersection visits the latest utilization in descending
    // order, then by PID. Preserve that addition order within each group.
    activity.sort_unstable_by(|(left_pid, left), (right_pid, right)| {
        right.total_cmp(left).then_with(|| left_pid.cmp(right_pid))
    });

    let mut apps = BTreeMap::<i32, GpuApp>::new();
    let mut omissions = BTreeMap::new();
    for (&pid, &utilization) in activity {
        let (before, process) = match stable_process(&previous, &current, pid, &compare) {
            Ok(pair) => pair,
            Err(reason) => {
                omissions.insert(pid, reason);
                continue;
            }
        };
        let (target_pid, target) =
            owning_app(pid, before, process, &previous, &current, user, &compare);
        if ["explorer", "nvidia overlay"]
            .iter()
            .any(|name| compare(&target.name, name).is_eq())
        {
            continue;
        }
        let app = apps.entry(target_pid).or_insert_with(|| GpuApp {
            process_id: target_pid,
            start_time_utc_ticks: target.start_time_utc_ticks,
            name: target.name.clone(),
            utilization_percent: 0.0,
            block_reason: block_reason(target, user, &compare),
        });
        app.utilization_percent += utilization;
    }

    apps.retain(|&pid, app| {
        if app.utilization_percent.is_finite() {
            return true;
        }
        omissions.insert(pid, OmissionReason::InvalidUtilization);
        false
    });

    let mut apps: Vec<_> = apps.into_values().collect();
    apps.sort_unstable_by(|left, right| {
        right
            .can_close()
            .cmp(&left.can_close())
            .then_with(|| {
                right
                    .utilization_percent
                    .total_cmp(&left.utilization_percent)
            })
            .then_with(|| compare(&left.name, &right.name))
            .then_with(|| left.process_id.cmp(&right.process_id))
    });
    let truncated = apps.len() > app_limit;
    apps.truncate(app_limit);
    GpuAppInventory {
        apps,
        truncated,
        omissions,
    }
}

fn stable_process<'a>(
    previous: &'a Snapshot,
    current: &'a Snapshot,
    pid: i32,
    compare: &impl Fn(&str, &str) -> Ordering,
) -> Result<(&'a Process, &'a Process), OmissionReason> {
    let before = previous.process(pid);
    let after = current.process(pid);
    if matches!(before, Err(OmissionReason::StaleIdentity))
        || matches!(after, Err(OmissionReason::StaleIdentity))
    {
        return Err(OmissionReason::StaleIdentity);
    }
    let (before, after) = (before?, after?);
    if before.start_time_utc_ticks != after.start_time_utc_ticks
        || before.user_sid != after.user_sid
        || before.session_id != after.session_id
        || !compare(&before.name, &after.name).is_eq()
    {
        return Err(OmissionReason::StaleIdentity);
    }
    Ok((before, after))
}

fn owning_app<'a>(
    mut pid: i32,
    mut before: &'a Process,
    mut process: &'a Process,
    previous: &'a Snapshot,
    current: &'a Snapshot,
    user: &CurrentUser<'_>,
    compare: &impl Fn(&str, &str) -> Ordering,
) -> (i32, &'a Process) {
    let mut visited = BTreeSet::from([pid]);
    while let Some(parent_pid) = process.parent_pid
        && before.parent_pid == Some(parent_pid)
        && process.user_sid == user.sid
        && visited.insert(parent_pid)
        && let Ok((prior_parent, parent)) = stable_process(previous, current, parent_pid, compare)
        && parent.session_id == process.session_id
        && parent.user_sid == user.sid
        && parent.start_time_utc_ticks <= process.start_time_utc_ticks
        && !is_protected(&parent.name, compare)
        && compare(&parent.name, &process.name).is_eq()
    {
        pid = parent_pid;
        before = prior_parent;
        process = parent;
    }
    (pid, process)
}

fn block_reason(
    process: &Process,
    user: &CurrentUser<'_>,
    compare: &impl Fn(&str, &str) -> Ordering,
) -> Option<BlockReason> {
    if process.session_id != user.session_id {
        Some(BlockReason::OtherSession)
    } else if process.user_sid != user.sid {
        Some(BlockReason::OtherAccount)
    } else if is_protected(&process.name, compare) {
        Some(BlockReason::Protected)
    } else {
        None
    }
}

fn is_protected(name: &str, compare: &impl Fn(&str, &str) -> Ordering) -> bool {
    const PREFIX: &str = "OpenBlade";
    const PROTECTED: &[&str] = &[
        "applicationframehost",
        "csrss",
        "dwm",
        "explorer",
        "fontdrvhost",
        "idle",
        "lsass",
        "registry",
        "searchhost",
        "secure system",
        "services",
        "shellexperiencehost",
        "sihost",
        "smss",
        "startmenuexperiencehost",
        "system",
        "taskhostw",
        "textinputhost",
        "wininit",
        "winlogon",
    ];
    name.get(..PREFIX.len())
        .is_some_and(|prefix| compare(prefix, PREFIX).is_eq())
        || PROTECTED
            .iter()
            .any(|protected| compare(name, protected).is_eq())
}
