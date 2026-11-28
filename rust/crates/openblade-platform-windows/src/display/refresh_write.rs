//! Manual refresh rate for the single active internal display.
//!
//! Mirrors managed `WindowsDisplaySettingsController.PrepareRefresh`,
//! `PrepareRefreshTopology`/`PrepareRefreshPath`, `ValidateTopology`,
//! `ApplyTopology` and `ExecuteRefreshMutation`: one fresh observation
//! classifies the request and supplies the paired CCD frame and timing
//! evidence, an unavailable option or the already current option submits
//! nothing, and the prepared configuration must pass `SDC_VALIDATE` before
//! the single `SDC_APPLY`. A fixed rate supplies the panel's explicit target
//! timing when one is known (issue #884); the target index is omitted only
//! when no exact timing exists, and that request must still validate. The
//! native layer adds the host fence immediately before `SDC_APPLY`
//! (validation is not a submission). Any APPLY error may have been applied,
//! so it is uncertain whatever the readback shows. Success requires an
//! independent readback whose classified key, which matches the desktop rate,
//! the physical signal rate and the boost flag, equals the request. Nothing
//! is rolled back or replayed after a possible submission.

pub use super::refresh_packet::PrepareError;
use super::{
    ActiveInternalDisplay, DisplayReadError, InternalDisplayRefresh, PATH_BOOST_REFRESH_RATE,
    RefreshRate, SubmissionFence, Topology,
    fence::{FenceRefusal, PanelIdentity, admit_submission},
    refresh::{RefreshApi, edid, observe_refresh},
    refresh_packet::RefreshPacket,
};
use windows_sys::Win32::Devices::Display::DISPLAYCONFIG_TARGET_MODE;

/// One fresh refresh observation for mutation, retaining the CCD frame and
/// timing evidence of the same bracketed read: managed `BuildSnapshot` with
/// the requested key over the original `QueryTopology`, so an exact requested
/// Dynamic maximum is offered besides the default one (`AddDynamicRefreshOptions`).
pub struct RefreshObservation {
    reading: InternalDisplayRefresh,
    evidence: Option<Evidence>,
}

struct Evidence {
    topology: Topology,
    preferred: Option<DISPLAYCONFIG_TARGET_MODE>,
    timings: Vec<edid::DetailedTiming>,
}

impl RefreshObservation {
    pub(super) fn observe_with(
        api: &mut impl RefreshApi,
        key: &str,
    ) -> Result<Self, DisplayReadError> {
        let mut evidence = None;
        let reading = observe_refresh(api, Some(key), |topology, preferred, timings| {
            evidence = Some(Evidence {
                topology,
                preferred,
                timings,
            });
        })?;
        Ok(Self { reading, evidence })
    }
}

/// A complete SetDisplayConfig request built from one observation's frame.
/// Only the chosen path's refresh, boost flag and target timing differ.
pub struct RefreshPlan(pub(super) RefreshPacket);

impl RefreshPlan {
    /// The requested desktop (path) rate.
    pub fn desktop_rate(&self) -> Option<RefreshRate> {
        RefreshRate::observed(self.0.selected_path().targetInfo.refreshRate)
    }

    pub fn boost(&self) -> bool {
        self.0.selected_path().flags & PATH_BOOST_REFRESH_RATE != 0
    }

    /// The explicit target signal rate, or `None` when the target mode index
    /// is omitted so that Windows selects the signal.
    pub fn target_rate(&self) -> Option<RefreshRate> {
        self.0
            .selected_mode()
            .and_then(|mode| RefreshRate::observed(mode.targetVideoSignalInfo.vSyncFreq))
    }
}

/// Current-user Windows boundary for one refresh request.
pub trait RefreshNative {
    /// A fresh bracketed observation for mutation to `key`.
    fn observe(&mut self, key: &str) -> Result<RefreshObservation, DisplayReadError>;

    /// `SetDisplayConfig(SDC_VALIDATE)`; never applies. Returns the Win32
    /// status (0 is ERROR_SUCCESS).
    fn validate(&mut self, plan: &RefreshPlan) -> u32;

    /// The one `SetDisplayConfig(SDC_APPLY | SDC_SAVE_TO_DATABASE)`.
    fn apply(&mut self, plan: &RefreshPlan) -> u32;

    /// An independent refresh read (managed `QueryCore`).
    fn read(&mut self) -> Result<InternalDisplayRefresh, DisplayReadError>;
}

#[derive(Debug, PartialEq, Eq)]
pub enum RefreshOutcome {
    /// The fresh read already classified the requested option; nothing was
    /// validated or submitted.
    Unchanged,
    /// The independent readback classifies the requested option.
    Confirmed,
}

#[derive(Debug, PartialEq, Eq)]
pub struct RefreshFailure {
    pub reason: RefreshError,
    pub submission_may_have_occurred: bool,
}

#[derive(Debug, PartialEq, Eq)]
pub enum RefreshError {
    /// The fresh observation failed.
    Topology(DisplayReadError),
    NoInternalDisplay,
    MultipleInternalDisplays,
    /// The key is not an option of the fresh observation.
    OptionUnavailable,
    /// The configuration could not be built from the observed frame.
    Prepare(PrepareError),
    /// `SDC_VALIDATE` returned this Win32 status. Nothing was applied.
    ValidationRejected(u32),
    Cancelled,
    /// The fenced topology no longer matches the observed panel and frame.
    PanelChanged,
    /// The host's lifetime, session or power admission no longer holds.
    AdmissionDenied,
    /// `SDC_APPLY` returned this Win32 status. It may still have applied.
    Rejected(u32),
    /// APPLY succeeded but the independent readback did not confirm it.
    Unconfirmed(Readback),
}

