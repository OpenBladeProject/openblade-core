//! GPU clock tuning on the explicit performance path of exact RZ09-0581: the
//! GPU half of managed `ControlCoordinator.ApplyPerformanceOutcomeAsync`
//! (`ControlCoordinator.cs:591-698`) over the clock-offset backend
//! (`gpu_clock_offsets`), composed by `service-gpu-clock-tuning-v1`.
//!
//! A performance step runs in two halves inside its request, both bounded by
//! the request's deadline:
//!
//! - [`Lane::prepare`] runs inside `RequestPermit::resolve`, after the
//!   override was stored and every firmware refusal was decided, before
//!   any journal row or setter. With no tuning selected it relinquishes
//!   management (managed `beforeInitialStateRead`). With tuning it reads the
//!   managed offsets (managed `ReadStateAsync`), then decides: the firmware
//!   destination already confirmed and the tuning matching is nothing to
//!   write; the firmware confirmed alone is the GPU-only setter (managed
//!   `CanApplyGpuClockOffsetsOnly`); otherwise it is the paired write, whose
//!   NVML preflight (managed `PreflightGpuClockOffsets`) runs here, so a
//!   refused range writes no firmware. A confirmed reset is reused for a
//!   paired disable without a query.
//! - [`Lane::complete`] runs after the firmware's independent readback
//!   confirmed the destination, under fresh write admission: the NVML
//!   submission, then the read that judges it (managed `matches(final)`, or
//!   `ResolveIndeterminateWriteByReadbackAsync` after a failed submission).
//!   A paired failure is managed's `The power mode was applied, but GPU clock
//!   tuning failed.` indeterminate write. Nothing is replayed.
//!
//! Each call is bounded by what is left of the request, less
//! [`REPLY_MARGIN`], and by [`CALL_BUDGET`], so the reply is sent before the
//! request's deadline. A started call cannot be withdrawn: one that outlives
//! its budget finishes on the worker after the reply, which reports it as
//! indeterminate. Until it finishes, and while its result or any other
//! submission leaves the management uncertain, [`Lane::fenced`] refuses
//! every native thermal writer (managed `PendingWriteConfirmationTracker`),
//! after one read-only resolution attempt under the management's own query
//! rules; [`Lane::prepare`] resolves it read-only too and replies pending
//! while it stays uncertain. The fence's attempt crosses no boundary: the
//! one ungated query a power or source change rearms is taken by the next
//! performance step, so until then the writers stay fenced (the safe side).
//!
//! The low-power boundary and the power-change rearm are applied lazily:
//! each step carries the gate generation, the Windows power generation and
//! the power class it was admitted under ([`Boundary`]), and the first GPU
//! call after any moved invalidates the confirmations (managed
//! `InvalidateGpuClockOffsetConfirmation` at resume) or rearms the one
//! ungated query (managed `RearmUncertainGpuClockOffsetResolution` at a power
//! change) before it reads or writes anything.
//!
//! Nothing is written at Stop, shutdown or low power, as managed writes
//! nothing then. There is no durable record of a GPU submission: managed
//! keeps uncertainty in memory only, and a restart begins at `Unknown`.
use super::gpu_clock_offsets::{Called, Capabilities, Failure, Management, Nvml, State, Worker};
use openblade_domain::profile_set::model::GpuClockOffsets;
use serde_json::Value;
use std::{
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, AtomicUsize, Ordering},
    },
    time::Duration,
};

/// The bound on each NVML call, measured on the real clock.
pub(super) const CALL_BUDGET: Duration = Duration::from_secs(5);
/// What each GPU call leaves of the request for the reply after it.
pub(super) const REPLY_MARGIN: Duration = Duration::from_millis(500);
/// What the firmware half's pace leaves for a paired submission and its
/// read (`Permit::reserve`).
pub(super) const COMPLETE_RESERVE: Duration = Duration::from_secs(1);
/// The bound on [`Lane::fenced`]'s read-only resolution attempt, which runs
/// before the writer's own rows.
pub(super) const FENCE_READ_BUDGET: Duration = Duration::from_secs(1);

