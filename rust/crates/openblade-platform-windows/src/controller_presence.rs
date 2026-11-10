//! Read-only competing-controller classification for the Windows Rust fixture.

#[cfg(feature = "controller-process-compile-check")]
pub mod admission;
#[cfg(feature = "controller-process-compile-check")]
pub mod process_inventory;
#[cfg(feature = "controller-process-compile-check")]
pub mod service_inventory;

/// Collects read-only native evidence for development callers. Patch, helper
/// image verification and LampArray acquisition are unresolved in this path.
#[cfg(feature = "controller-process-compile-check")]
pub fn read_native() -> Observation {
    let processes = process_inventory::read();
    let services = service_inventory::read();
    let service_rows = services.service_rows();
    observe_inventory(
        &processes,
        services.scan(&service_rows),
        process_inventory::now(),
    )
}

#[cfg(feature = "controller-process-compile-check")]
fn observe_inventory(
    processes: &process_inventory::ProcessInventory,
    services: Scan<'_, Service<'_>>,
    observed_at: u64,
) -> Observation {
    let process_rows = processes.process_rows();
    let parents = processes.parent_snapshot();
    observe(
        if processes.is_complete() && parents.started != 0 && parents.started <= observed_at {
            Scan::Complete(&process_rows)
        } else {
            Scan::Incomplete(&process_rows)
        },
        services,
        Some(&parents),
        // The native process rows carry no verified image. Keep LampArray
        // unknown as well so the peripheral-only exception stays unavailable.
        Scan::Incomplete(&[]),
        observed_at,
    )
}

const ENGINE: &str = "RazerAppEngine";
const HELPER: &str = "razerwdl";
const UPDATER: &str = "RGS";
const UPDATER_DISPLAY: &str = "Razer Gaming Services Updater";
const SYNAPSE_SERVICES: &[&str] = &["RazerSynapseService", "Razer Synapse Service"];
const CHROMA_SERVICES: &[&str] = &[
    "Razer Chroma SDK Diagnostic Service",
    "Razer Chroma SDK Server",
    "Razer Chroma SDK Service",
    "Razer Chroma Stream Server",
];

#[derive(Clone, Copy)]
pub enum Scan<'a, T> {
    Complete(&'a [T]),
    Incomplete(&'a [T]),
}

impl<'a, T> Scan<'a, T> {
    fn rows(&self) -> &'a [T] {
        match self {
            Self::Complete(rows) | Self::Incomplete(rows) => rows,
        }
    }

    fn is_complete(&self) -> bool {
        matches!(self, Self::Complete(_))
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ProcessIdentity {
    pub pid: u32,
    pub session: u32,
    /// Windows FILETIME, on the same clock as the snapshot start.
    pub created: u64,
}

#[derive(Clone, Copy)]
pub enum VerifiedImage<'a> {
    /// Excluded product IDs from the verified, running Synapse patch.
    EngineExclusions(&'a [u16]),
    /// Verified exact LampArray path and version metadata.
    LampArrayHelper,
}

#[derive(Clone, Copy)]
pub struct Process<'a> {
    /// Basename without the executable extension.
    pub name: &'a str,
    pub pid: Option<u32>,
    pub identity: Option<ProcessIdentity>,
    /// Verification must be from the same process instance as the observation.
    pub verified_image: Option<(ProcessIdentity, VerifiedImage<'a>)>,
}

pub struct ParentSnapshot<'a> {
    pub started: u64,
    pub links: Scan<'a, (u32, u32)>,
}

#[derive(Clone, Copy)]
pub enum ServiceState {
    Running,
    StartPending,
    ContinuePending,
    Inactive,
    Unknown,
}

pub struct Service<'a> {
    pub name: &'a str,
    pub display_name: &'a str,
    pub state: ServiceState,
}

pub struct LampArray {
    pub vendor: u16,
    pub product: u16,
}

#[derive(Debug, PartialEq, Eq)]
pub enum DetectionWarning {
    ProcessScan,
    ServiceScan,
    ProcessAndServiceScans,
}

#[derive(Debug, PartialEq, Eq)]
pub struct Observation {
    potential_controllers: Vec<String>,
    hyperboost_blockers: Vec<String>,
    warning: Option<DetectionWarning>,
}

impl Observation {
    pub fn potential_controllers(&self) -> &[String] {
        &self.potential_controllers
    }

    pub fn hyperboost_blockers(&self) -> &[String] {
        &self.hyperboost_blockers
    }

    pub fn warning(&self) -> Option<&DetectionWarning> {
        self.warning.as_ref()
    }

    pub fn confirms_absence(&self) -> bool {
        self.warning.is_none() && self.potential_controllers.is_empty()
    }
}

