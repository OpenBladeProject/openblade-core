//! Binds JSON to the typed model as `System.Text.Json` binds the managed
//! records: required constructor parameters, unmapped-member rejection, the
//! record constructors' own range checks and the `kind` discriminator first.
//!
//! Member names are matched exactly. Managed's IPC reader also accepts other
//! letter case; the Tray and the stored documents always write camelCase.
//! A member managed computes or ignores, such as `isConfigured`, is accepted
//! with any value and dropped, as managed does.

use super::json::{Json, Malformed};
use super::model::*;
use super::text;
use std::string::String;
use std::vec::Vec;

type Decoded<T> = Result<T, Malformed>;

/// Which managed serializer options read the value.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Dialect {
    /// `IpcMessageCodec`: the battery policy has its own strict converter.
    Request,
    /// `ProfileStore.SerializerOptions`.
    Document,
}

struct Object<'a>(&'a [(String, Json)]);

impl<'a> Object<'a> {
    /// A JSON object whose every member is one of `names`.
    fn of(json: &'a Json, names: &[&str]) -> Decoded<Self> {
        let members = json.members().ok_or(Malformed)?;
        if members
            .iter()
            .all(|(name, _)| names.contains(&name.as_str()))
        {
            Ok(Self(members))
        } else {
            Err(Malformed)
        }
    }

