//! Managed `BatteryConfigurationStore.LoadCoreAsync` where it rewrites the
//! store (`BatteryConfigurationStore.cs:349-417`): a committed aggregate that
//! reading migrated, and the schema-less legacy profile, become the three
//! documents managed writes at the current schema.
//!
//! This is the payload half. The envelope (which files exist, the marker,
//! its hashes and a future schema in any file) is the host's inspection; the
//! durable replacement is the host's transaction.

use super::document::CURRENT_SCHEMA;
use super::encode;
use super::json::{self, Json};
use super::model::*;
use super::normalize::{self, Invalid};
use super::preferences::{Preferences, commit_marker, preferences_document, read_aggregate};
use super::validate::Validator;
use std::string::String;
use std::vec::Vec;

/// The stored aggregate the host's inspection admitted.
#[derive(Clone, Copy, Debug)]
pub enum Stored<'a> {
    /// `AggregateSnapshot.IsLegacyCandidate`: `profiles.json` alone, without
    /// a schema version.
    Legacy { profile: &'a [u8] },
    /// The marker and both documents agree on `schema`, the marker's.
    Committed {
        schema: i32,
        profile: &'a [u8],
        preferences: &'a [u8],
    },
}

/// Why managed would preserve the aggregate instead of rewriting it, as its
/// `ConfigurationQuarantineReason`.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum Refusal {
    /// `FutureSchema`: a schema newer than this reader.
    FutureSchema,
    /// `IncompleteAggregate`: a lone profile that declares a schema.
    IncompleteAggregate,
    /// `InvalidDocument`. The message is this reader's own; the managed
    /// vectors record only the reason.
    Invalid(Invalid),
}

/// The aggregate managed writes back, and the set it reads back from it.
#[derive(Clone, Debug, PartialEq)]
pub struct Migration {
    pub profiles: ProfileSet,
    pub preferences: Preferences,
    /// The source schema; `None` for the schema-less legacy profile.
    pub schema: Option<i32>,
}

impl Migration {
    /// `profiles.json`, `battery-preferences.json` and the marker, byte for
    /// byte as `SaveConfigurationTransactionAsync` writes them
    /// (`BatteryConfigurationStore.cs:696-702`). `sha256` is the uppercase
    /// hexadecimal digest of its input; this crate has no hash of its own.
    pub fn documents(&self, sha256: impl Fn(&[u8]) -> String) -> [Vec<u8>; 3] {
        let profile = encode::profile_document(CURRENT_SCHEMA, &self.profiles);
        let preferences = preferences_document(CURRENT_SCHEMA, &self.preferences);
        let marker = commit_marker(CURRENT_SCHEMA, &sha256(&profile), &sha256(&preferences));
        [profile, preferences, marker]
    }
}

/// What managed's load does with `stored`: `Ok(None)` leaves it as it is,
/// `Ok(Some)` is the aggregate it writes in its place, and a refusal
/// quarantines it unchanged.
pub fn migrate(stored: Stored<'_>, validator: &Validator) -> Result<Option<Migration>, Refusal> {
    match stored {
        Stored::Legacy { profile } => Ok(Some(Migration {
            profiles: read_legacy(profile, validator)?,
            preferences: legacy_preferences(),
            schema: None,
        })),
        Stored::Committed {
            schema,
            profile,
            preferences,
        } => {
            if schema > CURRENT_SCHEMA || [profile, preferences].into_iter().any(future_schema) {
                return Err(Refusal::FutureSchema);
            }
            // A declared difference: the decoder refuses a null application
            // profile list and null Windows power modes below schema 15, and
            // a null application profile or one with null overrides, path or
            // name below 22, where managed reads them and then discards or
            // migrates them (`ProfileStore.cs:235-256`, `:492-553`).
            let aggregate = read_aggregate(schema, profile, preferences, validator)
                .map_err(Refusal::Invalid)?;
            // `ReadCommittedSnapshot` reports `ConfigurationMigrated`; only
            // then does `LoadCoreAsync` write (`:406-417`).
            Ok(aggregate.migrated.then_some(Migration {
                profiles: aggregate.profiles,
                preferences: aggregate.preferences,
                schema: Some(schema),
            }))
        }
    }
}

