//! Managed's durable thermal convergence ledger, `thermal-convergence.json`
//! (`ThermalConvergenceStateStore`, `ThermalConvergenceLedgerTransitions`).
//!
//! Native and managed share the file: the same per-user path, schema 1 and
//! members, so a reservation either Service makes is honoured by the other.
//! This module is the pure part: the document model, managed's validation,
//! the strict codec and the four transitions. The host owns the file, its
//! compare-and-swap and every admission check; nothing here grants a write.
//!
//! Documents are managed's `JsonSerializerDefaults.Web` shape (camelCase,
//! enums as numbers, every constructor member present). The three revisions
//! are managed's `CreateThermalRevision`: SHA-256 over the System.Text.Json
//! default (PascalCase, compact) serialization of the thermal intent, as a
//! .NET `Guid` in "D" form. The managed vectors pin both.
use crate::thermal_history::validate_json_members;
use serde::de::{self, Deserializer};
use serde::{Deserialize, Serialize, Serializer};
use serde_json::Value;
use sha2::{Digest, Sha256};
use std::prelude::rust_2024::*;
use std::{fmt, format, vec};

/// The ledger's file name in the user's configuration directory.
pub const LEDGER: &str = "thermal-convergence.json";
/// Managed `CurrentSchemaVersion`.
pub const SCHEMA: i32 = 1;
/// Managed `MaximumAggregateClaims` per destination and policy revision.
const MAXIMUM_AGGREGATE_CLAIMS: i32 = 4;

/// A .NET `Guid`, held in the byte order of its "D" text.
#[derive(Clone, Copy, Debug, Default, Eq, Hash, PartialEq)]
pub struct Guid(pub [u8; 16]);

impl Guid {
    pub const EMPTY: Self = Self([0; 16]);

    /// `Guid.TryParseExact(text, "D")`: 8-4-4-4-12 hex digits, any case.
    pub fn parse(text: &str) -> Option<Self> {
        let bytes = text.as_bytes();
        if bytes.len() != 36 {
            return None;
        }
        let mut value = [0; 16];
        let mut nibble = 0;
        for (index, &byte) in bytes.iter().enumerate() {
            if matches!(index, 8 | 13 | 18 | 23) {
                if byte != b'-' {
                    return None;
                }
                continue;
            }
            let digit = (byte as char).to_digit(16)? as u8;
            value[nibble / 2] |= if nibble % 2 == 0 { digit << 4 } else { digit };
            nibble += 1;
        }
        Some(Self(value))
    }

    /// `new Guid(hash.AsSpan(0, 16))`: .NET reads the first three groups
    /// little-endian.
    fn from_dotnet_bytes(bytes: &[u8]) -> Self {
        let mut value = [0; 16];
        value[..4].copy_from_slice(&[bytes[3], bytes[2], bytes[1], bytes[0]]);
        value[4..6].copy_from_slice(&[bytes[5], bytes[4]]);
        value[6..8].copy_from_slice(&[bytes[7], bytes[6]]);
        value[8..].copy_from_slice(&bytes[8..16]);
        Self(value)
    }
}

impl fmt::Display for Guid {
    /// `ToString("D")`, lowercase.
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        for (index, byte) in self.0.iter().enumerate() {
            if matches!(index, 4 | 6 | 8 | 10) {
                f.write_str("-")?;
            }
            write!(f, "{byte:02x}")?;
        }
        Ok(())
    }
}

impl Serialize for Guid {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        serializer.collect_str(self)
    }
}

impl<'de> Deserialize<'de> for Guid {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        let text = String::deserialize(deserializer)?;
        Self::parse(&text).ok_or_else(|| de::Error::custom("invalid Guid"))
    }
}

/// An integer enum member whose defined values are `0..COUNT`, read and
/// written as managed's number. An undefined number still reads, as
/// System.Text.Json reads it; validation rejects it.
macro_rules! wire_enum {
    ($(#[$meta:meta])* $name:ident, $count:expr) => {
        $(#[$meta])*
        #[derive(Clone, Copy, Debug, Deserialize, Eq, Hash, Ord, PartialEq, PartialOrd, Serialize)]
        #[serde(transparent)]
        pub struct $name(pub i32);

        impl $name {
            pub fn defined(self) -> bool {
                (0..$count).contains(&self.0)
            }
        }
    };
}

wire_enum!(
    /// Managed `PowerSource`; `ExternalUnknown` (3) is defined but is never
    /// a destination.
    Power,
    4
);
wire_enum!(
    /// Managed `ThermalComponent`: Performance (0), Fans (1).
    Component,
    2
);
wire_enum!(
    /// Managed `ThermalTransitionDisposition`: Confirmed, Acknowledged,
    /// External, ReconciliationUnavailable, Blocked, Failed.
    Disposition,
    6
);

impl Power {
    pub const PLUGGED_IN: Self = Self(0);
    pub const BATTERY: Self = Self(1);
    pub const USB_C: Self = Self(2);

    /// Managed `IsSupportedDestination`.
    pub fn is_destination(self) -> bool {
        (0..=2).contains(&self.0)
    }
}

impl Component {
    pub const PERFORMANCE: Self = Self(0);
    pub const FANS: Self = Self(1);
}

impl Disposition {
    pub const CONFIRMED: Self = Self(0);
    pub const ACKNOWLEDGED: Self = Self(1);
    pub const EXTERNAL: Self = Self(2);
    pub const RECONCILIATION_UNAVAILABLE: Self = Self(3);
    pub const BLOCKED: Self = Self(4);
    pub const FAILED: Self = Self(5);
}

/// Managed `ThermalConvergenceContradiction` flags.
#[derive(Clone, Copy, Debug, Default, Deserialize, Eq, Hash, PartialEq, Serialize)]
#[serde(transparent)]
pub struct Contradiction(pub i32);

impl Contradiction {
    pub const NONE: Self = Self(0);
    pub const PERFORMANCE: Self = Self(1);
    pub const FAN_TOPOLOGY: Self = Self(2);

    pub fn has(self, flag: Self) -> bool {
        self.0 & flag.0 == flag.0
    }

    /// Managed `IsContradiction`: no flag outside the two components.
    fn known(self) -> bool {
        self.0 & !3 == 0
    }

    /// Managed `ThermalComponentsFor`, in component order.
    pub fn components(self) -> Vec<Component> {
        let mut components = Vec::with_capacity(2);
        if self.has(Self::PERFORMANCE) {
            components.push(Component::PERFORMANCE);
        }
        if self.has(Self::FAN_TOPOLOGY) {
            components.push(Component::FANS);
        }
        components
    }

    /// Managed `ComponentsToContradiction`.
    pub fn of(components: &[Component]) -> Self {
        Self(components.iter().fold(0, |flags, component| {
            flags
                | match *component {
                    Component::PERFORMANCE => 1,
                    Component::FANS => 2,
                    _ => 0,
                }
        }))
    }
}

/// Managed `ResolvedSetting<T>`; the source is a `SettingValueSource`.
#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Resolved {
    pub value: i32,
    pub source: i32,
}

/// Managed `GpuClockOffsetSettings`.
#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct GpuOffsets {
    pub core_megahertz: i32,
    pub memory_megahertz: i32,
}

