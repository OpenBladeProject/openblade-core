//! The manual-operation frame shared by the synchronous display writes
//! (`setHdr` and `setRefreshRate` through CCD, `setColorProfile` through WCS).
//! Brightness owns its operation order (`brightness_write`). `applyProfile`
//! (`profile_apply`) composes the same admission, attempts and reconciliation
//! over several fields.
//!
//! Mirrors managed `DisplayAutomationCoordinator.BeginManualOperation` (the
//! shared `write::begin`), the applier's
//! `RunExpectedAsync`/`ValidateExpectedAsync` power admission and
//! `ExecuteSetterAsync`, and `CompleteManualOperation`: a durable manual-hold
//! checkpoint for the written field precedes any device interaction, the
//! trusted power source must match the request, the reply comes from a fresh
//! query, and a successful setter whose power context changed is stale. Each
//! write module supplies only its own native attempt and reply predicate.
use super::{
    observation::{SharedBrightness, Snapshot},
    recovery::{CheckpointFile, Fields, Recovery, Status},
    write::{
        Refused, STALE_AFTER, UNCONFIRMED_POWER, UNREFRESHED, WindowsHost, begin, lock,
        power_refusal, refreshed,
    },
};
use crate::session_native::power_admission::PowerContext;
use openblade_platform_windows::display::{
    DisplayReadError, Operation as ReadOperation, SubmissionFence,
};
use std::{
    borrow::Cow,
    io,
    sync::{Arc, Mutex, atomic::AtomicBool},
};

/// Managed `GetSingleInternalTarget` and `QueryTopology` preparation failures.
pub(super) const NO_INTERNAL: &str = "No active internal laptop display was found.";
pub(super) const MULTIPLE: &str = "More than one active internal display was found, so OpenBlade did not choose one automatically.";
pub(super) const CHANGING: &str =
    "The display configuration kept changing while OpenBlade was reading it.";
/// Native-only topology validation failures (target mode, counts, limits).
pub(super) const UNREADABLE: &str = "Windows could not read the internal display.";

/// Managed `ThrowIfWin32Error(status, operation)` formats
/// "{operation}: {Win32Exception(status).Message}". The native Agent uses the
/// decimal status in place of the localized system message:
/// "{operation}: Win32 error {status}."
pub(super) fn win32(operation: &str, status: u32) -> Cow<'static, str> {
    Cow::Owned(format!("{operation}: Win32 error {status}."))
}

/// Managed `QueryTopology` exception text for a failed CCD read.
pub(super) fn unreadable(error: DisplayReadError) -> Cow<'static, str> {
    match error {
        DisplayReadError::Native {
            operation: ReadOperation::BufferSizes,
            status,
        } => win32(
            "Windows could not size the active display configuration",
            status,
        ),
        DisplayReadError::Native {
            operation: ReadOperation::ActivePaths,
            status,
        } => win32(
            "Windows could not read the active display configuration",
            status,
        ),
        DisplayReadError::Native {
            operation: ReadOperation::SourceName,
            status,
        } => win32(
            "Windows could not identify the internal display source",
            status,
        ),
        DisplayReadError::TopologyChanging => Cow::Borrowed(CHANGING),
        _ => Cow::Borrowed(UNREADABLE),
    }
}

/// One managed `SessionDisplayResponse` body for a manual display write.
pub(super) struct ManualWriteOutcome {
    pub(super) status: Status,
    pub(super) error: Option<Cow<'static, str>>,
    pub(super) state: Snapshot,
}

impl From<Refused> for ManualWriteOutcome {
    fn from(refused: Refused) -> Self {
        Self {
            status: Status::Blocked,
            error: Some(Cow::Borrowed(refused.error)),
            state: refused.state,
        }
    }
}

/// Injected OS boundary. Production reads the trusted power context and the
/// full display snapshot and owns the real native; the fence re-verifies
/// topology, the loaded Origin, the active console session and power.
pub(super) trait ManualHost {
    type Native: ?Sized;
    fn power(&self) -> Option<PowerContext>;
    fn fence(&self, admitted: PowerContext) -> Arc<dyn SubmissionFence>;
    fn native(&mut self) -> &mut Self::Native;
    fn query(
        &mut self,
        brightness: &SharedBrightness,
        canceled: &AtomicBool,
    ) -> io::Result<Snapshot>;
}

/// Production host: the shared admission state and one real native.
pub(super) struct WindowsManualHost<N> {
    host: WindowsHost,
    native: N,
}

impl<N> WindowsManualHost<N> {
    pub(super) fn new(host: WindowsHost, native: N) -> Self {
        Self { host, native }
    }
}

impl<N> ManualHost for WindowsManualHost<N> {
    type Native = N;

    fn power(&self) -> Option<PowerContext> {
        self.host.manual_power()
    }

    fn fence(&self, admitted: PowerContext) -> Arc<dyn SubmissionFence> {
        self.host.fence(admitted)
    }

    fn native(&mut self) -> &mut N {
        &mut self.native
    }

