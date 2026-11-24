//! Managed `DiagnosticArchiveProvider` sections over the managed-shaped
//! status JSON this Service publishes: versions, device identity,
//! capabilities and configuration. Enum names, the model catalog and the
//! HyperBoost evidence gates come from the contract file the managed tests
//! record, so a managed rename or a new model fails those tests rather than
//! drifting here.
//!
//! A member absent from the native status reads as the managed
//! `ServiceStatusSnapshot` default the managed codec gives it. A capability
//! member native never composes (`unexported`) fails the export instead of
//! being summarized from an assumed shape.

use std::{collections::BTreeMap, sync::OnceLock};

use serde::Deserialize;
use serde_json::Value;

use super::{Failure, json::Json};

const CONTRACT: &str = include_str!("../../tests/data/diagnostic-export-contract.json");
const UNAVAILABLE: &str = "Unavailable";

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Contract {
    enums: BTreeMap<String, Vec<(String, i64)>>,
    flags: Vec<String>,
    models: Vec<Model>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct Model {
    pub(crate) model_number: String,
    pub(crate) usb_vendor_id: String,
    pub(crate) usb_product_id: String,
    support_basis: String,
    hyper_boost_without_cooling_pad_gates: Option<Gates>,
}

#[derive(Clone, Copy, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Gates {
    immediate_no_load: bool,
    sustained_no_load: bool,
    reviewed_load: bool,
    adapter_transition: bool,
    low_power_lifecycle: bool,
    crash_recovery: bool,
}

fn contract() -> &'static Contract {
    static CONTRACT_TABLES: OnceLock<Contract> = OnceLock::new();
    CONTRACT_TABLES.get_or_init(|| {
        serde_json::from_str(CONTRACT).expect("the recorded diagnostic export contract")
    })
}

fn values(name: &str) -> &'static [(String, i64)] {
    contract()
        .enums
        .get(name)
        .unwrap_or_else(|| panic!("the contract records {name}"))
}

/// Managed `Enum.ToString()` of a numeric value: its name, the flags
/// decomposition for a `[Flags]` enum, otherwise the number.
pub(crate) fn enum_name(name: &str, value: i64) -> String {
    let table = values(name);
    if let Some((label, _)) = table.iter().find(|(_, defined)| *defined == value) {
        return label.clone();
    }
    if contract().flags.iter().any(|flag| flag == name) && value > 0 {
        let mut remaining = value;
        let mut parts = Vec::new();
        let mut defined: Vec<&(String, i64)> = table.iter().filter(|(_, bit)| *bit > 0).collect();
        defined.sort_by_key(|(_, bit)| std::cmp::Reverse(*bit));
        for (label, bit) in defined {
            if remaining & bit == *bit {
                remaining -= bit;
                parts.push(label.as_str());
            }
        }
        if remaining == 0 {
            parts.reverse();
            return parts.join(", ");
        }
    }
    value.to_string()
}

fn defined(name: &str) -> impl Iterator<Item = &'static (String, i64)> {
    values(name).iter()
}

/// Managed `RazerBladeModelCatalog.Find`: the first catalog model whose
/// number prefixes the trimmed report, ignoring ASCII case.
pub(crate) fn find_model(reported: Option<&str>) -> Option<&'static Model> {
    let candidate = reported?.trim();
    if candidate.is_empty() {
        return None;
    }
    contract().models.iter().find(|model| {
        candidate.len() >= model.model_number.len()
            && candidate.as_bytes()[..model.model_number.len()]
                .eq_ignore_ascii_case(model.model_number.as_bytes())
    })
}

fn bool_text(value: bool) -> String {
    if value { "True" } else { "False" }.into()
}

/// One status member: absent and null are told apart only where managed
/// defaults them differently.
fn member<'a>(object: &'a Value, name: &str) -> Option<&'a Value> {
    object.get(name).filter(|value| !value.is_null())
}

fn flag(object: &Value, name: &str, default: bool) -> bool {
    member(object, name)
        .and_then(Value::as_bool)
        .unwrap_or(default)
}

fn integer(value: &Value) -> Result<i64, Failure> {
    value
        .as_i64()
        .ok_or_else(|| Failure::new("Diagnostic status carries a non-integer value."))
}

