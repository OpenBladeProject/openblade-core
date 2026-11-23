//! Production state shared by the selected partial display writes.
//!
//! One managed recovery checkpoint, one trusted power admission and one
//! submission fence serve manual `setBrightness`, `setHdr`, `setRefreshRate`,
//! `setColorProfile` and `applyProfile`. Brightness owns its operation order
//! (`brightness_write`); the other single-field writes share the
//! manual-operation frame of `manual_write`, and each supplies only its native
//! attempt and reply predicate. `profile_apply` composes those attempts.
use super::{
    observation::{SharedBrightness, Snapshot},
    recovery::{CheckpointFile, Fields, Recovery, WindowsCheckpointFile},
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
    sync::{Arc, Mutex, MutexGuard},
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

/// Managed `ValidateExpectedAsync`: the refusal of `power` for a request that
/// expects `expected`, or None when admitted. A manual-external context is
/// unconfirmed, so it admits only an ExternalUnknown request and otherwise
/// reports the unconfirmed refusal; a confirmed one admits only its own source.
pub(super) fn power_refusal(power: Option<PowerContext>, expected: u8) -> Option<&'static str> {
    match power {
        None => Some(UNCONFIRMED_POWER),
        Some(power) if power.manual_external() && power.source != expected => {
            Some(UNCONFIRMED_POWER)
        }
        Some(power) if power.source != expected => Some(CHANGED_POWER),
        Some(_) => None,
    }
}

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

pub(super) fn lock<F>(recovery: &Mutex<Recovery<F>>) -> io::Result<MutexGuard<'_, Recovery<F>>> {
    recovery
        .lock()
        .map_err(|_| io::Error::other("display recovery state failed"))
}

/// A manual request that was not admitted: managed returns Blocked without
/// completing the operation, with a fresh reply snapshot.
pub(super) struct Refused {
    pub(super) error: &'static str,
    pub(super) state: Snapshot,
}

/// Managed `BeginManualOperation`, after the native write gate, for every
/// manual display write: a request touching an unresolved field is refused
/// with `paused`, then the durable checkpoint for `fields` precedes any device
/// interaction. A refusal is answered by `query`; `None` admits the
/// operation, which must then be completed.
pub(super) fn begin<F: CheckpointFile>(
    recovery: &Mutex<Recovery<F>>,
    fields: Fields,
    paused: &'static str,
    power: Option<PowerContext>,
    query: impl FnOnce() -> io::Result<Snapshot>,
) -> io::Result<Option<Refused>> {
    let refused = {
        let mut recovery = lock(recovery)?;
        if recovery.unresolved().overlaps(fields) {
            Some(paused)
        } else if !recovery.begin(fields, power.map(|power| power.source)) {
            Some(CHECKPOINT)
        } else {
            None
        }
    };
    let Some(error) = refused else {
        return Ok(None);
    };
    Ok(Some(Refused {
        error,
        state: refreshed(query())?,
    }))
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
/// A failed join keeps the gate closed and stops the feature. HDR,
/// refresh-rate and color-profile submissions are synchronous, so the read
/// itself is the only reopening condition.
pub(super) fn explicit_read<F: CheckpointFile>(
    brightness: &SharedBrightness,
    recovery: &Mutex<Recovery<F>>,
    state: &Snapshot,
) -> io::Result<()> {
    #[cfg(feature = "agent-display-brightness-write-v1")]
    if lock(recovery)?.unresolved().brightness && state.brightness().is_some() {
        brightness
            .lock()
            .map_err(|_| io::Error::other("brightness owner state failed"))?
            .retire_worker()?;
    }
    #[cfg(not(feature = "agent-display-brightness-write-v1"))]
    let _ = brightness;
    lock(recovery)?.explicit_read(state);
    Ok(())
}

/// Managed `DisplayProfileApplier.operationGate`: with display automation,
/// manual queries and writes and automatic transitions each hold it for one
/// complete operation. Without automation the endpoint serves one request at
/// a time, so there is nothing else to serialize with.
#[cfg(feature = "agent-display-automation-v1")]
pub(super) type Gate = Arc<Mutex<()>>;

#[cfg(feature = "agent-display-automation-v1")]
pub(super) fn turn(gate: &Gate) -> io::Result<MutexGuard<'_, ()>> {
    gate.lock()
        .map_err(|_| io::Error::other("display operation gate failed"))
}

