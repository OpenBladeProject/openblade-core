//! Selected manual `setHdr` through the shared Rust HDR writer.
//!
//! Order mirrors the managed dispatcher, applier and controller: a durable
//! manual-hold checkpoint for the HDR field precedes any device interaction,
//! then fresh power admission, then one fenced SET_HDR_STATE submission whose
//! fence re-checks topology, Origin, session and power generation, then an
//! independent readback. Every admitted operation records its certainty
//! before returning, even when the client, deadline or Stop has already
//! abandoned the reply.
use super::{
    observation::{SharedBrightness, Snapshot},
    recovery::{CheckpointFile, Fields, Recovery, Status},
    write::{CHANGED_POWER, CHECKPOINT, UNCONFIRMED_POWER, UNREFRESHED, WindowsHost},
};
use crate::session_native::power_admission::PowerContext;
use openblade_platform_windows::display::{
    DisplayReadError, Operation as ReadOperation, SubmissionFence,
    hdr::{
        HdrError, HdrFailure, HdrNative, HdrOutcome, HdrTarget, WindowsHdrNative, set_internal_hdr,
    },
};
use std::{
    borrow::Cow,
    io,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
};

/// Native-only write gate (no managed equivalent).
pub(super) const PAUSED: &str =
    "HDR changes are paused until a display read confirms the last change.";
/// Native fence refusals, parallel to the brightness texts.
pub(super) const STALE: &str =
    "The display operation became stale before the HDR change was submitted.";
pub(super) const PANEL_CHANGED: &str =
    "The internal display changed before the HDR change was submitted.";
pub(super) const CANCELED: &str =
    "The display operation was canceled before the HDR change was submitted.";
/// Managed `PrepareHdrCore`.
pub(super) const NOT_SUPPORTED: &str = "HDR is not supported for the active internal display.";
/// Managed `PrepareHdrCore` readback mismatch.
pub(super) const UNCONFIRMED: &str = "Windows did not confirm the requested HDR state.";
/// Managed dispatcher: `EnsureCurrent` failed after a successful setter.
pub(super) const STALE_AFTER: &str =
    "The display operation became stale after the power source changed.";
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
fn win32(operation: &str, status: u32) -> Cow<'static, str> {
    Cow::Owned(format!("{operation}: Win32 error {status}."))
}

/// Managed `PrepareHdrCore` text for a rejected SET_HDR_STATE submission.
pub(super) fn rejected(status: u32) -> Cow<'static, str> {
    win32("Windows could not change HDR", status)
}

fn unreadable(error: DisplayReadError) -> Cow<'static, str> {
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

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) struct SetHdr {
    pub(super) expected_power_source: u8,
    pub(super) enabled: bool,
}

pub(super) struct HdrWriteOutcome {
    pub(super) status: Status,
    pub(super) error: Option<Cow<'static, str>>,
    pub(super) state: Snapshot,
}

/// Injected OS boundary. Production reads the trusted power context and the
/// full display snapshot, and owns the real SET_HDR_STATE native; the fence
/// re-verifies topology, the loaded Origin, active console session and power.
pub(super) trait HdrHost {
    fn power(&self) -> Option<PowerContext>;
    fn fence(&self, admitted: PowerContext) -> Arc<dyn SubmissionFence>;
    fn native(&mut self) -> &mut dyn HdrNative;
    fn query(
        &mut self,
        brightness: &SharedBrightness,
        canceled: &AtomicBool,
    ) -> io::Result<Snapshot>;
}

pub(super) fn set_hdr<F: CheckpointFile>(
    host: &mut impl HdrHost,
    brightness: &SharedBrightness,
    recovery: &Mutex<Recovery<F>>,
    request: SetHdr,
    canceled: &AtomicBool,
) -> io::Result<HdrWriteOutcome> {
    let lock = || {
        recovery
            .lock()
            .map_err(|_| io::Error::other("display recovery state failed"))
    };
    let power = host.power();
    {
        let mut recovery = lock()?;
        let refused = if recovery.unresolved().hdr {
            Some(PAUSED)
        } else if !recovery.begin(Fields::HDR, power.map(|power| power.source)) {
            Some(CHECKPOINT)
        } else {
            None
        };
        if let Some(error) = refused {
            drop(recovery);
            // Not admitted: managed returns Blocked without completing.
            return Ok(HdrWriteOutcome {
                status: Status::Blocked,
                error: Some(Cow::Borrowed(error)),
                state: refreshed(host, brightness, canceled)?,
            });
        }
    }

    // Admitted manual operation: whatever happens next, record its certainty.
    let attempt = attempt(host, request, power, canceled);
    let (state, fatal) = match refreshed(host, brightness, canceled) {
        Ok(state) => (state, None),
        Err(error) => (Snapshot::unavailable(UNREFRESHED), Some(error)),
    };
    let shows_request = state.hdr() == Some(request.enabled);
    let (status, error) = match attempt {
        // Nothing was submitted: the writer's own read is the confirmation,
        // and a later reply-query miss cannot make a no-op uncertain.
        Attempt::Unchanged => (Status::Succeeded, None),
        // The reply must still show the confirmed state.
        Attempt::Confirmed if shows_request => (Status::Succeeded, None),
        Attempt::Confirmed => (Status::Indeterminate, Some(Cow::Borrowed(UNCONFIRMED))),
        // Managed PrepareHdr failure: ResolveHdrMutationResult over a fresh
        // snapshot, without a possible submission.
        Attempt::Prepare(_) if shows_request => (Status::Succeeded, None),
        Attempt::Prepare(error) => (Status::Failed, Some(error)),
        Attempt::Done(status, error) => (status, Some(error)),
    };
    // Managed ExecuteSetterAsync.EnsureCurrent after a successful setter.
    let (status, error) = match power {
        Some(admitted) if status == Status::Succeeded && host.power() != Some(admitted) => {
            (Status::Indeterminate, Some(Cow::Borrowed(STALE_AFTER)))
        }
        _ => (status, error),
    };
    lock()?.complete(status, &state);
    match fatal {
        Some(error) => Err(error),
        None => Ok(HdrWriteOutcome {
            status,
            error,
            state,
        }),
    }
}

