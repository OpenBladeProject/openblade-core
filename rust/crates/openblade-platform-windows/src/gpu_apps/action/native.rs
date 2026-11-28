//! Retained-handle close backend, a port of managed `WindowsGpuAppManager`
//! `CloseGracefully` and `ForceClose`.
//!
//! Each confirmed row is opened once. PID, creation time, user SID, session,
//! image, exit state and parent are re-read on that held handle, and the same
//! handle is the one terminated or whose process owns the window that is
//! asked to close. Holding it pins the PID, so no other process can take the
//! identity between the recheck and the action. Policy runs over
//! [`ProcessApi`] so tests use a scripted process table; only the separately
//! selected `gpu-close` feature compiles the Windows bindings.
//!
//! Graceful close is `Process.CloseMainWindow`: the first visible top-level
//! window without an owner, refused when disabled, then `WM_CLOSE`. Force
//! close is `Process.Kill(entireProcessTree: true)`: terminate the target,
//! then each descendant whose parent link and later creation time are read on
//! its own handle, unless the calling Agent is in that tree. As managed
//! `KillTree` holds each child until its own subtree is done, every
//! descendant's handle stays open until its children have been enumerated,
//! so its PID cannot pass to an unrelated process in between.
//!
//! Matching managed by an explicit check: managed opens the target with
//! `PROCESS_ALL_ACCESS`, which Windows refuses for a process above the
//! caller's integrity level, and never recognises such a process as a child.
//! Both actions therefore read the mandatory integrity level from the token of
//! each held process and act only at or below the Agent's own level. A higher
//! or unreadable level refuses the target before any post or termination, and
//! force close skips such a descendant with its subtree.
//!
//! Deliberate deviations from managed:
//! - The handle asks only for query, synchronize and, for force close,
//!   terminate rights, not `PROCESS_ALL_ACCESS`. The integrity check above
//!   removes the cross-integrity difference. Within one integrity level a
//!   difference remains: a hardened same-user process whose DACL denies
//!   `PROCESS_ALL_ACCESS` but grants these three rights can be force-closed by
//!   native, where managed fails to open it.
//! - A root that exits between its identity read and its integrity read has no
//!   readable token, so it reports the action failure and no tree is walked.
//!   Managed instead treats a process that has already exited as closed.
//! - A failed `PostMessageW` is "Windows could not close this process.";
//!   managed ignores the result and reports success.
//! - A submitted termination must be observed as process exit within
//!   [`EXIT_CONFIRMATION`]. A timeout, a failed wait or Stop during that wait is
//!   uncertain: managed reports success once `TerminateProcess` returns.
//! - No exact-process fallback: a definite failure to terminate the target is
//!   reported once, and descendants are terminated only after the target's
//!   exit is confirmed. Managed walks descendants even when the target refused
//!   termination, then retries the exact target.
//! - Descendants must also belong to the current user and session and must not
//!   carry a protected name; at most [`MAX_TREE`] are visited and Stop ends
//!   the walk. A descendant failure leaves the confirmed result, as managed.
//! - The owning app is rechecked on the held handle: a parent that the scan
//!   would have grouped this process under makes the row stale.
//! - A failed host name comparison refuses the submission.

use super::{Backend, Kind, Observation, Report, RetainedTarget, Submission, Target, decide};
use crate::gpu_apps::{
    CurrentUser, GpuAppInventory, is_protected,
    sampling::{dotnet_ticks, executable_stem},
};
use std::{
    cell::{Cell, RefCell},
    cmp::Ordering,
    collections::{BTreeSet, VecDeque},
    sync::atomic::{AtomicBool, Ordering::Relaxed},
    time::Duration,
};

#[cfg(test)]
mod tests;
#[cfg(feature = "gpu-close")]
mod windows;

