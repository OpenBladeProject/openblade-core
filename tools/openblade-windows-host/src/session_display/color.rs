use openblade_platform_windows::{
    color_profile::{
        ColorProfileSnapshot, HdrState,
        scripted::{Disposition, Failure, Outcome as ProfileOutcome, Profiles, WcsFixture},
    },
    display::{DisplayReadError, InternalDisplayRefresh, InternalDisplayTopology},
};
use serde_json::{Value, json};

use super::{
    BLOCKED, FAILED, INDETERMINATE, SUCCEEDED,
    journal::Panel,
    panel::{Outcome, ScriptedDisplay, wait_for_disconnect},
};

#[derive(Clone, Copy, PartialEq, Eq)]
pub(crate) enum ColorFixture {
    Disabled,
    Available,
    Unavailable,
    StaleTarget,
    StalePower,
    Inactive,
    StaleHdr,
    Restored,
    SubmissionError,
    ReadbackMismatch,
    StalledReply,
}

impl ColorFixture {
    pub(crate) fn parse(name: &str) -> Option<Self> {
        Some(match name {
            "icc-available" => Self::Available,
            "icc-unavailable" => Self::Unavailable,
            "icc-stale-target" => Self::StaleTarget,
            "icc-stale-power" => Self::StalePower,
            "icc-inactive" => Self::Inactive,
            "icc-stale-hdr" => Self::StaleHdr,
            "icc-restored" => Self::Restored,
            "icc-submission-error" => Self::SubmissionError,
            "icc-readback-mismatch" => Self::ReadbackMismatch,
            "icc-stalled-reply" => Self::StalledReply,
            _ => return None,
        })
    }
}

pub(super) struct ColorDisplay {
    fixture: ColorFixture,
    profiles: Profiles,
}

impl ColorDisplay {
    pub(super) fn new(fixture: ColorFixture) -> Self {
        let wcs = match fixture {
            ColorFixture::Unavailable => WcsFixture::Unavailable,
            ColorFixture::Restored => WcsFixture::FailedWithoutEffect,
            ColorFixture::SubmissionError => WcsFixture::FailedWithEffect,
            ColorFixture::ReadbackMismatch => WcsFixture::MismatchedReadback,
            _ => WcsFixture::Available,
        };
        Self {
            fixture,
            profiles: Profiles::new(wcs),
        }
    }

    pub(super) fn enabled(&self) -> bool {
        self.fixture != ColorFixture::Disabled
    }

    pub(super) fn project(
        &mut self,
        reading: &Result<InternalDisplayRefresh, DisplayReadError>,
        state: &mut Value,
    ) {
        if self.enabled()
            && state["isAvailable"] == true
            && let Ok(InternalDisplayRefresh::Single(snapshot)) = reading
            && let Ok(profiles) = self.profiles.query(&snapshot.display)
        {
            set_profiles(state, profiles);
        }
    }

    pub(super) fn apply(
        &mut self,
        (expected_power, key): (u8, &str),
        panel: &ScriptedDisplay,
        reading: &Result<InternalDisplayRefresh, DisplayReadError>,
        state: &mut Value,
        cancelled: &impl Fn() -> bool,
    ) -> Outcome {
        let Some(context) = panel
            .context(false)
            .filter(|context| context.power_source == expected_power)
        else {
            return (
                BLOCKED,
                Some("The expected power or active session is unavailable.".into()),
                false,
            );
        };
        let Ok(InternalDisplayRefresh::Single(before)) = reading else {
            return (
                BLOCKED,
                Some("The internal display is unavailable.".into()),
                false,
            );
        };
        let Some(identity) = Panel::observe(&before.display) else {
            return (
                BLOCKED,
                Some("The exact internal display is unavailable.".into()),
                false,
            );
        };
        if self.fixture == ColorFixture::Disabled {
            return (
                FAILED,
                Some("The ICC profile fixture is unavailable.".into()),
                false,
            );
        }

        let fixture = self.fixture;
        let mut topology_reads = 0;
        let fresh_topology = || {
            topology_reads += 1;
            let topology = panel.scenario.topology_with_hdr(
                panel.enabled || fixture == ColorFixture::StaleHdr,
                panel.timing,
                fixture == ColorFixture::StaleTarget && topology_reads > 1,
            )?;
            if matches!(&topology, InternalDisplayTopology::Single(display)
                if Panel::observe(display).as_ref() == Some(&identity))
            {
                Ok(topology)
            } else {
                Err(DisplayReadError::TopologyChanging)
            }
        };
        let admitted = || !cancelled() && panel.context(true) == Some(context);
        match self.profiles.set(key, fresh_topology, &admitted) {
            Ok(ProfileOutcome::AlreadySelected(profiles)) => {
                set_profiles(state, profiles);
                (SUCCEEDED, None, false)
            }
            Ok(ProfileOutcome::Applied(profiles)) => {
                set_profiles(state, profiles);
                if fixture == ColorFixture::StalledReply {
                    wait_for_disconnect("ICC CONFIRMED", cancelled);
                }
                (SUCCEEDED, None, true)
            }
            Err(rejected) => {
                let (status, possible_submission, message) = match rejected.disposition {
                    Disposition::NoSubmission => {
                        let (status, reason) = match rejected.cause {
                            Failure::Topology(_) | Failure::Target => {
                                (BLOCKED, "the internal display changed")
                            }
                            Failure::Context => (
                                BLOCKED,
                                "the power or session context became stale or the request was cancelled",
                            ),
                            Failure::Hdr(_) => {
                                (BLOCKED, "a fresh SDR preference could not be confirmed")
                            }
                            Failure::ProfileUnavailable => {
                                (FAILED, "that color profile is unavailable")
                            }
                            _ => (
                                FAILED,
                                "the color profile configuration could not be confirmed",
                            ),
                        };
                        (status, false, format!("ICC was not submitted: {reason}."))
                    }
                    Disposition::BaselineConfirmed => (
                        FAILED,
                        true,
                        "ICC failed; the previous configuration was confirmed.".into(),
                    ),
                    Disposition::Restored => (
                        FAILED,
                        true,
                        "ICC failed; the previous configuration was restored.".into(),
                    ),
                    Disposition::Unresolved(ref reason) => (
                        INDETERMINATE,
                        true,
                        format!("ICC submission is indeterminate ({reason:?}); writes are paused."),
                    ),
                };
                if matches!(
                    rejected.disposition,
                    Disposition::BaselineConfirmed | Disposition::Restored
                ) {
                    self.project(reading, state);
                }
                (status, Some(message), possible_submission)
            }
        }
    }
}

fn set_profiles(state: &mut Value, profiles: ColorProfileSnapshot) {
    state["colorProfilesEnabled"] = json!(profiles.hdr == HdrState::Off);
    state["activeColorProfileId"] = json!(profiles.selected.as_deref().and_then(|id| id.to_str()));
    state["colorProfiles"] = json!(
        profiles
            .profiles
            .iter()
            .map(|profile| json!({
                "id": profile.id.to_str().expect("fixture profiles are valid UTF-8"),
                "name": profile.name,
                "isActive": profile.active,
            }))
            .collect::<Vec<_>>()
    );
}
