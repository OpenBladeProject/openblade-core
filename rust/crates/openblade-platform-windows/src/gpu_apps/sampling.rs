//! Read-only sampling policy over a narrow Windows observation interface.

use std::sync::atomic::{AtomicBool, Ordering as AtomicOrdering};

use super::*;
use QueryUnavailable as Unavailable;

pub(crate) mod activity;
mod native;
#[cfg(feature = "scripted-gpu")]
pub mod scripted;
#[cfg(test)]
mod tests;

const MAX_COUNTER_BYTES: u32 = 16 * 1024 * 1024;
const DOTNET_FILETIME_OFFSET: i64 = 504_911_232_000_000_000;
const MAX_DATETIME_TICKS: i64 = 3_155_378_975_999_999_999;

/// Samples on the calling thread. A host should use a worker and set the
/// cancellation flag to interrupt polling. An in-flight OS call must return
/// before its resources can be released.
pub fn sample_windows_gpu_apps(
    cancelled: &AtomicBool,
) -> Result<GpuAppInventory, QueryUnavailable> {
    let mut api = native::WindowsSampling::default();
    let inventory = scan(&mut api, cancelled)?;
    if api.comparison_failed.get() {
        return Err(Unavailable::NameComparison);
    }
    Ok(inventory)
}

/// Managed `SessionGpuRequestDispatcher` close over `WindowsGpuAppManager`:
/// a fresh scan, matched against its complete projection (only a response
/// caps the rows), then the shared decision with the retained-handle backend.
/// Runs on the calling thread; the flag interrupts sampling, stops before each
/// target and ends a force-close confirmation wait as uncertain.
#[cfg(feature = "gpu-close")]
pub fn close_windows_gpu_apps(
    targets: &[Target],
    kind: Kind,
    cancelled: &AtomicBool,
) -> Result<(GpuAppInventory, Report), QueryUnavailable> {
    let mut api = native::WindowsSampling::default();
    let (inventory, (sid, session_id)) = scan_for_user(&mut api, cancelled, usize::MAX)?;
    if api.comparison_failed.get() {
        return Err(Unavailable::NameComparison);
    }
    let report = super::action::native::close_windows(
        &inventory,
        targets,
        kind,
        &CurrentUser {
            sid: &sid,
            session_id,
        },
        &|left, right| api.compare_names(left, right),
        &api.comparison_failed,
        cancelled,
    );
    Ok((inventory, report))
}

fn scan(
    api: &mut impl SamplingApi,
    cancelled: &AtomicBool,
) -> Result<GpuAppInventory, Unavailable> {
    scan_with_limit(api, cancelled, MAXIMUM_APPS)
}

fn scan_with_limit(
    api: &mut impl SamplingApi,
    cancelled: &AtomicBool,
    app_limit: usize,
) -> Result<GpuAppInventory, Unavailable> {
    scan_for_user(api, cancelled, app_limit).map(|(inventory, _)| inventory)
}

/// The inventory and the current user it was projected for. A close decision
/// must use the same user as the scan it matches against.
fn scan_for_user(
    api: &mut impl SamplingApi,
    cancelled: &AtomicBool,
    app_limit: usize,
) -> Result<(GpuAppInventory, (Vec<u8>, u32)), Unavailable> {
    check_cancelled(cancelled)?;
    let user = api.current_user();
    check_cancelled(cancelled)?;
    let (sid, session_id) = user.ok_or(Unavailable::CurrentUser)?;
    let mut source = SamplingSource {
        api,
        cancelled,
        query: None,
        adapter: None,
        counters: Vec::new(),
        identities: BTreeMap::new(),
    };
    let inventory = query_gpu_apps_with_limit(
        &mut source,
        &CurrentUser {
            sid: &sid,
            session_id,
        },
        app_limit,
    );
    check_cancelled(cancelled)?;
    Ok((inventory?, (sid, session_id)))
}

