//! Selected manual `setRefreshRate` through the shared Rust refresh writer.
//!
//! Order mirrors managed `DisplayProfileApplier.SetRefreshRateAsync` and
//! `WindowsDisplaySettingsController.SetRefreshAsync`: the shared
//! manual-operation frame (`manual_write::run`) checkpoints the refresh field
//! and admits power; then one observation classifies the request and prepares the
//! CCD configuration, `SDC_VALIDATE` checks it, the fence (topology, Origin,
//! session and power generation) runs immediately before the one `SDC_APPLY`,
//! and an independent readback confirms it. Every admitted operation records
//! its certainty before returning, even when the client, deadline or Stop has
//! already abandoned the reply.
use super::{
    manual_write::{
        Attempt, Field, MULTIPLE, ManualHost, ManualRequest, NO_INTERNAL, unreadable, win32,
    },
    observation::Snapshot,
    recovery::{Fields, Status},
};
use crate::session_native::power_admission::PowerContext;
use openblade_platform_windows::display::refresh_write::{
    PrepareError, Readback, RefreshError, RefreshFailure, RefreshNative, RefreshOutcome,
    set_internal_refresh,
};
use std::{
    borrow::Cow,
    sync::atomic::{AtomicBool, Ordering},
};

/// Native-only write gate (no managed equivalent).
pub(super) const PAUSED: &str =
    "Refresh-rate changes are paused until a display read confirms the last change.";
/// Native fence refusals, parallel to the HDR texts.
pub(super) const STALE: &str =
    "The display operation became stale before the refresh-rate change was submitted.";
pub(super) const PANEL_CHANGED: &str =
    "The internal display changed before the refresh-rate change was submitted.";
pub(super) const CANCELED: &str =
    "The display operation was canceled before the refresh-rate change was submitted.";
/// Managed `WindowsDisplaySettingsController.PrepareRefresh` for a key the
/// fresh observation does not offer.
pub(super) const UNAVAILABLE: &str =
    "That refresh-rate option is not available for the internal display.";
/// Managed `ExecuteRefreshMutation` readback mismatch.
pub(super) const UNCONFIRMED: &str = "Windows did not confirm the requested refresh-rate mode.";

/// Managed `PrepareRefreshAfterSnapshot`: a read-only preparation or
/// `SDC_VALIDATE` failure, with the exception text of `PrepareRefreshTopology`,
/// `PrepareRefreshPath` or `ValidateTopology`.
pub(super) fn not_prepared(error: &str) -> Cow<'static, str> {
    Cow::Owned(format!(
        "The refresh-rate configuration could not be prepared: {error}"
    ))
}

/// The exception texts of managed `PrepareRefreshTopology` and
/// `PrepareRefreshPath`.
fn prepare_error(error: PrepareError) -> &'static str {
    match error {
        PrepareError::PathChanged => {
            "The internal display path changed before the refresh-rate setting could be applied."
        }
        PrepareError::DynamicTimingUnavailable => {
            "Windows no longer reports the requested Dynamic refresh-rate timing."
        }
        // Managed `checked((uint)requested.DesktopHertz)`.
        PrepareError::InvalidOption => "Arithmetic operation resulted in an overflow.",
    }
}

/// Managed `ValidateTopology` text inside `PrepareRefreshAfterSnapshot`.
pub(super) fn validation_rejected(status: u32) -> Cow<'static, str> {
    not_prepared(&win32(
        "Windows rejected the requested display configuration",
        status,
    ))
}

/// Managed `ExecuteRefreshMutation` text for a failed `ApplyTopology`.
pub(super) fn apply_failed(status: u32) -> Cow<'static, str> {
    Cow::Owned(format!(
        "The refresh-rate change failed: {}",
        win32(
            "Windows could not apply the requested display configuration",
            status
        )
    ))
}

/// Managed `ExecuteRefreshMutation` text for a failed `QueryCore` readback.
pub(super) fn not_confirmed(error: &str) -> Cow<'static, str> {
    Cow::Owned(format!(
        "The refresh-rate change could not be confirmed: {error}"
    ))
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub(super) struct SetRefreshRate {
    pub(super) expected_power_source: u8,
    pub(super) key: String,
}

