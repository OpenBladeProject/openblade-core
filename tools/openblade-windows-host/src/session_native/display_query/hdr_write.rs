//! Selected manual `setHdr` through the shared Rust HDR writer.
//!
//! Order mirrors the managed dispatcher, applier and controller: the shared
//! CCD manual-operation frame (`ccd_write::run`) checkpoints the HDR field and
//! admits power, then one fenced SET_HDR_STATE submission whose fence
//! re-checks topology, Origin, session and power generation, then an
//! independent readback. Every admitted operation records its certainty
//! before returning, even when the client, deadline or Stop has already
//! abandoned the reply.
use super::{
    ccd_write::{
        self, Attempt, CcdHost, CcdRequest, CcdWriteOutcome, Field, MULTIPLE, NO_INTERNAL,
        unreadable, win32,
    },
    observation::{SharedBrightness, Snapshot},
    recovery::{CheckpointFile, Fields, Recovery, Status},
};
use crate::session_native::power_admission::PowerContext;
use openblade_platform_windows::display::hdr::{
    HdrError, HdrFailure, HdrNative, HdrOutcome, HdrTarget, set_internal_hdr,
};
use std::{
    borrow::Cow,
    io,
    sync::{
        Mutex,
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

/// Managed `PrepareHdrCore` text for a rejected SET_HDR_STATE submission.
pub(super) fn rejected(status: u32) -> Cow<'static, str> {
    win32("Windows could not change HDR", status)
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) struct SetHdr {
    pub(super) expected_power_source: u8,
    pub(super) enabled: bool,
}

pub(super) fn set_hdr<F: CheckpointFile, H: CcdHost<Native: HdrNative>>(
    host: &mut H,
    brightness: &SharedBrightness,
    recovery: &Mutex<Recovery<F>>,
    request: SetHdr,
    canceled: &AtomicBool,
) -> io::Result<CcdWriteOutcome> {
    ccd_write::run(host, brightness, recovery, &request, canceled)
}

impl<H: CcdHost<Native: HdrNative>> CcdRequest<H> for SetHdr {
    const FIELD: Field = Field {
        fields: Fields::HDR,
        paused: PAUSED,
        unconfirmed: UNCONFIRMED,
    };

    fn expected_power_source(&self) -> u8 {
        self.expected_power_source
    }

    fn attempt(&self, host: &mut H, admitted: PowerContext, canceled: &AtomicBool) -> Attempt {
        let done = |status, error: &'static str| Attempt::Done(status, Cow::Borrowed(error));
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
                let fence = host.fence(admitted);
                set_internal_hdr(
                    host.native(),
                    &target,
                    self.enabled,
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
                // Only produced after a possible submission; never claim certainty.
                HdrError::Rejected(_) | HdrError::Unconfirmed => {
                    done(Status::Indeterminate, UNCONFIRMED)
                }
            },
        }
    }

    /// Managed `ResolveHdrMutationResult`: the reply shows the requested state.
    fn shown_by(&self, state: &Snapshot) -> bool {
        state.hdr() == Some(self.enabled)
    }
}

#[cfg(test)]
#[path = "hdr_write_tests.rs"]
mod tests;