    fn query(
        &mut self,
        brightness: &SharedBrightness,
        canceled: &AtomicBool,
    ) -> io::Result<Snapshot> {
        self.host.query(brightness, canceled)
    }
}

/// The write gate and texts of one CCD field.
pub(super) struct Field {
    pub(super) fields: Fields,
    /// Native-only gate while an earlier write of this field is unresolved.
    pub(super) paused: &'static str,
    /// A confirmed write that the reply query does not show.
    pub(super) unconfirmed: &'static str,
}

/// The result of one admitted native attempt, before reply reconciliation.
pub(super) enum Attempt {
    /// Nothing was submitted: the writer's own read already showed the
    /// request, so a later reply-query miss cannot make it uncertain.
    Unchanged,
    /// The independent readback confirmed the request.
    Confirmed,
    /// The setter failed before any possible submission. When `reply_resolves`,
    /// the managed flow reconciles the failure with the reply snapshot
    /// (`ResolveHdrMutationResult` for a failed `PrepareHdr`), so a reply that
    /// shows the request succeeds.
    Failed {
        error: Cow<'static, str>,
        reply_resolves: bool,
    },
    /// A Blocked refusal, or an Indeterminate result after a possible submission.
    Done(Status, Cow<'static, str>),
}

/// One manual display write request: its field and the parts that differ per
/// setter.
pub(super) trait ManualRequest<H: ManualHost> {
    const FIELD: Field;
    fn expected_power_source(&self) -> u8;
    /// The native attempt, after the checkpoint and power admission.
    fn attempt(&self, host: &mut H, admitted: PowerContext, canceled: &AtomicBool) -> Attempt;
    /// Whether the reply snapshot shows the request.
    fn shown_by(&self, state: &Snapshot) -> bool;
}

impl Attempt {
    /// Reconcile this attempt with whether the reply snapshot shows the
    /// request: a confirmed write it does not show is `unconfirmed`.
    pub(super) fn resolve(
        self,
        shown: bool,
        unconfirmed: &'static str,
    ) -> (Status, Option<Cow<'static, str>>) {
        match self {
            Attempt::Unchanged => (Status::Succeeded, None),
            Attempt::Confirmed if shown => (Status::Succeeded, None),
            Attempt::Confirmed => (Status::Indeterminate, Some(Cow::Borrowed(unconfirmed))),
            Attempt::Failed {
                reply_resolves: true,
                ..
            } if shown => (Status::Succeeded, None),
            Attempt::Failed { error, .. } => (Status::Failed, Some(error)),
            Attempt::Done(status, error) => (status, Some(error)),
        }
    }
}

/// Managed `ValidateExpectedAsync`: the trusted power context admits the
/// request's expected source (see `power_refusal`), or the Blocked refusal.
/// Manual setters pass a manual-aware context; profile apply passes only a
/// confirmed one, as managed `ApplyProfileAsync` never allows unclassified
/// external power.
pub(super) fn admit_power(
    power: Option<PowerContext>,
    expected: u8,
) -> Result<PowerContext, Attempt> {
    match (power, power_refusal(power, expected)) {
        (Some(power), None) => Ok(power),
        (_, refusal) => Err(Attempt::Done(
            Status::Blocked,
            Cow::Borrowed(refusal.unwrap_or(UNCONFIRMED_POWER)),
        )),
    }
}

/// The complete admitted manual operation.
pub(super) fn run<F: CheckpointFile, H: ManualHost, R: ManualRequest<H>>(
    host: &mut H,
    brightness: &SharedBrightness,
    recovery: &Mutex<Recovery<F>>,
    request: &R,
    canceled: &AtomicBool,
) -> io::Result<ManualWriteOutcome> {
    let field = R::FIELD;
    let power = host.power();
    if let Some(refused) = begin(recovery, field.fields, field.paused, power, || {
        host.query(brightness, canceled)
    })? {
        return Ok(refused.into());
    }

    // Admitted manual operation: whatever happens next, record its certainty.
    let attempt = match admit_power(power, request.expected_power_source()) {
        Err(refused) => refused,
        Ok(power) => request.attempt(host, power, canceled),
    };
    let (state, fatal) = match refreshed(host.query(brightness, canceled)) {
        Ok(state) => (state, None),
        Err(error) => (Snapshot::unavailable(UNREFRESHED), Some(error)),
    };
    let (status, error) = attempt.resolve(request.shown_by(&state), field.unconfirmed);
    // Managed ExecuteSetterAsync.EnsureCurrent after a successful setter,
    // including a no-op one.
    let (status, error) = match power {
        Some(admitted) if status == Status::Succeeded && host.power() != Some(admitted) => {
            (Status::Indeterminate, Some(Cow::Borrowed(STALE_AFTER)))
        }
        _ => (status, error),
    };
    lock(recovery)?.complete(status, &state);
    match fatal {
        Some(error) => Err(error),
        None => Ok(ManualWriteOutcome {
            status,
            error,
            state,
        }),
    }
}

#[cfg(test)]
#[path = "manual_write_tests.rs"]
mod tests;