/// One manual operation's hold on the display operation gate. A mutation is
/// a pending manual operation from `preempt`, before the gate, until the turn
/// ends; ending it, still under the gate, clears what a mutation refused
/// before its `begin` left pending.
#[cfg(feature = "agent-display-automation-v1")]
pub(super) struct ManualTurn<'a, F: CheckpointFile> {
    mutation: Option<&'a Mutex<Recovery<F>>>,
    _gate: MutexGuard<'a, ()>,
}

#[cfg(feature = "agent-display-automation-v1")]
impl<F: CheckpointFile> Drop for ManualTurn<'_, F> {
    fn drop(&mut self) {
        // A poisoned recovery state already failed the operation and the
        // feature; nothing is pending that a later operation could observe.
        if let Some(Ok(mut recovery)) = self.mutation.map(Mutex::lock) {
            recovery.end_manual_turn();
        }
    }
}

/// The display operation gate for one manual operation. A mutation first
/// marks itself pending and cancels the running transition, as managed
/// `BeginManualOperation` runs before the applier's gate; the mutation's own
/// `begin` records the rest.
#[cfg(feature = "agent-display-automation-v1")]
pub(super) fn manual_turn<'a, F: CheckpointFile>(
    recovery: &'a Mutex<Recovery<F>>,
    gate: &'a Gate,
    mutation: bool,
) -> io::Result<ManualTurn<'a, F>> {
    let mutation = mutation.then_some(recovery);
    if mutation.is_some() {
        lock(recovery)?.preempt();
    }
    match turn(gate) {
        Ok(gate) => Ok(ManualTurn {
            mutation,
            _gate: gate,
        }),
        Err(error) => {
            if mutation.is_some() {
                lock(recovery)?.end_manual_turn();
            }
            Err(error)
        }
    }
}

/// Without display automation the endpoint serves one request at a time, so
/// a manual operation has no gate to hold.
#[cfg(not(feature = "agent-display-automation-v1"))]
struct NoGate;

/// Production state shared by queries and writes on this Display endpoint.
#[derive(Clone)]
pub(super) struct Writer {
    recovery: Arc<Mutex<Recovery<WindowsCheckpointFile>>>,
    host: WindowsHost,
    #[cfg(feature = "agent-display-automation-v1")]
    gate: Gate,
}

impl Writer {
    /// Blocking: reads the current user's managed checkpoint once.
    #[cfg(not(feature = "agent-display-automation-v1"))]
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

    /// Display automation already loaded the checkpoint that both share.
    #[cfg(feature = "agent-display-automation-v1")]
    pub(super) fn new(
        recovery: Arc<Mutex<Recovery<WindowsCheckpointFile>>>,
        origin: Arc<Origin>,
        power: PowerAdmission,
        stop: watch::Receiver<bool>,
    ) -> Self {
        Self {
            recovery,
            host: WindowsHost {
                origin,
                power,
                stop,
            },
            gate: Gate::default(),
        }
    }

    /// The transition applier, on the same host, brightness owner and gate.
    #[cfg(feature = "agent-display-automation-v1")]
    pub(super) fn applier(
        &self,
        brightness: SharedBrightness,
    ) -> super::automation::ProfileApplier {
        super::automation::ProfileApplier {
            host: self.host.clone(),
            brightness,
            gate: self.gate.clone(),
        }
    }

