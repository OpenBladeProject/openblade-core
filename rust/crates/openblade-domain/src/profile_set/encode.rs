//! Projects the typed model to the JSON `System.Text.Json` writes for the
//! managed records: declaration order, numeric enums, `null` members kept,
//! the get-only `isConfigured` and `effectiveOffsets` members included, and
//! the three members managed omits at their default.

use super::json::{self, Json};
use super::model::*;
use std::string::String;
use std::vec::Vec;

fn object<const N: usize>(members: [(&str, Json); N]) -> Json {
    Json::Object(
        members
            .into_iter()
            .map(|(name, value)| (name.into(), value))
            .collect(),
    )
}

fn optional<T>(value: Option<T>, encode: impl FnOnce(T) -> Json) -> Json {
    value.map_or(Json::Null, encode)
}

fn int(value: i32) -> Json {
    Json::int(value.into())
}

fn text(value: &str) -> Json {
    Json::string(value)
}

fn color(value: RgbColor) -> Json {
    object([
        ("red", Json::int(value.red.into())),
        ("green", Json::int(value.green.into())),
        ("blue", Json::int(value.blue.into())),
    ])
}

fn gpu_clock_offsets(value: GpuClockOffsets) -> Json {
    object([
        ("coreMegahertz", int(value.core_megahertz)),
        ("memoryMegahertz", int(value.memory_megahertz)),
    ])
}

fn gpu_clock_tuning(value: GpuClockTuning) -> Json {
    // Managed `EffectiveOffsets`: the offsets while enabled, else zero.
    let effective = if value.enabled {
        value.offsets
    } else {
        Some(GpuClockOffsets::default())
    };
    object([
        ("enabled", Json::Bool(value.enabled)),
        ("offsets", optional(value.offsets, gpu_clock_offsets)),
        ("effectiveOffsets", optional(effective, gpu_clock_offsets)),
    ])
}

fn custom_performance(value: CustomPerformance) -> Json {
    object([
        ("cpu", int(value.cpu.0)),
        ("gpu", int(value.gpu.0)),
        ("maximumFans", Json::Bool(value.maximum_fans)),
        (
            "gpuClockOffsets",
            optional(value.gpu_clock_offsets, gpu_clock_offsets),
        ),
    ])
}

fn fan_curve(points: &[FanCurvePoint]) -> Json {
    Json::Array(
        points
            .iter()
            .map(|point| {
                object([
                    ("temperatureCelsius", int(point.temperature_celsius)),
                    ("speedRpm", int(point.speed_rpm)),
                ])
            })
            .collect(),
    )
}

fn fan_control(value: &FanControl) -> Json {
    object([
        ("mode", int(value.mode.0)),
        ("fixedSpeedRpm", int(value.fixed_speed_rpm)),
        (
            "manualCurve",
            object([
                ("cpu", fan_curve(&value.manual_curve.cpu)),
                ("gpu", fan_curve(&value.manual_curve.gpu)),
                (
                    "useHigherTemperatureForBoth",
                    Json::Bool(value.manual_curve.use_higher_temperature_for_both),
                ),
            ]),
        ),
    ])
}