impl GpuOffsets {
    pub const DEFAULT: Self = Self {
        core_megahertz: 0,
        memory_megahertz: 0,
    };

    fn well_formed(self) -> bool {
        let bound = -10_000..=10_000;
        bound.contains(&self.core_megahertz) && bound.contains(&self.memory_megahertz)
    }
}

/// Managed `CustomPerformanceSettings`.
#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CustomPerformance {
    pub cpu: i32,
    pub gpu: i32,
    #[serde(default)]
    pub maximum_fans: bool,
    #[serde(default)]
    pub gpu_clock_offsets: Option<GpuOffsets>,
}

impl CustomPerformance {
    /// Managed `CustomPerformanceSettings.IsWellFormed`.
    fn well_formed(self) -> bool {
        (0..=3).contains(&self.cpu)
            && (0..=2).contains(&self.gpu)
            && self.gpu_clock_offsets.is_none_or(GpuOffsets::well_formed)
    }
}

/// Managed `GpuClockTuningSettings`. `EffectiveOffsets` is a computed
/// member managed writes and ignores when it reads.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct GpuClockTuning {
    pub enabled: bool,
    pub offsets: GpuOffsets,
}

impl GpuClockTuning {
    fn effective(self) -> GpuOffsets {
        if self.enabled {
            self.offsets
        } else {
            GpuOffsets::DEFAULT
        }
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct GpuClockTuningWire {
    enabled: bool,
    offsets: GpuOffsets,
    effective_offsets: GpuOffsets,
}

impl Serialize for GpuClockTuning {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        GpuClockTuningWire {
            enabled: self.enabled,
            offsets: self.offsets,
            effective_offsets: self.effective(),
        }
        .serialize(serializer)
    }
}

impl<'de> Deserialize<'de> for GpuClockTuning {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        // The computed member is accepted and ignored, as managed ignores
        // a get-only property.
        #[derive(Deserialize)]
        #[serde(rename_all = "camelCase", deny_unknown_fields)]
        struct Read {
            enabled: bool,
            offsets: GpuOffsets,
            #[serde(default, rename = "effectiveOffsets")]
            _effective: Option<serde::de::IgnoredAny>,
        }
        let read = Read::deserialize(deserializer)?;
        Ok(Self {
            enabled: read.enabled,
            offsets: read.offsets,
        })
    }
}

/// Managed `FanCurvePoint`.
#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CurvePoint {
    pub temperature_celsius: i32,
    pub speed_rpm: i32,
}

/// Managed `ManualFanCurve`.
#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ManualCurve {
    pub cpu: Vec<CurvePoint>,
    pub gpu: Vec<CurvePoint>,
    pub use_higher_temperature_for_both: bool,
}

/// Managed `FanControlSettings`.
#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct FanControl {
    pub mode: i32,
    pub fixed_speed_rpm: i32,
    pub manual_curve: ManualCurve,
}

/// Managed `FanMode.Maximum` and `FanControlSettings.MaximumSpeedRpm`.
const MAXIMUM: i32 = 3;
const MAXIMUM_SPEED: i32 = 5400;

impl FanControl {
    /// Managed `FanControlSettings.FromLegacy` over `Default`'s 3700 RPM
    /// speed and curve.
    pub fn from_legacy(mode: i32) -> Self {
        Self {
            mode,
            fixed_speed_rpm: if mode == MAXIMUM { MAXIMUM_SPEED } else { 3700 },
            manual_curve: default_curve(),
        }
    }
}

impl<'de> Deserialize<'de> for FanControl {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        #[derive(Deserialize)]
        #[serde(rename_all = "camelCase", deny_unknown_fields)]
        struct Read {
            mode: i32,
            fixed_speed_rpm: i32,
            manual_curve: ManualCurve,
        }
        let read = Read::deserialize(deserializer)?;
        // Managed's constructor validates both and replaces a Maximum speed.
        if !(0..=4).contains(&read.mode)
            || !valid_speed(read.fixed_speed_rpm)
            || !valid_curve(&read.manual_curve.cpu)
            || !valid_curve(&read.manual_curve.gpu)
        {
            return Err(de::Error::custom("invalid fan control"));
        }
        Ok(Self {
            mode: read.mode,
            fixed_speed_rpm: if read.mode == MAXIMUM {
                MAXIMUM_SPEED
            } else {
                read.fixed_speed_rpm
            },
            manual_curve: read.manual_curve,
        })
    }
}

