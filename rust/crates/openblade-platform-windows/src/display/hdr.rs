//! Manual HDR state for the single active internal display.
//!
//! Mirrors managed `WindowsDisplaySettingsController.PrepareHdrCore` and
//! `ResolveHdrMutationResult`: a fresh read selects the target and its
//! advanced-color state, an unsupported or already matching state submits
//! nothing, and one `DISPLAYCONFIG_DEVICE_INFO_SET_HDR_STATE` packet is
//! classified by an independent readback. The native layer adds the host
//! fence immediately before submission. Nothing is rolled back or replayed
//! after a possible submission.

use super::{
    ActiveInternalDisplay, AdvancedColorState, DisplayReadError, EndpointId,
    InternalDisplayTopology, SubmissionFence,
};
use std::ffi::OsString;

/// CCD identity of the internal display admitted for one HDR request.
#[derive(Debug)]
pub struct HdrTarget {
    source: EndpointId,
    target: EndpointId,
    path: Option<OsString>,
}

impl HdrTarget {
    /// Managed `GetSingleInternalTarget`: exactly one active internal path.
    pub fn from_topology(topology: InternalDisplayTopology) -> Result<Self, HdrError> {
        match topology {
            InternalDisplayTopology::Single(display) => Ok(Self::from_display(&display)),
            InternalDisplayTopology::MultipleInternalDisplays => {
                Err(HdrError::MultipleInternalDisplays)
            }
            InternalDisplayTopology::Empty | InternalDisplayTopology::NoInternalDisplay => {
                Err(HdrError::NoInternalDisplay)
            }
        }
    }

    pub fn from_display(display: &ActiveInternalDisplay) -> Self {
        Self {
            source: display.source,
            target: display.target,
            path: device_path(display).cloned(),
        }
    }

    /// Same CCD endpoints and the same monitor device path (ASCII case-
    /// insensitive, or both unreadable). Endpoint IDs alone can be reused.
    fn same_as(&self, display: &ActiveInternalDisplay) -> bool {
        self.source == display.source
            && self.target == display.target
            && match (self.path.as_ref(), device_path(display)) {
                (None, None) => true,
                (Some(left), Some(right)) => {
                    left == right
                        || left
                            .to_str()
                            .zip(right.to_str())
                            .is_some_and(|(left, right)| left.eq_ignore_ascii_case(right))
                }
                _ => false,
            }
    }
}

fn device_path(display: &ActiveInternalDisplay) -> Option<&OsString> {
    display
        .monitor_name
        .as_ref()
        .ok()
        .and_then(|name| name.device_path.as_ref())
}

/// Current-user Windows boundary for one HDR request.
pub trait HdrNative {
    /// A fresh CCD read, including the target's advanced-color state.
    fn topology(&mut self) -> Result<InternalDisplayTopology, DisplayReadError>;

    /// One synchronous SET_HDR_STATE submission for exactly this target.
    /// Returns the Win32 status (0 is ERROR_SUCCESS).
    fn submit(&mut self, target: EndpointId, enabled: bool) -> u32;
}

#[derive(Debug, PartialEq, Eq)]
pub enum HdrOutcome {
    /// The fresh read already showed the requested state; nothing was submitted.
    Unchanged,
    /// The independent readback shows the requested state.
    Confirmed,
}

#[derive(Debug, PartialEq, Eq)]
pub struct HdrFailure {
    pub reason: HdrError,
    pub submission_may_have_occurred: bool,
}

#[derive(Debug, PartialEq, Eq)]
pub enum HdrError {
    /// The fresh topology read failed.
    Topology(DisplayReadError),
    NoInternalDisplay,
    MultipleInternalDisplays,
    /// The fresh or fenced topology no longer identifies the admitted target.
    PanelChanged,
    /// Managed `ProjectAdvancedColor`: a failed advanced-color read is unsupported.
    NotSupported,
    Cancelled,
    /// The host's lifetime, session or power admission no longer holds.
    AdmissionDenied,
    /// The submission returned this Win32 status and the readback did not
    /// show the requested state.
    Rejected(u32),
    /// The submission succeeded but the readback did not show the requested
    /// state, or could not be taken.
    Unconfirmed,
}

