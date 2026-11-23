//! Managed `DisplayAutomationCoordinator.ResolveTarget` over the raw members
//! of verified Service status (`Snapshot::display_automation`).
//!
//! Each display member is decoded by the reviewed profile-set codec
//! (`openblade_domain::profile_set::decode::display_settings`), with managed
//! `DisplayProfileSettings` rules. The application list is searched for the
//! first enabled entry whose ID equals the active one, as managed does;
//! entries before it that are not that profile are skipped without being
//! decoded, because managed never reads their display members. Every power
//! profile's display member is decoded and range-checked, as managed
//! `ValidProfileDisplays` checks all three before any is used. The managed
//! status codec refuses an active ID without an enabled matching profile, so
//! that, like a display member managed would refuse, makes the status
//! unreadable (`None`).
use super::super::{profile_apply::Settings, recovery::Target};
use crate::service_status::DisplayAutomationFields;
use openblade_domain::profile_set::{decode, json};
use serde_json::Value;

/// Managed `ApplicationProfileOverrideSet` member names by `PowerSource`.
const OVERRIDES: [&str; 3] = ["pluggedIn", "battery", "usbC"];
/// Managed `ValidDisplay`: the largest admitted `BrightnessPercent`.
const MAXIMUM_PERCENT: u8 = 100;

/// The target for confirmed `source` (0..=2), or `None` when a member managed
/// reads is malformed.
pub(super) fn resolve(fields: DisplayAutomationFields<'_>, source: u8) -> Option<Target> {
    let mut power = [None, None, None];
    for (decoded, member) in power.iter_mut().zip(fields.power) {
        *decoded = optional(member)?;
    }
    let base = power[usize::from(source)].take().unwrap_or_default();
    let application = match fields.active {
        None | Some(Value::Null) => None,
        Some(Value::String(id)) if super::super::recovery::guid(id) => {
            Some(id.to_ascii_lowercase())
        }
        Some(_) => return None,
    };
    let application_settings = match &application {
        None => None,
        Some(id) => display_override(active_profile(fields.applications?, id)?, source)?,
    };
    Some(Target {
        source,
        application,
        base,
        application_settings,
    })
}

/// The first enabled application profile with this ID.
fn active_profile<'a>(applications: &'a Value, id: &str) -> Option<&'a Value> {
    for profile in applications.as_array()? {
        let matches = profile
            .get("id")
            .and_then(Value::as_str)
            .is_some_and(|candidate| candidate.eq_ignore_ascii_case(id));
        if !matches {
            continue;
        }
        // Managed `ApplicationProfileDefinition.Enabled` defaults to true.
        let enabled = match profile.get("enabled") {
            None => true,
            Some(Value::Bool(enabled)) => *enabled,
            Some(_) => return None,
        };
        if enabled {
            return Some(profile);
        }
    }
    None
}

/// Managed `Overrides.ForPowerSource(source)?.Display`: `Some(None)` when the
/// profile overrides nothing for this source's display.
fn display_override(profile: &Value, source: u8) -> Option<Option<Settings>> {
    let overrides = match profile.get("overrides") {
        None | Some(Value::Null) => return Some(None),
        Some(Value::Object(overrides)) => overrides,
        Some(_) => return None,
    };
    let entry = match overrides.get(OVERRIDES[usize::from(source)]) {
        None | Some(Value::Null) => return Some(None),
        Some(Value::Object(entry)) => entry,
        Some(_) => return None,
    };
    optional(entry.get("display"))
}

/// A nullable `DisplayProfileSettings` member: `Some(None)` when absent or null.
fn optional(member: Option<&Value>) -> Option<Option<Settings>> {
    match member {
        None | Some(Value::Null) => Some(None),
        Some(value) => settings(value).map(Some),
    }
}

fn settings(value: &Value) -> Option<Settings> {
    let bytes = serde_json::to_vec(value).ok()?;
    let parsed = json::parse(&bytes).ok()?;
    let display = decode::display_settings(&parsed).ok()?;
    // Managed `ValidDisplay` (IpcMessageCodec): a percentage above 100 makes
    // the whole status invalid.
    if display
        .brightness_percent
        .is_some_and(|percent| percent > MAXIMUM_PERCENT)
    {
        return None;
    }
    Some(Settings {
        refresh_rate_key: display.refresh_rate_key,
        hdr_enabled: display.hdr_enabled,
        color_profile_key: display.color_profile_key,
        brightness_percent: display.brightness_percent,
    })
}
