//! Shared close decision over a fresh GPU inventory. The scripted fixture
//! (`scripted-gpu-close`) and the retained-handle Windows backend
//! (`gpu-close`, see `native`) supply the process observations and actions.

use super::{
    CurrentUser, GpuAppInventory, MAXIMUM_APPS, is_protected, sampling::executable_stem, valid_name,
};
use std::{
    cmp::Ordering,
    collections::BTreeSet,
    sync::atomic::{AtomicBool, Ordering::Relaxed},
};

const STALE: &str =
    "The process no longer holds GPU resources or its identity changed after confirmation.";
const CHANGED: &str = "The process exited or its identity changed after the GPU scan.";
const ACTION_FAILED: &str = "Windows could not close this process.";
const NOT_ATTEMPTED: &str = "This process was not attempted because the GPU app operation stopped.";
const STOPPED: &str =
    "The GPU app operation stopped before all selected applications were processed.";
const UNCERTAIN: &str = "The close submission could not be confirmed.";

pub struct Target {
    pub process_id: i32,
    pub start_time_utc_ticks: i64,
    pub name: String,
}

fn valid_targets(targets: &[Target], kind: Kind) -> bool {
    let mut seen = BTreeSet::new();
    !targets.is_empty()
        && targets.len() <= MAXIMUM_APPS
        && (kind != Kind::ForceClose || targets.len() == 1)
        && targets.iter().all(|target| {
            target.process_id > 0
                && target.start_time_utc_ticks > 0
                && valid_name(&target.name)
                && seen.insert(target.process_id)
        })
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Kind {
    Close,
    ForceClose,
}

pub struct ActionResult {
    pub process_id: i32,
    #[cfg_attr(test, allow(dead_code))] // Read by the Session Agent's close report.
    pub start_time_utc_ticks: i64,
    pub error: Option<&'static str>,
}

pub struct Report {
    pub results: Vec<ActionResult>,
    pub error: Option<&'static str>,
}

#[derive(Clone, Copy)]
struct Observation<'a> {
    process_id: i32,
    start_time_utc_ticks: i64,
    user_sid: &'a [u8],
    session_id: u32,
    image_path: &'a str,
    exited: bool,
    // Separate action evidence. An unresolved parent can leave a helper
    // visible in inventory without establishing it as the app to close.
    owning_process_id: Option<i32>,
}

trait RetainedTarget {
    fn observe(&self) -> Observation<'_>;
    fn submit(&mut self, kind: Kind) -> Submission;
}

trait Backend {
    type Retained: RetainedTarget;

    // None is an exited/unavailable target. Err is a definite failure before submission.
    fn retain(&mut self, process_id: i32) -> Result<Option<Self::Retained>, ()>;
}

#[derive(Clone, Copy)]
enum Submission {
    Accepted,
    NoWindow,
    #[cfg_attr(
        not(any(test, feature = "gpu-close")),
        expect(dead_code, reason = "produced by the Windows and test backends")
    )]
    Rejected,
    #[cfg_attr(
        not(any(test, feature = "gpu-close")),
        expect(dead_code, reason = "produced by the Windows and test backends")
    )]
    Uncertain,
}

fn decide(
    inventory: &GpuAppInventory,
    targets: &[Target],
    kind: Kind,
    user: &CurrentUser<'_>,
    compare: impl Fn(&str, &str) -> Ordering,
    cancelled: &AtomicBool,
    backend: &mut impl Backend,
) -> Report {
    // The wire parser rejects malformed targets before sampling. Keep the seam
    // closed if a caller supplies an invalid force request or absent owner.
    if kind == Kind::ForceClose && targets.len() != 1 {
        return Report {
            results: vec![],
            error: Some("Force close requires exactly one confirmed application."),
        };
    }
    if !valid_targets(targets, kind) {
        return Report {
            results: vec![],
            error: Some("The GPU app close targets are invalid."),
        };
    }
    if user.sid.is_empty() {
        return Report {
            results: vec![],
            error: Some("The current Windows user is unavailable."),
        };
    }

    let mut results = Vec::with_capacity(targets.len());
    let mut matched = Vec::new();
    for target in targets {
        let result = ActionResult {
            process_id: target.process_id,
            start_time_utc_ticks: target.start_time_utc_ticks,
            error: Some(STALE),
        };
        if let Some(app) = inventory.apps.iter().find(|app| {
            app.process_id == target.process_id
                && app.start_time_utc_ticks == target.start_time_utc_ticks
                && compare(&app.name, &target.name).is_eq()
        }) {
            matched.push((app, result));
        } else {
            results.push(result);
        }
    }

    // As in the managed dispatcher, stale confirmations come first.
    let mut uncertain = false;
    let mut interrupted = false;
    for (app, mut result) in matched {
        result.error = if uncertain || cancelled.load(Relaxed) {
            interrupted = true;
            Some(NOT_ATTEMPTED)
        } else if let Some(reason) = app.block_reason {
            Some(reason.message())
        } else {
            match backend.retain(app.process_id) {
                Ok(None) => Some(CHANGED),
                Err(()) => Some(ACTION_FAILED),
                Ok(Some(mut retained)) => {
                    let actual = retained.observe();
                    // The same `Process.ProcessName` projection the scan named
                    // the row with; the handle, creation time, user and session
                    // carry the identity.
                    let name = executable_stem(actual.image_path);
                    let name = name.as_deref();
                    if actual.exited
                        || actual.process_id != app.process_id
                        || actual.start_time_utc_ticks != app.start_time_utc_ticks
                        || actual.user_sid != user.sid
                        || actual.session_id != user.session_id
                        || actual.owning_process_id != Some(app.process_id)
                        || !name.is_some_and(|name| compare(name, &app.name).is_eq())
                    {
                        Some(CHANGED)
                    } else if name.is_some_and(|name| is_protected(name, &compare)) {
                        Some("OpenBlade does not close protected processes.")
                    } else if cancelled.load(Relaxed) {
                        interrupted = true;
                        Some(NOT_ATTEMPTED)
                    } else {
                        match retained.submit(kind) {
                            Submission::Accepted => None,
                            Submission::NoWindow if kind == Kind::Close => {
                                Some("The application has no window that can be closed gracefully.")
                            }
                            Submission::NoWindow | Submission::Rejected => Some(ACTION_FAILED),
                            Submission::Uncertain => {
                                // The original request may have taken effect. Never
                                // retry, fall back, or continue with another target.
                                uncertain = true;
                                Some(UNCERTAIN)
                            }
                        }
                    }
                }
            }
        };
        results.push(result);
    }

    let succeeded = results
        .iter()
        .filter(|result| result.error.is_none())
        .count();
    let error = if uncertain {
        Some(UNCERTAIN)
    } else if interrupted {
        Some(STOPPED)
    } else if succeeded == results.len() {
        None
    } else if succeeded == 0 {
        Some("The selected applications could not be closed.")
    } else {
        Some("Some applications could not be closed.")
    };
    Report { results, error }
}

// The scripted backend owns fixed records. No request value becomes an
// image path or is sent to the operating system.
#[cfg(feature = "scripted-gpu-close")]
pub(super) mod fixture;

// The Windows backend holds each target handle across its identity recheck
// and action. Its policy is compiled for tests over a scripted process API.
#[cfg(any(test, feature = "gpu-close"))]
pub(super) mod native;

#[cfg(test)]
mod tests;
