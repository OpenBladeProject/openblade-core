//! Managed `BatteryAutomationPreferences` and the committed aggregate that
//! pairs it with `profiles.json`: `BatteryConfigurationStore.ReadPreferences`
//! and `ReadCommittedSnapshot` for every schema managed still accepts.
//!
//! The envelope (marker, hashes, schema agreement) is the host's inspection;
//! this reads the two payloads it admitted.

use super::decode;
use super::document::{self, CURRENT_SCHEMA, contains_member};
use super::encode;
use super::json::{self, Json, Malformed};
use super::lighting;
use super::model::LightingSettings;
use super::normalize::Invalid;
use super::validate::Validator;
use super::{ProfileSet, StoredProfiles};
use std::string::String;
use std::vec::Vec;

/// Managed `FanStopSettings`.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct FanStopSettings {
    pub end_mode: i32,
    pub cpu_limit_celsius: i32,
    pub gpu_limit_celsius: i32,
    pub cpu_immediate_cutoff_celsius: i32,
    pub gpu_immediate_cutoff_celsius: i32,
    pub seconds_above_limit: i32,
    pub automatic_enabled: bool,
    pub automatic_cpu_below_celsius: i32,
    pub automatic_gpu_below_celsius: i32,
    pub seconds_below_limits: i32,
}

impl Default for FanStopSettings {
    fn default() -> Self {
        Self {
            end_mode: 0,
            cpu_limit_celsius: 55,
            gpu_limit_celsius: 55,
            cpu_immediate_cutoff_celsius: 65,
            gpu_immediate_cutoff_celsius: 65,
            seconds_above_limit: 5,
            automatic_enabled: false,
            automatic_cpu_below_celsius: 50,
            automatic_gpu_below_celsius: 50,
            seconds_below_limits: 30,
        }
    }
}

impl FanStopSettings {
    const MAXIMUM_TEMPERATURE_CELSIUS: i32 = 100;

    /// Managed `FanStopSettings.IsWellFormed`.
    pub fn is_well_formed(&self) -> bool {
        let temperature = |value: i32| (10..=Self::MAXIMUM_TEMPERATURE_CELSIUS).contains(&value);
        let custom = self.end_mode == 1;
        matches!(self.end_mode, 0 | 1)
            && temperature(self.cpu_limit_celsius)
            && temperature(self.gpu_limit_celsius)
            && temperature(self.cpu_immediate_cutoff_celsius)
            && temperature(self.gpu_immediate_cutoff_celsius)
            && self.cpu_immediate_cutoff_celsius >= self.cpu_limit_celsius
            && self.gpu_immediate_cutoff_celsius >= self.gpu_limit_celsius
            && temperature(self.automatic_cpu_below_celsius)
            && temperature(self.automatic_gpu_below_celsius)
            && (1..=300).contains(&self.seconds_above_limit)
            && (1..=3600).contains(&self.seconds_below_limits)
            && (!self.automatic_enabled
                || self.automatic_cpu_below_celsius
                    < if custom { self.cpu_limit_celsius } else { 55 }
                    && (!custom || self.automatic_gpu_below_celsius < self.gpu_limit_celsius))
    }
}

/// Managed `BatteryAutomationPreferences`; the enums keep their wire value.
#[derive(Clone, Debug, PartialEq)]
pub struct Preferences {
    /// Schemas 1 to 10 only: the protection setting profiles now hold.
    pub protection_enabled: Option<bool>,
    pub always_override_external_lighting_changes: bool,
    pub last_known_lighting: Option<LightingSettings>,
    pub keyboard_lighting_control_mode: i32,
    pub allow_untested_firmware: bool,
    pub lighting_refresh_rate: i32,
    pub thermal_control_policy: i32,
    pub thermal_policy_upgrade_disclosure: i32,
    pub allow_experimental_features: bool,
    /// `None` is a `null` the reader reports.
    pub fan_stop_settings: Option<FanStopSettings>,
}

impl Default for Preferences {
    fn default() -> Self {
        Self {
            protection_enabled: None,
            always_override_external_lighting_changes: false,
            last_known_lighting: None,
            keyboard_lighting_control_mode: 0,
            allow_untested_firmware: false,
            lighting_refresh_rate: 0,
            thermal_control_policy: 0,
            thermal_policy_upgrade_disclosure: 0,
            allow_experimental_features: true,
            fan_stop_settings: Some(FanStopSettings::default()),
        }
    }
}