/// The backend a lane drives: production NVML, or a scripted driver.
pub(super) type Backend = Box<dyn Nvml>;

/// Managed `GpuClockTuningSettings` as a step selected it.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) struct Tuning {
    pub(super) enabled: bool,
    pub(super) offsets: GpuClockOffsets,
}

impl Tuning {
    /// The saved JSON form (`{"enabled","offsets"}`), or None for tuning
    /// managed `GpuClockTuningSettings.IsWellFormed` refuses: null offsets.
    pub(super) fn parse(value: &Value) -> Option<Self> {
        let offset = |key: &str| {
            value["offsets"][key]
                .as_i64()
                .and_then(|n| i32::try_from(n).ok())
        };
        Some(Self {
            enabled: value["enabled"].as_bool()?,
            offsets: GpuClockOffsets {
                core_megahertz: offset("coreMegahertz")?,
                memory_megahertz: offset("memoryMegahertz")?,
            },
        })
    }

    /// Managed `EffectiveOffsets`: the offsets when enabled, else the
    /// defaults.
    pub(super) fn effective(self) -> GpuClockOffsets {
        if self.enabled {
            self.offsets
        } else {
            GpuClockOffsets::default()
        }
    }
}

/// The lifecycle a step was admitted under: the low-power gate's clear
/// generation, the Windows power generation and the classified power source
/// the step resolved, `None` where unknown. Managed rearms on any classified
/// source change (`ServiceControlRuntime.cs:18684`), PluggedIn and USB-C
/// included; a change and its return between two GPU calls is not seen,
/// which leaves the query unarmed: the safe side.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub(super) struct Boundary {
    pub(super) gate: Option<u64>,
    pub(super) power: Option<u64>,
    pub(super) source: Option<i32>,
}

/// What the firmware step must leave to [`Lane::complete`].
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum Half {
    /// No tuning: management was relinquished. Nothing follows.
    Relinquished,
    /// The tuning already matches. Nothing follows.
    Matches,
    /// The firmware destination is confirmed already: the GPU-only setter
    /// (managed `SetGpuClockOffsetsAsync`), judged against `initial`, the
    /// managed offsets read before it.
    GpuOnly {
        tuning: Tuning,
        initial: Option<GpuClockOffsets>,
    },
    /// The paired write, its preflight passed.
    Paired(Tuning),
    /// The paired disable that reuses a confirmed reset.
    PairedReuse,
}

/// [`Lane::prepare`]'s decision.
#[derive(Clone, Debug, PartialEq, Eq)]
pub(super) enum Prepared {
    Proceed(Half),
    /// An earlier submission is still uncertain after its read-only
    /// resolution: no thermal setter may be submitted.
    Pending,
    /// The managed reply: nothing was written. `failed` is managed's
    /// Failed status (a write refused as not applied); otherwise Blocked.
    Refused {
        message: String,
        failed: bool,
    },
}

/// [`Lane::complete`]'s result: managed's reply for the GPU half.
#[derive(Clone, Debug, PartialEq, Eq)]
pub(super) struct Completed {
    pub(super) applied: bool,
    /// Managed's Failed status (`Indeterminate`, `WriteRejected`), else
    /// Blocked (`CapabilityRefused`).
    pub(super) failed: bool,
    pub(super) message: String,
}

impl Completed {
    fn applied(message: &str) -> Self {
        Self {
            applied: true,
            failed: false,
            message: message.into(),
        }
    }

    fn failed(message: impl Into<String>) -> Self {
        Self {
            applied: false,
            failed: true,
            message: message.into(),
        }
    }

    fn blocked(message: impl Into<String>) -> Self {
        Self {
            applied: false,
            failed: false,
            message: message.into(),
        }
    }
}

/// Managed's pending-confirmation block reason for a performance request
/// (`thermal_mutation`).
pub(super) use super::thermal_mutation::PENDING_PERFORMANCE as PENDING;
/// A GPU call that could not run within the request. Nothing was submitted.
pub(super) const UNRESPONSIVE: &str =
    "GPU clock management did not respond; no thermal setter was submitted.";