/// Managed `ContainsFutureSchema` (`BatteryConfigurationStore.cs:1195-1222`):
/// a root `schemaVersion` that is a 32-bit integer above the current schema.
/// Managed checks every stored file before it reads any of them. A document
/// this reader cannot parse declares nothing; managed also parses one with a
/// duplicate member here, which is refused as invalid instead.
fn future_schema(bytes: &[u8]) -> bool {
    json::parse(bytes).ok().is_some_and(|root| {
        root.member("schemaVersion")
            .and_then(|version| version.integer(i32::MIN.into(), i32::MAX.into()))
            .is_some_and(|version| version > CURRENT_SCHEMA.into())
    })
}

/// The preferences `LoadCoreAsync` pairs with a legacy profile
/// (`BatteryConfigurationStore.cs:354-362`): the constructor's defaults with
/// Maintain desired state and the upgrade disclosure pending. The transaction
/// then clears the protection setting and allows experimental features
/// (`:696`), which the defaults already hold.
fn legacy_preferences() -> Preferences {
    Preferences {
        thermal_control_policy: 0,
        thermal_policy_upgrade_disclosure: 1,
        ..Preferences::default()
    }
}

/// `RequireObjectMembers`: an object whose member names are `names`
/// exactly, or a subset of them when `subset` (`:1255-1276`).
fn object<'a>(json: &'a Json, names: &[&str], subset: bool) -> Result<&'a Json, Invalid> {
    let members = json
        .members()
        .ok_or_else(|| Invalid::new("A required JSON object is missing."))?;
    // `json::parse` never yields a duplicate name, so the counts compare sets.
    let known = members
        .iter()
        .all(|(name, _)| names.contains(&name.as_str()));
    if known && (subset || members.len() == names.len()) {
        Ok(json)
    } else {
        Err(Invalid::new(
            "The JSON document has missing or unknown members.",
        ))
    }
}

/// `ReadInt32`: a JSON number that `TryGetInt32` takes, never its text
/// (`:1243-1253`).
fn int(json: &Json, name: &str) -> Result<i32, Invalid> {
    json.member(name)
        .and_then(|value| value.integer(i32::MIN.into(), i32::MAX.into()))
        .map(|value| value as i32)
        .ok_or_else(|| Invalid::new(std::format!("The JSON member '{name}' must be an integer.")))
}

/// `checked((byte)ReadInt32(...))`: the integer is read first, so `-0` is a
/// zero, and only then is its range checked.
fn byte(json: &Json, name: &str) -> Result<u8, Invalid> {
    u8::try_from(int(json, name)?)
        .map_err(|_| Invalid::new("Arithmetic operation resulted in an overflow."))
}

/// `ReadLegacyProfile` (`:554-571`): the performance, fan and lighting
/// intent of a pre-schema power profile. Each enum takes any integer, as a
/// cast does; the validator judges it afterwards.
fn legacy_profile(json: &Json) -> Result<PowerProfile, Invalid> {
    let profile = object(json, &["performance", "fans", "lighting"], false)?;
    let lighting = object(
        profile.member("lighting").unwrap_or(&Json::Null),
        &["effect", "color", "brightness"],
        false,
    )?;
    let color = object(
        lighting.member("color").unwrap_or(&Json::Null),
        &["red", "green", "blue"],
        true,
    )?;
    // `ReadByte`: an absent component is zero.
    let component = |name| match color.member(name) {
        Some(_) => byte(color, name),
        None => Ok(0),
    };
    Ok(PowerProfile {
        performance: PerformancePreset(int(profile, "performance")?),
        fans: FanMode(int(profile, "fans")?),
        lighting: Some(LightingSettings {
            effect: LightingEffect(int(lighting, "effect")?),
            color: RgbColor {
                red: component("red")?,
                green: component("green")?,
                blue: component("blue")?,
            },
            brightness: byte(lighting, "brightness")?,
            parameters: None,
            logo_mode: None,
            independent_controls: None,
            controls_keyboard_effect: false,
        }),
        display: None,
        stop_fans_in_battery_saver: false,
        custom_performance: None,
        gpu_clock_tuning: None,
        fan_control: None,
        lighting_power_policy: None,
        laptop_behavior: None,
        battery_protection_enabled: None,
        thx_audio: None,
        windows_power_mode: None,
    })
}

