use openblade_platform_windows::{
    color_profile::{
        ColorProfileSnapshot, HdrState,
        scripted::{
            Disposition, Failure, Outcome as ProfileOutcome, Profiles, WcsEvidence, WcsFixture,
        },
    },
    display::{DisplayReadError, InternalDisplayRefresh, InternalDisplayTopology},
};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};

use super::{
    Attempt, BLOCKED, FAILED, INDETERMINATE, SUCCEEDED,
    journal::{Change, Panel},
    panel::{Context, Outcome, ScriptedDisplay, wait_for_disconnect},
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
    PartialSubmissionError,
    ReadbackMismatch,
    StalledReply,
    StalledAdmission,
    RecoveryApplied,
    RecoveryPartial,
    RecoveryWrongScope,
    RecoveryWrongAssociations,
    RecoveryHdr,
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
            "icc-partial-submission-error" => Self::PartialSubmissionError,
            "icc-readback-mismatch" => Self::ReadbackMismatch,
            "icc-stalled-reply" => Self::StalledReply,
            "icc-stalled-admission" => Self::StalledAdmission,
            "icc-recovery-applied" => Self::RecoveryApplied,
            "icc-recovery-partial" => Self::RecoveryPartial,
            "icc-recovery-wrong-scope" => Self::RecoveryWrongScope,
            "icc-recovery-wrong-associations" => Self::RecoveryWrongAssociations,
            "icc-recovery-hdr" => Self::RecoveryHdr,
            _ => return None,
        })
    }
}

// The field limits and shared record cap bound the fixture's scratch evidence.
#[derive(Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub(super) struct ColorChange {
    #[serde(with = "WcsEvidenceDef")]
    before: WcsEvidence,
    requested: String,
}

#[derive(Serialize, Deserialize)]
#[serde(remote = "WcsEvidence", deny_unknown_fields)]
struct WcsEvidenceDef {
    source_name: String,
    monitor_key: String,
    hdr_enabled: bool,
    current_user: bool,
    #[serde(deserialize_with = "Deserialize::deserialize")]
    user_default: Option<String>,
    user_associations: Vec<String>,
    #[serde(deserialize_with = "Deserialize::deserialize")]
    system_default: Option<String>,
    system_associations: Vec<String>,
}

impl ColorChange {
    pub(super) fn valid(&self) -> bool {
        let valid_text = |value: &str| {
            !value.is_empty() && value.len() <= 256 && !value.chars().any(char::is_control)
        };
        !self.before.hdr_enabled
            && valid_text(&self.before.source_name)
            && valid_text(&self.before.monitor_key)
            && valid_text(&self.requested)
            && self.before.user_associations.len() <= 16
            && self.before.system_associations.len() <= 16
            && self
                .before
                .user_associations
                .iter()
                .chain(&self.before.system_associations)
                .chain(&self.before.user_default)
                .chain(&self.before.system_default)
                .all(|value| valid_text(value))
            && self.before.after_selection(&self.requested) != self.before
    }

    pub(super) fn classifies(&self, observed: &WcsEvidence) -> bool {
        *observed == self.before || *observed == self.before.after_selection(&self.requested)
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
            ColorFixture::PartialSubmissionError => WcsFixture::FailedAssociationWithEffect,
            ColorFixture::RecoveryPartial => WcsFixture::StartsWithPartialAssociation,
            ColorFixture::ReadbackMismatch => WcsFixture::MismatchedReadback,
            ColorFixture::RecoveryApplied | ColorFixture::RecoveryHdr => WcsFixture::StartsApplied,
            ColorFixture::RecoveryWrongScope => WcsFixture::StartsWithDifferentScope,
            ColorFixture::RecoveryWrongAssociations => WcsFixture::StartsWithDifferentAssociations,
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

    pub(super) fn observe(
        &mut self,
        panel: &ScriptedDisplay,
        identity: &Panel,
        context: Context,
        cancelled: &dyn Fn() -> bool,
    ) -> Option<WcsEvidence> {
        if !self.enabled() {
            return None;
        }
        let read = || {
            let Ok(InternalDisplayRefresh::Single(snapshot)) = panel.read(false) else {
                return None;
            };
            (Panel::observe(&snapshot.display).as_ref() == Some(identity)).then_some(snapshot)
        };
        let snapshot = read()?;
        let observed = self.profiles.evidence(&snapshot.display).ok()?;
        let fresh = read()?;
        if fresh.display.advanced_color != snapshot.display.advanced_color
            || fresh.display.source_name != snapshot.display.source_name
            || panel.context(true) != Some(context)
            || cancelled()
        {
            return None;
        }
        Some(observed)
    }

    pub(super) fn apply(
        &mut self,
        (expected_power, key): (u8, &str),
        panel: &ScriptedDisplay,
        reading: &Result<InternalDisplayRefresh, DisplayReadError>,
        state: &mut Value,
        attempt: Attempt<'_>,
    ) -> Outcome {
        let cancelled = attempt.cancelled;
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
        let mut intent = None;
        let result = self
            .profiles
            .set(key, fresh_topology, &admitted, |before, requested| {
                let evidence = ColorChange { before, requested };
                if !admitted() {
                    return Err(Failure::Context);
                }
                let expected = evidence.before.after_selection(&evidence.requested);
                let baseline = evidence.before.clone();
                if !(attempt.record)(
                    identity.clone(),
                    Change::Icc {
                        evidence: Box::new(evidence),
                    },
                ) {
                    return Err(Failure::IntentUnavailable);
                }
                intent = Some((baseline, expected));
                if fixture == ColorFixture::StalledAdmission {
                    wait_for_disconnect("ICC ADMISSION", cancelled);
                }
                Ok(())
            });
        match result {
            Ok(ProfileOutcome::AlreadySelected(profiles)) => {
                set_profiles(state, profiles);
                (SUCCEEDED, None, false)
            }
            Ok(ProfileOutcome::Applied(profiles)) => {
                let (_, expected) = intent.expect("ICC intent precedes submission");
                if self.observe(panel, &identity, context, cancelled).as_ref() != Some(&expected) {
                    return indeterminate("the final WCS configuration could not be confirmed");
                }
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
                            Failure::IntentUnavailable => {
                                return (BLOCKED, Some(super::JOURNAL_BLOCKED.into()), false);
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
                    let (baseline, _) = intent.expect("ICC intent precedes submission");
                    if self.observe(panel, &identity, context, cancelled).as_ref()
                        != Some(&baseline)
                    {
                        return indeterminate(
                            "the previous WCS configuration could not be confirmed",
                        );
                    }
                    self.project(reading, state);
                }
                (status, Some(message), possible_submission)
            }
        }
    }
}

fn indeterminate(reason: &str) -> Outcome {
    (
        INDETERMINATE,
        Some(format!(
            "ICC submission is indeterminate ({reason}); writes are paused."
        )),
        true,
    )
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
