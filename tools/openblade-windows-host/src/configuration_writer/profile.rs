//! Scoped domain decoding for the managed fixture. The untouched profile and
//! preferences fields must match the managed default fixture as JSON values.

use super::{CURRENT_SCHEMA, Inspection, inspect_bytes};
use serde::{Deserialize, Deserializer, Serialize, de};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use std::io;

pub(super) const LEGACY_SCHEMA: i32 = 24;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) struct ProfilePolicy {
    pub(super) charge_limit: u8,
    pub(super) fan_stop: FanStop,
}

impl Default for ProfilePolicy {
    fn default() -> Self {
        Self {
            charge_limit: 80,
            fan_stop: FanStop::default(),
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[repr(i32)]
enum EndMode {
    Auto = 0,
    Custom = 1,
}

impl<'de> Deserialize<'de> for EndMode {
    fn deserialize<D: Deserializer<'de>>(reader: D) -> Result<Self, D::Error> {
        match i32::deserialize(reader)? {
            0 => Ok(Self::Auto),
            1 => Ok(Self::Custom),
            _ => Err(de::Error::custom("unknown Fan Stop end mode")),
        }
    }
}

impl Serialize for EndMode {
    fn serialize<S: serde::Serializer>(&self, writer: S) -> Result<S::Ok, S::Error> {
        writer.serialize_i32(*self as i32)
    }
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(default, deny_unknown_fields, rename_all = "camelCase")]
pub(super) struct FanStop {
    end_mode: EndMode,
    #[serde(deserialize_with = "web_i32")]
    cpu_limit_celsius: i32,
    #[serde(deserialize_with = "web_i32")]
    gpu_limit_celsius: i32,
    #[serde(deserialize_with = "web_i32")]
    cpu_immediate_cutoff_celsius: i32,
    #[serde(deserialize_with = "web_i32")]
    gpu_immediate_cutoff_celsius: i32,
    #[serde(deserialize_with = "web_i32")]
    seconds_above_limit: i32,
    automatic_enabled: bool,
    #[serde(deserialize_with = "web_i32")]
    automatic_cpu_below_celsius: i32,
    #[serde(deserialize_with = "web_i32")]
    automatic_gpu_below_celsius: i32,
    #[serde(deserialize_with = "web_i32")]
    seconds_below_limits: i32,
}

impl Default for FanStop {
    fn default() -> Self {
        Self {
            end_mode: EndMode::Auto,
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

impl FanStop {
    fn read(value: Value, schema: i32) -> io::Result<Self> {
        let fields = value.as_object().ok_or_else(invalid)?;
        if schema == CURRENT_SCHEMA
            && (!fields.contains_key("cpuImmediateCutoffCelsius")
                || !fields.contains_key("gpuImmediateCutoffCelsius"))
        {
            return Err(invalid());
        }
        let mut settings: Self = serde_json::from_value(value)?;
        if schema == LEGACY_SCHEMA {
            settings.cpu_immediate_cutoff_celsius =
                (i64::from(settings.cpu_limit_celsius) + 10).min(100) as i32;
            settings.gpu_immediate_cutoff_celsius =
                (i64::from(settings.gpu_limit_celsius) + 10).min(100) as i32;
        }
        if !settings.valid() {
            return Err(invalid());
        }
        Ok(settings)
    }

    fn valid(self) -> bool {
        let temperatures = [
            self.cpu_limit_celsius,
            self.gpu_limit_celsius,
            self.cpu_immediate_cutoff_celsius,
            self.gpu_immediate_cutoff_celsius,
            self.automatic_cpu_below_celsius,
            self.automatic_gpu_below_celsius,
        ];
        let automatic_thresholds_valid = match self.end_mode {
            EndMode::Auto => self.automatic_cpu_below_celsius < 55,
            EndMode::Custom => {
                self.automatic_cpu_below_celsius < self.cpu_limit_celsius
                    && self.automatic_gpu_below_celsius < self.gpu_limit_celsius
            }
        };
        temperatures.iter().all(|value| (10..=100).contains(value))
            && self.cpu_immediate_cutoff_celsius >= self.cpu_limit_celsius
            && self.gpu_immediate_cutoff_celsius >= self.gpu_limit_celsius
            && (1..=300).contains(&self.seconds_above_limit)
            && (1..=3600).contains(&self.seconds_below_limits)
            && (!self.automatic_enabled || automatic_thresholds_valid)
    }
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
struct BatteryPolicy {
    #[serde(deserialize_with = "web_i32")]
    charge_limit_percent: i32,
}

fn web_i32<'de, D: Deserializer<'de>>(reader: D) -> Result<i32, D::Error> {
    let value = Value::deserialize(reader)?;
    match value {
        Value::Number(number) => number.as_i64().and_then(|n| i32::try_from(n).ok()),
        Value::String(text) => text.parse().ok(),
        _ => None,
    }
    .ok_or_else(|| de::Error::custom("expected a managed Int32"))
}

fn invalid() -> io::Error {
    io::Error::new(
        io::ErrorKind::InvalidData,
        "invalid fixture battery policy or Fan Stop settings",
    )
}

pub(super) struct ProfileTemplate {
    profile: Value,
    preferences: Value,
}

impl ProfileTemplate {
    pub(super) fn from_managed(docs: &[String; 3]) -> io::Result<Self> {
        let mut profile: Value = serde_json::from_str(&docs[0])?;
        let mut preferences: Value = serde_json::from_str(&docs[1])?;
        take_fields(&mut profile, &mut preferences)?;
        Ok(Self {
            profile,
            preferences,
        })
    }

    pub(super) fn decode(&self, docs: [&[u8]; 3]) -> io::Result<ProfilePolicy> {
        let inspection = inspect_bytes(docs.map(Some));
        let Inspection::CommittedEnvelope {
            schema: schema @ (LEGACY_SCHEMA | CURRENT_SCHEMA),
        } = inspection
        else {
            return Err(io::Error::other(format!(
                "fixture policy refused: {inspection:?}"
            )));
        };
        let mut profile: Value = serde_json::from_slice(docs[0])?;
        let mut preferences: Value = serde_json::from_slice(docs[1])?;
        let (policy, fan_stop) = take_fields(&mut profile, &mut preferences)?;
        if profile != self.profile || preferences != self.preferences {
            return Err(io::Error::other(
                "profile or preferences outside the scoped fixture",
            ));
        }

        if !policy.is_object() {
            return Err(invalid());
        }
        let policy: BatteryPolicy = serde_json::from_value(policy)?;
        if !(50..=99).contains(&policy.charge_limit_percent) {
            return Err(invalid());
        }
        Ok(ProfilePolicy {
            charge_limit: policy.charge_limit_percent as u8,
            fan_stop: FanStop::read(fan_stop, schema)?,
        })
    }

    pub(super) fn encode(&self, policy: ProfilePolicy) -> io::Result<[String; 3]> {
        self.documents(
            CURRENT_SCHEMA,
            policy.charge_limit,
            serde_json::to_value(policy.fan_stop)?,
        )
    }

    // The fixture builder uses the same untouched managed document fields for
    // both schemas. It never receives a user-supplied path or payload.
    pub(super) fn documents(
        &self,
        schema: i32,
        charge_limit: u8,
        fan_stop: Value,
    ) -> io::Result<[String; 3]> {
        let mut profile = self.profile.clone();
        profile["schemaVersion"] = schema.into();
        profile["profiles"]["batteryPolicy"] = json!({ "chargeLimitPercent": charge_limit });
        let mut preferences = self.preferences.clone();
        preferences["schemaVersion"] = schema.into();
        preferences["preferences"]["fanStopSettings"] = fan_stop;
        let profile = serde_json::to_string_pretty(&profile)?;
        let preferences = serde_json::to_string_pretty(&preferences)?;
        let marker = serde_json::to_string_pretty(&json!({
            "schemaVersion": schema,
            "profileSha256": format!("{:X}", Sha256::digest(profile.as_bytes())),
            "preferencesSha256": format!("{:X}", Sha256::digest(preferences.as_bytes())),
        }))?;
        Ok([profile, preferences, marker])
    }
}

fn take_fields(profile: &mut Value, preferences: &mut Value) -> io::Result<(Value, Value)> {
    profile
        .as_object_mut()
        .ok_or_else(invalid)?
        .remove("schemaVersion");
    preferences
        .as_object_mut()
        .ok_or_else(invalid)?
        .remove("schemaVersion");
    let battery = profile
        .get_mut("profiles")
        .and_then(Value::as_object_mut)
        .ok_or_else(invalid)?
        .remove("batteryPolicy")
        .ok_or_else(invalid)?;
    let fan_stop = preferences
        .get_mut("preferences")
        .and_then(Value::as_object_mut)
        .ok_or_else(invalid)?
        .remove("fanStopSettings")
        .ok_or_else(invalid)?;
    Ok((battery, fan_stop))
}