/// Managed `FanControlSettings.ValidateSpeed`.
fn valid_speed(speed: i32) -> bool {
    (2000..=5500).contains(&speed) && speed % 100 == 0
}

/// Managed `ManualFanCurve.ValidateCurve` with `FanCurvePoint`'s bounds.
fn valid_curve(points: &[CurvePoint]) -> bool {
    (2..=10).contains(&points.len())
        && points.iter().all(|point| {
            (20..=100).contains(&point.temperature_celsius) && valid_speed(point.speed_rpm)
        })
        && points.windows(2).all(|pair| {
            pair[1].temperature_celsius > pair[0].temperature_celsius
                && pair[1].speed_rpm >= pair[0].speed_rpm
        })
}

/// Managed `ManualFanCurve.Default`.
fn default_curve() -> ManualCurve {
    let points = |pairs: [(i32, i32); 6]| {
        pairs
            .into_iter()
            .map(|(temperature_celsius, speed_rpm)| CurvePoint {
                temperature_celsius,
                speed_rpm,
            })
            .collect()
    };
    ManualCurve {
        cpu: points([
            (40, 2000),
            (50, 2100),
            (60, 2600),
            (70, 3400),
            (80, 4400),
            (90, 5400),
        ]),
        gpu: points([
            (35, 2000),
            (45, 2100),
            (55, 2700),
            (65, 3600),
            (75, 4500),
            (85, 5400),
        ]),
        use_higher_temperature_for_both: true,
    }
}

/// Managed `DesiredDeviceState`: the effective intent snapshot. Lighting
/// and battery are carried as managed wrote them; native neither reads nor
/// changes them here, and checks only that each is an object or null
/// (managed admits null, and rejects an object its records cannot bind).
#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Intent {
    pub performance: Resolved,
    pub fans: Resolved,
    #[serde(
        deserialize_with = "object_or_null",
        serialize_with = "crate::kind_first::serialize"
    )]
    pub lighting: Value,
    #[serde(deserialize_with = "object_or_null")]
    pub battery: Value,
    #[serde(default)]
    pub custom_performance: Option<CustomPerformance>,
    #[serde(default)]
    pub gpu_clock_tuning: Option<GpuClockTuning>,
    #[serde(default)]
    pub fan_control: Option<FanControl>,
}

impl Intent {
    /// Managed `NormalizeThermalIntent`: a missing fan control is the
    /// legacy one for the resolved mode.
    pub fn normalized(mut self) -> Self {
        if self.fan_control.is_none() {
            self.fan_control = Some(FanControl::from_legacy(self.fans.value));
        }
        self
    }

    /// Managed `ValidateDesired`.
    fn valid(&self) -> bool {
        (0..=9).contains(&self.performance.value)
            && (0..=4).contains(&self.performance.source)
            && (0..=4).contains(&self.fans.value)
            && (0..=4).contains(&self.fans.source)
            && self
                .custom_performance
                .is_none_or(CustomPerformance::well_formed)
            && self
                .gpu_clock_tuning
                .is_none_or(|tuning| tuning.offsets.well_formed())
            && self
                .fan_control
                .as_ref()
                .is_some_and(|control| control.mode == self.fans.value)
    }
}

/// Managed `ConfirmedDeviceState`, the pre-write observation. Native fills
/// only the thermal members; the others are carried as written.
#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Observation {
    #[serde(deserialize_with = "required")]
    pub performance: Option<i32>,
    #[serde(deserialize_with = "required")]
    pub fans: Option<i32>,
    #[serde(deserialize_with = "required")]
    pub battery: Option<Value>,
    #[serde(
        deserialize_with = "required",
        serialize_with = "crate::kind_first::serialize_option"
    )]
    pub lighting: Option<Value>,
    /// Managed's initializer makes a missing member false; null is unknown.
    #[serde(default = "known_off")]
    pub hyper_boost_active: Option<bool>,
    #[serde(default)]
    pub custom_performance: Option<CustomPerformance>,
    #[serde(default)]
    pub gpu_clock_offsets: Option<Value>,
    #[serde(default)]
    pub logo_lighting: Option<i32>,
    #[serde(
        default,
        skip_serializing_if = "Option::is_none",
        serialize_with = "crate::kind_first::serialize_option"
    )]
    pub independent_lighting: Option<Value>,
}

fn known_off() -> Option<bool> {
    Some(false)
}

impl Observation {
    /// The thermal members only, as native's reading reports them.
    pub fn thermal(
        performance: Option<i32>,
        fans: Option<i32>,
        custom: Option<CustomPerformance>,
        hyper_boost_active: Option<bool>,
    ) -> Self {
        Self {
            performance,
            fans,
            battery: None,
            lighting: None,
            hyper_boost_active,
            custom_performance: custom,
            gpu_clock_offsets: None,
            logo_lighting: None,
            independent_lighting: None,
        }
    }

    /// Managed `ValidateObserved`. Managed checks the GPU clock capability
    /// with `IsWellFormed`; native never writes one and checks its ranges.
    fn valid(&self) -> bool {
        self.performance
            .is_none_or(|value| (0..=9).contains(&value))
            && self.fans.is_none_or(|value| (0..=4).contains(&value))
            && self
                .custom_performance
                .is_none_or(CustomPerformance::well_formed)
            && self.gpu_clock_offsets.as_ref().is_none_or(gpu_capabilities)
    }
}