fn lighting_parameters(value: &LightingParameters) -> Json {
    let kind = |name: &str, members: Vec<(&str, Json)>| {
        let mut all = Vec::from([(String::from("kind"), Json::string(name))]);
        all.extend(
            members
                .into_iter()
                .map(|(name, value)| (name.into(), value)),
        );
        Json::Object(all)
    };
    let shade = |value: &Option<RgbColor>| optional(*value, color);
    match value {
        LightingParameters::Off => kind("off", Vec::new()),
        LightingParameters::SpectrumCycling => kind("spectrumCycling", Vec::new()),
        LightingParameters::Fire => kind("fire", Vec::new()),
        LightingParameters::Static { color: value } => {
            kind("static", Vec::from([("color", color(*value))]))
        }
        LightingParameters::Ripple { color: value } => {
            kind("ripple", Vec::from([("color", color(*value))]))
        }
        LightingParameters::Breathing {
            color_mode,
            primary_color,
            secondary_color,
        } => kind(
            "breathing",
            Vec::from([
                ("colorMode", int(color_mode.0)),
                ("primaryColor", shade(primary_color)),
                ("secondaryColor", shade(secondary_color)),
            ]),
        ),
        LightingParameters::Reactive {
            color: value,
            duration,
        } => kind(
            "reactive",
            Vec::from([("color", color(*value)), ("duration", int(duration.0))]),
        ),
        LightingParameters::Tidal {
            primary_color,
            secondary_color,
            random_colors,
            direction,
        } => kind(
            "tidal",
            Vec::from([
                ("primaryColor", color(*primary_color)),
                ("secondaryColor", color(*secondary_color)),
                ("randomColors", Json::Bool(*random_colors)),
                ("direction", int(direction.0)),
            ]),
        ),
        LightingParameters::Starlight {
            color_mode,
            primary_color,
            secondary_color,
            duration,
        } => kind(
            "starlight",
            Vec::from([
                ("colorMode", int(color_mode.0)),
                ("primaryColor", shade(primary_color)),
                ("secondaryColor", shade(secondary_color)),
                ("duration", int(duration.0)),
            ]),
        ),
        LightingParameters::Wave { direction } => {
            kind("wave", Vec::from([("direction", int(direction.0))]))
        }
        LightingParameters::Wheel { direction } => {
            kind("wheel", Vec::from([("direction", int(direction.0))]))
        }
        LightingParameters::AmbientAwareness { region } => {
            kind("ambientAwareness", Vec::from([("region", int(region.0))]))
        }
        LightingParameters::AudioMeter {
            color_boost,
            decay_milliseconds,
        } => kind(
            "audioMeter",
            Vec::from([
                ("colorBoost", json::double(*color_boost)),
                ("decayMilliseconds", int(*decay_milliseconds)),
            ]),
        ),
        LightingParameters::Sequence {
            layout_id,
            timeline_base64,
            looped,
        } => kind(
            "sequence",
            Vec::from([
                ("layoutId", optional(layout_id.as_deref(), text)),
                ("timelineBase64", optional(timeline_base64.as_deref(), text)),
                ("loop", Json::Bool(*looped)),
            ]),
        ),
    }
}

pub fn lighting(value: &LightingSettings) -> Json {
    let mut members = Vec::from([
        (String::from("effect"), int(value.effect.0)),
        ("color".into(), color(value.color)),
        ("brightness".into(), Json::int(value.brightness.into())),
        (
            "parameters".into(),
            optional(value.parameters.as_ref(), lighting_parameters),
        ),
        (
            "logoMode".into(),
            optional(value.logo_mode, |mode| int(mode.0)),
        ),
    ]);
    // `WhenWritingNull` and `WhenWritingDefault` respectively.
    if let Some(independent) = value.independent_controls {
        members.push((
            "independentControls".into(),
            object([
                (
                    "brightness",
                    optional(independent.brightness, |value| Json::int(value.into())),
                ),
                (
                    "logoMode",
                    optional(independent.logo_mode, |mode| int(mode.0)),
                ),
                (
                    "vaporChamberLightingEnabled",
                    optional(independent.vapor_chamber_lighting_enabled, Json::Bool),
                ),
                (
                    "touchpadStatusIndicatorEnabled",
                    optional(independent.touchpad_status_indicator_enabled, Json::Bool),
                ),
            ]),
        ));
    }
    if value.controls_keyboard_effect {
        members.push(("controlsKeyboardEffect".into(), Json::Bool(true)));
    }
    Json::Object(members)
}

fn lighting_power_policy(value: LightingPowerPolicy) -> Json {
    object([
        (
            "turnOffWhenDisplayTurnsOff",
            Json::Bool(value.turn_off_when_display_turns_off),
        ),
        (
            "idleTimeoutMinutes",
            optional(value.idle_timeout_minutes, int),
        ),
    ])
}

fn display(value: &DisplaySettings) -> Json {
    object([
        (
            "refreshRateKey",
            optional(value.refresh_rate_key.as_deref(), text),
        ),
        ("hdrEnabled", optional(value.hdr_enabled, Json::Bool)),
        (
            "colorProfileKey",
            optional(value.color_profile_key.as_deref(), text),
        ),
        (
            "brightnessPercent",
            optional(value.brightness_percent, |value| Json::int(value.into())),
        ),
        ("isConfigured", Json::Bool(value.is_configured())),
    ])
}

fn gaming_mode_key_policy(value: GamingModeKeyPolicy) -> Json {
    object([
        ("blockWindowsKey", Json::Bool(value.block_windows_key)),
        ("blockAltTab", Json::Bool(value.block_alt_tab)),
    ])
}