/// Managed `ApplyScopedAsync`'s refusal of tuning `IsWellFormed` rejects.
pub(super) const UNSUPPORTED: &str =
    "Performance value is not supported by the verified capability table.";
const APPLIED: &str = "Performance setting applied and confirmed by readback.";
const APPLIED_AFTER_INDETERMINATE: &str = "Performance setting applied and confirmed by fresh readback after an indeterminate submission.";
const READBACK_MISMATCH: &str =
    "Performance write is indeterminate because readback did not match.";
const PAIRED_FAILED: &str = "The power mode was applied, but GPU clock tuning failed.";
const NOT_STARTED: &str = "GPU clock tuning did not start.";
const ABANDONED: &str = "GPU clock tuning did not finish within its budget.";
const UNADMITTED: &str = "GPU clock tuning lost its write admission before it was submitted.";

/// The calls the lane has started that have not finished, and whether the
/// management was left with an uncertain submission when the last finished.
/// A started call outlives its caller when its budget ends first.
#[derive(Default)]
struct Outstanding {
    calls: AtomicUsize,
    uncertain: AtomicBool,
}

/// Held by a call while it runs on the worker: its end, a panic included,
/// retires the call, after the call itself recorded the management's state.
struct Running(Arc<Outstanding>);

impl Drop for Running {
    fn drop(&mut self) {
        // A panic leaves an interrupted submission uncertain (`Worker`).
        if std::thread::panicking() {
            self.0.uncertain.store(true, Ordering::SeqCst);
        }
        self.0.calls.fetch_sub(1, Ordering::SeqCst);
    }
}

/// One clock-offset worker, the boundaries its management last saw, and its
/// outstanding calls.
pub(super) struct Lane {
    worker: Worker<Backend>,
    seen: Arc<Mutex<Option<Boundary>>>,
    outstanding: Arc<Outstanding>,
}

impl Lane {
    pub(super) fn new(worker: Worker<Backend>) -> Self {
        Self {
            worker,
            seen: Arc::new(Mutex::new(None)),
            outstanding: Arc::default(),
        }
    }