type Decoded<T> = Result<T, Malformed>;

fn members<'a>(json: &'a Json, names: &[&str]) -> Decoded<&'a [(String, Json)]> {
    json.members()
        .filter(|members| {
            members
                .iter()
                .all(|(name, _)| names.contains(&name.as_str()))
        })
        .ok_or(Malformed)
}

fn int(json: &Json) -> Decoded<i32> {
    json.web_integer(i32::MIN.into(), i32::MAX.into())
        .map(|value| value as i32)
        .ok_or(Malformed)
}

fn wire(json: &Json) -> Decoded<i32> {
    json.integer(i32::MIN.into(), i32::MAX.into())
        .map(|value| value as i32)
        .ok_or(Malformed)
}

fn fan_stop_settings(json: &Json) -> Decoded<FanStopSettings> {
    let mut settings = FanStopSettings::default();
    for (name, value) in json.members().ok_or(Malformed)? {
        match name.as_str() {
            "endMode" => settings.end_mode = wire(value)?,
            "cpuLimitCelsius" => settings.cpu_limit_celsius = int(value)?,
            "gpuLimitCelsius" => settings.gpu_limit_celsius = int(value)?,
            "cpuImmediateCutoffCelsius" => settings.cpu_immediate_cutoff_celsius = int(value)?,
            "gpuImmediateCutoffCelsius" => settings.gpu_immediate_cutoff_celsius = int(value)?,
            "secondsAboveLimit" => settings.seconds_above_limit = int(value)?,
            "automaticEnabled" => settings.automatic_enabled = value.boolean().ok_or(Malformed)?,
            "automaticCpuBelowCelsius" => settings.automatic_cpu_below_celsius = int(value)?,
            "automaticGpuBelowCelsius" => settings.automatic_gpu_below_celsius = int(value)?,
            "secondsBelowLimits" => settings.seconds_below_limits = int(value)?,
            _ => return Err(Malformed),
        }
    }
    Ok(settings)
}

/// Every member is optional: the managed constructor defaults them all.
fn preferences(json: &Json) -> Decoded<Preferences> {
    let mut preferences = Preferences::default();
    let boolean = |value: &Json| value.boolean().ok_or(Malformed);
    for (name, value) in json.members().ok_or(Malformed)? {
        match name.as_str() {
            "protectionEnabled" => {
                preferences.protection_enabled = if value.is_null() {
                    None
                } else {
                    Some(boolean(value)?)
                }
            }
            "alwaysOverrideExternalLightingChanges" => {
                preferences.always_override_external_lighting_changes = boolean(value)?
            }
            "lastKnownLighting" => {
                preferences.last_known_lighting = if value.is_null() {
                    None
                } else {
                    Some(decode::lighting(value)?)
                }
            }
            "keyboardLightingControlMode" => {
                preferences.keyboard_lighting_control_mode = wire(value)?
            }
            "allowUntestedFirmware" => preferences.allow_untested_firmware = boolean(value)?,
            "lightingRefreshRate" => preferences.lighting_refresh_rate = wire(value)?,
            "thermalControlPolicy" => preferences.thermal_control_policy = wire(value)?,
            "thermalPolicyUpgradeDisclosure" => {
                preferences.thermal_policy_upgrade_disclosure = wire(value)?
            }
            "allowExperimentalFeatures" => {
                preferences.allow_experimental_features = boolean(value)?
            }
            "fanStopSettings" => {
                preferences.fan_stop_settings = if value.is_null() {
                    None
                } else {
                    Some(fan_stop_settings(value)?)
                }
            }
            _ => return Err(Malformed),
        }
    }
    Ok(preferences)
}

