use openblade_platform_windows::display::{
    ActiveInternalDisplay, DisplayReadError, InternalDisplayRefresh, InternalDisplayTopology,
    scripted::submit_hdr,
};
use serde_json::{Value, json};

use super::{
    Attempt, BLOCKED, FAILED, INDETERMINATE, SUCCEEDED, display_state,
    journal::{Change, Panel},
    panel::{Context, Outcome, ScriptedDisplay, wait_for_disconnect},
};

#[derive(Clone, Copy, PartialEq, Eq)]
pub(crate) enum HdrFixture {
    Available,
    StaleTarget,
    StalePower,
    Inactive,
    SubmissionError,
    ReadbackMismatch,
    SiblingReadback,
    StalledAdmission,
    StalledSubmission,
}

pub(super) struct HdrObservation {
    panel: Panel,
    pub(super) supported: bool,
    pub(super) enabled: bool,
}

impl HdrObservation {
    fn from_display(display: &ActiveInternalDisplay) -> Option<Self> {
        let hdr = display.advanced_color.as_ref().ok()?;
        Some(Self {
            panel: Panel::observe(display)?,
            supported: hdr.hdr_supported,
            enabled: hdr.hdr_user_enabled,
        })
    }

    pub(super) fn from_refresh(
        reading: &Result<InternalDisplayRefresh, DisplayReadError>,
    ) -> Option<Self> {
        let Ok(InternalDisplayRefresh::Single(snapshot)) = reading else {
            return None;
        };
        Self::from_display(&snapshot.display)
    }
}

impl ScriptedDisplay {
    // SessionDisplay holds its single request lock across the entire operation.
    pub(super) fn apply_hdr(
        &mut self,
        expected_power_source: u8,
        enabled: bool,
        context: Option<Context>,
        before: Result<HdrObservation, String>,
        state: &mut Value,
        attempt: Attempt<'_>,
    ) -> Outcome {
        let cancelled = attempt.cancelled;
        let Some(context) = context else {
            return (
                BLOCKED,
                Some("The display session is not active; HDR was not submitted.".into()),
                false,
            );
        };
        if context.power_source != expected_power_source {
            return (
                BLOCKED,
                Some("The expected power source changed; HDR was not submitted.".into()),
                false,
            );
        }
        let before = match before {
            Ok(before) => before,
            Err(error) => return (FAILED, Some(error), false),
        };
        if !before.supported {
            return (
                FAILED,
                Some("HDR is not supported for the active internal display.".into()),
                false,
            );
        }

        if self.hdr_fixture == HdrFixture::StalledAdmission {
            wait_for_disconnect("HDR ADMISSION", cancelled);
        }
        let fresh = self
            .scenario
            .topology_with_hdr(
                self.enabled,
                self.timing,
                self.hdr_fixture == HdrFixture::StaleTarget,
            )
            .ok()
            .and_then(|topology| match topology {
                InternalDisplayTopology::Single(display) => HdrObservation::from_display(&display),
                _ => None,
            });
        if self.context(true) != Some(context) {
            return (
                BLOCKED,
                Some("The power or session context became stale; HDR was not submitted.".into()),
                false,
            );
        }
        if cancelled() {
            return (
                FAILED,
                Some("HDR was cancelled before submission.".into()),
                false,
            );
        }
        let Some(fresh) = fresh.filter(|fresh| fresh.panel == before.panel && fresh.supported)
        else {
            return (
                BLOCKED,
                Some(
                    "The internal display changed or HDR is unavailable; HDR was not submitted."
                        .into(),
                ),
                false,
            );
        };
        state["hdrEnabled"] = json!(fresh.enabled);
        if fresh.enabled == enabled {
            return (SUCCEEDED, None, false);
        }

        // A native error can occur after Windows accepts the request. Treat
        // every such result as uncertain, even if a later query sees the value.
        if !(attempt.record)(
            fresh.panel.clone(),
            Change::Hdr {
                before: fresh.enabled,
                requested: enabled,
            },
        ) {
            return (BLOCKED, Some(super::JOURNAL_BLOCKED.into()), false);
        }
        if cancelled() {
            return (
                FAILED,
                Some("HDR was cancelled before submission.".into()),
                false,
            );
        }
        let applied = submit_hdr(fresh.panel.target(), enabled);
        if self.hdr_fixture != HdrFixture::ReadbackMismatch {
            self.enabled = applied;
        }
        if self.hdr_fixture == HdrFixture::SubmissionError {
            return pending("the native result was uncertain");
        }
        if self.hdr_fixture == HdrFixture::StalledSubmission {
            wait_for_disconnect("HDR SUBMITTED", cancelled);
        }

        // Observe the exact same target independently. Do not treat a sibling's
        // matching preference or the physical signal mode as confirmation.
        let readback = self.read(self.hdr_fixture == HdrFixture::SiblingReadback);
        let Some(after) = HdrObservation::from_refresh(&readback)
            .filter(|after| after.panel == fresh.panel && after.supported)
        else {
            return pending("the exact target could not be read back");
        };
        *state = display_state(&readback).0;
        if cancelled() {
            return pending("the request was cancelled after submission");
        }
        if after.enabled != enabled {
            return pending("Windows did not confirm the requested HDR preference");
        }
        (SUCCEEDED, None, true)
    }
}

fn pending(reason: &str) -> Outcome {
    (
        INDETERMINATE,
        Some(format!(
            "HDR submission is indeterminate ({reason}); writes are paused."
        )),
        true,
    )
}