/// Bound on the wait for a submitted termination to become process exit.
/// It fits inside the managed 20-second GPU dispatch deadline after a scan.
const EXIT_CONFIRMATION: Duration = Duration::from_secs(5);
/// Bound on the descendants a force close visits.
const MAX_TREE: usize = 256;
/// Bound on the Agent ancestry walked to detect that it is in the tree.
const MAX_ANCESTRY: usize = 64;

/// Identity read on one held process handle.
struct Held {
    process_id: i32,
    created_filetime: i64,
    user_sid: Vec<u8>,
    session_id: u32,
    image_path: String,
    parent_process_id: i32,
    exited: bool,
}

impl Held {
    /// An exited process supplies no identity; every comparison fails.
    #[cfg_attr(not(feature = "gpu-close"), allow(dead_code))]
    fn exited() -> Self {
        Self {
            process_id: 0,
            created_filetime: 0,
            user_sid: Vec::new(),
            session_id: 0,
            image_path: String::new(),
            parent_process_id: 0,
            exited: true,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Terminated {
    Submitted,
    /// Termination was refused because the process had already exited.
    Exited,
    /// Definitely not submitted.
    Failed,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Exit {
    Exited,
    /// Timed out, cancelled or failed while the exit was still unobserved.
    Unconfirmed,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum WindowClose {
    Posted,
    NoWindow,
    /// The close message was definitely not posted.
    Failed,
}

/// The narrow operating-system boundary. Every call that reads or acts on a
/// process takes a handle returned by [`ProcessApi::open`].
trait ProcessApi {
    type Process;

    /// `Ok(None)` when no process has this PID, `Err` on any other failure.
    fn open(&mut self, process_id: i32, terminate: bool) -> Result<Option<Self::Process>, ()>;
    /// `None` when the held process cannot be read.
    fn identity(&mut self, process: &Self::Process) -> Option<Held>;
    fn terminate(&mut self, process: &Self::Process) -> Terminated;
    fn wait_exit(
        &mut self,
        process: &Self::Process,
        timeout: Duration,
        cancelled: &AtomicBool,
    ) -> Exit;
    /// Snapshot PIDs that name `parent` as their creator. Unverified until each
    /// is opened and its own parent link and creation time are read.
    fn child_ids(&mut self, parent: i32) -> Vec<i32>;
    fn close_main_window(&mut self, process: &Self::Process, process_id: i32) -> WindowClose;
    fn current_process_id(&self) -> i32;
    /// The mandatory integrity RID of the held process's token, `None` when it
    /// cannot be read.
    fn integrity(&mut self, process: &Self::Process) -> Option<u32>;
    /// The calling Agent's own integrity RID.
    fn current_integrity(&mut self) -> Option<u32>;
}

type Compare<'a> = &'a dyn Fn(&str, &str) -> Ordering;

struct Context<'a, A> {
    api: RefCell<A>,
    user: &'a CurrentUser<'a>,
    compare: Compare<'a>,
    comparison_failed: &'a Cell<bool>,
    cancelled: &'a AtomicBool,
    kind: Kind,
}

#[cfg(feature = "gpu-close")]
pub(in crate::gpu_apps) fn close_windows(
    inventory: &GpuAppInventory,
    targets: &[Target],
    kind: Kind,
    user: &CurrentUser<'_>,
    compare: Compare<'_>,
    comparison_failed: &Cell<bool>,
    cancelled: &AtomicBool,
) -> Report {
    close(
        inventory,
        targets,
        Context {
            api: RefCell::new(windows::WindowsProcesses),
            user,
            compare,
            comparison_failed,
            cancelled,
            kind,
        },
    )
}

fn close<A: ProcessApi>(
    inventory: &GpuAppInventory,
    targets: &[Target],
    context: Context<'_, A>,
) -> Report {
    decide(
        inventory,
        targets,
        context.kind,
        context.user,
        context.compare,
        context.cancelled,
        &mut Processes(&context),
    )
}

struct Processes<'a, A>(&'a Context<'a, A>);

struct Retained<'a, A: ProcessApi> {
    context: &'a Context<'a, A>,
    process: A::Process,
    held: Held,
    start_time_utc_ticks: i64,
    owning_process_id: i32,
}

impl<'a, A: ProcessApi> Backend for Processes<'a, A> {
    type Retained = Retained<'a, A>;

    fn retain(&mut self, process_id: i32) -> Result<Option<Retained<'a, A>>, ()> {
        let context = self.0;
        let mut api = context.api.borrow_mut();
        let Some(process) = api.open(process_id, context.kind == Kind::ForceClose)? else {
            return Ok(None);
        };
        // Managed reads the same fields from its opened process; a failed read
        // is a definite failure before any action.
        let held = api.identity(&process).ok_or(())?;
        let (start_time_utc_ticks, owning_process_id) = if held.exited {
            (0, 0)
        } else {
            (
                dotnet_ticks(held.created_filetime).ok_or(())?,
                owning_process(&mut *api, &held, context),
            )
        };
        drop(api);
        Ok(Some(Retained {
            context,
            process,
            held,
            start_time_utc_ticks,
            owning_process_id,
        }))
    }
}

impl<A: ProcessApi> RetainedTarget for Retained<'_, A> {
    fn observe(&self) -> Observation<'_> {
        Observation {
            process_id: self.held.process_id,
            start_time_utc_ticks: self.start_time_utc_ticks,
            user_sid: &self.held.user_sid,
            session_id: self.held.session_id,
            image_path: &self.held.image_path,
            exited: self.held.exited,
            owning_process_id: Some(self.owning_process_id),
        }
    }