/// Managed `GpuClockOffsetCapabilities.IsWellFormed` over its JSON.
fn gpu_capabilities(value: &Value) -> bool {
    let range = |member: &str| {
        let bound = |name: &str| value[member][name].as_i64();
        match (
            bound("minimumMegahertz"),
            bound("maximumMegahertz"),
            bound("currentMegahertz"),
        ) {
            (Some(minimum), Some(maximum), Some(current)) => {
                minimum >= -10_000
                    && maximum <= 10_000
                    && minimum <= maximum
                    && (minimum..=maximum).contains(&current)
            }
            _ => false,
        }
    };
    value["deviceName"]
        .as_str()
        .is_some_and(|name| !name.trim().is_empty() && name.encode_utf16().count() <= 128)
        && range("core")
        && range("memory")
}

/// Managed `ThermalConvergenceInFlight`.
#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct InFlight {
    pub transition_attempt_id: Guid,
    pub declared_components: Vec<Component>,
    pub pre_write_observation: Observation,
    pub desired_target: Intent,
    pub reconciliation_required: bool,
}

/// Managed `ThermalConvergenceTombstone`.
#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Tombstone {
    pub destination: Power,
    pub contradiction: Contradiction,
    pub affected_components: Vec<Component>,
    #[serde(deserialize_with = "required")]
    pub performance_desired_revision: Option<String>,
    #[serde(deserialize_with = "required")]
    pub fan_desired_revision: Option<String>,
    pub policy_revision: String,
    pub terminal_disposition: Disposition,
}

/// Managed `ThermalConvergenceBudget`.
#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Budget {
    pub destination: Power,
    pub component: Component,
    pub desired_revision: String,
    pub policy_revision: String,
    pub consumed_claims: i32,
    pub maximum_claims: i32,
}

/// Managed `ThermalConvergenceLedgerState`.
#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct State {
    pub active_user_sid: String,
    pub device_authority_epoch: Guid,
    #[serde(deserialize_with = "required")]
    pub device_fingerprint: Option<String>,
    pub intent_generation: i64,
    #[serde(deserialize_with = "required")]
    pub effective_intent: Option<Intent>,
    #[serde(deserialize_with = "required")]
    pub performance_desired_revision: Option<String>,
    #[serde(deserialize_with = "required")]
    pub fan_desired_revision: Option<String>,
    #[serde(deserialize_with = "required")]
    pub policy_revision: Option<String>,
    #[serde(deserialize_with = "required")]
    pub settled_destination: Option<Power>,
    pub contradiction: Contradiction,
    #[serde(deserialize_with = "required")]
    pub in_flight: Option<InFlight>,
    pub tombstones: Vec<Tombstone>,
    #[serde(deserialize_with = "required")]
    pub terminal_disposition: Option<Disposition>,
    pub terminal_affected_components: Vec<Component>,
    pub budgets: Vec<Budget>,
}

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Document {
    schema_version: i32,
    state: State,
}

/// A present member that may be null: managed requires every constructor
/// member, and serde would otherwise default a missing `Option`.
fn required<'de, D: Deserializer<'de>, T: Deserialize<'de>>(
    deserializer: D,
) -> Result<Option<T>, D::Error> {
    Option::deserialize(deserializer)
}

fn object_or_null<'de, D: Deserializer<'de>>(deserializer: D) -> Result<Value, D::Error> {
    let value = Value::deserialize(deserializer)?;
    if value.is_object() || value.is_null() {
        Ok(value)
    } else {
        Err(de::Error::custom("expected an object or null"))
    }
}

/// The three revisions `RefreshIntent` derives.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Revisions {
    pub performance: String,
    pub fans: String,
    pub policy: String,
}

impl Revisions {
    /// Managed `CreateThermalRevision` for a normalized intent and the saved
    /// policy and disclosure.
    pub fn of(intent: &Intent, policy: i32, disclosure: i32) -> Self {
        Self {
            performance: revision(&performance_input(intent)),
            fans: revision(&fan_input(intent)),
            policy: revision(&format!(
                "{{\"ThermalControlPolicy\":{policy},\"ThermalPolicyUpgradeDisclosure\":{disclosure}}}"
            )),
        }
    }

    fn of_component(&self, component: Component) -> &str {
        if component == Component::PERFORMANCE {
            &self.performance
        } else {
            &self.fans
        }
    }
}

fn revision(json: &str) -> String {
    Guid::from_dotnet_bytes(&Sha256::digest(json.as_bytes())).to_string()
}

fn resolved_input(value: Resolved) -> String {
    format!("{{\"Value\":{},\"Source\":{}}}", value.value, value.source)
}

fn offsets_input(offsets: GpuOffsets) -> String {
    format!(
        "{{\"CoreMegahertz\":{},\"MemoryMegahertz\":{}}}",
        offsets.core_megahertz, offsets.memory_megahertz
    )
}

/// `new { desired.Performance, desired.CustomPerformance, desired.GpuClockTuning }`.
fn performance_input(intent: &Intent) -> String {
    let custom = intent.custom_performance.map_or_else(
        || "null".into(),
        |custom| {
            format!(
                "{{\"Cpu\":{},\"Gpu\":{},\"MaximumFans\":{},\"GpuClockOffsets\":{}}}",
                custom.cpu,
                custom.gpu,
                custom.maximum_fans,
                custom
                    .gpu_clock_offsets
                    .map_or_else(|| "null".into(), offsets_input)
            )
        },
    );
    let tuning = intent.gpu_clock_tuning.map_or_else(
        || "null".into(),
        |tuning| {
            format!(
                "{{\"Enabled\":{},\"Offsets\":{},\"EffectiveOffsets\":{}}}",
                tuning.enabled,
                offsets_input(tuning.offsets),
                offsets_input(tuning.effective())
            )
        },
    );
    format!(
        "{{\"Performance\":{},\"CustomPerformance\":{custom},\"GpuClockTuning\":{tuning}}}",
        resolved_input(intent.performance)
    )
}

