//! Selected manual `setBrightness` through the shared Rust WMI policy.
//!
//! Order mirrors the managed dispatcher and applier: a durable manual-hold
//! checkpoint precedes any device interaction, then fresh power admission,
//! then one fenced targeted write whose worker re-checks Origin, session,
//! power generation and panel topology after preparation. Every admitted
//! operation records its certainty before returning, even when the client,
//! deadline or Stop has already abandoned the reply.
#[cfg(test)]
use super::write::explicit_read;
#[cfg(test)]
pub(super) use super::write::{CHANGED_POWER, CHECKPOINT};
pub(super) use super::write::{STALE_AFTER, UNCONFIRMED_POWER, UNREFRESHED};
use super::{
    observation::{SharedBrightness, Snapshot},
    recovery::{CheckpointFile, Fields, Recovery, Status},
    write::{WindowsHost, begin, lock, power_refusal, refreshed},
};
use crate::session_native::power_admission::PowerContext;
use openblade_platform_windows::{
    brightness::{
        BrightnessError, BrightnessOutcome, BrightnessTarget, NativeFailure, SubmissionFence,
    },
    display::{InternalDisplayTopology, read_internal_display},
};
use std::{
    io,
    sync::{Arc, Mutex, atomic::AtomicBool},
};

pub(super) const PAUSED: &str =
    "Brightness changes are paused until a display read confirms the last change.";
pub(super) const STALE: &str =
    "The display operation became stale before the brightness change was submitted.";
pub(super) const PANEL_CHANGED: &str =
    "The internal display changed before the brightness change was submitted.";
pub(super) const CANCELED: &str =
    "The display operation was canceled before the brightness change was submitted.";
pub(super) const BUSY: &str = "Display brightness is busy. Try again.";
pub(super) const UNSUPPORTED: &str = "The display does not support the requested brightness.";
pub(super) const UNAVAILABLE: &str = "Display brightness is unavailable.";
pub(super) const UNCONFIRMED: &str = "Windows did not confirm the requested display brightness.";

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) struct SetBrightness {
    pub(super) expected_power_source: u8,
    pub(super) percent: u8,
}

pub(super) struct WriteOutcome {
    pub(super) status: Status,
    pub(super) error: Option<&'static str>,
    pub(super) state: Snapshot,
}

/// Injected OS boundary. Production reads the trusted power context, CCD
/// topology and the full display snapshot; the fence re-verifies the loaded
/// Origin, active console session and power generation.
pub(super) trait WriteHost {
    fn power(&self) -> Option<PowerContext>;
    fn fence(&self, admitted: PowerContext) -> Arc<dyn SubmissionFence>;
    fn target(&mut self) -> Result<BrightnessTarget, BrightnessError>;
    fn query(
        &mut self,
        brightness: &SharedBrightness,
        canceled: &AtomicBool,
    ) -> io::Result<Snapshot>;
}

pub(super) fn set_brightness<F: CheckpointFile>(
    host: &mut impl WriteHost,
    brightness: &SharedBrightness,
    recovery: &Mutex<Recovery<F>>,
    request: SetBrightness,
    canceled: &AtomicBool,
) -> io::Result<WriteOutcome> {
    let power = host.power();
    if let Some(refused) = begin(recovery, Fields::BRIGHTNESS, PAUSED, power, || {
        host.query(brightness, canceled)
    })? {
        return Ok(WriteOutcome {
            status: Status::Blocked,
            error: Some(refused.error),
            state: refused.state,
        });
    }

    // Admitted manual operation: whatever happens next, record its certainty.
    let attempt = attempt(host, brightness, request, power, canceled);
    let (status, error, confirmed, fatal) = match attempt {
        Ok((status, error, confirmed)) => (status, error, confirmed, None),
        Err((submitted, error)) => {
            let status = if submitted {
                Status::Indeterminate
            } else {
                Status::Failed
            };
            (status, Some(UNAVAILABLE), None, Some(error))
        }
    };
    // A failed reply query is fatal, but the admitted operation is recorded
    // first, as for HDR (a Confirmed level it cannot show is Indeterminate).
    let (state, fatal) = match fatal {
        Some(error) => (Snapshot::unavailable(UNREFRESHED), Some(error)),
        None => match refreshed(host.query(brightness, canceled)) {
            Ok(state) => (state, None),
            Err(error) => (Snapshot::unavailable(UNREFRESHED), Some(error)),
        },
    };
    let (status, error) = match confirmed {
        // Managed applier: success must still show the selected level.
        Some(percent) if state.brightness() != Some(percent) => {
            (Status::Indeterminate, Some(UNCONFIRMED))
        }
        _ => (status, error),
    };
    // Managed ExecuteSetterAsync.EnsureCurrent after a successful setter. A
    // no-op returns before the setter in managed SetBrightnessAsync, unchecked.
    // (Managed still checks a controller-level no-op after its fresh read; native
    // skips that too. Nothing is submitted on either path, so it is not uncertain.)
    let (status, error) = match power {
        Some(admitted)
            if status == Status::Succeeded
                && confirmed.is_some()
                && host.power() != Some(admitted) =>
        {
            (Status::Indeterminate, Some(STALE_AFTER))
        }
        _ => (status, error),
    };
    lock(recovery)?.complete(status, &state);
    match fatal {
        Some(error) => Err(error),
        None => Ok(WriteOutcome {
            status,
            error,
            state,
        }),
    }
}