fn check_cancelled(cancelled: &AtomicBool) -> Result<(), Unavailable> {
    if cancelled.load(AtomicOrdering::Relaxed) {
        return Err(Unavailable::Cancelled);
    }
    Ok(())
}

trait SamplingApi {
    type Query;

    fn current_user(&mut self) -> Option<(Vec<u8>, u32)>;
    fn locate_adapter(&mut self) -> Result<NvidiaAdapterLuid, Unavailable>;
    fn open_query(&mut self) -> Result<Self::Query, Unavailable>;
    fn collect(&mut self, query: &mut Self::Query) -> Result<i64, Unavailable>;
    fn counter_array(
        &mut self,
        query: &mut Self::Query,
        bytes: u32,
    ) -> Result<CounterArray, Unavailable>;
    fn inspect(&mut self, pid: i32) -> Inspection;
    fn read_parents(&mut self, cancelled: &AtomicBool) -> BTreeMap<i32, i32>;
    fn pause(&mut self, interval: Duration, cancelled: &AtomicBool);
    fn compare_names(&self, left: &str, right: &str) -> Ordering;
}

enum CounterArray {
    Size(u32),
    Resized(u32),
    Items(Vec<EngineCounter>),
}

enum Inspection {
    Identified(Identity),
    Stale,
    Unavailable,
}

struct Identity {
    created_filetime: i64,
    user_sid: Vec<u8>,
    session_id: u32,
    name: String,
}

struct SamplingSource<'a, A: SamplingApi> {
    api: &'a mut A,
    cancelled: &'a AtomicBool,
    query: Option<A::Query>,
    adapter: Option<(NvidiaAdapterLuid, String)>,
    counters: Vec<EngineCounter>,
    identities: BTreeMap<i32, Inspection>,
}

impl<A: SamplingApi> GpuAppSource for SamplingSource<'_, A> {
    fn refresh(&mut self) -> Result<NvidiaAdapterLuid, Unavailable> {
        check_cancelled(self.cancelled)?;
        let adapter = self.api.locate_adapter()?;
        check_cancelled(self.cancelled)?;
        self.query = Some(self.api.open_query()?);
        self.adapter = Some((adapter, adapter.counter_token()));
        Ok(adapter)
    }

    fn read(&mut self) -> Result<Sample<'_>, Unavailable> {
        check_cancelled(self.cancelled)?;
        let (expected, token) = self.adapter.as_ref().ok_or(Unavailable::Counters)?;
        let query = self.query.as_mut().ok_or(Unavailable::Counters)?;
        let collected = self.api.collect(query);
        check_cancelled(self.cancelled)?;
        let sample_time = collected?;
        if dotnet_ticks(sample_time).is_none() {
            return Err(Unavailable::MalformedCounters);
        }
        self.counters = read_counter_array(self.api, query, self.cancelled)?;
        self.identities.clear();
        let pids: BTreeSet<_> = self
            .counters
            .iter()
            .filter_map(|counter| counter_pid(counter, token))
            .collect();
        let parents = if pids.is_empty() {
            BTreeMap::new()
        } else {
            self.api.read_parents(self.cancelled)
        };
        for mut pid in pids {
            // Inspect each PID at most once per frame. This also stops cycles
            // and retains an inaccessible parent as a boundary for all helpers.
            while let std::collections::btree_map::Entry::Vacant(entry) = self.identities.entry(pid)
            {
                check_cancelled(self.cancelled)?;
                let inspection = self.api.inspect(pid);
                let observed = matches!(
                    &inspection,
                    Inspection::Identified(identity) if identity.created_filetime <= sample_time
                );
                entry.insert(inspection);
                if !observed {
                    break;
                }
                let Some(&parent) = parents.get(&pid) else {
                    break;
                };
                pid = parent;
            }
        }

        check_cancelled(self.cancelled)?;
        // Every available frame must still be bound to the adapter which
        // opened the query. A disappearing adapter invalidates the scan.
        match self.api.locate_adapter() {
            Ok(actual) if actual == *expected => {}
            Ok(_) | Err(Unavailable::UnsupportedAdapter) => {
                return Err(Unavailable::AdapterChanged);
            }
            Err(error) => return Err(error),
        }
        check_cancelled(self.cancelled)?;

        let processes = self
            .identities
            .iter()
            .filter_map(|(&pid, identity)| {
                let parent_pid = parents.get(&pid).copied();
                project_identity(identity, sample_time, parent_pid)
                    .map(|observation| (pid, observation))
            })
            .collect();
        Ok(Sample {
            counters: &self.counters,
            processes,
        })
    }

    fn wait(&mut self, interval: Duration) -> Result<(), Unavailable> {
        check_cancelled(self.cancelled)?;
        self.api.pause(interval, self.cancelled);
        check_cancelled(self.cancelled)
    }

    fn compare_names(&self, left: &str, right: &str) -> Ordering {
        self.api.compare_names(left, right)
    }
}