/// `new { desired.Fans, desired.FanControl }`.
fn fan_input(intent: &Intent) -> String {
    let points = |points: &[CurvePoint]| {
        let items: Vec<String> = points
            .iter()
            .map(|point| {
                format!(
                    "{{\"TemperatureCelsius\":{},\"SpeedRpm\":{}}}",
                    point.temperature_celsius, point.speed_rpm
                )
            })
            .collect();
        format!("[{}]", items.join(","))
    };
    let control = intent.fan_control.as_ref().map_or_else(
        || "null".into(),
        |control| {
            format!(
                "{{\"Mode\":{},\"FixedSpeedRpm\":{},\"ManualCurve\":{{\"Cpu\":{},\"Gpu\":{},\"UseHigherTemperatureForBoth\":{}}}}}",
                control.mode,
                control.fixed_speed_rpm,
                points(&control.manual_curve.cpu),
                points(&control.manual_curve.gpu),
                control.manual_curve.use_higher_temperature_for_both
            )
        },
    );
    format!(
        "{{\"Fans\":{},\"FanControl\":{control}}}",
        resolved_input(intent.fans)
    )
}

/// Managed `RefreshIntent`'s inputs.
#[derive(Clone, Debug)]
pub struct Refresh<'a> {
    pub desired: Intent,
    pub policy: i32,
    pub disclosure: i32,
    /// The status's power source when it is a destination.
    pub destination: Option<Power>,
    /// `OpenBlade:{model}`.
    pub fingerprint: &'a str,
    pub device_invalidated: bool,
    pub policy_changed: bool,
}

/// Managed `ThermalConvergenceCandidate`'s members `Reserve` reads.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Candidate {
    pub destination: Power,
    pub contradiction: Contradiction,
    pub performance_revision: String,
    pub fan_revision: String,
}

impl State {
    /// Managed `ThermalConvergenceLedgerState.Empty`.
    pub fn empty(sid: &str) -> Self {
        Self {
            active_user_sid: sid.into(),
            device_authority_epoch: Guid::EMPTY,
            device_fingerprint: None,
            intent_generation: 0,
            effective_intent: None,
            performance_desired_revision: None,
            fan_desired_revision: None,
            policy_revision: None,
            settled_destination: None,
            contradiction: Contradiction::NONE,
            in_flight: None,
            tombstones: vec![],
            terminal_disposition: None,
            terminal_affected_components: vec![],
            budgets: vec![],
        }
    }

    /// Managed `IsEmpty`: everything but the SID is unset.
    pub fn is_empty(&self) -> bool {
        Self {
            active_user_sid: self.active_user_sid.clone(),
            ..Self::empty("")
        } == *self
    }

    /// Managed `RequiresReadOnlyReconciliation`.
    pub fn requires_read_only_reconciliation(&self) -> bool {
        self.in_flight
            .as_ref()
            .is_some_and(|in_flight| in_flight.reconciliation_required)
    }

    /// The revisions this state holds, once it is not empty.
    pub fn revisions(&self) -> Option<Revisions> {
        Some(Revisions {
            performance: self.performance_desired_revision.clone()?,
            fans: self.fan_desired_revision.clone()?,
            policy: self.policy_revision.clone()?,
        })
    }

    /// Managed `ThermalConvergenceLedgerTransitions.RefreshIntent`. A state
    /// with a reservation is returned unchanged. `new_epoch` is called only
    /// when the device authority changes.
    pub fn refresh_intent(&self, refresh: Refresh<'_>, new_epoch: impl FnOnce() -> Guid) -> Self {
        let desired = refresh.desired.normalized();
        let revisions = Revisions::of(&desired, refresh.policy, refresh.disclosure);
        if self.in_flight.is_some() {
            return self.clone();
        }
        let intent_changed = refresh.policy_changed
            || self.performance_desired_revision.as_deref() != Some(&revisions.performance)
            || self.fan_desired_revision.as_deref() != Some(&revisions.fans)
            || self.policy_revision.as_deref() != Some(&revisions.policy);
        let device_changed = self.device_authority_epoch == Guid::EMPTY
            || self.device_fingerprint.as_deref() != Some(refresh.fingerprint)
            || refresh.device_invalidated;
        let generation = if self.is_empty() || intent_changed || device_changed {
            self.intent_generation + 1
        } else {
            self.intent_generation
        };
        let current = refresh.destination;
        let (tombstones, budgets) = if refresh.policy_changed {
            (vec![], vec![])
        } else {
            let tombstones = self
                .tombstones
                .iter()
                .filter(|tombstone| {
                    Some(tombstone.destination) != current
                        || tombstone.policy_revision == revisions.policy
                            && (!tombstone.contradiction.has(Contradiction::PERFORMANCE)
                                || tombstone.performance_desired_revision.as_deref()
                                    == Some(&revisions.performance))
                            && (!tombstone.contradiction.has(Contradiction::FAN_TOPOLOGY)
                                || tombstone.fan_desired_revision.as_deref()
                                    == Some(&revisions.fans))
                })
                .cloned()
                .collect();
            let budgets = self
                .budgets
                .iter()
                .filter(|budget| {
                    Some(budget.destination) != current
                        || budget.policy_revision == revisions.policy
                            && budget.desired_revision == revisions.of_component(budget.component)
                })
                .cloned()
                .collect();
            (tombstones, budgets)
        };
        Self {
            device_authority_epoch: if device_changed {
                new_epoch()
            } else {
                self.device_authority_epoch
            },
            device_fingerprint: Some(refresh.fingerprint.into()),
            intent_generation: generation,
            effective_intent: Some(desired),
            performance_desired_revision: Some(revisions.performance),
            fan_desired_revision: Some(revisions.fans),
            policy_revision: Some(revisions.policy),
            settled_destination: current.or(self.settled_destination),
            contradiction: Contradiction::NONE,
            tombstones,
            budgets,
            terminal_disposition: None,
            terminal_affected_components: vec![],
            ..self.clone()
        }
    }