/// The status, error and confirmed level of one attempt, or a fatal error and
/// whether a submission may have occurred before it.
pub(super) type Attempt = Result<(Status, Option<&'static str>, Option<u8>), (bool, io::Error)>;

fn attempt(
    host: &mut impl WriteHost,
    brightness: &SharedBrightness,
    request: SetBrightness,
    power: Option<PowerContext>,
    canceled: &AtomicBool,
) -> Attempt {
    match (power, power_refusal(power, request.expected_power_source)) {
        (Some(power), None) => admitted(host, brightness, request.percent, power, canceled),
        (_, refusal) => Ok((Status::Blocked, refusal.or(Some(UNCONFIRMED_POWER)), None)),
    }
}

/// One fenced targeted write after power admission. A confirmed level is
/// the worker's own readback; an unchanged one submitted nothing.
pub(super) fn admitted(
    host: &mut impl WriteHost,
    brightness: &SharedBrightness,
    percent: u8,
    power: PowerContext,
    canceled: &AtomicBool,
) -> Attempt {
    let target = match host.target() {
        Ok(target) => target,
        Err(_) => return Ok((Status::Failed, Some(UNAVAILABLE), None)),
    };
    let fence = host.fence(power);
    let result = brightness
        .lock()
        .map_err(|_| (false, io::Error::other("brightness owner state failed")))?
        .apply(percent, target, fence, canceled)
        .map_err(|error| (false, error))?;
    Ok(match result {
        // Nothing was submitted: the worker's own read is the confirmation,
        // and a later reply-query miss cannot make a no-op uncertain.
        Ok(BrightnessOutcome::Unchanged(_)) => (Status::Succeeded, None, None),
        Ok(BrightnessOutcome::Confirmed(observed)) => {
            (Status::Succeeded, None, Some(observed.percent))
        }
        Err(failure)
            if failure.reason == BrightnessError::Native(NativeFailure::WorkerDisconnected) =>
        {
            // The worker thread failed: stop the feature after recording.
            return Err((
                failure.submission_may_have_occurred,
                io::Error::other("brightness worker disconnected"),
            ));
        }
        Err(failure) if failure.submission_may_have_occurred => {
            (Status::Indeterminate, Some(UNCONFIRMED), None)
        }
        Err(failure) => match failure.reason {
            BrightnessError::Native(NativeFailure::AdmissionDenied) => {
                (Status::Blocked, Some(STALE), None)
            }
            BrightnessError::PanelChanged => (Status::Blocked, Some(PANEL_CHANGED), None),
            BrightnessError::SubmissionUnresolved => (Status::Blocked, Some(PAUSED), None),
            BrightnessError::Native(NativeFailure::Cancelled) => {
                (Status::Blocked, Some(CANCELED), None)
            }
            BrightnessError::Native(NativeFailure::WorkerBusy) => {
                (Status::Failed, Some(BUSY), None)
            }
            BrightnessError::UnsupportedLevel => (Status::Failed, Some(UNSUPPORTED), None),
            _ => (Status::Failed, Some(UNAVAILABLE), None),
        },
    })
}

impl WriteHost for WindowsHost {
    fn power(&self) -> Option<PowerContext> {
        WindowsHost::manual_power(self)
    }

    fn fence(&self, admitted: PowerContext) -> Arc<dyn SubmissionFence> {
        WindowsHost::fence(self, admitted)
    }

    fn target(&mut self) -> Result<BrightnessTarget, BrightnessError> {
        match read_internal_display().map_err(BrightnessError::Topology)? {
            InternalDisplayTopology::Single(display) => BrightnessTarget::from_display(&display),
            InternalDisplayTopology::MultipleInternalDisplays => {
                Err(BrightnessError::AmbiguousPanel)
            }
            _ => Err(BrightnessError::PanelUnavailable),
        }
    }

    fn query(
        &mut self,
        brightness: &SharedBrightness,
        canceled: &AtomicBool,
    ) -> io::Result<Snapshot> {
        WindowsHost::query(self, brightness, canceled)
    }
}

#[cfg(test)]
#[path = "brightness_write_tests.rs"]
mod tests;