/// Managed `BatteryConfigurationStore.ReadLegacy` (`:525-552`): the profile
/// document from before schemas, with two power profiles and a battery
/// policy at its root.
fn read_legacy(bytes: &[u8], validator: &Validator) -> Result<ProfileSet, Refusal> {
    if future_schema(bytes) {
        return Err(Refusal::FutureSchema);
    }
    // A declared difference: managed names a lone profile with a duplicate
    // member and a schema version incomplete (`HasSchemaVersion`, `:387-389`),
    // and this parser refuses the duplicate first. The host's inspection
    // matches managed, and both leave the file untouched.
    let root = json::parse(bytes)
        .map_err(|_| Refusal::Invalid(Invalid::new("The legacy profile is not valid JSON.")))?;
    // Managed reads the document even with a schema version, and its
    // quarantine then names the aggregate incomplete (`:387-389`).
    if root.member("schemaVersion").is_some() {
        return Err(Refusal::IncompleteAggregate);
    }
    read_legacy_root(&root, validator).map_err(Refusal::Invalid)
}

fn read_legacy_root(root: &Json, validator: &Validator) -> Result<ProfileSet, Invalid> {
    let root = object(root, &["pluggedIn", "battery", "batteryPolicy"], false)?;
    let member = |name| root.member(name).unwrap_or(&Json::Null);
    let plugged_in = legacy_profile(member("pluggedIn"))?;
    let battery = legacy_profile(member("battery"))?;
    let policy = object(
        member("batteryPolicy"),
        &["chargeLimitPercent", "temporaryFullCharge"],
        false,
    )?;
    if policy.member("temporaryFullCharge") != Some(&Json::Bool(false)) {
        return Err(Invalid::new("Legacy temporary full charge must be false."));
    }
    // `BatteryPolicy.Create` (`Profiles.cs:662-677`).
    let charge_limit_percent = int(policy, "chargeLimitPercent")?;
    if !(50..=99).contains(&charge_limit_percent) {
        return Err(Invalid::new(
            "Enabled charge limits must be from 50 to 99 percent. No limit is represented by protection off.",
        ));
    }
    // `new ProfileSet(pluggedIn, battery, SafeDefaults.UsbC, policy)`: every
    // other member at the record's own default.
    let mut profiles = ProfileSet {
        plugged_in,
        battery,
        usb_c: ProfileSet::safe_defaults().usb_c,
        battery_policy: Some(BatteryPolicy {
            charge_limit_percent,
        }),
        lighting_power_policy: None,
        application_profiles: Vec::new(),
        windows_power_modes: WindowsPowerModes::default(),
    };
    normalize::normalize_legacy_battery_profile(&mut profiles, Some(true))?;
    // `ProfileStore.ValidateLegacyBatteryPolicy` (`ProfileStore.cs:374-382`).
    if !(50..=80).contains(&charge_limit_percent) || charge_limit_percent % 5 != 0 {
        return Err(Invalid::new(
            "Profile schemas 1 through 17 require a battery charge limit from 50 to 80 percent in 5 percent steps.",
        ));
    }
    validator.validate_stored_custom_intent(&profiles)?;
    Ok(profiles)
}