fn enum_member(object: &Value, member_name: &str, name: &str) -> Result<Option<String>, Failure> {
    member(object, member_name)
        .map(|value| integer(value).map(|value| enum_name(name, value)))
        .transpose()
}

fn enum_or(object: &Value, member_name: &str, name: &str, default: i64) -> Result<String, Failure> {
    Ok(enum_member(object, member_name, name)?.unwrap_or_else(|| enum_name(name, default)))
}

fn set(object: &Value, member_name: &str) -> Result<Vec<i64>, Failure> {
    match member(object, member_name) {
        None => Ok(Vec::new()),
        Some(Value::Array(items)) => items.iter().map(integer).collect(),
        Some(_) => Err(Failure::new("Diagnostic status carries a malformed set.")),
    }
}

fn strings(object: &Value, member_name: &str) -> Option<Vec<String>> {
    member(object, member_name)
        .and_then(Value::as_array)
        .map(|items| {
            items
                .iter()
                .map(|item| item.as_str().unwrap_or_default().to_owned())
                .collect()
        })
}

fn text(object: &Value, member_name: &str) -> Option<String> {
    member(object, member_name)
        .and_then(Value::as_str)
        .map(str::to_owned)
}

fn number_text(value: Option<&Value>) -> String {
    value
        .filter(|value| !value.is_null())
        .map_or_else(|| UNAVAILABLE.to_owned(), Value::to_string)
}

fn bool_or_unavailable(value: Option<&Value>) -> String {
    value
        .and_then(Value::as_bool)
        .map_or_else(|| UNAVAILABLE.to_owned(), bool_text)
}

/// Managed `DeviceCapabilities.Unavailable()` when the status carries none.
fn capability_record(status: &Value) -> Value {
    member(status, "capabilities").cloned().unwrap_or_else(|| {
        serde_json::json!({
            "performancePresets": [], "fanModes": [], "lightingEffects": [], "chargeLimits": [],
            "supportsBatteryProtectionOff": false, "supportsTemporaryFullCharge": false,
        })
    })
}

/// Capability members whose managed summary reads a typed record native
/// never composes. A non-empty value refuses the export.
const UNEXPORTED: [&str; 7] = [
    "customPerformance",
    "cpuPowerLimit",
    "cpuVoltageOptimizer",
    "gpuOverclock",
    "hyperBoostBaseModes",
    "fanSpeedRangesByPerformancePreset",
    "observedPowerSourceBehaviors",
];

fn unexported(capabilities: &Value) -> Result<(), Failure> {
    for name in UNEXPORTED {
        let empty = match member(capabilities, name) {
            None => true,
            Some(Value::Object(members)) if name == "fanSpeedRangesByPerformancePreset" => {
                members.is_empty()
            }
            Some(Value::Array(items)) if name == "observedPowerSourceBehaviors" => items.is_empty(),
            Some(_) => false,
        };
        if !empty {
            return Err(Failure::new(format!(
                "The capability member {name} has no native diagnostic summary."
            )));
        }
    }
    Ok(())
}

fn model_number(status: &Value) -> Option<&str> {
    member(status, "firmware").and_then(|firmware| firmware.get("modelNumber")?.as_str())
}

/// Managed `DiagnosticVersions` as `CreateAsync` composes it: the host
/// metadata, with every firmware field from the status record (native's
/// metadata reads no fresh firmware) and the OpenBlade version from the
/// status `serviceVersion`.
pub(crate) fn versions(status: &Value, windows_version: &str) -> Json {
    let firmware = member(status, "firmware");
    let field = |name: &str| {
        firmware
            .and_then(|firmware| firmware.get(name)?.as_str())
            .unwrap_or(UNAVAILABLE)
            .to_owned()
    };
    let access = |name: &str| {
        firmware
            .and_then(|firmware| member(firmware, name)?.as_i64())
            .map(|value| enum_name("DeviceFeatureAccess", value))
    };
    Json::object([
        (
            "openBladeVersion",
            Json::str(text(status, "serviceVersion").unwrap_or_else(|| UNAVAILABLE.into())),
        ),
        ("windowsVersion", Json::str(windows_version)),
        ("biosVersion", Json::str(field("biosVersion"))),
        ("ecVersion", Json::str(field("ecVersion"))),
        ("mcuVersion", Json::str(field("mcuVersion"))),
        ("vbiosVersion", Json::str(field("vbiosVersion"))),
        ("vbiosGpuName", Json::str(field("vbiosGpuName"))),
        (
            "thunderboltFirmwareVersion",
            Json::str(field("thunderboltFirmwareVersion")),
        ),
        (
            "touchpadFirmwareVersion",
            Json::str(field("touchpadFirmwareVersion")),
        ),
        (
            "thunderboltFirmwareAccess",
            Json::nullable(access("thunderboltFirmwareAccess")),
        ),
        (
            "touchpadFirmwareAccess",
            Json::nullable(access("touchpadFirmwareAccess")),
        ),
    ])
}

