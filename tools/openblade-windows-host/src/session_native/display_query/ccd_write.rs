//! The manual-operation frame shared by the CCD display writes (`setHdr` and
//! `setRefreshRate`).
//!
//! Mirrors managed `DisplayAutomationCoordinator.BeginManualOperation`, the
//! applier's `RunExpectedAsync`/`ValidateExpectedAsync` power admission and
//! `ExecuteSetterAsync`, and `CompleteManualOperation`: a durable manual-hold
//! checkpoint for the written field precedes any device interaction, the
//! trusted power source must match the request, the reply comes from a fresh
//! query, and a successful setter whose power context changed is stale. Each
//! write module supplies only its own native attempt and reply predicate.
use super::{
    observation::{SharedBrightness, Snapshot},
    recovery::{CheckpointFile, Fields, Recovery, Status},
    write::{
        CHANGED_POWER, CHECKPOINT, STALE_AFTER, UNCONFIRMED_POWER, UNREFRESHED, WindowsHost,
        refreshed,
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

/// One managed `SessionDisplayResponse` body for a CCD write.
pub(super) struct CcdWriteOutcome {
    pub(super) status: Status,
    pub(super) error: Option<Cow<'static, str>>,
    pub(super) state: Snapshot,
}

/// Injected OS boundary. Production reads the trusted power context and the
/// full display snapshot and owns the real native; the fence re-verifies
/// topology, the loaded Origin, the active console session and power.
pub(super) trait CcdHost {
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
pub(super) struct WindowsCcdHost<N> {
    host: WindowsHost,
    native: N,
}

impl<N> WindowsCcdHost<N> {
    pub(super) fn new(host: WindowsHost, native: N) -> Self {
        Self { host, native }
    }
}

impl<N> CcdHost for WindowsCcdHost<N> {
    type Native = N;

    fn power(&self) -> Option<PowerContext> {
        self.host.power()
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
    /// Managed `PrepareHdr` failure without a possible submission, resolved by
    /// `ResolveHdrMutationResult` over the reply snapshot.
    #[cfg(feature = "agent-display-hdr-write-v1")]
    Prepare(Cow<'static, str>),
    Done(Status, Cow<'static, str>),
}

/// One CCD write request: its field and the parts that differ per setter.
pub(super) trait CcdRequest<H: CcdHost> {
    const FIELD: Field;
    fn expected_power_source(&self) -> u8;
    /// The native attempt, after the checkpoint and power admission.
    fn attempt(&self, host: &mut H, admitted: PowerContext, canceled: &AtomicBool) -> Attempt;
    /// Whether the reply snapshot shows the request.
    fn shown_by(&self, state: &Snapshot) -> bool;
}

/// The complete admitted manual operation.
pub(super) fn run<F: CheckpointFile, H: CcdHost, R: CcdRequest<H>>(
    host: &mut H,
    brightness: &SharedBrightness,
    recovery: &Mutex<Recovery<F>>,
    request: &R,
    canceled: &AtomicBool,
) -> io::Result<CcdWriteOutcome> {
    let field = R::FIELD;
    let lock = || {
        recovery
            .lock()
            .map_err(|_| io::Error::other("display recovery state failed"))
    };
    let power = host.power();
    {
        let mut recovery = lock()?;
        let refused = if recovery.unresolved().overlaps(field.fields) {
            Some(field.paused)
        } else if !recovery.begin(field.fields, power.map(|power| power.source)) {
            Some(CHECKPOINT)
        } else {
            None
        };
        if let Some(error) = refused {
            drop(recovery);
            // Not admitted: managed returns Blocked without completing.
            return Ok(CcdWriteOutcome {
                status: Status::Blocked,
                error: Some(Cow::Borrowed(error)),
                state: refreshed(host.query(brightness, canceled))?,
            });
        }
    }

    // Admitted manual operation: whatever happens next, record its certainty.
    let attempt = match power {
        None => Attempt::Done(Status::Blocked, Cow::Borrowed(UNCONFIRMED_POWER)),
        Some(power) if power.source != request.expected_power_source() => {
            Attempt::Done(Status::Blocked, Cow::Borrowed(CHANGED_POWER))
        }
        Some(power) => request.attempt(host, power, canceled),
    };
    let (state, fatal) = match refreshed(host.query(brightness, canceled)) {
        Ok(state) => (state, None),
        Err(error) => (Snapshot::unavailable(UNREFRESHED), Some(error)),
    };
    let (status, error) = match attempt {
        Attempt::Unchanged => (Status::Succeeded, None),
        Attempt::Confirmed if request.shown_by(&state) => (Status::Succeeded, None),
        Attempt::Confirmed => (
            Status::Indeterminate,
            Some(Cow::Borrowed(field.unconfirmed)),
        ),
        #[cfg(feature = "agent-display-hdr-write-v1")]
        Attempt::Prepare(_) if request.shown_by(&state) => (Status::Succeeded, None),
        #[cfg(feature = "agent-display-hdr-write-v1")]
        Attempt::Prepare(error) => (Status::Failed, Some(error)),
        Attempt::Done(status, error) => (status, Some(error)),
    };
    // Managed ExecuteSetterAsync.EnsureCurrent after a successful setter,
    // including a no-op one.
    let (status, error) = match power {
        Some(admitted) if status == Status::Succeeded && host.power() != Some(admitted) => {
            (Status::Indeterminate, Some(Cow::Borrowed(STALE_AFTER)))
        }
        _ => (status, error),
    };
    lock()?.complete(status, &state);
    match fatal {
        Some(error) => Err(error),
        None => Ok(CcdWriteOutcome {
            status,
            error,
            state,
        }),
    }
}