/// `LaptopBehaviorProfileSettings` as `ProfileStore.SerializerOptions` writes it.
pub fn laptop_behavior(value: LaptopBehavior) -> Json {
    object([
        (
            "startupAnimationEnabled",
            optional(value.startup_animation_enabled, Json::Bool),
        ),
        (
            "functionKeyPrimary",
            optional(value.function_key_primary, |mode| int(mode.0)),
        ),
        (
            "gamingModeEnabled",
            optional(value.gaming_mode_enabled, Json::Bool),
        ),
        (
            "gamingModeKeyPolicy",
            optional(value.gaming_mode_key_policy, gaming_mode_key_policy),
        ),
        ("isConfigured", Json::Bool(value.is_configured())),
    ])
}

fn thx_enhancement(value: ThxEnhancement) -> Json {
    object([
        ("enabled", Json::Bool(value.enabled)),
        ("levelPercent", int(value.level_percent)),
    ])
}

fn thx_endpoint(value: &ThxEndpoint) -> Json {
    let curve = |index: usize| {
        Json::Array(
            value.equalizer_curves.0[index]
                .iter()
                .copied()
                .map(json::double)
                .collect(),
        )
    };
    object([
        ("spatialEnabled", Json::Bool(value.spatial_enabled)),
        ("equalizerEnabled", Json::Bool(value.equalizer_enabled)),
        ("selectedPreset", int(value.selected_preset.0)),
        (
            "equalizerCurves",
            object([
                ("game", curve(0)),
                ("movie", curve(1)),
                ("music", curve(2)),
                ("voice", curve(3)),
                ("custom", curve(4)),
            ]),
        ),
        ("voiceClarity", thx_enhancement(value.voice_clarity)),
        (
            "soundNormalization",
            optional(value.sound_normalization, thx_enhancement),
        ),
    ])
}

/// The rule list alone, as managed measures it against its byte limit.
pub(super) fn thx_rules(rules: &[ThxApplicationRule]) -> Json {
    Json::Array(
        rules
            .iter()
            .map(|rule| {
                object([
                    ("executableName", text(&rule.executable_name)),
                    ("output", int(rule.output.0)),
                ])
            })
            .collect(),
    )
}

fn thx_audio(value: &ThxAudio) -> Json {
    match value {
        // Never stored: validation refuses it first.
        ThxAudio::NullMember => Json::Null,
        ThxAudio::Typed(profile) => object([
            (
                "applicationRoutingEnabled",
                Json::Bool(profile.application_routing_enabled),
            ),
            ("speakers", thx_endpoint(&profile.speakers)),
            ("headphones", thx_endpoint(&profile.headphones)),
            ("applicationRules", thx_rules(&profile.application_rules)),
        ]),
    }
}

/// A `PowerProfile` as `ProfileStore.SerializerOptions` writes it.
pub fn power_profile(value: &PowerProfile) -> Json {
    let mut members = Vec::from([
        (String::from("performance"), int(value.performance.0)),
        ("fans".into(), int(value.fans.0)),
        (
            "lighting".into(),
            optional(value.lighting.as_ref(), lighting),
        ),
        ("display".into(), optional(value.display.as_ref(), display)),
        (
            "stopFansInBatterySaver".into(),
            Json::Bool(value.stop_fans_in_battery_saver),
        ),
        (
            "customPerformance".into(),
            optional(value.custom_performance, custom_performance),
        ),
        (
            "gpuClockTuning".into(),
            optional(value.gpu_clock_tuning, gpu_clock_tuning),
        ),
        (
            "fanControl".into(),
            optional(value.fan_control.as_ref(), fan_control),
        ),
        (
            "lightingPowerPolicy".into(),
            optional(value.lighting_power_policy, lighting_power_policy),
        ),
        (
            "laptopBehavior".into(),
            optional(value.laptop_behavior, laptop_behavior),
        ),
        (
            "batteryProtectionEnabled".into(),
            optional(value.battery_protection_enabled, Json::Bool),
        ),
        (
            "thxAudio".into(),
            optional(value.thx_audio.as_ref(), thx_audio),
        ),
    ]);
    if let Some(mode) = value.windows_power_mode {
        members.push(("windowsPowerMode".into(), int(mode.0)));
    }
    Json::Object(members)
}

fn application_override(value: &ApplicationOverride) -> Json {
    object([
        (
            "performance",
            optional(value.performance, |preset| int(preset.0)),
        ),
        (
            "customPerformance",
            optional(value.custom_performance, custom_performance),
        ),
        (
            "gpuClockTuning",
            optional(value.gpu_clock_tuning, gpu_clock_tuning),
        ),
        ("fans", optional(value.fans, |mode| int(mode.0))),
        (
            "fanControl",
            optional(value.fan_control.as_ref(), fan_control),
        ),
        ("lighting", optional(value.lighting.as_ref(), lighting)),
        ("display", optional(value.display.as_ref(), display)),
        (
            "gamingModeEnabled",
            optional(value.gaming_mode_enabled, Json::Bool),
        ),
        (
            "gamingModeKeyPolicy",
            optional(value.gaming_mode_key_policy, gaming_mode_key_policy),
        ),
        ("isConfigured", Json::Bool(value.is_configured())),
    ])
}

