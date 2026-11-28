//! Managed `ProfileStore.ReadDocument`: `profiles.json` at any schema managed
//! still accepts, brought to the current schema in memory.

use super::decode::{self, Dialect};
use super::json::{self, Json};
use super::model::*;
use super::normalize::{self, Invalid};
use super::validate::Validator;

/// Managed `ProfileStore.CurrentSchemaVersion`.
pub const CURRENT_SCHEMA: i32 = 27;

#[derive(Clone, Debug, PartialEq)]
pub struct StoredProfiles {
    pub profiles: ProfileSet,
    /// The document must be rewritten to match what was read.
    pub migrated: bool,
    pub schema: i32,
}

/// Whether any member at any depth is named `name`, ignoring case, with a
/// value `rejects` refuses. Managed scans the raw document this way before
/// it binds anything, including members a migration later discards.
pub(super) fn contains_member(json: &Json, names: &[&str], rejects: fn(&Json) -> bool) -> bool {
    match json {
        Json::Object(members) => members.iter().any(|(name, value)| {
            names.iter().any(|known| name.eq_ignore_ascii_case(known)) && rejects(value)
                || contains_member(value, names, rejects)
        }),
        Json::Array(items) => items
            .iter()
            .any(|item| contains_member(item, names, rejects)),
        _ => false,
    }
}

/// The lighting members a document older than their schema may not opt in to.
pub(super) fn reject_legacy_lighting(root: &Json, schema: i32) -> Result<(), Invalid> {
    if schema < 24 && contains_member(root, &["independentControls"], |value| !value.is_null()) {
        return Err(Invalid::new(
            "Independent lighting controls require profile schema 24.",
        ));
    }
    if schema < 27
        && contains_member(
            root,
            &[
                "vaporChamberLightingEnabled",
                "touchpadStatusIndicatorEnabled",
            ],
            |value| !value.is_null(),
        )
    {
        return Err(Invalid::new(
            "Lighting zone controls require profile schema 27.",
        ));
    }
    if schema < 26
        && contains_member(root, &["controlsKeyboardEffect"], |value| {
            *value != Json::Bool(false)
        })
    {
        return Err(Invalid::new(
            "Keyboard effect control requires the current lighting-intent schema.",
        ));
    }
    Ok(())
}

/// Managed `MigrateLegacyWindowsPowerModes`: schemas 12 to 14 stored the mode
/// on each power profile. A preset keeps a mode only where every profile
/// selecting that preset agrees.
fn migrate_legacy_windows_power_modes(profiles: &mut ProfileSet) {
    let mut modes = WindowsPowerModes::default();
    for role in WindowsPowerModes::LEGACY_ROLES {
        let mut matching = PowerSource::ALL
            .into_iter()
            .map(|source| profiles.for_source(source))
            .filter(|profile| profile.performance == role)
            .map(|profile| profile.windows_power_mode);
        if let Some(first) = matching.next()
            && matching.all(|mode| mode == first)
        {
            modes.0[role.0 as usize] = first;
        }
    }
    normalize::clear_legacy_windows_power_modes(profiles);
    profiles.windows_power_modes = modes;
}

fn schema_1(root: &Json, protection_default: bool) -> Result<ProfileSet, Invalid> {
    let missing = || Invalid::new("The legacy profile document is missing.");
    let body = root
        .members()
        .filter(|members| {
            members
                .iter()
                .all(|(name, _)| name == "schemaVersion" || name == "profiles")
        })
        .and(root.member("profiles"))
        .filter(|profiles| !profiles.is_null())
        .ok_or_else(missing)?;
    let (plugged_in, battery, battery_policy) =
        decode::legacy_profile_set(body).map_err(|_| missing())?;
    let mut usb_c = ProfileSet::safe_defaults().usb_c;
    usb_c.battery_protection_enabled = Some(protection_default);
    let mut profiles = ProfileSet {
        plugged_in,
        battery,
        usb_c,
        battery_policy,
        ..ProfileSet::safe_defaults()
    };
    normalize::normalize_legacy_battery_profile(&mut profiles, Some(protection_default))?;
    normalize::clear_legacy_windows_power_modes(&mut profiles);
    Ok(profiles)
}