pub fn observe(
    processes: Scan<'_, Process<'_>>,
    services: Scan<'_, Service<'_>>,
    parents: Option<&ParentSnapshot<'_>>,
    lamps: Scan<'_, LampArray>,
    observed_at: u64,
) -> Observation {
    let process_rows = processes.rows();
    let engines: Vec<_> = process_rows.iter().filter(|p| eq(p.name, ENGINE)).collect();
    let helpers: Vec<_> = process_rows.iter().filter(|p| eq(p.name, HELPER)).collect();
    let exclusions: Option<Vec<_>> = engines
        .iter()
        .map(
            |process| match verified(process, process_rows, observed_at)?.1 {
                VerifiedImage::EngineExclusions(ids)
                    if ids.contains(&0x02e0) && ids.contains(&0x02c6) =>
                {
                    Some(ids)
                }
                _ => None,
            },
        )
        .collect();
    let patched = processes.is_complete() && !engines.is_empty() && exclusions.is_some();
    let verified_helpers = processes.is_complete()
        && !helpers.is_empty()
        && helpers.iter().all(|helper| {
            matches!(
                verified(helper, process_rows, observed_at),
                Some((_, VerifiedImage::LampArrayHelper))
            )
        });
    let peripheral_only = patched
        && verified_helpers
        && parents.is_some_and(|parents| {
            parents.links.is_complete()
                && parents.started <= observed_at
                && helpers
                    .iter()
                    .all(|helper| is_engine_child(helper, &engines, parents))
        })
        && lamps.is_complete()
        && lamps.rows().iter().all(|lamp| {
            lamp.vendor != 0
                && lamp.product != 0
                && (lamp.vendor != 0x1532
                    || !exclusions
                        .iter()
                        .flatten()
                        .any(|ids| ids.contains(&lamp.product)))
        });

    let mut potential = Vec::new();
    let mut blockers = Vec::new();
    for process in process_rows {
        let name = process.name;
        if !(eq(name, ENGINE) || eq(name, HELPER) || known_service(name))
            || (patched && eq(name, ENGINE))
            || (peripheral_only && eq(name, HELPER))
        {
            continue;
        }
        push_distinct(&mut potential, name);
        if updater_alias(name)
            || (eq(name, HELPER) && !verified_helpers)
            || (thermal_blocker(name) && !(patched && synapse_service(name)))
        {
            push_distinct(&mut blockers, name);
        }
    }

    let mut service_failed = !services.is_complete();
    for service in services.rows() {
        if !(known_service(service.name) || display_alias(service.display_name)) {
            continue;
        }
        match service.state {
            ServiceState::Running | ServiceState::StartPending | ServiceState::ContinuePending => {}
            ServiceState::Inactive => continue,
            ServiceState::Unknown => {
                service_failed = true;
                continue;
            }
        }
        let name = if known_service(service.display_name) {
            service.display_name
        } else {
            service.name
        };
        push_distinct(&mut potential, name);
        let synapse = synapse_service(service.name) || synapse_service(service.display_name);
        if !eq(service.name, UPDATER)
            && (updater_alias(name) || (synapse && !patched) || (!synapse && thermal_blocker(name)))
        {
            push_distinct(&mut blockers, name);
        }
    }

    potential.sort_unstable();
    blockers.sort_unstable();
    Observation {
        potential_controllers: potential,
        hyperboost_blockers: blockers,
        warning: match (!processes.is_complete(), service_failed) {
            (false, false) => None,
            (true, false) => Some(DetectionWarning::ProcessScan),
            (false, true) => Some(DetectionWarning::ServiceScan),
            (true, true) => Some(DetectionWarning::ProcessAndServiceScans),
        },
    }
}

fn verified<'a>(
    process: &Process<'a>,
    rows: &[Process<'_>],
    observed_at: u64,
) -> Option<(ProcessIdentity, VerifiedImage<'a>)> {
    let identity = process.identity?;
    let proof = process.verified_image?;
    // PID alone cannot attach image evidence to a reused process.
    (identity == proof.0
        && process.pid == Some(identity.pid)
        && identity.pid > 0
        && identity.created > 0
        && identity.created <= observed_at
        && rows.iter().filter(|row| row.pid == process.pid).count() == 1)
        .then_some(proof)
}

fn is_engine_child(
    helper: &Process<'_>,
    engines: &[&Process<'_>],
    snapshot: &ParentSnapshot<'_>,
) -> bool {
    let Some(child) = helper.identity else {
        return false;
    };
    if child.created >= snapshot.started {
        return false;
    }
    let mut parents = snapshot
        .links
        .rows()
        .iter()
        .filter(|&&(pid, _)| pid == child.pid);
    let Some(&(_, parent)) = parents.next() else {
        return false;
    };
    parents.next().is_none()
        && engines.iter().any(|engine| {
            engine.identity.is_some_and(|identity| {
                identity.pid == parent
                    && identity.session == child.session
                    && identity.created < child.created
            })
        })
}

fn eq(left: &str, right: &str) -> bool {
    left.eq_ignore_ascii_case(right)
}

fn push_distinct(names: &mut Vec<String>, name: &str) {
    if !names.iter().any(|entry| eq(entry, name)) {
        names.push(name.to_owned());
    }
}

fn synapse_service(name: &str) -> bool {
    SYNAPSE_SERVICES.iter().any(|alias| eq(name, alias))
}

fn chroma_service(name: &str) -> bool {
    CHROMA_SERVICES.iter().any(|alias| eq(name, alias))
}

fn display_alias(name: &str) -> bool {
    synapse_service(name) || chroma_service(name) || eq(name, UPDATER_DISPLAY)
}

fn known_service(name: &str) -> bool {
    display_alias(name) || eq(name, UPDATER)
}

fn updater_alias(name: &str) -> bool {
    eq(name, UPDATER) || eq(name, UPDATER_DISPLAY)
}

fn thermal_blocker(name: &str) -> bool {
    !(chroma_service(name) || eq(name, HELPER) || updater_alias(name))
}

#[cfg(test)]
mod tests;