fn application_profile(value: &ApplicationProfile) -> Json {
    let overrides = &value.overrides;
    object([
        ("id", text(&value.id)),
        ("name", text(&value.name)),
        ("executablePath", text(&value.executable_path)),
        ("activationMode", int(value.activation_mode.0)),
        (
            "overrides",
            object([
                (
                    "pluggedIn",
                    optional(overrides.plugged_in.as_ref(), application_override),
                ),
                (
                    "battery",
                    optional(overrides.battery.as_ref(), application_override),
                ),
                (
                    "usbC",
                    optional(overrides.usb_c.as_ref(), application_override),
                ),
                ("isConfigured", Json::Bool(overrides.is_configured())),
            ]),
        ),
        ("enabled", Json::Bool(value.enabled)),
    ])
}

/// A managed `ProfileSet`, as either serializer writes it.
pub fn profile_set(value: &ProfileSet) -> Json {
    let mode = |preset: usize| optional(value.windows_power_modes.0[preset], |mode| int(mode.0));
    object([
        ("pluggedIn", power_profile(&value.plugged_in)),
        ("battery", power_profile(&value.battery)),
        ("usbC", power_profile(&value.usb_c)),
        (
            "batteryPolicy",
            optional(value.battery_policy, |policy| {
                object([("chargeLimitPercent", int(policy.charge_limit_percent))])
            }),
        ),
        (
            "lightingPowerPolicy",
            optional(value.lighting_power_policy, lighting_power_policy),
        ),
        (
            "applicationProfiles",
            Json::Array(
                value
                    .application_profiles
                    .iter()
                    .map(application_profile)
                    .collect(),
            ),
        ),
        (
            "windowsPowerModes",
            object([
                ("silent", mode(0)),
                ("balanced", mode(1)),
                ("performance", mode(2)),
                ("batterySaver", mode(3)),
                ("batteryBalanced", mode(4)),
                ("custom", mode(5)),
                ("turbo", mode(6)),
                ("maximumPower", mode(7)),
                ("gaming", mode(8)),
                ("creator", mode(9)),
            ]),
        ),
    ])
}

fn provenance(value: crate::automation::Provenance) -> Json {
    use crate::automation::Provenance;
    // Managed `SettingValueSource` order.
    int(match value {
        Provenance::ConfirmedFirmwareState => 0,
        Provenance::PowerProfile => 1,
        Provenance::ApplicationProfile => 2,
        Provenance::TemporaryOverride => 3,
        Provenance::PersistentOverride => 4,
    })
}

fn setting(value: Json, source: crate::automation::Provenance) -> Json {
    object([("value", value), ("source", provenance(source))])
}

/// Managed `DesiredDeviceState`'s members as `Resolve` fills them from the
/// profile layer, without the battery's temporary full charge. Lighting
/// with no value is `null`.
pub fn desired_intent(value: &super::application::DesiredIntent<'_>) -> Json {
    object([
        (
            "performance",
            setting(int(value.performance.value.0), value.performance.source),
        ),
        ("fans", setting(int(value.fans.value.0), value.fans.source)),
        (
            "lighting",
            setting(
                optional(value.lighting.value, lighting),
                value.lighting.source,
            ),
        ),
        (
            "battery",
            object([
                (
                    "configuredPolicy",
                    optional(value.battery_policy, |policy| {
                        object([("chargeLimitPercent", int(policy.charge_limit_percent))])
                    }),
                ),
                (
                    "protectionEnabled",
                    Json::Bool(value.battery_protection_enabled),
                ),
            ]),
        ),
        (
            "customPerformance",
            optional(value.custom_performance, custom_performance),
        ),
        (
            "gpuClockTuning",
            optional(value.gpu_clock_tuning, gpu_clock_tuning),
        ),
        ("fanControl", fan_control(&value.fan_control)),
    ])
}

/// `profiles.json` at the current schema, byte for byte as `ProfileStore`
/// writes it.
pub fn profile_document(schema: i32, value: &ProfileSet) -> Vec<u8> {
    json::write(
        &object([
            ("schemaVersion", int(schema)),
            ("profiles", profile_set(value)),
        ]),
        true,
    )
}