    /// Managed `Reserve`: claim one budget per declared component and
    /// record the reservation. The caller persists it before any setter.
    pub fn reserve(
        &self,
        candidate: &Candidate,
        attempt: Guid,
        fresh: Observation,
        desired: Intent,
        affected: &[Component],
    ) -> Self {
        let policy = self.policy_revision.clone().unwrap_or_default();
        let mut budgets = self.budgets.clone();
        for &component in affected {
            let revision = if component == Component::PERFORMANCE {
                &candidate.performance_revision
            } else {
                &candidate.fan_revision
            };
            match budgets.iter_mut().find(|budget| {
                budget.destination == candidate.destination
                    && budget.component == component
                    && budget.desired_revision == *revision
                    && budget.policy_revision == policy
            }) {
                Some(budget) => budget.consumed_claims += 1,
                None => budgets.push(Budget {
                    destination: candidate.destination,
                    component,
                    desired_revision: revision.clone(),
                    policy_revision: policy.clone(),
                    consumed_claims: 1,
                    maximum_claims: 1,
                }),
            }
        }
        // `OrderBy(Destination).ThenBy(Component)` is stable.
        budgets.sort_by_key(|budget| (budget.destination, budget.component));
        Self {
            settled_destination: Some(candidate.destination),
            contradiction: candidate.contradiction,
            in_flight: Some(InFlight {
                transition_attempt_id: attempt,
                declared_components: affected.to_vec(),
                pre_write_observation: fresh,
                desired_target: desired,
                reconciliation_required: true,
            }),
            budgets,
            terminal_disposition: None,
            terminal_affected_components: vec![],
            ..self.clone()
        }
    }

    /// Managed `Complete`: retire the reservation with a tombstone for any
    /// disposition. Managed removes an existing tombstone only when it is
    /// the same list instance, which never holds for one it read; native
    /// removes an equal one, so a repeated key stays persistable.
    pub fn complete(
        &self,
        destination: Power,
        contradiction: Contradiction,
        affected: &[Component],
        disposition: Disposition,
    ) -> Self {
        let tombstone = Tombstone {
            destination,
            contradiction,
            affected_components: affected.to_vec(),
            performance_desired_revision: contradiction
                .has(Contradiction::PERFORMANCE)
                .then(|| self.performance_desired_revision.clone())
                .flatten(),
            fan_desired_revision: contradiction
                .has(Contradiction::FAN_TOPOLOGY)
                .then(|| self.fan_desired_revision.clone())
                .flatten(),
            policy_revision: self.policy_revision.clone().unwrap_or_default(),
            terminal_disposition: disposition,
        };
        let mut tombstones: Vec<Tombstone> = self
            .tombstones
            .iter()
            .filter(|existing| **existing != tombstone)
            .cloned()
            .collect();
        tombstones.push(tombstone);
        Self {
            in_flight: None,
            tombstones,
            terminal_disposition: Some(disposition),
            terminal_affected_components: affected.to_vec(),
            ..self.clone()
        }
    }

    /// Managed `IsExpectedReservation`: `self` is the ledger read back and
    /// `expected` the reservation this attempt persisted.
    pub fn is_expected_reservation(&self, expected: &Self) -> bool {
        let (Some(actual_flight), Some(expected_flight)) = (&self.in_flight, &expected.in_flight)
        else {
            return false;
        };
        actual_flight.reconciliation_required
            && actual_flight.transition_attempt_id == expected_flight.transition_attempt_id
            && actual_flight.declared_components == expected_flight.declared_components
            && actual_flight.pre_write_observation == expected_flight.pre_write_observation
            && actual_flight.desired_target == expected_flight.desired_target
            && self
                .active_user_sid
                .eq_ignore_ascii_case(&expected.active_user_sid)
            && self.device_authority_epoch == expected.device_authority_epoch
            && self.intent_generation == expected.intent_generation
            && self.device_fingerprint == expected.device_fingerprint
            && self.effective_intent == expected.effective_intent
            && self.performance_desired_revision == expected.performance_desired_revision
            && self.fan_desired_revision == expected.fan_desired_revision
            && self.policy_revision == expected.policy_revision
            && self.settled_destination == expected.settled_destination
            && self.contradiction == expected.contradiction
    }

    /// The budget for `component` at the candidate's key, if any.
    pub fn budget(&self, candidate: &Candidate, component: Component) -> Option<&Budget> {
        let revision = if component == Component::PERFORMANCE {
            &candidate.performance_revision
        } else {
            &candidate.fan_revision
        };
        self.budgets.iter().find(|budget| {
            budget.destination == candidate.destination
                && budget.component == component
                && budget.desired_revision == *revision
                && Some(&budget.policy_revision) == self.policy_revision.as_ref()
        })
    }