    /// One call within `budget` (at most [`CALL_BUDGET`]), recording what it
    /// leaves even when it finishes after its caller stopped waiting. A zero
    /// budget starts nothing.
    fn call<R: Send + 'static>(
        &self,
        budget: Duration,
        job: impl FnOnce(&mut Management<Backend>) -> R + Send + 'static,
    ) -> Called<R> {
        let budget = budget.min(CALL_BUDGET);
        if budget.is_zero() {
            return Called::NotStarted;
        }
        let outstanding = self.outstanding.clone();
        outstanding.calls.fetch_add(1, Ordering::SeqCst);
        let called = self.worker.call_blocking(budget, move |management| {
            let running = Running(outstanding);
            let result = job(management);
            running.0.uncertain.store(
                management.state() == State::SubmissionUncertain,
                Ordering::SeqCst,
            );
            drop(running);
            result
        });
        // A call that never started never runs: it retires here.
        if matches!(called, Called::NotStarted) {
            self.outstanding.calls.fetch_sub(1, Ordering::SeqCst);
        }
        called
    }

    /// Whether every native thermal writer must refuse (managed
    /// `PendingWriteConfirmationTracker.Blocks`): a started call has not
    /// finished, or the management holds an uncertain submission after one
    /// read-only resolution attempt within `budget` less [`REPLY_MARGIN`],
    /// at most [`FENCE_READ_BUDGET`]. The attempt reads under the
    /// management's own query rules and writes nothing.
    pub(super) fn fenced(&self, budget: Duration) -> bool {
        if self.outstanding.calls.load(Ordering::SeqCst) > 0 {
            return true;
        }
        if !self.outstanding.uncertain.load(Ordering::SeqCst) {
            return false;
        }
        match self.call(
            budget.saturating_sub(REPLY_MARGIN).min(FENCE_READ_BUDGET),
            |management| {
                let _ = management.read();
            },
        ) {
            Called::Done(()) => self.outstanding.uncertain.load(Ordering::SeqCst),
            Called::NotStarted | Called::Abandoned => true,
        }
    }

    /// The first half (see the module) within `budget`;
    /// `firmware_unchanged` is whether the fresh reading already confirms
    /// the destination (`Plan::Unchanged`).
    pub(super) fn prepare(
        &self,
        boundary: Boundary,
        tuning: Option<Tuning>,
        firmware_unchanged: bool,
        budget: Duration,
    ) -> Prepared {
        // A call that outlived its request may still submit: pending until
        // it finishes.
        if self.outstanding.calls.load(Ordering::SeqCst) > 0 {
            return Prepared::Pending;
        }
        let seen = self.seen.clone();
        match self.call(budget, move |management| {
            cross(management, &seen, boundary);
            prepare(management, tuning, firmware_unchanged)
        }) {
            Called::Done(prepared) => prepared,
            // A relinquish or a preflight is read-only: an abandoned one
            // submitted nothing, and fences the writers until it finishes.
            Called::NotStarted | Called::Abandoned => Prepared::Refused {
                message: UNRESPONSIVE.into(),
                failed: false,
            },
        }
    }

    /// The second half (see the module) within `budget`, or None when
    /// nothing follows.
    pub(super) fn complete(
        &self,
        boundary: Boundary,
        half: Half,
        budget: Duration,
    ) -> Option<Completed> {
        let paired = match half {
            Half::Relinquished | Half::Matches => return None,
            Half::GpuOnly { .. } => false,
            Half::Paired(_) | Half::PairedReuse => true,
        };
        let seen = self.seen.clone();
        Some(
            match self.call(budget, move |management| {
                cross(management, &seen, boundary);
                complete(management, half)
            }) {
                Called::Done(completed) => completed,
                // Nothing was submitted.
                Called::NotStarted if paired => {
                    Completed::failed(indeterminate(&paired_detail(NOT_STARTED)))
                }
                Called::NotStarted => Completed::failed(not_applied(NOT_STARTED)),
                // It may have submitted and finishes on the worker, which
                // records the driver's result; the writers stay fenced until
                // then (`fenced`), and no read can follow it now.
                Called::Abandoned if paired => {
                    Completed::failed(indeterminate(&paired_detail(ABANDONED)))
                }
                Called::Abandoned => Completed::failed(indeterminate(ABANDONED)),
            },
        )
    }

    /// Leaves the management's recorded state uncertain, as an
    /// unconfirmable submission does.
    #[cfg(test)]
    pub(super) fn mark_uncertain_for_tests(&self) {
        self.outstanding.uncertain.store(true, Ordering::SeqCst);
    }

    /// One call within [`CALL_BUDGET`], recorded as the lane records its own.
    #[cfg(test)]
    pub(super) fn call_for_tests<R: Send + 'static>(
        &self,
        job: impl FnOnce(&mut Management<Backend>) -> R + Send + 'static,
    ) -> Called<R> {
        self.call(CALL_BUDGET, job)
    }
}

/// The second half when the step lost its own write admission after the
/// firmware readback: nothing is submitted, and a paired write is left with
/// its firmware half applied.
pub(super) fn unadmitted(half: Half) -> Option<Completed> {
    match half {
        Half::Relinquished | Half::Matches => None,
        Half::GpuOnly { .. } => Some(Completed::failed(not_applied(UNADMITTED))),
        Half::Paired(_) | Half::PairedReuse => {
            Some(Completed::failed(indeterminate(&paired_detail(UNADMITTED))))
        }
    }
}

/// The second half whose blocking task ended without a result: it may
/// have submitted.
pub(super) fn lost(half: Half) -> Option<Completed> {
    match half {
        Half::Relinquished | Half::Matches => None,
        Half::GpuOnly { .. } => Some(Completed::failed(indeterminate(ABANDONED))),
        Half::Paired(_) | Half::PairedReuse => {
            Some(Completed::failed(indeterminate(&paired_detail(ABANDONED))))
        }
    }
}