    /// `manual_turn` on this endpoint's checkpoint and gate.
    #[cfg(feature = "agent-display-automation-v1")]
    fn turn(&self, mutation: bool) -> io::Result<ManualTurn<'_, WindowsCheckpointFile>> {
        manual_turn(&self.recovery, &self.gate, mutation)
    }

    #[cfg(not(feature = "agent-display-automation-v1"))]
    fn turn(&self, _mutation: bool) -> io::Result<NoGate> {
        Ok(NoGate)
    }

    /// Blocking: one explicit query; a completed one may resume paused
    /// recovery (managed `ResumeAfterSuccessfulExplicitRead`).
    pub(super) fn query(
        &self,
        brightness: &SharedBrightness,
        canceled: &std::sync::atomic::AtomicBool,
    ) -> io::Result<Snapshot> {
        let _turn = self.turn(false)?;
        let snapshot = self.host.query(brightness, canceled)?;
        explicit_read(brightness, &self.recovery, &snapshot)?;
        Ok(snapshot)
    }

    /// Blocking: the complete admitted brightness operation, including its record.
    #[cfg(feature = "agent-display-brightness-write-v1")]
    pub(super) fn set_brightness(
        &self,
        brightness: &SharedBrightness,
        request: super::brightness_write::SetBrightness,
        canceled: &std::sync::atomic::AtomicBool,
    ) -> io::Result<super::brightness_write::WriteOutcome> {
        let _turn = self.turn(true)?;
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
    ) -> io::Result<super::manual_write::ManualWriteOutcome> {
        let _turn = self.turn(true)?;
        super::manual_write::run(
            &mut self.host.hdr_writer(),
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
    ) -> io::Result<super::manual_write::ManualWriteOutcome> {
        let _turn = self.turn(true)?;
        super::manual_write::run(
            &mut self.host.refresh_writer(),
            brightness,
            &self.recovery,
            request,
            canceled,
        )
    }

    /// Blocking: the complete admitted color-profile operation, including its record.
    #[cfg(feature = "agent-display-color-write-v1")]
    pub(super) fn set_color_profile(
        &self,
        brightness: &SharedBrightness,
        request: &super::color_write::SetColorProfile,
        canceled: &std::sync::atomic::AtomicBool,
    ) -> io::Result<super::manual_write::ManualWriteOutcome> {
        let _turn = self.turn(true)?;
        super::manual_write::run(
            &mut self.host.color_writer(),
            brightness,
            &self.recovery,
            request,
            canceled,
        )
    }

    /// Blocking: the complete admitted profile operation, including its record.
    #[cfg(feature = "agent-display-profile-apply-v1")]
    pub(super) fn apply_profile(
        &self,
        brightness: &SharedBrightness,
        request: &super::profile_apply::ApplyProfile,
        canceled: &std::sync::atomic::AtomicBool,
    ) -> io::Result<super::manual_write::ManualWriteOutcome> {
        let _turn = self.turn(true)?;
        super::profile_apply::run(
            &mut self.host.clone(),
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
    /// The trusted confirmed power context, if any (automation and profile
    /// apply).
    #[cfg(feature = "agent-display-profile-apply-v1")]
    pub(super) fn power(&self) -> Option<PowerContext> {
        self.power.capture()
    }

    /// The context a manual setter may admit: confirmed, or managed
    /// `ManualExternalPowerPresent`.
    pub(super) fn manual_power(&self) -> Option<PowerContext> {
        self.power.capture_manual()
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

    /// The setHdr host: this admission state and the real HDR native.
    #[cfg(feature = "agent-display-hdr-write-v1")]
    pub(super) fn hdr_writer(
        &self,
    ) -> super::manual_write::WindowsManualHost<
        openblade_platform_windows::display::hdr::WindowsHdrNative,
    > {
        super::manual_write::WindowsManualHost::new(
            self.clone(),
            openblade_platform_windows::display::hdr::WindowsHdrNative::current_user(),
        )
    }

    /// The setRefreshRate host: this admission state and the real CCD native.
    #[cfg(feature = "agent-display-refresh-write-v1")]
    pub(super) fn refresh_writer(
        &self,
    ) -> super::manual_write::WindowsManualHost<
        openblade_platform_windows::display::refresh_write::WindowsRefreshNative,
    > {
        super::manual_write::WindowsManualHost::new(
            self.clone(),
            openblade_platform_windows::display::refresh_write::WindowsRefreshNative::current_user(
            ),
        )
    }

    /// The setColorProfile host: this admission state and the real WCS native.
    #[cfg(feature = "agent-display-color-write-v1")]
    pub(super) fn color_writer(
        &self,
    ) -> super::manual_write::WindowsManualHost<
        openblade_platform_windows::color_profile::write::WindowsColorProfileNative,
    > {
        super::manual_write::WindowsManualHost::new(
            self.clone(),
            openblade_platform_windows::color_profile::write::WindowsColorProfileNative::current_user(),
        )
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