/// Managed `BatteryConfigurationStore.ReadPreferences`. Returns whether the
/// document must be rewritten.
pub fn read_preferences_document(
    bytes: &[u8],
    schema: i32,
    validator: &Validator,
) -> Result<(Preferences, bool), Invalid> {
    let missing = || Invalid::new("The battery preferences are missing.");
    let root = json::parse(bytes).map_err(|_| missing())?;
    let body = root
        .member("preferences")
        .filter(|body| body.members().is_some())
        .ok_or_else(missing)?;
    if schema >= 16
        && (body.member("thermalControlPolicy").is_none()
            || body.member("thermalPolicyUpgradeDisclosure").is_none())
    {
        return Err(Invalid::new(
            "The current battery preferences are missing thermal policy state.",
        ));
    }
    if (17..19).contains(&schema) && body.member("allowExperimentalFeatures").is_none() {
        return Err(Invalid::new(
            "The current battery preferences are missing the legacy experimental preference.",
        ));
    }
    let unsupported = || Invalid::new("The battery preference schema is unsupported.");
    let declared = members(&root, &["schemaVersion", "preferences"])
        .ok()
        .and_then(|_| root.member("schemaVersion"))
        .and_then(|version| int(version).ok());
    if declared != Some(schema) {
        return Err(unsupported());
    }
    let mut preferences = preferences(body).map_err(|_| unsupported())?;
    if schema < 11 && preferences.protection_enabled.is_none() {
        return Err(Invalid::new(
            "The legacy battery protection preference is missing.",
        ));
    }
    if schema >= 11 && preferences.protection_enabled.is_some() {
        return Err(Invalid::new(
            "The current battery preference document contains a legacy setting.",
        ));
    }
    if schema < 27
        && contains_member(
            body,
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
        && contains_member(body, &["controlsKeyboardEffect"], |value| {
            *value != Json::Bool(false)
        })
    {
        return Err(Invalid::new(
            "Keyboard effect control requires the current lighting-intent schema.",
        ));
    }
    if schema == 26
        && let Some(settings) = &mut preferences.last_known_lighting
    {
        lighting::preserve_legacy_independent_intent(settings);
    }
    if schema < 24 {
        preferences.fan_stop_settings = Some(FanStopSettings::default());
    }
    let stored = body
        .member("fanStopSettings")
        .filter(|settings| settings.members().is_some());
    let Some(fan_stop) = &mut preferences.fan_stop_settings else {
        return Err(Invalid::new(
            "The current preferences are missing valid Fan Stop settings.",
        ));
    };
    if schema >= 24 && stored.is_none() {
        return Err(Invalid::new(
            "The current preferences are missing valid Fan Stop settings.",
        ));
    }
    if schema == 24 {
        // Schema 24 had no immediate cutoff; managed derives one per limit.
        let cutoff = |limit: i32| {
            (i64::from(limit) + 10).min(FanStopSettings::MAXIMUM_TEMPERATURE_CELSIUS.into()) as i32
        };
        fan_stop.cpu_immediate_cutoff_celsius = cutoff(fan_stop.cpu_limit_celsius);
        fan_stop.gpu_immediate_cutoff_celsius = cutoff(fan_stop.gpu_limit_celsius);
    } else if schema >= 25
        && stored.is_some_and(|settings| {
            settings.member("cpuImmediateCutoffCelsius").is_none()
                || settings.member("gpuImmediateCutoffCelsius").is_none()
        })
    {
        return Err(Invalid::new(
            "The current Fan Stop settings are missing immediate cutoffs.",
        ));
    }
    if schema < 16 {
        // Maintain desired state, with the upgrade disclosure pending.
        preferences.thermal_control_policy = 0;
        preferences.thermal_policy_upgrade_disclosure = 1;
    }
    let normalized_automatic_support = !preferences.allow_experimental_features;
    preferences.allow_experimental_features = true;
    validate_preferences(&preferences, validator)?;
    let migrated = schema < CURRENT_SCHEMA
        || preferences.protection_enabled.is_some()
        || normalized_automatic_support;
    Ok((preferences, migrated))
}

/// Managed `ProfileSetValidator.ValidatePreferences`.
fn validate_preferences(preferences: &Preferences, validator: &Validator) -> Result<(), Invalid> {
    if !preferences
        .fan_stop_settings
        .is_some_and(|settings| settings.is_well_formed())
    {
        return Err(Invalid::new("Fan Stop settings are invalid."));
    }
    for (value, count, name) in [
        (
            preferences.keyboard_lighting_control_mode,
            2,
            "keyboard lighting control mode",
        ),
        (
            preferences.lighting_refresh_rate,
            2,
            "lighting refresh rate",
        ),
        (
            preferences.thermal_control_policy,
            2,
            "thermal control policy",
        ),
        (
            preferences.thermal_policy_upgrade_disclosure,
            3,
            "thermal policy upgrade disclosure state",
        ),
    ] {
        if !(0..count).contains(&value) {
            return Err(Invalid::new(std::format!("The {name} is undefined.")));
        }
    }
    match &preferences.last_known_lighting {
        Some(settings) => validator.validate_lighting(Some(settings), "last-known", true),
        None => Ok(()),
    }
}

/// The committed aggregate, at the current schema in memory.
#[derive(Clone, Debug, PartialEq)]
pub struct Aggregate {
    pub profiles: ProfileSet,
    pub preferences: Preferences,
    /// The stored documents differ from what managed would write for them.
    pub migrated: bool,
    pub schema: i32,
}

/// Managed `ReadCommittedSnapshot` after the marker and hashes agree:
/// `schema` is the marker's, which both documents must declare.
pub fn read_aggregate(
    schema: i32,
    profile: &[u8],
    preferences: &[u8],
    validator: &Validator,
) -> Result<Aggregate, Invalid> {
    let (mut preferences, preferences_migrated) =
        read_preferences_document(preferences, schema, validator)?;
    let StoredProfiles {
        profiles,
        migrated,
        schema: declared,
    } = document::read_profile_document(
        profile,
        preferences.protection_enabled.unwrap_or(true),
        validator,
    )?;
    if declared != schema {
        return Err(Invalid::new(
            "The profile and battery configuration marker schemas do not match.",
        ));
    }
    preferences.protection_enabled = None;
    Ok(Aggregate {
        profiles,
        preferences,
        migrated: migrated || preferences_migrated,
        schema,
    })
}

/// `battery-preferences.json` as managed writes it.
pub fn preferences_document(schema: i32, preferences: &Preferences) -> Vec<u8> {
    let member = |name: &str, value: Json| (String::from(name), value);
    let int = |value: i32| Json::int(value.into());
    let mut body = Vec::new();
    if let Some(enabled) = preferences.protection_enabled {
        body.push(member("protectionEnabled", Json::Bool(enabled)));
    }
    body.extend([
        member(
            "alwaysOverrideExternalLightingChanges",
            Json::Bool(preferences.always_override_external_lighting_changes),
        ),
        member(
            "lastKnownLighting",
            preferences
                .last_known_lighting
                .as_ref()
                .map_or(Json::Null, encode::lighting),
        ),
        member(
            "keyboardLightingControlMode",
            int(preferences.keyboard_lighting_control_mode),
        ),
        member(
            "allowUntestedFirmware",
            Json::Bool(preferences.allow_untested_firmware),
        ),
        member(
            "lightingRefreshRate",
            int(preferences.lighting_refresh_rate),
        ),
        member(
            "thermalControlPolicy",
            int(preferences.thermal_control_policy),
        ),
        member(
            "thermalPolicyUpgradeDisclosure",
            int(preferences.thermal_policy_upgrade_disclosure),
        ),
        member(
            "allowExperimentalFeatures",
            Json::Bool(preferences.allow_experimental_features),
        ),
        member(
            "fanStopSettings",
            preferences
                .fan_stop_settings
                .map_or(Json::Null, |settings| {
                    Json::Object(Vec::from([
                        member("endMode", int(settings.end_mode)),
                        member("cpuLimitCelsius", int(settings.cpu_limit_celsius)),
                        member("gpuLimitCelsius", int(settings.gpu_limit_celsius)),
                        member(
                            "cpuImmediateCutoffCelsius",
                            int(settings.cpu_immediate_cutoff_celsius),
                        ),
                        member(
                            "gpuImmediateCutoffCelsius",
                            int(settings.gpu_immediate_cutoff_celsius),
                        ),
                        member("secondsAboveLimit", int(settings.seconds_above_limit)),
                        member("automaticEnabled", Json::Bool(settings.automatic_enabled)),
                        member(
                            "automaticCpuBelowCelsius",
                            int(settings.automatic_cpu_below_celsius),
                        ),
                        member(
                            "automaticGpuBelowCelsius",
                            int(settings.automatic_gpu_below_celsius),
                        ),
                        member("secondsBelowLimits", int(settings.seconds_below_limits)),
                    ]))
                }),
        ),
    ]);
    json::write(
        &Json::Object(Vec::from([
            member("schemaVersion", int(schema)),
            member("preferences", Json::Object(body)),
        ])),
        true,
    )
}

/// `battery-configuration.commit.json` for the two document digests, each
/// the uppercase hexadecimal SHA-256 of the exact file bytes.
pub fn commit_marker(schema: i32, profile_sha256: &str, preferences_sha256: &str) -> Vec<u8> {
    json::write(
        &Json::Object(Vec::from([
            (String::from("schemaVersion"), Json::int(schema.into())),
            (String::from("profileSha256"), Json::string(profile_sha256)),
            (
                String::from("preferencesSha256"),
                Json::string(preferences_sha256),
            ),
        ])),
        true,
    )
}