    fn get(&self, name: &str) -> Option<&'a Json> {
        self.0
            .iter()
            .find_map(|(key, value)| (key == name).then_some(value))
    }

    /// A constructor parameter without a default must be present.
    fn required(&self, name: &str) -> Decoded<&'a Json> {
        self.get(name).ok_or(Malformed)
    }

    /// A member that is absent or `null`, or else read by `read`.
    fn optional<T>(
        &self,
        name: &str,
        read: impl FnOnce(&'a Json) -> Decoded<T>,
    ) -> Decoded<Option<T>> {
        nullable(self.get(name), read)
    }

    /// A present member that may hold `null`.
    fn nullable<T>(
        &self,
        name: &str,
        read: impl FnOnce(&'a Json) -> Decoded<T>,
    ) -> Decoded<Option<T>> {
        nullable(Some(self.required(name)?), read)
    }

    /// An init-only value member: its default when absent, never `null`.
    fn or<T>(
        &self,
        name: &str,
        default: T,
        read: impl FnOnce(&'a Json) -> Decoded<T>,
    ) -> Decoded<T> {
        self.get(name).map_or(Ok(default), read)
    }
}

fn nullable<'a, T>(
    json: Option<&'a Json>,
    read: impl FnOnce(&'a Json) -> Decoded<T>,
) -> Decoded<Option<T>> {
    match json {
        None | Some(Json::Null) => Ok(None),
        Some(value) => read(value).map(Some),
    }
}

fn int(json: &Json) -> Decoded<i32> {
    json.web_integer(i32::MIN.into(), i32::MAX.into())
        .map(|value| value as i32)
        .ok_or(Malformed)
}

fn byte(json: &Json) -> Decoded<u8> {
    json.web_integer(0, 255)
        .map(|value| value as u8)
        .ok_or(Malformed)
}

/// An enum binds a JSON number only, never its text.
fn wire(json: &Json) -> Decoded<i32> {
    json.integer(i32::MIN.into(), i32::MAX.into())
        .map(|value| value as i32)
        .ok_or(Malformed)
}

fn boolean(json: &Json) -> Decoded<bool> {
    json.boolean().ok_or(Malformed)
}

fn string(json: &Json) -> Decoded<String> {
    json.text().map(Into::into).ok_or(Malformed)
}

fn double(json: &Json) -> Decoded<f64> {
    json.web_double().ok_or(Malformed)
}

fn list<'a, T>(json: &'a Json, read: impl Fn(&'a Json) -> Decoded<T>) -> Decoded<Vec<T>> {
    match json {
        Json::Array(items) => items.iter().map(read).collect(),
        _ => Err(Malformed),
    }
}

/// A record struct binds through its parameterless constructor, so every
/// component is optional.
fn color(json: &Json) -> Decoded<RgbColor> {
    let object = Object::of(json, &["red", "green", "blue"])?;
    Ok(RgbColor {
        red: object.or("red", 0, byte)?,
        green: object.or("green", 0, byte)?,
        blue: object.or("blue", 0, byte)?,
    })
}

fn gpu_clock_offsets(json: &Json) -> Decoded<GpuClockOffsets> {
    let object = Object::of(json, &["coreMegahertz", "memoryMegahertz"])?;
    let offsets = GpuClockOffsets {
        core_megahertz: int(object.required("coreMegahertz")?)?,
        memory_megahertz: int(object.required("memoryMegahertz")?)?,
    };
    let bounded = |value: i32| (-10_000..=10_000).contains(&value);
    if bounded(offsets.core_megahertz) && bounded(offsets.memory_megahertz) {
        Ok(offsets)
    } else {
        Err(Malformed)
    }
}

/// `GpuClockTuningSettings` as a stored document binds it; other readers of
/// managed records (the HyperBoost ownership journal) reuse it.
pub fn gpu_clock_tuning(json: &Json) -> Decoded<GpuClockTuning> {
    let object = Object::of(json, &["enabled", "offsets", "effectiveOffsets"])?;
    Ok(GpuClockTuning {
        enabled: boolean(object.required("enabled")?)?,
        offsets: object.nullable("offsets", gpu_clock_offsets)?,
    })
}

/// `CustomPerformanceSettings` with its constructor's level checks.
pub fn custom_performance(json: &Json) -> Decoded<CustomPerformance> {
    let object = Object::of(json, &["cpu", "gpu", "maximumFans", "gpuClockOffsets"])?;
    let settings = CustomPerformance {
        cpu: CustomPerformanceLevel(wire(object.required("cpu")?)?),
        gpu: CustomPerformanceLevel(wire(object.required("gpu")?)?),
        maximum_fans: object.or("maximumFans", false, boolean)?,
        gpu_clock_offsets: object.optional("gpuClockOffsets", gpu_clock_offsets)?,
    };
    if settings.cpu.is_defined()
        && settings.gpu.is_defined()
        && settings.gpu != CustomPerformanceLevel::BOOST
    {
        Ok(settings)
    } else {
        Err(Malformed)
    }
}

fn fan_curve_point(json: &Json) -> Decoded<FanCurvePoint> {
    let object = Object::of(json, &["temperatureCelsius", "speedRpm"])?;
    let point = FanCurvePoint {
        temperature_celsius: int(object.required("temperatureCelsius")?)?,
        speed_rpm: int(object.required("speedRpm")?)?,
    };
    if (20..=100).contains(&point.temperature_celsius) && FanControl::valid_speed(point.speed_rpm) {
        Ok(point)
    } else {
        Err(Malformed)
    }
}

fn fan_curve_points(json: &Json) -> Decoded<Vec<FanCurvePoint>> {
    let points = list(json, fan_curve_point)?;
    let ordered = points.windows(2).all(|pair| {
        pair[1].temperature_celsius > pair[0].temperature_celsius
            && pair[1].speed_rpm >= pair[0].speed_rpm
    });
    if (2..=10).contains(&points.len()) && ordered {
        Ok(points)
    } else {
        Err(Malformed)
    }
}

fn manual_fan_curve(json: &Json) -> Decoded<ManualFanCurve> {
    let object = Object::of(json, &["cpu", "gpu", "useHigherTemperatureForBoth"])?;
    Ok(ManualFanCurve {
        cpu: fan_curve_points(object.required("cpu")?)?,
        gpu: fan_curve_points(object.required("gpu")?)?,
        use_higher_temperature_for_both: boolean(object.required("useHigherTemperatureForBoth")?)?,
    })
}

/// `FanControlSettings` with its required members and its constructor's
/// speed and curve checks.
pub fn fan_control(json: &Json) -> Decoded<FanControl> {
    let object = Object::of(json, &["mode", "fixedSpeedRpm", "manualCurve"])?;
    let mode = FanMode(wire(object.required("mode")?)?);
    let fixed_speed_rpm = int(object.required("fixedSpeedRpm")?)?;
    let manual_curve = manual_fan_curve(object.required("manualCurve")?)?;
    if !mode.is_defined() || !FanControl::valid_speed(fixed_speed_rpm) {
        return Err(Malformed);
    }
    Ok(FanControl {
        mode,
        fixed_speed_rpm: if mode == FanMode::MAXIMUM {
            FanControl::MAXIMUM_SPEED_RPM
        } else {
            fixed_speed_rpm
        },
        manual_curve,
    })
}

fn independent_lighting(json: &Json) -> Decoded<IndependentLighting> {
    let object = Object::of(
        json,
        &[
            "brightness",
            "logoMode",
            "vaporChamberLightingEnabled",
            "touchpadStatusIndicatorEnabled",
            "isWellFormed",
        ],
    )?;
    Ok(IndependentLighting {
        brightness: object.optional("brightness", byte)?,
        logo_mode: object.optional("logoMode", wire)?.map(LogoLightingMode),
        vapor_chamber_lighting_enabled: object.optional("vaporChamberLightingEnabled", boolean)?,
        touchpad_status_indicator_enabled: object
            .optional("touchpadStatusIndicatorEnabled", boolean)?,
    })
}

fn lighting_parameters(json: &Json) -> Decoded<LightingParameters> {
    // The discriminator of an abstract managed type must come first.
    let Some(((first, kind), _)) = json.members().ok_or(Malformed)?.split_first() else {
        return Err(Malformed);
    };
    if first != "kind" {
        return Err(Malformed);
    }
    let fields = |names: &[&str]| {
        let mut all = Vec::from(["kind"]);
        all.extend_from_slice(names);
        Object::of(json, &all)
    };
    Ok(match kind.text().ok_or(Malformed)? {
        "off" => fields(&[]).map(|_| LightingParameters::Off)?,
        "spectrumCycling" => fields(&[]).map(|_| LightingParameters::SpectrumCycling)?,
        "fire" => fields(&[]).map(|_| LightingParameters::Fire)?,
        "static" => LightingParameters::Static {
            color: color(fields(&["color"])?.required("color")?)?,
        },
        "ripple" => LightingParameters::Ripple {
            color: color(fields(&["color"])?.required("color")?)?,
        },
        "breathing" => {
            let object = fields(&["colorMode", "primaryColor", "secondaryColor"])?;
            LightingParameters::Breathing {
                color_mode: ColorMode(wire(object.required("colorMode")?)?),
                primary_color: object.nullable("primaryColor", color)?,
                secondary_color: object.nullable("secondaryColor", color)?,
            }
        }
        "reactive" => {
            let object = fields(&["color", "duration"])?;
            LightingParameters::Reactive {
                color: color(object.required("color")?)?,
                duration: LightingDuration(wire(object.required("duration")?)?),
            }
        }
        "tidal" => {
            let object = fields(&[
                "primaryColor",
                "secondaryColor",
                "randomColors",
                "direction",
            ])?;
            LightingParameters::Tidal {
                primary_color: color(object.required("primaryColor")?)?,
                secondary_color: color(object.required("secondaryColor")?)?,
                random_colors: boolean(object.required("randomColors")?)?,
                direction: Direction(wire(object.required("direction")?)?),
            }
        }
        "starlight" => {
            let object = fields(&["colorMode", "primaryColor", "secondaryColor", "duration"])?;
            LightingParameters::Starlight {
                color_mode: ColorMode(wire(object.required("colorMode")?)?),
                primary_color: object.nullable("primaryColor", color)?,
                secondary_color: object.nullable("secondaryColor", color)?,
                duration: LightingDuration(wire(object.required("duration")?)?),
            }
        }
        "wave" => LightingParameters::Wave {
            direction: Direction(wire(fields(&["direction"])?.required("direction")?)?),
        },
        "wheel" => LightingParameters::Wheel {
            direction: Direction(wire(fields(&["direction"])?.required("direction")?)?),
        },
        "ambientAwareness" => LightingParameters::AmbientAwareness {
            region: AmbientAwarenessRegion(wire(fields(&["region"])?.required("region")?)?),
        },
        "audioMeter" => {
            let object = fields(&["colorBoost", "decayMilliseconds"])?;
            LightingParameters::AudioMeter {
                color_boost: double(object.required("colorBoost")?)?,
                decay_milliseconds: object.or("decayMilliseconds", 0, int)?,
            }
        }
        "sequence" => {
            let object = fields(&["layoutId", "timelineBase64", "loop"])?;
            LightingParameters::Sequence {
                layout_id: object.nullable("layoutId", string)?,
                timeline_base64: object.nullable("timelineBase64", string)?,
                looped: boolean(object.required("loop")?)?,
            }
        }
        _ => return Err(Malformed),
    })
}

pub(super) fn lighting(json: &Json) -> Decoded<LightingSettings> {
    let object = Object::of(
        json,
        &[
            "effect",
            "color",
            "brightness",
            "parameters",
            "logoMode",
            "independentControls",
            "controlsKeyboardEffect",
        ],
    )?;
    Ok(LightingSettings {
        effect: LightingEffect(wire(object.required("effect")?)?),
        color: color(object.required("color")?)?,
        brightness: byte(object.required("brightness")?)?,
        parameters: object.optional("parameters", lighting_parameters)?,
        logo_mode: object.optional("logoMode", wire)?.map(LogoLightingMode),
        independent_controls: object.optional("independentControls", independent_lighting)?,
        controls_keyboard_effect: object.or("controlsKeyboardEffect", false, boolean)?,
    })
}

fn lighting_power_policy(json: &Json) -> Decoded<LightingPowerPolicy> {
    let object = Object::of(json, &["turnOffWhenDisplayTurnsOff", "idleTimeoutMinutes"])?;
    let policy = LightingPowerPolicy {
        turn_off_when_display_turns_off: boolean(object.required("turnOffWhenDisplayTurnsOff")?)?,
        idle_timeout_minutes: object.nullable("idleTimeoutMinutes", int)?,
    };
    if policy
        .idle_timeout_minutes
        .is_none_or(|minutes| (1..=60).contains(&minutes))
    {
        Ok(policy)
    } else {
        Err(Malformed)
    }
}

/// A managed `DisplayProfileSettings` alone, as the IPC codec writes it inside
/// a power profile or an application override. The native Agent's display
/// automation reads these members of Service status without the rest of the
/// profile set.
pub fn display_settings(json: &Json) -> Result<DisplaySettings, Malformed> {
    display(json)
}

fn display(json: &Json) -> Decoded<DisplaySettings> {
    let object = Object::of(
        json,
        &[
            "refreshRateKey",
            "hdrEnabled",
            "colorProfileKey",
            "brightnessPercent",
            "isConfigured",
        ],
    )?;
    Ok(DisplaySettings {
        refresh_rate_key: object.nullable("refreshRateKey", string)?,
        hdr_enabled: object.nullable("hdrEnabled", boolean)?,
        color_profile_key: object.nullable("colorProfileKey", string)?,
        brightness_percent: object.optional("brightnessPercent", byte)?,
    })
}

fn gaming_mode_key_policy(json: &Json) -> Decoded<GamingModeKeyPolicy> {
    let object = Object::of(json, &["blockWindowsKey", "blockAltTab"])?;
    Ok(GamingModeKeyPolicy {
        block_windows_key: boolean(object.required("blockWindowsKey")?)?,
        block_alt_tab: boolean(object.required("blockAltTab")?)?,
    })
}

fn laptop_behavior(json: &Json) -> Decoded<LaptopBehavior> {
    let object = Object::of(
        json,
        &[
            "startupAnimationEnabled",
            "functionKeyPrimary",
            "gamingModeEnabled",
            "gamingModeKeyPolicy",
            "isConfigured",
            "effectiveGamingModeKeyPolicy",
        ],
    )?;
    Ok(LaptopBehavior {
        startup_animation_enabled: object.nullable("startupAnimationEnabled", boolean)?,
        function_key_primary: object
            .nullable("functionKeyPrimary", wire)?
            .map(FunctionKeyPrimaryMode),
        gaming_mode_enabled: object.nullable("gamingModeEnabled", boolean)?,
        gaming_mode_key_policy: object.optional("gamingModeKeyPolicy", gaming_mode_key_policy)?,
    })
}

/// A THX member read below `thxAudio`: `None` once managed would hold `null`
/// in a reference member there.
type Thx<T> = Decoded<Option<T>>;

fn thx_enhancement(json: &Json) -> Decoded<ThxEnhancement> {
    let object = Object::of(json, &["enabled", "levelPercent"])?;
    Ok(ThxEnhancement {
        enabled: boolean(object.required("enabled")?)?,
        level_percent: int(object.required("levelPercent")?)?,
    })
}

fn thx_curves(json: &Json) -> Thx<ThxEqualizerCurves> {
    const NAMES: [&str; 5] = ["game", "movie", "music", "voice", "custom"];
    let object = Object::of(json, &NAMES)?;
    let mut curves: [Vec<f64>; 5] = Default::default();
    let mut typed = true;
    for (curve, name) in curves.iter_mut().zip(NAMES) {
        match object.nullable(name, |gains| list(gains, double))? {
            Some(gains) => *curve = gains,
            None => typed = false,
        }
    }
    Ok(typed.then_some(ThxEqualizerCurves(curves)))
}

fn thx_endpoint(json: &Json) -> Thx<ThxEndpoint> {
    let object = Object::of(
        json,
        &[
            "spatialEnabled",
            "equalizerEnabled",
            "selectedPreset",
            "equalizerCurves",
            "voiceClarity",
            "soundNormalization",
        ],
    )?;
    let spatial_enabled = boolean(object.required("spatialEnabled")?)?;
    let equalizer_enabled = boolean(object.required("equalizerEnabled")?)?;
    let selected_preset = ThxEqualizerPreset(wire(object.required("selectedPreset")?)?);
    let equalizer_curves = object.nullable("equalizerCurves", thx_curves)?.flatten();
    let voice_clarity = object.nullable("voiceClarity", thx_enhancement)?;
    let sound_normalization = object.nullable("soundNormalization", thx_enhancement)?;
    Ok(equalizer_curves
        .zip(voice_clarity)
        .map(|(equalizer_curves, voice_clarity)| ThxEndpoint {
            spatial_enabled,
            equalizer_enabled,
            selected_preset,
            equalizer_curves,
            voice_clarity,
            sound_normalization,
        }))
}

fn thx_rule(json: &Json) -> Decoded<ThxApplicationRule> {
    let object = Object::of(json, &["executableName", "output"])?;
    let name = string(object.required("executableName")?)?;
    let output = ThxApplicationAudioOutput(wire(object.required("output")?)?);
    Ok(ThxApplicationRule {
        executable_name: text::normalize_thx_executable_name(&name).ok_or(Malformed)?,
        output,
    })
}

fn thx_audio(json: &Json) -> Decoded<ThxAudio> {
    let object = Object::of(
        json,
        &[
            "applicationRoutingEnabled",
            "speakers",
            "headphones",
            "applicationRules",
        ],
    )?;
    let application_routing_enabled = boolean(object.required("applicationRoutingEnabled")?)?;
    let speakers = object.nullable("speakers", thx_endpoint)?.flatten();
    let headphones = object.nullable("headphones", thx_endpoint)?.flatten();
    let rules = object.nullable("applicationRules", |rules| {
        list(rules, |rule| nullable(Some(rule), thx_rule))
    })?;
    let rules: Option<Vec<ThxApplicationRule>> =
        rules.and_then(|rules| rules.into_iter().collect());
    Ok(match (speakers, headphones, rules) {
        (Some(speakers), Some(headphones), Some(application_rules)) => {
            ThxAudio::Typed(std::boxed::Box::new(ThxAudioProfile {
                application_routing_enabled,
                speakers,
                headphones,
                application_rules,
            }))
        }
        _ => ThxAudio::NullMember,
    })
}

fn power_profile(json: &Json) -> Decoded<PowerProfile> {
    let object = Object::of(
        json,
        &[
            "performance",
            "fans",
            "lighting",
            "display",
            "stopFansInBatterySaver",
            "customPerformance",
            "gpuClockTuning",
            "fanControl",
            "lightingPowerPolicy",
            "laptopBehavior",
            "batteryProtectionEnabled",
            "thxAudio",
            "windowsPowerMode",
            "effectiveLightingPowerPolicy",
            "effectiveLaptopBehavior",
            "effectiveBatteryProtectionEnabled",
        ],
    )?;
    Ok(PowerProfile {
        performance: PerformancePreset(wire(object.required("performance")?)?),
        fans: FanMode(wire(object.required("fans")?)?),
        lighting: object.nullable("lighting", lighting)?,
        display: object.optional("display", display)?,
        stop_fans_in_battery_saver: object.or("stopFansInBatterySaver", false, boolean)?,
        custom_performance: object.optional("customPerformance", custom_performance)?,
        gpu_clock_tuning: object.optional("gpuClockTuning", gpu_clock_tuning)?,
        fan_control: object.optional("fanControl", fan_control)?,
        lighting_power_policy: object.optional("lightingPowerPolicy", lighting_power_policy)?,
        laptop_behavior: object.optional("laptopBehavior", laptop_behavior)?,
        battery_protection_enabled: object.optional("batteryProtectionEnabled", boolean)?,
        thx_audio: object.optional("thxAudio", thx_audio)?,
        windows_power_mode: object
            .optional("windowsPowerMode", wire)?
            .map(WindowsPowerMode),
    })
}

fn battery_policy(json: &Json, dialect: Dialect) -> Decoded<BatteryPolicy> {
    let limit = json.member("chargeLimitPercent").ok_or(Malformed)?;
    let _ = Object::of(json, &["chargeLimitPercent"])?;
    let charge_limit_percent = match dialect {
        Dialect::Request => wire(limit)?,
        Dialect::Document => int(limit)?,
    };
    if (50..=99).contains(&charge_limit_percent) {
        Ok(BatteryPolicy {
            charge_limit_percent,
        })
    } else {
        Err(Malformed)
    }
}

fn application_override(json: &Json) -> Decoded<ApplicationOverride> {
    let object = Object::of(
        json,
        &[
            "performance",
            "customPerformance",
            "gpuClockTuning",
            "fans",
            "fanControl",
            "lighting",
            "display",
            "gamingModeEnabled",
            "gamingModeKeyPolicy",
            "isConfigured",
        ],
    )?;
    Ok(ApplicationOverride {
        performance: object.optional("performance", wire)?.map(PerformancePreset),
        custom_performance: object.optional("customPerformance", custom_performance)?,
        gpu_clock_tuning: object.optional("gpuClockTuning", gpu_clock_tuning)?,
        fans: object.optional("fans", wire)?.map(FanMode),
        fan_control: object.optional("fanControl", fan_control)?,
        lighting: object.optional("lighting", lighting)?,
        display: object.optional("display", display)?,
        gaming_mode_enabled: object.optional("gamingModeEnabled", boolean)?,
        gaming_mode_key_policy: object.optional("gamingModeKeyPolicy", gaming_mode_key_policy)?,
    })
}

fn application_overrides(json: &Json) -> Decoded<ApplicationOverrides> {
    let object = Object::of(json, &["pluggedIn", "battery", "usbC", "isConfigured"])?;
    Ok(ApplicationOverrides {
        plugged_in: object.optional("pluggedIn", application_override)?,
        battery: object.optional("battery", application_override)?,
        usb_c: object.optional("usbC", application_override)?,
    })
}

fn application_profile(json: &Json) -> Decoded<ApplicationProfile> {
    let object = Object::of(
        json,
        &[
            "id",
            "name",
            "executablePath",
            "activationMode",
            "overrides",
            "enabled",
        ],
    )?;
    Ok(ApplicationProfile {
        id: text::guid(object.required("id")?.text().ok_or(Malformed)?).ok_or(Malformed)?,
        name: string(object.required("name")?)?,
        executable_path: string(object.required("executablePath")?)?,
        activation_mode: ApplicationActivationMode(wire(object.required("activationMode")?)?),
        overrides: application_overrides(object.required("overrides")?)?,
        enabled: object.or("enabled", true, boolean)?,
    })
}

fn windows_power_modes(json: &Json) -> Decoded<WindowsPowerModes> {
    // In `PerformancePreset` order; the first six are constructor parameters.
    const NAMES: [&str; 10] = [
        "silent",
        "balanced",
        "performance",
        "batterySaver",
        "batteryBalanced",
        "custom",
        "turbo",
        "maximumPower",
        "gaming",
        "creator",
    ];
    let object = Object::of(json, &NAMES)?;
    let mut modes = WindowsPowerModes::default();
    for (preset, name) in NAMES.into_iter().enumerate() {
        let member = if WindowsPowerModes::LEGACY_ROLES.contains(&PerformancePreset(preset as i32))
        {
            Some(object.required(name)?)
        } else {
            object.get(name)
        };
        modes.0[preset] = nullable(member, wire)?.map(WindowsPowerMode);
    }
    Ok(modes)
}

/// A managed `ProfileSet`.
pub fn profile_set(json: &Json, dialect: Dialect) -> Decoded<ProfileSet> {
    let object = Object::of(
        json,
        &[
            "pluggedIn",
            "battery",
            "usbC",
            "batteryPolicy",
            "lightingPowerPolicy",
            "applicationProfiles",
            "windowsPowerModes",
            "legacyLightingPowerPolicy",
        ],
    )?;
    Ok(ProfileSet {
        plugged_in: power_profile(object.required("pluggedIn")?)?,
        battery: power_profile(object.required("battery")?)?,
        usb_c: power_profile(object.required("usbC")?)?,
        battery_policy: object
            .nullable("batteryPolicy", |policy| battery_policy(policy, dialect))?,
        lighting_power_policy: object.optional("lightingPowerPolicy", lighting_power_policy)?,
        application_profiles: object
            .get("applicationProfiles")
            .map_or(Ok(Vec::new()), |profiles| {
                list(profiles, application_profile)
            })?,
        windows_power_modes: object
            .get("windowsPowerModes")
            .map_or(Ok(WindowsPowerModes::default()), windows_power_modes)?,
    })
}

/// The two profiles and policy of a schema 1 `LegacyProfileSet`.
pub(super) fn legacy_profile_set(
    json: &Json,
) -> Decoded<(PowerProfile, PowerProfile, Option<BatteryPolicy>)> {
    let object = Object::of(json, &["pluggedIn", "battery", "batteryPolicy"])?;
    Ok((
        power_profile(object.required("pluggedIn")?)?,
        power_profile(object.required("battery")?)?,
        object.nullable("batteryPolicy", |policy| {
            battery_policy(policy, Dialect::Document)
        })?,
    ))
}
