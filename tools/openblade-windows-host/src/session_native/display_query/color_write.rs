//! Selected manual `setColorProfile` through the shared Rust WCS writer.
//!
//! Order mirrors managed `DisplayProfileApplier.SetColorProfileAsync` and
//! `WindowsDisplaySettingsController.PrepareColorProfile`: the shared
//! manual-operation frame (`manual_write::run`) checkpoints the color field
//! and admits power; then one observation selects the listed profile, the
//! current-user baseline and the monitor device key are read, the fence
//! (topology, Origin, session and power generation) runs immediately before the
//! first WCS setter, and managed `ColorProfileMutationTransaction` associates,
//! scopes, selects, confirms and, on failure, restores. Every admitted
//! operation records its certainty before returning, even when the client,
//! deadline or Stop has already abandoned the reply.
use super::{
    manual_write::{Attempt, Field, MULTIPLE, ManualHost, ManualRequest, NO_INTERNAL, unreadable},
    observation::{HDR_UNKNOWN, Snapshot},
    recovery::{Fields, Status},
};
use crate::session_native::power_admission::PowerContext;
use openblade_platform_windows::color_profile::{
    MonitorKeyError, ProfileRead, ProfileReadError, profiles_match,
    write::{
        ColorFailure, ColorOutcome, ColorProfileNative, ColorRefusal, Fault, Readback, Unverified,
        set_internal_color_profile,
    },
};
use std::{
    borrow::Cow,
    ffi::OsStr,
    sync::atomic::{AtomicBool, Ordering},
};

/// Native-only write gate (no managed equivalent).
pub(super) const PAUSED: &str =
    "Color-profile changes are paused until a display read confirms the last change.";
/// Native fence refusals, parallel to the HDR texts.
pub(super) const STALE: &str =
    "The display operation became stale before the color-profile change was submitted.";
pub(super) const PANEL_CHANGED: &str =
    "The internal display changed before the color-profile change was submitted.";
pub(super) const CANCELED: &str =
    "The display operation was canceled before the color-profile change was submitted.";
/// Managed `PrepareColorProfile` refusals.
pub(super) const HDR_ON: &str = "Color profiles are unavailable while HDR is on.";
pub(super) const UNAVAILABLE: &str =
    "That color profile is not available for the internal display.";
/// Managed `ColorProfileMutationTransaction.Execute` readback mismatch.
pub(super) const UNCONFIRMED: &str = "Windows did not confirm the requested color profile.";
/// Native-only: the readback did not re-identify the panel under a known
/// advanced-color state, so managed `Restore` was not attempted and the
/// field stays unresolved like [`PAUSED`].
pub(super) const UNVERIFIED: &str = "Windows applied the color profile, but OpenBlade could not confirm it on the internal display. Color-profile changes are paused until a display read confirms it.";
/// Native-only: the readback found another panel. The fence texts say
/// "before ... submitted", which is false once a setter ran.
pub(super) const PANEL_CHANGED_AFTER: &str =
    "The internal display changed during the color-profile change.";

#[derive(Clone, Debug, PartialEq, Eq)]
pub(super) struct SetColorProfile {
    pub(super) expected_power_source: u8,
    pub(super) key: String,
}

impl<H: ManualHost<Native: ColorProfileNative>> ManualRequest<H> for SetColorProfile {
    const FIELD: Field = Field {
        fields: Fields::COLOR,
        paused: PAUSED,
        unconfirmed: UNCONFIRMED,
    };

    fn expected_power_source(&self) -> u8 {
        self.expected_power_source
    }

    fn attempt(&self, host: &mut H, admitted: PowerContext, canceled: &AtomicBool) -> Attempt {
        let fence = host.fence(admitted);
        let result = set_internal_color_profile(host.native(), &self.key, fence.as_ref(), &|| {
            canceled.load(Ordering::Acquire)
        });
        match result {
            Ok(ColorOutcome::Unchanged) => Attempt::Unchanged,
            Ok(ColorOutcome::Confirmed) => Attempt::Confirmed,
            Err(ColorFailure::Refused(reason)) => refused(reason),
            Err(ColorFailure::Uncertain { fault, restoration }) => {
                Attempt::Done(Status::Indeterminate, uncertain(fault, restoration))
            }
            Err(ColorFailure::Unverified(reason)) => Attempt::Done(
                Status::Indeterminate,
                Cow::Owned(format!("{UNVERIFIED} {}", unverified_text(reason))),
            ),
        }
    }

    fn shown_by(&self, state: &Snapshot) -> bool {
        self.shown(state)
    }
}

impl SetColorProfile {
    /// Managed `verifyApplied`: the reply's active profile matches the request.
    pub(super) fn shown(&self, state: &Snapshot) -> bool {
        state
            .color_profile()
            .is_some_and(|active| profiles_match(OsStr::new(active), OsStr::new(&self.key)))
    }
}

/// Managed `PrepareColorProfile` after a possible submission: the
/// transaction's message, prefixed by the controller and followed by the
/// restoration result. Uncertain whatever restoration achieved; never replayed.
fn uncertain(fault: Fault, restoration: Result<(), Fault>) -> Cow<'static, str> {
    let fault = fault_text(fault);
    Cow::Owned(match restoration {
        Ok(()) => format!(
            "The color-profile change failed: {fault} The previous color-profile configuration was restored."
        ),
        Err(restoration) => format!(
            "The color-profile change failed: {fault} The previous color-profile configuration could not be restored: {}",
            fault_text(restoration)
        ),
    })
}