/// The native reply for a confirmed step: the firmware destination, and the
/// GPU half `completed` confirmed, if it had one.
pub(super) fn confirmed(completed: Option<&Completed>) -> &'static str {
    match completed.map(|c| c.message.as_str()) {
        None => {
            "Bounded thermal preset confirmed by independent paired readback. Broad device capabilities remain unavailable; other native controls are incomplete."
        }
        Some(APPLIED) => {
            "Bounded thermal preset and GPU clock tuning confirmed by independent readback. Broad device capabilities remain unavailable; other native controls are incomplete."
        }
        Some(_) => {
            "Bounded thermal preset confirmed by independent paired readback, and GPU clock tuning by fresh readback after an indeterminate submission. Broad device capabilities remain unavailable; other native controls are incomplete."
        }
    }
}

/// Applies the boundaries crossed since the management's last call: a moved
/// gate generation is a resume, which withdraws every confirmation, and a
/// moved power generation rearms the one ungated query. The first call has
/// nothing to withdraw: the management starts `Unknown`.
fn cross<N: Nvml>(
    management: &mut Management<N>,
    seen: &Mutex<Option<Boundary>>,
    boundary: Boundary,
) {
    // A poisoned record is a crossed boundary of every kind. The lock is
    // only held for this replace, which cannot panic, so it stays poisoned
    // only in theory: each later call then also counts as crossed.
    let previous = match seen.lock() {
        Ok(mut seen) => seen.replace(boundary),
        Err(poisoned) => {
            *poisoned.into_inner() = Some(boundary);
            Some(Boundary {
                gate: boundary.gate.map(|g| g.wrapping_add(1)).or(Some(0)),
                power: boundary.power.map(|p| p.wrapping_add(1)).or(Some(0)),
                source: boundary.source,
            })
        }
    };
    let Some(previous) = previous else {
        return;
    };
    if previous.gate != boundary.gate {
        // Revalidation is the next read's: nothing is published here.
        let _ = management.invalidate();
    }
    if previous.power != boundary.power || previous.source != boundary.source {
        management.rearm();
    }
}

/// Managed `GpuClockTuningMatches` (`ControlCoordinator.cs:3675-3688`)
/// against the managed offsets `current` read in `state`.
fn matches(tuning: Tuning, current: Option<&Capabilities>, state: State) -> bool {
    if tuning.enabled {
        current.map(Capabilities::current) == Some(tuning.effective())
    } else {
        current.is_none()
            && matches!(
                state,
                State::ResetConfirmed | State::RelinquishedDefaultConfirmed
            )
    }
}

pub(super) fn prepare<N: Nvml>(
    management: &mut Management<N>,
    tuning: Option<Tuning>,
    firmware_unchanged: bool,
) -> Prepared {
    let Some(tuning) = tuning else {
        // Managed `RelinquishGpuClockOffsetManagementAsync`: an unreadable
        // uncertain submission is kept, and then blocks the write.
        if !management.relinquish() {
            return Prepared::Pending;
        }
        return Prepared::Proceed(Half::Relinquished);
    };
    let current = management.read();
    if management.state() == State::SubmissionUncertain {
        return Prepared::Pending;
    }
    if firmware_unchanged {
        return Prepared::Proceed(if matches(tuning, current.as_ref(), management.state()) {
            Half::Matches
        } else {
            Half::GpuOnly {
                tuning,
                initial: current.as_ref().map(Capabilities::current),
            }
        });
    }
    // Managed `PreflightGpuClockOffsets`: a disable reuses a reset the
    // device still holds as confirmed, without a query.
    if !tuning.enabled && management.has_confirmed_default_reset() {
        return Prepared::Proceed(Half::PairedReuse);
    }
    match management.preflight(tuning.effective(), tuning.enabled) {
        Ok(()) => Prepared::Proceed(Half::Paired(tuning)),
        Err(error) => match error.failure {
            Failure::Unavailable => Prepared::Refused {
                message: unavailable(&error.message),
                failed: false,
            },
            Failure::NotApplied | Failure::Indeterminate => Prepared::Refused {
                message: not_applied(&error.message),
                failed: true,
            },
        },
    }
}

