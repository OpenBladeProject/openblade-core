//! Selected manual `setBrightness` through the shared Rust WMI policy.
//!
//! Order mirrors the managed dispatcher and applier: a durable manual-hold
//! checkpoint precedes any device interaction, then fresh power admission,
//! then one fenced targeted write whose worker re-checks Origin, session,
//! power generation and panel topology after preparation. Every admitted
//! operation records its certainty before returning, even when the client,
//! deadline or Stop has already abandoned the reply.
use super::{
    observation::{SharedBrightness, Snapshot},
    recovery::{CheckpointFile, Recovery, Status, WindowsCheckpointFile},
};
use crate::{
    loaded_origin::Origin,
    session_native::{
        power_admission::{PowerAdmission, PowerContext},
        user_environment::active_console,
    },
};
use openblade_platform_windows::{
    brightness::{
        BrightnessError, BrightnessOutcome, BrightnessTarget, NativeFailure, SubmissionFence,
    },
    display::{DisplayReadError, InternalDisplayTopology, read_internal_display},
};
use std::{
    io,
    sync::{Arc, Mutex, atomic::AtomicBool},
};

pub(super) const PAUSED: &str =
    "Brightness changes are paused until a display read confirms the last change.";
pub(super) const CHECKPOINT: &str =
    "The display operation was blocked because its recovery checkpoint could not be saved.";
pub(super) const UNCONFIRMED_POWER: &str =
    "Display changes are blocked until the power source is confirmed.";
pub(super) const CHANGED_POWER: &str =
    "The active power source changed before the display operation began.";
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
pub(super) const UNREFRESHED: &str = "Display state could not be refreshed.";

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
    fn query(&mut self, brightness: &SharedBrightness, canceled: &AtomicBool) -> io::Result<Snapshot>;
}

pub(super) fn set_brightness<F: CheckpointFile>(
    host: &mut impl WriteHost,
    brightness: &SharedBrightness,
    recovery: &Mutex<Recovery<F>>,
    request: SetBrightness,
    canceled: &AtomicBool,
) -> io::Result<WriteOutcome> {
    let lock = || {
        recovery
            .lock()
            .map_err(|_| io::Error::other("display recovery state failed"))
    };
    let power = host.power();
    {
        let mut recovery = lock()?;
        let refused = if recovery.brightness_unresolved() {
            Some(PAUSED)
        } else if !recovery.begin_brightness(power.map(|power| power.source)) {
            Some(CHECKPOINT)
        } else {
            None
        };
        if let Some(error) = refused {
            drop(recovery);
            // Not admitted: managed returns Blocked without completing.
            return Ok(WriteOutcome {
                status: Status::Blocked,
                error: Some(error),
                state: refreshed(host, brightness, canceled)?,
            });
        }
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
    let state = match fatal {
        Some(_) => Snapshot::unavailable(UNREFRESHED),
        None => refreshed(host, brightness, canceled)?,
    };
    let (status, error) = match confirmed {
        // Managed applier: success must still show the selected level.
        Some(percent) if state.brightness() != Some(percent) => {
            (Status::Indeterminate, Some(UNCONFIRMED))
        }
        _ => (status, error),
    };
    lock()?.complete(status, &state);
    match fatal {
        Some(error) => Err(error),
        None => Ok(WriteOutcome {
            status,
            error,
            state,
        }),
    }
}

type Attempt = Result<(Status, Option<&'static str>, Option<u8>), (bool, io::Error)>;

fn attempt(
    host: &mut impl WriteHost,
    brightness: &SharedBrightness,
    request: SetBrightness,
    power: Option<PowerContext>,
    canceled: &AtomicBool,
) -> Attempt {
    let Some(power) = power else {
        return Ok((Status::Blocked, Some(UNCONFIRMED_POWER), None));
    };
    if power.source != request.expected_power_source {
        return Ok((Status::Blocked, Some(CHANGED_POWER), None));
    }
    let target = match host.target() {
        Ok(target) => target,
        Err(_) => return Ok((Status::Failed, Some(UNAVAILABLE), None)),
    };
    let fence = host.fence(power);
    let result = brightness
        .lock()
        .map_err(|_| (false, io::Error::other("brightness owner state failed")))?
        .apply(request.percent, target, fence, canceled)
        .map_err(|error| (false, error))?;
    Ok(match result {
        Ok(BrightnessOutcome::Unchanged(observed)) => {
            (Status::Succeeded, None, Some(observed.percent))
        }
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

/// Production state shared by queries and writes on this Display endpoint.
#[derive(Clone)]
pub(super) struct Writer {
    recovery: Arc<Mutex<Recovery<WindowsCheckpointFile>>>,
    host: WindowsHost,
}

impl Writer {
    /// Blocking: reads the current user's managed checkpoint once.
    pub(super) fn load(origin: Arc<Origin>, power: PowerAdmission) -> Self {
        Self {
            recovery: Arc::new(Mutex::new(Recovery::load(
                WindowsCheckpointFile::current_user(),
            ))),
            host: WindowsHost { origin, power },
        }
    }

    /// Blocking: a completed explicit query may resume paused recovery.
    pub(super) fn explicit_read(&self, state: &Snapshot) -> io::Result<()> {
        self.recovery
            .lock()
            .map_err(|_| io::Error::other("display recovery state failed"))?
            .explicit_read(state);
        Ok(())
    }

    /// Blocking: the complete admitted operation, including its record.
    pub(super) fn set_brightness(
        &self,
        brightness: &SharedBrightness,
        request: SetBrightness,
        canceled: &AtomicBool,
    ) -> io::Result<WriteOutcome> {
        set_brightness(
            &mut self.host.clone(),
            brightness,
            &self.recovery,
            request,
            canceled,
        )
    }
}

#[derive(Clone)]
struct WindowsHost {
    origin: Arc<Origin>,
    power: PowerAdmission,
}

/// Re-checked by the worker after preparation, immediately before the one
/// possible WmiSetBrightness: fresh CCD topology, the loaded Origin, the
/// active console session and the unchanged trusted power generation.
struct WindowsFence {
    host: WindowsHost,
    admitted: PowerContext,
}

impl SubmissionFence for WindowsFence {
    fn topology(&self) -> Result<InternalDisplayTopology, DisplayReadError> {
        read_internal_display()
    }

    fn admit(&self) -> bool {
        self.host.origin.verify().is_ok()
            && active_console(self.host.origin.session)
            && self.host.power.current(self.admitted)
    }
}

impl WriteHost for WindowsHost {
    fn power(&self) -> Option<PowerContext> {
        self.power.capture()
    }

    fn fence(&self, admitted: PowerContext) -> Arc<dyn SubmissionFence> {
        Arc::new(WindowsFence {
            host: self.clone(),
            admitted,
        })
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

    fn query(&mut self, brightness: &SharedBrightness, canceled: &AtomicBool) -> io::Result<Snapshot> {
        super::observation::query(canceled, brightness.clone())
    }
}

/// Managed `TryQueryFailureAsync`: report the current snapshot, or an
/// unavailable one when the read itself is interrupted.
fn refreshed(
    host: &mut impl WriteHost,
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

#[cfg(test)]
#[path = "brightness_write_tests.rs"]
mod tests;
