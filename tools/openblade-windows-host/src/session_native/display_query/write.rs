//! Production state shared by the selected partial display writes.
//!
//! One managed recovery checkpoint, one trusted power admission and one
//! submission fence serve manual `setBrightness`, `setHdr` and `setRefreshRate`.
//! Brightness owns its operation order (`brightness_write`); the CCD writes share
//! the manual-operation frame of `ccd_write`, and each supplies only its native
//! attempt and reply predicate.
use super::{
    observation::{SharedBrightness, Snapshot},
    recovery::{CheckpointFile, Recovery, WindowsCheckpointFile},
};
use crate::{
    loaded_origin::Origin,
    session_native::{
        power_admission::{PowerAdmission, PowerContext},
        user_environment::active_console,
    },
};
use openblade_platform_windows::display::{
    DisplayReadError, InternalDisplayTopology, SubmissionFence, read_internal_display,
};
use std::{
    io,
    sync::{Arc, Mutex},
};
use tokio::sync::watch;

pub(super) const CHECKPOINT: &str =
    "The display operation was blocked because its recovery checkpoint could not be saved.";
pub(super) const UNCONFIRMED_POWER: &str =
    "Display changes are blocked until the power source is confirmed.";
pub(super) const CHANGED_POWER: &str =
    "The active power source changed before the display operation began.";
pub(super) const UNREFRESHED: &str = "Display state could not be refreshed.";
/// Managed dispatcher: `EnsureCurrent` failed after a successful setter.
pub(super) const STALE_AFTER: &str =
    "The display operation became stale after the power source changed.";

/// Managed `TryQueryFailureAsync`: report the reply query's snapshot, or an
/// unavailable one when the read itself is interrupted.
pub(super) fn refreshed(query: io::Result<Snapshot>) -> io::Result<Snapshot> {
    match query {
        Err(error) if error.kind() == io::ErrorKind::Interrupted => {
            Ok(Snapshot::unavailable(UNREFRESHED))
        }
        result => result,
    }
}

/// Managed `ResumeAfterSuccessfulExplicitRead`, plus the native write gates.
///
/// The brightness worker keeps its own in-process pause after a possible
/// submission (an unconfirmed acknowledgement, a native failure after the
/// submission boundary, or an abandoned started reply); a query never
/// resolves it. That gate reopens only when this explicit read, taken through
/// the same worker, observed brightness, which means the worker finished every
/// pending call. Before reopening, positively join and drop that worker so the
/// next write starts a fresh one: the same boundary an Agent restart provides.
/// A failed join keeps the gate closed and stops the feature. HDR and
/// refresh-rate submissions are synchronous, so the read itself is the only
/// reopening condition.
pub(super) fn explicit_read<F: CheckpointFile>(
    brightness: &SharedBrightness,
    recovery: &Mutex<Recovery<F>>,
    state: &Snapshot,
) -> io::Result<()> {
    let lock = || {
        recovery
            .lock()
            .map_err(|_| io::Error::other("display recovery state failed"))
    };
    #[cfg(feature = "agent-display-brightness-write-v1")]
    if lock()?.unresolved().brightness && state.brightness().is_some() {
        brightness
            .lock()
            .map_err(|_| io::Error::other("brightness owner state failed"))?
            .retire_worker()?;
    }
    #[cfg(not(feature = "agent-display-brightness-write-v1"))]
    let _ = brightness;
    lock()?.explicit_read(state);
    Ok(())
}

/// Production state shared by queries and writes on this Display endpoint.
#[derive(Clone)]
pub(super) struct Writer {
    recovery: Arc<Mutex<Recovery<WindowsCheckpointFile>>>,
    host: WindowsHost,
}

impl Writer {
    /// Blocking: reads the current user's managed checkpoint once.
    pub(super) fn load(
        origin: Arc<Origin>,
        power: PowerAdmission,
        stop: watch::Receiver<bool>,
    ) -> Self {
        Self {
            recovery: Arc::new(Mutex::new(Recovery::load(
                WindowsCheckpointFile::current_user(),
            ))),
            host: WindowsHost {
                origin,
                power,
                stop,
            },
        }
    }

    /// Blocking: a completed explicit query may resume paused recovery.
    pub(super) fn explicit_read(
        &self,
        brightness: &SharedBrightness,
        state: &Snapshot,
    ) -> io::Result<()> {
        explicit_read(brightness, &self.recovery, state)
    }

    /// Blocking: the complete admitted brightness operation, including its record.
    #[cfg(feature = "agent-display-brightness-write-v1")]
    pub(super) fn set_brightness(
        &self,
        brightness: &SharedBrightness,
        request: super::brightness_write::SetBrightness,
        canceled: &std::sync::atomic::AtomicBool,
    ) -> io::Result<super::brightness_write::WriteOutcome> {
        super::brightness_write::set_brightness(
            &mut self.host.clone(),
            brightness,
            &self.recovery,
            request,
            canceled,
        )
    }

    /// Blocking: the complete admitted HDR operation, including its record.
    #[cfg(feature = "agent-display-hdr-write-v1")]
    pub(super) fn set_hdr(
        &self,
        brightness: &SharedBrightness,
        request: super::hdr_write::SetHdr,
        canceled: &std::sync::atomic::AtomicBool,
    ) -> io::Result<super::ccd_write::CcdWriteOutcome> {
        super::ccd_write::run(
            &mut super::ccd_write::WindowsCcdHost::new(
                self.host.clone(),
                openblade_platform_windows::display::hdr::WindowsHdrNative::current_user(),
            ),
            brightness,
            &self.recovery,
            &request,
            canceled,
        )
    }

    /// Blocking: the complete admitted refresh-rate operation, including its record.
    #[cfg(feature = "agent-display-refresh-write-v1")]
    pub(super) fn set_refresh_rate(
        &self,
        brightness: &SharedBrightness,
        request: &super::refresh_write::SetRefreshRate,
        canceled: &std::sync::atomic::AtomicBool,
    ) -> io::Result<super::ccd_write::CcdWriteOutcome> {
        super::ccd_write::run(
            &mut super::ccd_write::WindowsCcdHost::new(
                self.host.clone(),
                openblade_platform_windows::display::refresh_write::WindowsRefreshNative::current_user(),
            ),
            brightness,
            &self.recovery,
            request,
            canceled,
        )
    }
}

#[derive(Clone)]
pub(super) struct WindowsHost {
    origin: Arc<Origin>,
    power: PowerAdmission,
    /// The display feature stop, which every lifetime exit (Stop, Origin
    /// loss, status watcher or sibling failure) signals before draining.
    stop: watch::Receiver<bool>,
}

impl WindowsHost {
    /// The trusted confirmed power context, if any.
    pub(super) fn power(&self) -> Option<PowerContext> {
        self.power.capture()
    }

    /// The production fence bound to the captured power context.
    pub(super) fn fence(&self, admitted: PowerContext) -> Arc<dyn SubmissionFence> {
        Arc::new(WindowsFence {
            host: self.clone(),
            admitted,
        })
    }

    pub(super) fn query(
        &self,
        brightness: &SharedBrightness,
        canceled: &std::sync::atomic::AtomicBool,
    ) -> io::Result<Snapshot> {
        super::observation::query(canceled, brightness.clone())
    }
}

/// Re-checked after preparation, immediately before the one possible native
/// submission: fresh CCD topology, the loaded Origin, the active console
/// session and the unchanged trusted power generation.
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
            && !*self.host.stop.borrow()
    }
}