pub(super) fn complete<N: Nvml>(management: &mut Management<N>, half: Half) -> Completed {
    match half {
        Half::Relinquished | Half::Matches => Completed::applied(APPLIED),
        Half::GpuOnly { tuning, initial } => {
            match management.set(tuning.effective(), tuning.enabled) {
                Ok(_) => judged(management, tuning),
                Err(error) => match error.failure {
                    Failure::Unavailable => Completed::blocked(unavailable(&error.message)),
                    Failure::NotApplied => Completed::failed(not_applied(&error.message)),
                    Failure::Indeterminate => {
                        resolve(management, tuning, &error.message, |current, state| {
                            // Managed `GpuClockOffsetsUnchangedAfterIndeterminateWrite`
                            // with the firmware half unchanged.
                            current.map(Capabilities::current) == initial
                                && (current.is_some()
                                    || matches!(
                                        state,
                                        State::ResetConfirmed | State::RelinquishedDefaultConfirmed
                                    ))
                        })
                    }
                },
            }
        }
        Half::Paired(tuning) => paired(management, tuning),
        Half::PairedReuse => {
            // Managed `SetGpuClockOffsetManagementState(ResetConfirmed)`. A
            // reset no longer confirmed is submitted instead.
            if management.mark_reset_confirmed() {
                judged(
                    management,
                    Tuning {
                        enabled: false,
                        offsets: GpuClockOffsets::default(),
                    },
                )
            } else {
                paired(
                    management,
                    Tuning {
                        enabled: false,
                        offsets: GpuClockOffsets::default(),
                    },
                )
            }
        }
    }
}

/// The paired submission after the firmware write (managed
/// `CompleteGpuClockOffsets`): any failure is indeterminate, since the
/// firmware half was applied.
fn paired<N: Nvml>(management: &mut Management<N>, tuning: Tuning) -> Completed {
    match management.submit(tuning.effective(), tuning.enabled) {
        Ok(_) => judged(management, tuning),
        // The firmware half changed, so the prior performance state is
        // never what remained (managed `PerformanceStateMatches`).
        Err(error) => resolve(
            management,
            tuning,
            &paired_detail(&error.message),
            |_, _| false,
        ),
    }
}

/// The final read after a completed setter (managed `ApplyScopedAsync`'s
/// `matches(final)`).
fn judged<N: Nvml>(management: &mut Management<N>, tuning: Tuning) -> Completed {
    let current = management.read();
    if matches(tuning, current.as_ref(), management.state()) {
        Completed::applied(APPLIED)
    } else {
        Completed::failed(READBACK_MISMATCH)
    }
}

/// Managed `ResolveIndeterminateWriteByReadbackAsync`
/// (`ControlCoordinator.cs:3172-3268`): one read-only read, never a replay.
fn resolve<N: Nvml>(
    management: &mut Management<N>,
    tuning: Tuning,
    detail: &str,
    unchanged: impl FnOnce(Option<&Capabilities>, State) -> bool,
) -> Completed {
    let current = management.read();
    let state = management.state();
    let indeterminate = indeterminate(detail);
    if matches(tuning, current.as_ref(), state) {
        Completed::applied(APPLIED_AFTER_INDETERMINATE)
    } else if current.is_none() && state == State::SubmissionUncertain {
        Completed::failed(format!(
            "{indeterminate} Fresh readback cannot confirm the GPU clock offsets."
        ))
    } else if unchanged(current.as_ref(), state) {
        Completed::failed(format!(
            "{indeterminate} Fresh readback confirmed that the prior performance state remained active."
        ))
    } else {
        Completed::failed(format!(
            "{indeterminate} Fresh readback did not match the requested or prior performance state."
        ))
    }
}

fn paired_detail(message: &str) -> String {
    format!("{PAIRED_FAILED} {message}")
}

/// Managed `Indeterminate(name, exception)`.
fn indeterminate(detail: &str) -> String {
    format!("Performance write is indeterminate: {detail}")
}

/// Managed `WriteRejected(name, exception)`.
fn not_applied(detail: &str) -> String {
    format!("Performance write was not applied: {detail}")
}

/// Managed `CapabilityRefused(name, exception)`.
fn unavailable(detail: &str) -> String {
    format!("Performance is unavailable: {detail}")
}

#[cfg(test)]
#[path = "thermal_gpu_tests.rs"]
mod tests;