impl<H: ManualHost<Native: RefreshNative>> ManualRequest<H> for SetRefreshRate {
    const FIELD: Field = Field {
        fields: Fields::REFRESH,
        paused: PAUSED,
        unconfirmed: UNCONFIRMED,
    };

    fn expected_power_source(&self) -> u8 {
        self.expected_power_source
    }

    fn attempt(&self, host: &mut H, admitted: PowerContext, canceled: &AtomicBool) -> Attempt {
        let fence = host.fence(admitted);
        let result = set_internal_refresh(host.native(), &self.key, fence.as_ref(), &|| {
            canceled.load(Ordering::Acquire)
        });
        match result {
            Ok(RefreshOutcome::Unchanged) => Attempt::Unchanged,
            Ok(RefreshOutcome::Confirmed) => Attempt::Confirmed,
            Err(RefreshFailure {
                reason,
                submission_may_have_occurred,
            }) if submission_may_have_occurred => {
                Attempt::Done(Status::Indeterminate, uncertain(reason))
            }
            Err(RefreshFailure { reason, .. }) => refused(reason),
        }
    }

    fn shown_by(&self, state: &Snapshot) -> bool {
        self.shown(state)
    }
}

impl SetRefreshRate {
    /// Managed `ExecuteRefreshMutation`: the reply classifies the requested key.
    pub(super) fn shown(&self, state: &Snapshot) -> bool {
        state.refresh_key() == Some(self.key.as_str())
    }
}

/// Managed `ExecuteRefreshMutation` after a possible submission: uncertain
/// whatever the readback shows, and never replayed.
fn uncertain(reason: RefreshError) -> Cow<'static, str> {
    match reason {
        RefreshError::Rejected(status) => apply_failed(status),
        RefreshError::Unconfirmed(Readback::Failed(error)) => not_confirmed(&unreadable(error)),
        RefreshError::Unconfirmed(Readback::NoInternalDisplay) => not_confirmed(NO_INTERNAL),
        RefreshError::Unconfirmed(Readback::MultipleInternalDisplays) => not_confirmed(MULTIPLE),
        _ => Cow::Borrowed(UNCONFIRMED),
    }
}

/// A refusal before `SDC_APPLY`: nothing was submitted.
///
/// Deliberate deviation: a failed observation (`Topology`, no or several
/// internal displays) is Failed here. Managed `PrepareRefresh` throws before
/// `PrepareRefreshAfterSnapshot`, so its dispatcher's generic catch reports
/// Indeterminate, although nothing could have been submitted.
fn refused(reason: RefreshError) -> Attempt {
    let done = |status, error: &'static str| Attempt::Done(status, Cow::Borrowed(error));
    let failed = |error| Attempt::Failed {
        error,
        reply_resolves: false,
    };
    match reason {
        RefreshError::Topology(error) => failed(unreadable(error)),
        RefreshError::NoInternalDisplay => failed(Cow::Borrowed(NO_INTERNAL)),
        RefreshError::MultipleInternalDisplays => failed(Cow::Borrowed(MULTIPLE)),
        RefreshError::OptionUnavailable => failed(Cow::Borrowed(UNAVAILABLE)),
        RefreshError::Prepare(error) => failed(not_prepared(prepare_error(error))),
        RefreshError::ValidationRejected(status) => failed(validation_rejected(status)),
        RefreshError::PanelChanged => done(Status::Blocked, PANEL_CHANGED),
        RefreshError::AdmissionDenied => done(Status::Blocked, STALE),
        RefreshError::Cancelled => done(Status::Blocked, CANCELED),
        // Only produced after a possible submission; never claim certainty.
        RefreshError::Rejected(_) | RefreshError::Unconfirmed(_) => {
            done(Status::Indeterminate, UNCONFIRMED)
        }
    }
}

#[cfg(test)]
#[path = "refresh_write_tests.rs"]
mod tests;