    fn submit(&mut self, kind: Kind) -> Submission {
        let context = self.context;
        // A comparison that fell back to ordinal order cannot vouch for the
        // protected-name and identity checks that admitted this target.
        if context.comparison_failed.get() {
            return Submission::Rejected;
        }
        let mut api = context.api.borrow_mut();
        // Managed cannot open a process above its own integrity level, so it
        // reports the action failure without posting or terminating.
        let ceiling = api.current_integrity();
        if !integrity_admitted(&mut *api, &self.process, ceiling) {
            return Submission::Rejected;
        }
        match kind {
            Kind::Close => match api.close_main_window(&self.process, self.held.process_id) {
                WindowClose::Posted => Submission::Accepted,
                WindowClose::NoWindow => Submission::NoWindow,
                WindowClose::Failed => Submission::Rejected,
            },
            Kind::ForceClose => {
                // Managed refuses a tree kill that contains the calling process
                // and then terminates only the exact target.
                let tree = !contains_current(&mut *api, &self.held);
                match api.terminate(&self.process) {
                    Terminated::Failed => return Submission::Rejected,
                    Terminated::Exited => {}
                    Terminated::Submitted => {
                        let exit =
                            api.wait_exit(&self.process, EXIT_CONFIRMATION, context.cancelled);
                        if exit == Exit::Unconfirmed {
                            return Submission::Uncertain;
                        }
                    }
                }
                if tree {
                    terminate_descendants(&mut *api, &self.held, context, ceiling);
                }
                Submission::Accepted
            }
        }
    }
}

/// Managed `SelectOwningApplication`, one step from the held process: a live,
/// earlier, same-session, current-user, unprotected parent with the same
/// name owns it, so this PID is not the confirmed app.
fn owning_process<A: ProcessApi>(api: &mut A, held: &Held, context: &Context<'_, A>) -> i32 {
    if parent_owns(api, held, context) {
        held.parent_process_id
    } else {
        held.process_id
    }
}