/// The managed exception message of one transaction failure. A rejected WCS
/// call is a `COMException(operation, hresult)`, whose message is the
/// operation alone; the scope setter's `Win32Exception` carries the error.
fn fault_text(fault: Fault) -> Cow<'static, str> {
    let text = |text: &'static str| Cow::Borrowed(text);
    match fault {
        Fault::Read(error) => text(read_text(error)),
        Fault::AssociateRejected(_) => text("Windows could not associate the color profile"),
        Fault::RemoveRejected(_) => text("Windows could not remove the color-profile association"),
        Fault::DefaultRejected(_) => text("Windows could not make the color profile active"),
        Fault::ScopeRejected(error) => Cow::Owned(format!(
            "Windows could not change the color-profile scope (Win32 error {error})"
        )),
        Fault::AssociationUnconfirmed => {
            text("Windows did not confirm the color-profile association.")
        }
        Fault::RemovalUnconfirmed => {
            text("Windows did not confirm removal of the color-profile association and default.")
        }
        Fault::DefaultUnconfirmed => {
            text("Windows did not confirm the requested default color profile.")
        }
        Fault::ScopeUnconfirmed => {
            text("Windows did not confirm the requested color-profile scope.")
        }
        Fault::Readback(readback) => text(readback_text(readback)),
        Fault::AssociationsChanged => text(
            "The current-user color-profile associations changed unexpectedly; restoration stopped without altering them.",
        ),
        Fault::NullDefault => text(
            "The previous null default cannot be restored without changing a pre-existing association.",
        ),
        Fault::RestorationUnconfirmed => {
            text("Windows did not confirm restoration of the previous color-profile configuration.")
        }
    }
}

/// Managed `verifyApplied` on the re-identified panel: a failed profile read
/// ends `Execute` with its exception, anything else is the managed mismatch.
fn readback_text(readback: Readback) -> &'static str {
    match readback {
        Readback::Read(error) => read_text(error),
        Readback::Mismatch => UNCONFIRMED,
    }
}

/// Why the readback could not re-identify the panel. The reply text is the
/// only record of it, so each reason keeps its own clause.
fn unverified_text(reason: Unverified) -> Cow<'static, str> {
    match reason {
        Unverified::Topology(error) => unreadable(error),
        Unverified::NoInternalDisplay => Cow::Borrowed(NO_INTERNAL),
        Unverified::MultipleInternalDisplays => Cow::Borrowed(MULTIPLE),
        Unverified::PanelChanged => Cow::Borrowed(PANEL_CHANGED_AFTER),
        Unverified::HdrUnknown => Cow::Borrowed(HDR_UNKNOWN),
    }
}

/// The managed `ThrowIfHResultFailed` operation text of one WCS read. An
/// unknown scope or malformed reply has no managed counterpart and reports
/// the read it came from.
fn read_text(error: ProfileReadError) -> &'static str {
    let operation = match error {
        ProfileReadError::Native { operation, .. }
        | ProfileReadError::InvalidResponse(operation) => operation,
        ProfileReadError::UnknownScope(_) => ProfileRead::Scope,
    };
    match operation {
        ProfileRead::Scope => "Windows could not determine the active color-profile scope",
        ProfileRead::Associations => "Windows could not enumerate display color profiles",
        ProfileRead::Default => "Windows could not read the default display color profile",
    }
}

/// Managed `DisplayMonitorDeviceKeyResolver` and `GetMonitorDeviceKey`.
fn monitor_text(error: MonitorKeyError) -> &'static str {
    match error {
        MonitorKeyError::NoDevicePath => {
            "Windows did not report a monitor device path for the internal display."
        }
        MonitorKeyError::NoActiveMonitor => {
            "Windows did not report one active monitor device for the internal display."
        }
        MonitorKeyError::MultipleActiveMonitors => {
            "Windows reported more than one active monitor device for the internal display."
        }
        MonitorKeyError::NoDeviceKey => {
            "Windows did not report a device key for the active internal display."
        }
    }
}

/// A refusal before the first WCS setter: nothing was submitted.
///
/// Deliberate deviations: a failed observation, baseline read or monitor-key
/// lookup is Failed here. Managed `PrepareColorProfile` throws before its
/// transaction, so its dispatcher's generic catch reports Indeterminate,
/// although nothing could have been submitted. A failed advanced-color read
/// is refused here; managed reads it as SDR.
fn refused(reason: ColorRefusal) -> Attempt {
    let done = |status, error: &'static str| Attempt::Done(status, Cow::Borrowed(error));
    let failed = |error| Attempt::Failed {
        error,
        reply_resolves: false,
    };
    match reason {
        ColorRefusal::Topology(error) => failed(unreadable(error)),
        ColorRefusal::NoInternalDisplay => failed(Cow::Borrowed(NO_INTERNAL)),
        ColorRefusal::MultipleInternalDisplays => failed(Cow::Borrowed(MULTIPLE)),
        ColorRefusal::Read(error) => failed(Cow::Borrowed(read_text(error))),
        ColorRefusal::HdrEnabled => failed(Cow::Borrowed(HDR_ON)),
        ColorRefusal::HdrUnknown => failed(Cow::Borrowed(HDR_UNKNOWN)),
        ColorRefusal::ProfileUnavailable => failed(Cow::Borrowed(UNAVAILABLE)),
        ColorRefusal::Monitor(error) => failed(Cow::Borrowed(monitor_text(error))),
        ColorRefusal::PanelChanged => done(Status::Blocked, PANEL_CHANGED),
        ColorRefusal::AdmissionDenied => done(Status::Blocked, STALE),
        ColorRefusal::Cancelled => done(Status::Blocked, CANCELED),
    }
}

#[cfg(test)]
#[path = "color_write_tests.rs"]
mod tests;