fn read_counter_array<A: SamplingApi>(
    api: &mut A,
    query: &mut A::Query,
    cancelled: &AtomicBool,
) -> Result<Vec<EngineCounter>, Unavailable> {
    for attempt in 0..2 {
        check_cancelled(cancelled)?;
        let CounterArray::Size(bytes) = api.counter_array(query, 0)? else {
            return Err(Unavailable::MalformedCounters);
        };
        if bytes == 0 {
            return Ok(Vec::new());
        }
        if bytes > MAX_COUNTER_BYTES {
            return Err(Unavailable::MalformedCounters);
        }
        check_cancelled(cancelled)?;
        match api.counter_array(query, bytes)? {
            CounterArray::Items(counters) => return Ok(counters),
            // Microsoft documents the size returned by a short fill as
            // unreliable. Repeat sizing from zero once, then stop.
            CounterArray::Resized(_) if attempt == 0 => {}
            CounterArray::Resized(status) => return Err(Unavailable::NativeCounter(status)),
            CounterArray::Size(_) => return Err(Unavailable::MalformedCounters),
        }
    }
    unreachable!("the second fill returns")
}

fn project_identity(
    inspection: &Inspection,
    sample_time: i64,
    parent_pid: Option<i32>,
) -> Option<ProcessObservation<'_>> {
    match inspection {
        Inspection::Stale => Some(ProcessObservation::Stale),
        Inspection::Unavailable => None,
        Inspection::Identified(record) => {
            // A process created after collection cannot own that observation
            // even if Windows has already reused the numeric PID.
            if record.created_filetime > sample_time {
                return Some(ProcessObservation::Stale);
            }
            Some(ProcessObservation::Identified {
                identity: ProcessIdentity {
                    start_time_utc_ticks: dotnet_ticks(record.created_filetime)?,
                    user_sid: &record.user_sid,
                    session_id: record.session_id,
                    name: &record.name,
                },
                parent_pid,
            })
        }
    }
}

pub(in crate::gpu_apps) fn dotnet_ticks(filetime: i64) -> Option<i64> {
    let ticks = filetime.checked_add(DOTNET_FILETIME_OFFSET)?;
    (filetime > 0 && ticks <= MAX_DATETIME_TICKS).then_some(ticks)
}

fn process_name(path: &[u16]) -> Option<String> {
    executable_stem(&String::from_utf16(path).ok()?)
}

/// The managed `Process.ProcessName` projection of an image path: the leaf
/// name without a final `.exe`.
pub(in crate::gpu_apps) fn executable_stem(path: &str) -> Option<String> {
    let file = path.rsplit('\\').next()?;
    let name = match file.rsplit_once('.') {
        Some((stem, extension)) if extension.eq_ignore_ascii_case("exe") => stem,
        _ => file,
    };
    valid_name(name).then(|| name.to_owned())
}