/// Managed `CreateDeviceIdentity`. The serial number is never exported.
pub(crate) fn device(status: &Value) -> Json {
    let reported = model_number(status);
    let (model, vendor, product) = match find_model(reported) {
        Some(model) => (
            model.model_number.clone(),
            model.usb_vendor_id.clone(),
            model.usb_product_id.clone(),
        ),
        None => (
            reported
                .map(str::trim)
                .filter(|model| !model.is_empty())
                .unwrap_or(UNAVAILABLE)
                .to_owned(),
            UNAVAILABLE.into(),
            UNAVAILABLE.into(),
        ),
    };
    Json::object([
        ("model", Json::Str(model)),
        ("vendorId", Json::Str(vendor)),
        ("productId", Json::Str(product)),
        ("serialNumber", Json::Null),
    ])
}

/// Managed `CreateCapabilities`.
pub(crate) fn capabilities(status: &Value) -> Result<BTreeMap<String, bool>, Failure> {
    let record = capability_record(status);
    let caps = &record;
    unexported(caps)?;
    let mut result = BTreeMap::new();
    let mut put = |key: String, value: bool| {
        result.insert(key, value);
    };
    let contains = |member: &str, value: i64| -> Result<bool, Failure> {
        Ok(set(caps, member)?.contains(&value))
    };
    for (label, value) in defined("PerformancePreset") {
        put(
            format!("performance.{label}"),
            contains("performancePresets", *value)?,
        );
    }
    for (label, value) in defined("FanMode") {
        put(format!("fans.{label}"), contains("fanModes", *value)?);
    }
    for (label, value) in defined("LightingEffect") {
        put(
            format!("lighting.{label}"),
            contains("lightingEffects", *value)?,
        );
        put(
            format!("lighting.observed.{label}"),
            contains("observedLightingEffects", *value)?,
        );
    }
    for (label, value) in defined("LightingRefreshRate") {
        put(
            format!("lighting.refreshRate.{label}"),
            contains("supportedLightingRefreshRates", *value)?,
        );
    }
    put(
        "fans.batterySaverStop".into(),
        flag(caps, "supportsBatterySaverFanStop", false),
    );
    put(
        "battery.chargeLimit".into(),
        !set(caps, "chargeLimits")?.is_empty(),
    );
    put(
        "battery.protectionOff".into(),
        flag(caps, "supportsBatteryProtectionOff", false),
    );
    put(
        "battery.temporaryFullCharge".into(),
        flag(caps, "supportsTemporaryFullCharge", false),
    );
    put(
        "lighting.keyboardBrightness".into(),
        flag(caps, "supportsKeyboardBrightness", false),
    );
    let color_selection = flag(caps, "supportsBreathingColorSelection", true);
    put("lighting.breathingColorSelection".into(), color_selection);
    let modes = member(caps, "supportedBreathingColorModes")
        .map(|_| set(caps, "supportedBreathingColorModes"))
        .transpose()?;
    let random = values("BreathingColorMode")
        .iter()
        .find(|(label, _)| label == "Random")
        .map(|(_, value)| *value);
    for (label, value) in defined("BreathingColorMode") {
        // Managed `SupportsBreathingMode`.
        let supported = match &modes {
            Some(modes) => modes.contains(value),
            None => color_selection || Some(*value) == random,
        };
        put(format!("lighting.breathingColorMode.{label}"), supported);
    }
    put(
        "lighting.uniformFrames".into(),
        flag(caps, "supportsUniformLightingFrames", false),
    );
    put(
        "lighting.waveDirectionSelection".into(),
        flag(caps, "supportsWaveDirectionSelection", true),
    );
    put(
        "lighting.playbackMuteIndicator".into(),
        flag(caps, "supportsPlaybackMuteIndicator", false),
    );
    // `unexported` admitted only absent typed records, which managed reads
    // as not well formed and not read-write.
    for key in [
        "performance.customCombinations",
        "performance.customMaximumFans",
        "performance.cpuPowerLimit.observed",
        "performance.cpuPowerLimit.write",
        "performance.cpuVoltageOptimizer.observed",
        "performance.cpuVoltageOptimizer.write",
        "performance.cpuVoltageOptimizer.enableDisableObserved",
        "performance.gpuOverclock.observed",
        "performance.gpuOverclock.write",
        "performance.gpuOverclock.enableDisableObserved",
    ] {
        put(key.into(), false);
    }
    put(
        "fans.manualOnLimitedPower".into(),
        flag(caps, "supportsManualFansOnLimitedPower", false),
    );
    put(
        "performance.ordinaryOnUnclassifiedExternalPower".into(),
        flag(
            caps,
            "supportsOrdinaryThermalControlOnUnclassifiedExternalPower",
            false,
        ),
    );
    let read_write = values("DeviceFeatureAccess")
        .iter()
        .find(|(label, _)| label == "ReadWrite")
        .map(|(_, value)| *value);
    let access = |name: &str| -> Result<Option<i64>, Failure> {
        member(caps, name).map(integer).transpose()
    };
    for (prefix, name) in [
        ("performance.hyperBoost", "hyperBoostAccess"),
        (
            "performance.hyperBoost.withoutCoolingPad",
            "hyperBoostWithoutCoolingPadAccess",
        ),
        ("laptop.specialKeyBindings", "specialKeyBindingsAccess"),
        ("lighting.lampArray", "lampArrayLightingAccess"),
    ] {
        let access = access(name)?;
        put(format!("{prefix}.observed"), access.is_some());
        put(
            format!("{prefix}.write"),
            access.is_some() && access == read_write,
        );
    }
    let gates = find_model(model_number(status))
        .and_then(|model| model.hyper_boost_without_cooling_pad_gates)
        .unwrap_or_default();
    for (name, value) in [
        ("immediateNoLoad", gates.immediate_no_load),
        ("sustainedNoLoad", gates.sustained_no_load),
        ("reviewedLoad", gates.reviewed_load),
        ("adapterTransition", gates.adapter_transition),
        ("lowPowerLifecycle", gates.low_power_lifecycle),
        ("crashRecovery", gates.crash_recovery),
    ] {
        put(
            format!("performance.hyperBoost.withoutCoolingPad.gate.{name}"),
            value,
        );
    }
    let slots = member(caps, "writableSpecialKeySlots")
        .map(integer)
        .transpose()?
        .unwrap_or(0);
    for slot in ["M1", "M2", "M3", "M4", "M5"] {
        let bit = values("SpecialKeySlotSet")
            .iter()
            .find(|(label, _)| label == slot)
            .map_or(0, |(_, value)| *value);
        put(
            format!("laptop.specialKeyBindings.write.{slot}"),
            slots & bit != 0,
        );
    }
    let thx = member(caps, "thxAudio");
    let thx_access = thx
        .map(|thx| thx.get("access").map_or(Ok(0), integer))
        .transpose()?;
    put("audio.thx.observed".into(), thx.is_some());
    put(
        "audio.thx.write".into(),
        thx_access.is_some() && thx_access == read_write,
    );
    Ok(result)
}