fn parent_owns<A: ProcessApi>(api: &mut A, held: &Held, context: &Context<'_, A>) -> bool {
    let compare = context.compare;
    if held.parent_process_id <= 0 || held.user_sid != context.user.sid {
        return false;
    }
    let Ok(Some(process)) = api.open(held.parent_process_id, false) else {
        return false;
    };
    let Some(parent) = api.identity(&process) else {
        return false;
    };
    let (Some(name), Some(parent_name)) = (
        executable_stem(&held.image_path),
        executable_stem(&parent.image_path),
    ) else {
        return false;
    };
    !parent.exited
        && parent.process_id == held.parent_process_id
        && parent.session_id == held.session_id
        && parent.user_sid == context.user.sid
        && parent.created_filetime <= held.created_filetime
        && !is_protected(&parent_name, &compare)
        && compare(&parent_name, &name).is_eq()
}

/// True when the calling process is the target or one of its descendants.
/// An unreadable Agent identity or an unbounded chain counts as contained, so
/// the force close falls back to the exact target only.
fn contains_current<A: ProcessApi>(api: &mut A, target: &Held) -> bool {
    let current = api.current_process_id();
    if current == target.process_id {
        return true;
    }
    let Some(mut child) = api
        .open(current, false)
        .ok()
        .flatten()
        .and_then(|process| api.identity(&process))
    else {
        return true;
    };
    for _ in 0..MAX_ANCESTRY {
        let parent = child.parent_process_id;
        if parent <= 0 {
            return false;
        }
        if parent == target.process_id {
            return target.created_filetime < child.created_filetime;
        }
        let Some(next) = api
            .open(parent, false)
            .ok()
            .flatten()
            .and_then(|process| api.identity(&process))
        else {
            return false;
        };
        // A reused PID was created after the child it claims to parent.
        if next.exited || next.created_filetime >= child.created_filetime {
            return false;
        }
        child = next;
    }
    true
}

/// The held process is at or below the Agent's integrity level. Either level
/// unreadable refuses it.
fn integrity_admitted<A: ProcessApi>(
    api: &mut A,
    process: &A::Process,
    ceiling: Option<u32>,
) -> bool {
    matches!((api.integrity(process), ceiling), (Some(level), Some(ceiling)) if level <= ceiling)
}

/// Managed `KillTree` below a target whose exit was confirmed. The caller
/// holds the target's handle for the whole walk, and each queued descendant
/// keeps its own handle until its children have been enumerated. A held
/// handle pins its PID, so a later-created child naming it was created by
/// that process and not by a later owner of a recycled PID.
fn terminate_descendants<A: ProcessApi>(
    api: &mut A,
    target: &Held,
    context: &Context<'_, A>,
    ceiling: Option<u32>,
) {
    let current = api.current_process_id();
    let mut pending: VecDeque<(i32, i64, Option<A::Process>)> =
        VecDeque::from([(target.process_id, target.created_filetime, None)]);
    let mut visited = BTreeSet::from([target.process_id]);
    while let Some((parent, parent_created, parent_handle)) = pending.pop_front() {
        for process_id in api.child_ids(parent) {
            if context.cancelled.load(Relaxed) || visited.len() > MAX_TREE {
                return;
            }
            if process_id == current || !visited.insert(process_id) {
                continue;
            }
            let Ok(Some(process)) = api.open(process_id, true) else {
                continue;
            };
            let Some(child) = api.identity(&process) else {
                continue;
            };
            if child.exited
                || child.process_id != process_id
                || child.parent_process_id != parent
                || child.created_filetime <= parent_created
                || child.user_sid != context.user.sid
                || child.session_id != context.user.session_id
                || executable_stem(&child.image_path)
                    .is_none_or(|name| is_protected(&name, &context.compare))
                // Managed never sees a child above its integrity level, nor
                // that child's subtree.
                || !integrity_admitted(api, &process, ceiling)
            {
                continue;
            }
            // As managed, a descendant failure does not change the result.
            let _ = api.terminate(&process);
            pending.push_back((process_id, child.created_filetime, Some(process)));
        }
        // Only now may this descendant's PID be released.
        drop(parent_handle);
    }
}