enum Attempt {
    Unchanged,
    Confirmed,
    Prepare(Cow<'static, str>),
    Done(Status, Cow<'static, str>),
}

fn attempt(
    host: &mut impl HdrHost,
    request: SetHdr,
    power: Option<PowerContext>,
    canceled: &AtomicBool,
) -> Attempt {
    let done = |status, error: &'static str| Attempt::Done(status, Cow::Borrowed(error));
    let Some(power) = power else {
        return done(Status::Blocked, UNCONFIRMED_POWER);
    };
    if power.source != request.expected_power_source {
        return done(Status::Blocked, CHANGED_POWER);
    }
    let target = match host.native().topology() {
        Ok(topology) => HdrTarget::from_topology(topology),
        Err(error) => Err(HdrError::Topology(error)),
    };
    let result = target
        .map_err(|reason| HdrFailure {
            reason,
            submission_may_have_occurred: false,
        })
        .and_then(|target| {
            let fence = host.fence(power);
            set_internal_hdr(
                host.native(),
                &target,
                request.enabled,
                fence.as_ref(),
                &|| canceled.load(Ordering::Acquire),
            )
        });
    match result {
        Ok(HdrOutcome::Unchanged) => Attempt::Unchanged,
        Ok(HdrOutcome::Confirmed) => Attempt::Confirmed,
        Err(HdrFailure {
            reason: HdrError::Rejected(status),
            submission_may_have_occurred: true,
        }) => Attempt::Done(Status::Indeterminate, rejected(status)),
        Err(HdrFailure {
            submission_may_have_occurred: true,
            ..
        }) => done(Status::Indeterminate, UNCONFIRMED),
        Err(HdrFailure { reason, .. }) => match reason {
            HdrError::Topology(error) => Attempt::Prepare(unreadable(error)),
            HdrError::NoInternalDisplay => Attempt::Prepare(Cow::Borrowed(NO_INTERNAL)),
            HdrError::MultipleInternalDisplays => Attempt::Prepare(Cow::Borrowed(MULTIPLE)),
            HdrError::NotSupported => done(Status::Failed, NOT_SUPPORTED),
            HdrError::PanelChanged => done(Status::Blocked, PANEL_CHANGED),
            HdrError::AdmissionDenied => done(Status::Blocked, STALE),
            HdrError::Cancelled => done(Status::Blocked, CANCELED),
            // Not produced before a submission; never claim certainty.
            HdrError::Rejected(_) | HdrError::Unconfirmed => done(Status::Failed, UNCONFIRMED),
        },
    }
}

/// Production host: the shared admission state and the real HDR native.
pub(super) struct WindowsHdrHost {
    host: WindowsHost,
    native: WindowsHdrNative,
}

impl WindowsHdrHost {
    pub(super) fn new(host: WindowsHost) -> Self {
        Self {
            host,
            native: WindowsHdrNative::current_user(),
        }
    }
}

impl HdrHost for WindowsHdrHost {
    fn power(&self) -> Option<PowerContext> {
        self.host.power()
    }

    fn fence(&self, admitted: PowerContext) -> Arc<dyn SubmissionFence> {
        self.host.fence(admitted)
    }

    fn native(&mut self) -> &mut dyn HdrNative {
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

/// Managed `TryQueryFailureAsync`: report the current snapshot, or an
/// unavailable one when the read itself is interrupted.
fn refreshed(
    host: &mut impl HdrHost,
    brightness: &SharedBrightness,
    canceled: &AtomicBool,
) -> io::Result<Snapshot> {
    match host.query(brightness, canceled) {
        Ok(state) => Ok(state),
        Err(error) if error.kind() == io::ErrorKind::Interrupted => {
            Ok(Snapshot::unavailable(UNREFRESHED))
        }
        Err(error) => Err(error),
    }
}