/// Reads `profiles.json`. `legacy_battery_protection_default` is the
/// protection setting schemas 1 to 10 kept in the preferences document.
pub fn read_profile_document(
    bytes: &[u8],
    legacy_battery_protection_default: bool,
    validator: &Validator,
) -> Result<StoredProfiles, Invalid> {
    let unsupported = || Invalid::new("The profile schema version is unsupported.");
    let missing = || Invalid::new("The profile document is missing.");
    let root =
        json::parse(bytes).map_err(|_| Invalid::new("The profile document is not valid JSON."))?;
    let schema = root
        .member("schemaVersion")
        .and_then(|version| version.integer(i32::MIN.into(), i32::MAX.into()))
        .ok_or_else(unsupported)? as i32;
    let body = root.member("profiles");
    if schema >= 15
        && body
            .and_then(|profiles| profiles.member("windowsPowerModes"))
            .is_none()
    {
        return Err(Invalid::new(
            "The profile document is missing Windows power mode associations.",
        ));
    }
    reject_legacy_lighting(&root, schema)?;
    let mut migrated = true;
    let mut profiles = match schema {
        2..=CURRENT_SCHEMA => {
            let body = root
                .members()
                .filter(|members| {
                    members
                        .iter()
                        .all(|(name, _)| name == "schemaVersion" || name == "profiles")
                })
                .and(body)
                .filter(|profiles| !profiles.is_null())
                .ok_or_else(missing)?;
            let mut profiles =
                decode::profile_set(body, Dialect::Document).map_err(|_| missing())?;
            let protection_default = (schema < 11).then_some(legacy_battery_protection_default);
            migrated =
                normalize::normalize_legacy_battery_profile(&mut profiles, protection_default)?;
            if schema < 12 {
                normalize::clear_legacy_windows_power_modes(&mut profiles);
                profiles.windows_power_modes = WindowsPowerModes::default();
            } else if schema < 15 {
                for (source, name) in [
                    (PowerSource::PluggedIn, "plugged-in"),
                    (PowerSource::Battery, "battery"),
                    (PowerSource::UsbC, "low-power"),
                ] {
                    if profiles
                        .for_source(source)
                        .windows_power_mode
                        .is_some_and(|mode| !mode.is_defined())
                    {
                        return Err(Invalid::new(std::format!(
                            "The legacy {name} Windows power mode is undefined."
                        )));
                    }
                }
                migrate_legacy_windows_power_modes(&mut profiles);
            } else {
                let before = profiles.clone();
                normalize::clear_legacy_windows_power_modes(&mut profiles);
                migrated |= profiles != before;
            }
            if schema < 22 {
                profiles.application_profiles.clear();
                for source in PowerSource::ALL {
                    if let Some(display) = &mut profiles.for_source_mut(source).display {
                        display.brightness_percent = None;
                    }
                }
            }
            migrated |= schema != CURRENT_SCHEMA;
            profiles
        }
        1 => schema_1(&root, legacy_battery_protection_default)?,
        _ => return Err(unsupported()),
    };
    if schema <= 17 {
        // Schemas 1 to 17 stored a limit on the five percent grid up to 80.
        let limit = profiles
            .battery_policy
            .ok_or_else(missing)?
            .charge_limit_percent;
        if !(50..=80).contains(&limit) || limit % 5 != 0 {
            return Err(Invalid::new(
                "Profile schemas 1 through 17 require a battery charge limit from 50 to 80 percent in 5 percent steps.",
            ));
        }
    }
    if schema < 23 {
        for source in PowerSource::ALL {
            profiles.for_source_mut(source).stop_fans_in_battery_saver = false;
        }
    }
    if schema == 26 {
        normalize::preserve_legacy_independent_profile_intent(&mut profiles);
    }
    validator.validate_stored_custom_intent(&profiles)?;
    Ok(StoredProfiles {
        profiles,
        migrated,
        schema,
    })
}