impl From<FenceRefusal> for RefreshError {
    fn from(refusal: FenceRefusal) -> Self {
        match refusal {
            FenceRefusal::Cancelled => Self::Cancelled,
            FenceRefusal::PanelChanged => Self::PanelChanged,
            FenceRefusal::AdmissionDenied => Self::AdmissionDenied,
        }
    }
}

/// Why a readback after a successful APPLY did not confirm the request.
#[derive(Debug, PartialEq, Eq)]
pub enum Readback {
    Failed(DisplayReadError),
    NoInternalDisplay,
    MultipleInternalDisplays,
    /// A different panel (endpoints or monitor path) was read back.
    PanelChanged,
    /// The readback classified a different option, or none.
    Mismatch,
}

/// Change the internal display's refresh option to `key`. Every refusal
/// before `apply` reports `submission_may_have_occurred: false`.
pub fn set_internal_refresh<N: RefreshNative + ?Sized>(
    native: &mut N,
    key: &str,
    fence: &dyn SubmissionFence,
    cancelled: &dyn Fn() -> bool,
) -> Result<RefreshOutcome, RefreshFailure> {
    let refused = |reason| RefreshFailure {
        reason,
        submission_may_have_occurred: false,
    };
    let observation = native
        .observe(key)
        .map_err(|error| refused(RefreshError::Topology(error)))?;
    let snapshot = match observation.reading {
        InternalDisplayRefresh::Single(snapshot) => snapshot,
        InternalDisplayRefresh::MultipleInternalDisplays => {
            return Err(refused(RefreshError::MultipleInternalDisplays));
        }
        InternalDisplayRefresh::Empty | InternalDisplayRefresh::NoInternalDisplay => {
            return Err(refused(RefreshError::NoInternalDisplay));
        }
    };
    let Some(option) = snapshot
        .refresh_options
        .iter()
        .find(|option| option.key == key)
    else {
        return Err(refused(RefreshError::OptionUnavailable));
    };
    // The classifier matched the desktop rate, the physical signal rate and
    // the boost flag of this read, so a match needs no submission.
    if snapshot.current_refresh_key.as_deref() == Some(key) {
        return Ok(RefreshOutcome::Unchanged);
    }
    // observe_refresh supplies the evidence for every single-panel reading.
    let Some(evidence) = observation.evidence else {
        return Err(refused(RefreshError::Prepare(PrepareError::PathChanged)));
    };
    let plan = RefreshPacket::prepare(
        evidence.topology,
        &snapshot.display,
        option,
        evidence.preferred,
        &evidence.timings,
    )
    .map(RefreshPlan)
    .map_err(|error| refused(RefreshError::Prepare(error)))?;
    match native.validate(&plan) {
        0 => {}
        status => return Err(refused(RefreshError::ValidationRejected(status))),
    }
    // The fence reads immediately before the submission. The plan carries
    // this frame's arrays, so the fenced panel must equal the observed one
    // (identity, rates and flags), not just be the same panel.
    admit_submission(fence, cancelled, |fresh| {
        if *fresh == snapshot.display {
            Ok(())
        } else {
            Err(RefreshError::PanelChanged)
        }
    })
    .map_err(refused)?;

    // From here Windows may have applied the configuration. Caller
    // cancellation no longer applies; the readback always runs.
    let status = native.apply(&plan);
    let readback = confirm(native.read(), &snapshot.display, key);
    let uncertain = |reason| RefreshFailure {
        reason,
        submission_may_have_occurred: true,
    };
    match (status, readback) {
        // ExecuteRefreshMutation: a submission error is never a success.
        (0, Ok(())) => Ok(RefreshOutcome::Confirmed),
        (0, Err(readback)) => Err(uncertain(RefreshError::Unconfirmed(readback))),
        (status, _) => Err(uncertain(RefreshError::Rejected(status))),
    }
}

fn confirm(
    read: Result<InternalDisplayRefresh, DisplayReadError>,
    admitted: &ActiveInternalDisplay,
    key: &str,
) -> Result<(), Readback> {
    let after = match read.map_err(Readback::Failed)? {
        InternalDisplayRefresh::Single(after) => after,
        InternalDisplayRefresh::MultipleInternalDisplays => {
            return Err(Readback::MultipleInternalDisplays);
        }
        InternalDisplayRefresh::Empty | InternalDisplayRefresh::NoInternalDisplay => {
            return Err(Readback::NoInternalDisplay);
        }
    };
    if !PanelIdentity::of(admitted).same_as(&after.display) {
        return Err(Readback::PanelChanged);
    }
    if after.current_refresh_key.as_deref() != Some(key) {
        return Err(Readback::Mismatch);
    }
    Ok(())
}

/// Production native boundary: the CCD/GDI/EDID reader and the one real
/// `SetDisplayConfig` call site. Unit-test builds of this crate cannot
/// construct it; dependents inject `RefreshNative` doubles.
#[cfg(not(test))]
pub struct WindowsRefreshNative {
    _private: (),
}

#[cfg(not(test))]
impl WindowsRefreshNative {
    /// Call from the current-user desktop that owns the display settings.
    pub fn current_user() -> Self {
        Self { _private: () }
    }
}

#[cfg(not(test))]
impl RefreshNative for WindowsRefreshNative {
    fn observe(&mut self, key: &str) -> Result<RefreshObservation, DisplayReadError> {
        RefreshObservation::observe_with(&mut super::native::WindowsDisplay, key)
    }

    fn validate(&mut self, plan: &RefreshPlan) -> u32 {
        plan.0.validate()
    }

    fn apply(&mut self, plan: &RefreshPlan) -> u32 {
        plan.0.apply()
    }

    fn read(&mut self) -> Result<InternalDisplayRefresh, DisplayReadError> {
        super::read_internal_display_refresh()
    }
}

#[cfg(test)]
mod tests;