/// Managed `CreateConfiguration`. The RZ09-0581 capability diagnostics
/// have no native source, so their three keys read as managed's absent
/// `Rz090581CapabilityDiagnostics`.
pub(crate) fn configuration(
    status: &Value,
    live_readings_enabled: bool,
) -> Result<BTreeMap<String, String>, Failure> {
    let record = capability_record(status);
    let caps = &record;
    unexported(caps)?;
    let confirmed = member(status, "confirmedState");
    let model = find_model(model_number(status));
    let profiles = status.get("profiles").unwrap_or(&Value::Null);
    let desired = status.get("desiredState").unwrap_or(&Value::Null);
    let joined = |items: Vec<String>| items.join(", ");
    let names = |name: &str, items: Vec<i64>| -> Vec<String> {
        items
            .into_iter()
            .map(|value| enum_name(name, value))
            .collect()
    };
    let experimental = flag(status, "isExperimentalDevice", false);
    let mut map = BTreeMap::new();
    let mut put = |key: &str, value: String| {
        map.insert(key.to_owned(), value);
    };

    put(
        "status.revision",
        status
            .get("revision")
            .map_or_else(|| "0".into(), Value::to_string),
    );
    put(
        "status.deviceVerified",
        bool_text(flag(status, "deviceVerified", false)),
    );
    put(
        "status.writesBlocked",
        bool_text(flag(status, "writesBlocked", false)),
    );
    put(
        "status.hyperBoostRestorePending",
        bool_text(flag(status, "hyperBoostRestorePending", false)),
    );
    put(
        "status.potentialControllers",
        joined(strings(status, "potentialControllers").unwrap_or_default()),
    );
    put(
        "status.hyperBoostBlockingControllers",
        strings(status, "hyperBoostBlockingControllers")
            .map_or_else(|| UNAVAILABLE.to_owned(), joined),
    );
    put(
        "status.controllerDetectionWarning",
        text(status, "controllerDetectionWarning").unwrap_or_else(|| UNAVAILABLE.into()),
    );
    put(
        "status.externalChanges",
        joined(names("DeviceSubsystem", set(status, "externalChanges")?)),
    );
    put(
        "status.unconfirmedWrites",
        joined(names("DeviceSubsystem", set(status, "unconfirmedWrites")?)),
    );
    put(
        "status.message",
        text(status, "message").unwrap_or_default(),
    );
    put(
        "laptop.functionKeyPrimary",
        enum_member(status, "functionKeyPrimary", "FunctionKeyPrimaryMode")?
            .unwrap_or_else(|| UNAVAILABLE.into()),
    );
    put(
        "laptop.keyboardDriverModeActive",
        bool_or_unavailable(status.get("keyboardDriverModeActive")),
    );
    put(
        "power.source",
        enum_or(status, "activePowerSource", "PowerSource", 0)?,
    );
    put("liveReadings.enabled", bool_text(live_readings_enabled));
    let readings = member(status, "liveReadings");
    put(
        "liveReadings.available",
        bool_text(readings.is_some_and(|readings| {
            ["batteryPowerFlowMilliwatts", "fan1Rpm", "fan2Rpm"]
                .iter()
                .any(|name| member(readings, name).is_some())
        })),
    );
    for (source, label) in [
        ("pluggedIn", "pluggedIn"),
        ("battery", "battery"),
        ("usbC", "usbC"),
    ] {
        let profile = profiles.get(source).unwrap_or(&Value::Null);
        put(
            &format!("profile.{label}.performance"),
            enum_or(profile, "performance", "PerformancePreset", 0)?,
        );
        put(
            &format!("profile.{label}.fans"),
            enum_or(profile, "fans", "FanMode", 0)?,
        );
        put(
            &format!("profile.{label}.lighting"),
            enum_or(
                profile.get("lighting").unwrap_or(&Value::Null),
                "effect",
                "LightingEffect",
                0,
            )?,
        );
    }
    put(
        "battery.chargeLimitPercent",
        number_text(
            profiles
                .get("batteryPolicy")
                .and_then(|policy| policy.get("chargeLimitPercent")),
        ),
    );
    put(
        "battery.protectionEnabled",
        bool_text(flag(
            desired.get("battery").unwrap_or(&Value::Null),
            "protectionEnabled",
            false,
        )),
    );
    put(
        "battery.temporaryFullCharge",
        bool_text(flag(status, "fullChargeOverride", false)),
    );
    for (key, name, kind) in [
        ("performance", "performance", "PerformancePreset"),
        ("fans", "fans", "FanMode"),
    ] {
        let setting = desired.get(name).unwrap_or(&Value::Null);
        put(
            &format!("desired.{key}"),
            enum_or(setting, "value", kind, 0)?,
        );
        put(
            &format!("desired.{key}.source"),
            enum_or(setting, "source", "SettingValueSource", 0)?,
        );
    }
    let lighting = desired.get("lighting").unwrap_or(&Value::Null);
    put(
        "desired.lighting",
        enum_or(
            lighting.get("value").unwrap_or(&Value::Null),
            "effect",
            "LightingEffect",
            0,
        )?,
    );
    put(
        "desired.lighting.source",
        enum_or(lighting, "source", "SettingValueSource", 0)?,
    );
    put(
        "lighting.rendererOwnership",
        enum_or(
            status,
            "lightingRendererOwnership",
            "LightingRendererOwnershipState",
            0,
        )?,
    );
    put(
        "lighting.alwaysOverrideExternalChanges",
        bool_text(flag(status, "alwaysOverrideExternalLightingChanges", false)),
    );
    put(
        "lighting.refreshRate",
        enum_or(status, "lightingRefreshRate", "LightingRefreshRate", 0)?,
    );
    put(
        "experimental.supportPolicy",
        "AutomaticForRecognizedModels".into(),
    );
    put(
        "experimental.writesPaused",
        bool_text(flag(status, "experimentalWritesPaused", false)),
    );
    put("experimental.deviceDetected", bool_text(experimental));
    put(
        "experimental.evidenceSource",
        text(status, "experimentalSupportSource").unwrap_or_else(|| UNAVAILABLE.into()),
    );
    let basis = model.map_or("NotEstablished", |model| model.support_basis.as_str());
    put(
        "support.status",
        if matches!(basis, "SourceReviewed" | "PhysicallyValidated") {
            "Supported"
        } else {
            "NotEstablished"
        }
        .into(),
    );
    put("support.basis", basis.into());
    put(
        "support.physicalValidation",
        match model {
            None => "NotEstablished",
            Some(_) if basis == "PhysicallyValidated" => "Validated",
            Some(_) => "Pending",
        }
        .into(),
    );
    put(
        "experimental.validationBoundary",
        if experimental {
            "Source-reviewed support; physical validation tracked separately"
        } else {
            "See support.basis and support.physicalValidation"
        }
        .into(),
    );
    put(
        "firmware.allowUntested",
        bool_text(flag(status, "allowUntestedFirmware", false)),
    );
    put(
        "firmware.compatibility",
        enum_or(
            status,
            "firmwareCompatibility",
            "FirmwareCompatibilityStatus",
            0,
        )?,
    );
    let confirmed_enum = |member_name: &str, name: &str| -> Result<String, Failure> {
        Ok(confirmed
            .map(|state| enum_member(state, member_name, name))
            .transpose()?
            .flatten()
            .unwrap_or_else(|| UNAVAILABLE.into()))
    };
    put(
        "confirmed.performance",
        confirmed_enum("performance", "PerformancePreset")?,
    );
    put("confirmed.fans", confirmed_enum("fans", "FanMode")?);
    // Managed `HyperBoostActive` defaults to false when the member is absent.
    put(
        "confirmed.hyperBoostActive",
        match confirmed {
            None => UNAVAILABLE.into(),
            Some(state) => match state.get("hyperBoostActive") {
                None => bool_text(false),
                Some(value) => bool_or_unavailable(Some(value)),
            },
        },
    );
    put(
        "hyperBoost.padlessEligibility",
        enum_or(
            status,
            "padlessHyperBoostEligibility",
            "PadlessHyperBoostEligibilityState",
            0,
        )?,
    );
    let battery = confirmed.and_then(|state| member(state, "battery"));
    put(
        "confirmed.battery.chargeLimitPercent",
        number_text(battery.and_then(|battery| battery.get("chargeLimitPercent"))),
    );
    put(
        "confirmed.battery.protectionEnabled",
        bool_or_unavailable(battery.and_then(|battery| battery.get("protectionEnabled"))),
    );
    put(
        "confirmed.battery.temporaryFullCharge",
        bool_or_unavailable(battery.and_then(|battery| battery.get("temporaryFullCharge"))),
    );
    let confirmed_lighting = confirmed.and_then(|state| member(state, "lighting"));
    put(
        "confirmed.lighting.effect",
        confirmed_lighting
            .map(|lighting| enum_member(lighting, "effect", "LightingEffect"))
            .transpose()?
            .flatten()
            .unwrap_or_else(|| UNAVAILABLE.into()),
    );
    put(
        "confirmed.lighting.brightness",
        number_text(confirmed_lighting.and_then(|lighting| lighting.get("brightness"))),
    );
    let mut limits = set(caps, "chargeLimits")?;
    limits.sort_unstable();
    limits.dedup();
    put(
        "capability.chargeLimits",
        joined(limits.iter().map(i64::to_string).collect()),
    );
    for (key, name) in [
        (
            "supportsBatteryProtectionOff",
            "supportsBatteryProtectionOff",
        ),
        ("supportsTemporaryFullCharge", "supportsTemporaryFullCharge"),
        ("supportsKeyboardBrightness", "supportsKeyboardBrightness"),
        (
            "supportsPlaybackMuteIndicator",
            "supportsPlaybackMuteIndicator",
        ),
    ] {
        put(
            &format!("capability.{key}"),
            bool_text(flag(caps, name, false)),
        );
    }
    let mut observed: Vec<i64> = set(caps, "observedLightingEffects")?
        .into_iter()
        .filter(|value| defined("LightingEffect").any(|(_, defined)| defined == value))
        .collect();
    observed.sort_unstable();
    put(
        "capability.observedLightingEffects",
        joined(names("LightingEffect", observed)),
    );
    let access_name = |name: &str| -> Result<String, Failure> {
        Ok(enum_member(caps, name, "DeviceFeatureAccess")?.unwrap_or_else(|| UNAVAILABLE.into()))
    };
    put(
        "capability.specialKeyBindingsAccess",
        access_name("specialKeyBindingsAccess")?,
    );
    put(
        "capability.writableSpecialKeySlots",
        enum_or(caps, "writableSpecialKeySlots", "SpecialKeySlotSet", 0)?,
    );
    put(
        "capability.lampArrayLightingAccess",
        access_name("lampArrayLightingAccess")?,
    );
    let thx = member(caps, "thxAudio");
    put(
        "capability.thxAudioAccess",
        thx.map(|thx| enum_or(thx, "access", "DeviceFeatureAccess", 0))
            .transpose()?
            .unwrap_or_else(|| UNAVAILABLE.into()),
    );
    put(
        "capability.thxAudioProductId",
        thx.and_then(|thx| text(thx, "productId"))
            .unwrap_or_else(|| UNAVAILABLE.into()),
    );
    put(
        "capability.hyperBoostAccess",
        access_name("hyperBoostAccess")?,
    );
    put(
        "capability.hyperBoostWithoutCoolingPadAccess",
        access_name("hyperBoostWithoutCoolingPadAccess")?,
    );
    put("capability.hyperBoostBase.Balanced", UNAVAILABLE.into());
    put("capability.hyperBoostBase.Custom", UNAVAILABLE.into());
    let range = member(caps, "fanSpeedRange");
    for (key, name) in [
        ("capability.fanSpeedMinimumRpm", "minimumRpm"),
        ("capability.fanSpeedMaximumRpm", "maximumRpm"),
        ("capability.fanSpeedStepRpm", "stepRpm"),
    ] {
        put(key, number_text(range.and_then(|range| range.get(name))));
    }
    put(
        "capability.reportsPerformanceWithManualFans",
        bool_text(flag(caps, "reportsPerformanceWithManualFans", false)),
    );
    put(
        "capability.fanSpeedRangesByPerformancePreset",
        UNAVAILABLE.into(),
    );
    for key in [
        "capability.rz09-0581.skuHardwareConfigurationRaw",
        "capability.rz09-0581.miniLedPanelResolutionRaw",
        "capability.rz09-0581.thermalFanIds",
    ] {
        put(key, UNAVAILABLE.into());
    }
    for (key, name, kind) in [
        (
            "override.performance",
            "performanceOverride",
            "PerformancePreset",
        ),
        ("override.fans", "fanOverride", "FanMode"),
    ] {
        put(
            key,
            match member(status, name) {
                None => "None".into(),
                Some(value) => format!(
                    "{} ({})",
                    enum_or(value, "value", kind, 0)?,
                    enum_or(value, "lifetime", "OverrideLifetime", 0)?
                ),
            },
        );
    }
    put(
        "override.lighting",
        match member(status, "lightingOverride") {
            None => "None".into(),
            Some(value) => format!(
                "{} ({})",
                enum_or(
                    value.get("value").unwrap_or(&Value::Null),
                    "effect",
                    "LightingEffect",
                    0
                )?,
                enum_or(value, "lifetime", "OverrideLifetime", 0)?
            ),
        },
    );
    Ok(map)
}
