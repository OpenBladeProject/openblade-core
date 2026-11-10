use std::{
    ffi::OsString,
    thread,
    time::{Duration, Instant},
};

use openblade_platform_windows::display::{
    ActiveInternalDisplay, DisplayReadError, EndpointId, InternalDisplayRefresh,
    InternalDisplayTopology,
    scripted::{Scenario, submit_hdr},
};
use serde_json::{Value, json};

use super::{BLOCKED, FAILED, INDETERMINATE, SUCCEEDED, display_state};

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

#[derive(PartialEq, Eq)]
struct HdrTarget {
    source: EndpointId,
    target: EndpointId,
    monitor_path: OsString,
}

pub(super) struct HdrObservation {
    target: HdrTarget,
    supported: bool,
    enabled: bool,
}

impl HdrObservation {
    fn from_display(display: &ActiveInternalDisplay) -> Option<Self> {
        let hdr = display.advanced_color.as_ref().ok()?;
        Some(Self {
            target: HdrTarget {
                source: display.source,
                target: display.target,
                monitor_path: display.monitor_name.as_ref().ok()?.device_path.clone()?,
            },
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

#[derive(Clone, Copy, PartialEq, Eq)]
pub(super) struct Context {
    power_source: u8,
    generation: u64,
}

pub(super) struct ScriptedHdr {
    scenario: Scenario,
    fixture: HdrFixture,
    enabled: bool,
}

type Outcome = (i32, Option<String>, bool);

impl ScriptedHdr {
    pub(super) fn new(scenario: Scenario, fixture: HdrFixture) -> Self {
        Self {
            scenario,
            fixture,
            enabled: true,
        }
    }

    pub(super) fn read(&self, replaced: bool) -> Result<InternalDisplayRefresh, DisplayReadError> {
        self.scenario.read_with_hdr(self.enabled, replaced)
    }

    // This is synthetic session and power evidence only. Pipe token identity
    // does not prove a real foreground console or a confirmed power source.
    pub(super) fn context(&self, fresh: bool) -> Option<Context> {
        (self.fixture != HdrFixture::Inactive).then_some(Context {
            power_source: 0, // PluggedIn.
            generation: u64::from(fresh && self.fixture == HdrFixture::StalePower),
        })
    }

    // SessionDisplay holds its single request lock across the entire operation.
    pub(super) fn apply(
        &mut self,
        expected_power_source: u8,
        enabled: bool,
        context: Option<Context>,
        before: Result<HdrObservation, String>,
        state: &mut Value,
        cancelled: &impl Fn() -> bool,
    ) -> Outcome {
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

        if self.fixture == HdrFixture::StalledAdmission {
            wait_for_disconnect("HDR ADMISSION", cancelled);
        }
        let fresh = self
            .scenario
            .topology_with_hdr(self.enabled, self.fixture == HdrFixture::StaleTarget)
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
        let Some(fresh) = fresh.filter(|fresh| fresh.target == before.target && fresh.supported)
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
        let applied = submit_hdr(fresh.target.target, enabled);
        if self.fixture != HdrFixture::ReadbackMismatch {
            self.enabled = applied;
        }
        if self.fixture == HdrFixture::SubmissionError {
            return pending("the native result was uncertain");
        }
        if self.fixture == HdrFixture::StalledSubmission {
            wait_for_disconnect("HDR SUBMITTED", cancelled);
        }

        // Observe the exact same target independently. Do not treat a sibling's
        // matching preference or the physical signal mode as confirmation.
        let readback = self.read(self.fixture == HdrFixture::SiblingReadback);
        let Some(after) = HdrObservation::from_refresh(&readback)
            .filter(|after| after.target == fresh.target && after.supported)
        else {
            return pending("the exact target could not be read back");
        };
        *state = display_state(readback).0;
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

fn wait_for_disconnect(signal: &str, cancelled: &impl Fn() -> bool) {
    println!("{signal}");
    let deadline = Instant::now() + Duration::from_secs(2);
    while !cancelled() && Instant::now() < deadline {
        thread::sleep(Duration::from_millis(5));
    }
}