    /// Managed's observer `tombstoned`: a tombstone at the candidate's
    /// destination, contradiction and policy, carrying the revision of each
    /// component the contradiction names.
    pub fn tombstoned(&self, candidate: &Candidate) -> bool {
        let contradiction = candidate.contradiction;
        self.tombstones.iter().any(|tombstone| {
            tombstone.destination == candidate.destination
                && tombstone.contradiction == contradiction
                && Some(&tombstone.policy_revision) == self.policy_revision.as_ref()
                && (!contradiction.has(Contradiction::PERFORMANCE)
                    || tombstone.performance_desired_revision.as_ref()
                        == Some(&candidate.performance_revision))
                && (!contradiction.has(Contradiction::FAN_TOPOLOGY)
                    || tombstone.fan_desired_revision.as_ref() == Some(&candidate.fan_revision))
        })
    }

    /// Managed's observer `budgetExhausted`: an affected component whose
    /// budget at the candidate's key has no claim left.
    pub fn budget_exhausted(&self, candidate: &Candidate) -> bool {
        candidate
            .contradiction
            .components()
            .into_iter()
            .any(|component| {
                self.budget(candidate, component)
                    .is_some_and(|budget| budget.consumed_claims >= budget.maximum_claims)
            })
    }

    /// Managed `ReapplyChangedSettingsAsync`'s rearm: every tombstone,
    /// budget and terminal record retired, so the next observation may
    /// nominate again.
    pub fn rearmed(&self) -> Self {
        Self {
            tombstones: vec![],
            budgets: vec![],
            terminal_disposition: None,
            terminal_affected_components: vec![],
            ..self.clone()
        }
    }

    /// Whether a rearm has anything to retire.
    pub fn suppresses(&self) -> bool {
        !self.tombstones.is_empty() || !self.budgets.is_empty()
    }

    /// Managed `ValidateState`.
    pub fn validate(&self, sid: &str, persisted: bool) -> Result<(), Invalid> {
        if !self.active_user_sid.eq_ignore_ascii_case(sid) {
            return Err(Invalid(
                "The thermal convergence record belongs to a different user.",
            ));
        }
        if self.is_empty() {
            return if persisted {
                Err(Invalid(
                    "An empty thermal convergence record must not be persisted.",
                ))
            } else {
                Ok(())
            };
        }
        let complete = self.device_authority_epoch != Guid::EMPTY
            && self
                .device_fingerprint
                .as_deref()
                .is_some_and(bounded_identifier)
            && self.intent_generation > 0
            && self.effective_intent.is_some()
            && is_revision(self.performance_desired_revision.as_deref())
            && is_revision(self.fan_desired_revision.as_deref())
            && is_revision(self.policy_revision.as_deref())
            && self.settled_destination.is_some_and(Power::is_destination)
            && self.contradiction.known();
        if !complete {
            return Err(Invalid(
                "The thermal convergence record is incomplete or invalid.",
            ));
        }
        let intent = self
            .effective_intent
            .as_ref()
            .ok_or(Invalid("missing intent"))?;
        if !intent.valid() {
            return Err(Invalid("The effective thermal intent snapshot is invalid."));
        }
        if let Some(in_flight) = &self.in_flight {
            self.validate_in_flight(in_flight)?;
        }
        components(&self.terminal_affected_components, true)?;
        if self
            .terminal_disposition
            .is_some_and(|terminal| !terminal.defined())
        {
            return Err(Invalid(
                "The thermal convergence terminal disposition is undefined.",
            ));
        }
        if self.terminal_disposition.is_none() != self.terminal_affected_components.is_empty() {
            return Err(Invalid(
                "Thermal terminal evidence must include both disposition and components.",
            ));
        }
        validate_tombstones(&self.tombstones)?;
        validate_budgets(&self.budgets)
    }

    fn validate_in_flight(&self, in_flight: &InFlight) -> Result<(), Invalid> {
        if in_flight.transition_attempt_id == Guid::EMPTY || !in_flight.reconciliation_required {
            return Err(Invalid(
                "The in-flight thermal convergence reservation is invalid.",
            ));
        }
        components(&in_flight.declared_components, false)?;
        if !in_flight.pre_write_observation.valid() {
            return Err(Invalid("The pre-write thermal observation is invalid."));
        }
        if !in_flight.desired_target.valid() {
            return Err(Invalid("The effective thermal intent snapshot is invalid."));
        }
        if Some(&in_flight.desired_target) != self.effective_intent.as_ref() {
            return Err(Invalid(
                "The in-flight thermal target does not match the reserved intent snapshot.",
            ));
        }
        let declared = Contradiction::of(&in_flight.declared_components);
        if declared.0 & self.contradiction.0 != self.contradiction.0 {
            return Err(Invalid(
                "The in-flight reservation did not declare every contradicted component.",
            ));
        }
        Ok(())
    }
}

/// Why managed's `ValidateState` rejects a state.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Invalid(pub &'static str);