/// Set the internal display's HDR user preference. Every refusal before the
/// submission reports `submission_may_have_occurred: false`.
pub fn set_internal_hdr<N: HdrNative + ?Sized>(
    native: &mut N,
    admitted: &HdrTarget,
    enabled: bool,
    fence: &dyn SubmissionFence,
    cancelled: &dyn Fn() -> bool,
) -> Result<HdrOutcome, HdrFailure> {
    let refused = |reason| HdrFailure {
        reason,
        submission_may_have_occurred: false,
    };
    let display = match native.topology() {
        Ok(InternalDisplayTopology::Single(display)) => display,
        Ok(InternalDisplayTopology::MultipleInternalDisplays) => {
            return Err(refused(HdrError::MultipleInternalDisplays));
        }
        Ok(InternalDisplayTopology::Empty | InternalDisplayTopology::NoInternalDisplay) => {
            return Err(refused(HdrError::NoInternalDisplay));
        }
        Err(error) => return Err(refused(HdrError::Topology(error))),
    };
    if !admitted.same_as(&display) {
        return Err(refused(HdrError::PanelChanged));
    }
    let current = match &display.advanced_color {
        Ok(state) if state.hdr_supported => state.hdr_user_enabled,
        _ => return Err(refused(HdrError::NotSupported)),
    };
    if current == enabled {
        return Ok(HdrOutcome::Unchanged);
    }
    if cancelled() {
        return Err(refused(HdrError::Cancelled));
    }
    let fenced = fence.topology();
    if !matches!(&fenced, Ok(InternalDisplayTopology::Single(fresh)) if admitted.same_as(fresh)) {
        return Err(refused(HdrError::PanelChanged));
    }
    if !fence.admit() {
        return Err(refused(HdrError::AdmissionDenied));
    }
    if cancelled() {
        return Err(refused(HdrError::Cancelled));
    }

    // From here Windows may have applied the change. Caller cancellation no
    // longer applies; the readback always runs.
    let status = native.submit(admitted.target, enabled);
    let confirmed = matches!(
        native.topology(),
        Ok(InternalDisplayTopology::Single(after))
            if admitted.same_as(&after)
                && matches!(
                    after.advanced_color,
                    Ok(AdvancedColorState { hdr_supported: true, hdr_user_enabled, .. })
                        if hdr_user_enabled == enabled
                )
    );
    let uncertain = |reason| HdrFailure {
        reason,
        submission_may_have_occurred: true,
    };
    match (status, confirmed) {
        // ResolveHdrMutationResult: a matching readback succeeds even after
        // the submission reported an error.
        (_, true) => Ok(HdrOutcome::Confirmed),
        (0, false) => Err(uncertain(HdrError::Unconfirmed)),
        (status, false) => Err(uncertain(HdrError::Rejected(status))),
    }
}

/// Production native boundary: the CCD reader and the one real
/// `DisplayConfigSetDeviceInfo(SET_HDR_STATE)` call site. Unit-test builds of
/// this crate cannot construct it; dependents inject `HdrNative` doubles.
#[cfg(not(test))]
pub struct WindowsHdrNative {
    _private: (),
}

#[cfg(not(test))]
impl WindowsHdrNative {
    /// Call from the current-user desktop that owns the display settings.
    pub fn current_user() -> Self {
        Self { _private: () }
    }
}

#[cfg(not(test))]
impl HdrNative for WindowsHdrNative {
    fn topology(&mut self) -> Result<InternalDisplayTopology, DisplayReadError> {
        super::read_internal_display()
    }

    fn submit(&mut self, target: EndpointId, enabled: bool) -> u32 {
        super::native::submit_hdr_state(target, enabled)
    }
}

#[cfg(test)]
mod tests;