fn validate_tombstones(tombstones: &[Tombstone]) -> Result<(), Invalid> {
    let mut keys = Vec::with_capacity(tombstones.len());
    for tombstone in tombstones {
        if !tombstone.destination.is_destination()
            || tombstone.contradiction == Contradiction::NONE
            || !tombstone.contradiction.known()
            || !is_revision(Some(&tombstone.policy_revision))
            || !tombstone.terminal_disposition.defined()
        {
            return Err(Invalid("A thermal convergence tombstone is invalid."));
        }
        components(&tombstone.affected_components, false)?;
        if Contradiction::of(&tombstone.affected_components) != tombstone.contradiction {
            return Err(Invalid(
                "A thermal convergence tombstone has mismatched affected components.",
            ));
        }
        if tombstone.contradiction.has(Contradiction::PERFORMANCE)
            != is_revision(tombstone.performance_desired_revision.as_deref())
            || tombstone.contradiction.has(Contradiction::FAN_TOPOLOGY)
                != is_revision(tombstone.fan_desired_revision.as_deref())
        {
            return Err(Invalid(
                "A thermal convergence tombstone has mismatched component revisions.",
            ));
        }
        let key = (
            tombstone.destination,
            tombstone.contradiction,
            &tombstone.performance_desired_revision,
            &tombstone.fan_desired_revision,
            &tombstone.policy_revision,
        );
        if keys.contains(&key) {
            return Err(Invalid("A thermal convergence tombstone is duplicated."));
        }
        keys.push(key);
    }
    Ok(())
}

fn validate_budgets(budgets: &[Budget]) -> Result<(), Invalid> {
    let mut keys = Vec::with_capacity(budgets.len());
    for budget in budgets {
        if !budget.destination.is_destination()
            || !budget.component.defined()
            || !is_revision(Some(&budget.desired_revision))
            || !is_revision(Some(&budget.policy_revision))
            || budget.maximum_claims != 1
            || budget.consumed_claims < 0
            || budget.consumed_claims > budget.maximum_claims
        {
            return Err(Invalid("A thermal convergence budget is invalid."));
        }
        let key = (
            budget.destination,
            budget.component,
            &budget.desired_revision,
            &budget.policy_revision,
        );
        if keys.contains(&key) {
            return Err(Invalid("A thermal convergence budget is duplicated."));
        }
        keys.push(key);
    }
    for budget in budgets {
        let claims: i32 = budgets
            .iter()
            .filter(|other| {
                other.destination == budget.destination
                    && other.policy_revision == budget.policy_revision
            })
            .map(|other| other.consumed_claims)
            .sum();
        if claims > MAXIMUM_AGGREGATE_CLAIMS {
            return Err(Invalid(
                "A thermal convergence revision exceeded its aggregate claim cap.",
            ));
        }
    }
    Ok(())
}

/// Managed `ValidateComponents`: unique, defined and sorted.
fn components(components: &[Component], allow_empty: bool) -> Result<(), Invalid> {
    if (!allow_empty && components.is_empty())
        || components.iter().any(|component| !component.defined())
        || components.windows(2).any(|pair| pair[0] >= pair[1])
    {
        return Err(Invalid(
            "Thermal components must be unique, defined, and sorted.",
        ));
    }
    Ok(())
}

/// Managed `IsRevision`: a non-empty "D" Guid.
fn is_revision(value: Option<&str>) -> bool {
    value
        .and_then(Guid::parse)
        .is_some_and(|guid| guid != Guid::EMPTY)
}

/// Managed `IsBoundedIdentifier(value, 256)`.
fn bounded_identifier(value: &str) -> bool {
    !value.trim().is_empty()
        && value.encode_utf16().count() <= 256
        && !value.chars().any(char::is_control)
}

/// Managed `ConfigurationQuarantineReason` for a ledger load.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Quarantine {
    FutureSchema = 1,
    InvalidDocument = 2,
}

/// Managed `ThermalConvergenceLoadResult`.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum Load {
    /// The file's state and its SHA-256 (managed's uppercase hex); `None`
    /// for an absent file, whose state is empty.
    Admitted {
        state: Box<State>,
        sha256: Option<String>,
    },
    Quarantined(Quarantine),
}

/// Managed `ThermalConvergenceStateStore.LoadAsync` over the file's bytes
/// (`None` when absent) for the user `sid`.
pub fn decode(bytes: Option<&[u8]>, sid: &str) -> Load {
    let Some(bytes) = bytes else {
        return Load::Admitted {
            state: Box::new(State::empty(sid)),
            sha256: None,
        };
    };
    let quarantine = || {
        Load::Quarantined(if future_schema(bytes) {
            Quarantine::FutureSchema
        } else {
            Quarantine::InvalidDocument
        })
    };
    if validate_json_members(bytes).is_err() {
        return quarantine();
    }
    match serde_json::from_slice::<Document>(bytes) {
        Ok(document) if document.schema_version == SCHEMA => {
            if document.state.validate(sid, true).is_err() {
                return quarantine();
            }
            Load::Admitted {
                state: Box::new(document.state),
                sha256: Some(sha256(bytes)),
            }
        }
        _ => quarantine(),
    }
}

/// Managed `ContainsFutureSchema`.
fn future_schema(bytes: &[u8]) -> bool {
    serde_json::from_slice::<Value>(bytes).is_ok_and(|value| {
        value
            .get("schemaVersion")
            .and_then(Value::as_i64)
            .is_some_and(|schema| i32::try_from(schema).is_ok_and(|schema| schema > SCHEMA))
    })
}

/// Managed `TrySaveAsync`'s document bytes for `state`, after managed's
/// persisted validation, and their SHA-256.
pub fn encode(state: &State, sid: &str) -> Result<(Vec<u8>, String), Invalid> {
    state.validate(sid, true)?;
    let bytes = serde_json::to_vec_pretty(&Document {
        schema_version: SCHEMA,
        state: state.clone(),
    })
    .map_err(|_| Invalid("The thermal convergence record cannot be encoded."))?;
    let sha = sha256(&bytes);
    Ok((bytes, sha))
}

/// `Convert.ToHexString(SHA256.HashData(json))`.
pub fn sha256(bytes: &[u8]) -> String {
    format!("{:X}", Sha256::digest(bytes))
}

#[cfg(test)]
#[path = "thermal_convergence_tests.rs"]
mod tests;
