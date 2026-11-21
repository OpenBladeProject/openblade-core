//! The managed `ProfileSet` shape as the Service stores and exchanges it.
//!
//! Enumerations keep the integer a client wrote, because managed binds any
//! `int` to an enum and only its validator names an undefined value. A member
//! is an `Option` wherever managed can hold `null` there and still reach a
//! validation message; a `null` managed can only reject is refused on decode.

use std::string::String;
use std::vec::Vec;

/// A managed enum on the wire: the defined values are `0..COUNT`.
macro_rules! wire_enum {
    ($(#[$meta:meta])* $name:ident { $($constant:ident = $value:literal => $text:literal),+ $(,)? }) => {
        $(#[$meta])*
        #[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
        pub struct $name(pub i32);

        impl $name {
            $(pub const $constant: Self = Self($value);)+
            pub const ALL: &'static [Self] = &[$(Self::$constant),+];

            /// Managed `Enum.IsDefined`.
            pub fn is_defined(self) -> bool {
                Self::ALL.contains(&self)
            }

            /// Managed `Enum.ToString` for a defined value.
            pub fn name(self) -> Option<&'static str> {
                match self.0 {
                    $($value => Some($text),)+
                    _ => None,
                }
            }
        }
    };
}

wire_enum!(PerformancePreset {
    SILENT = 0 => "Silent",
    BALANCED = 1 => "Balanced",
    PERFORMANCE = 2 => "Performance",
    BATTERY_SAVER = 3 => "BatterySaver",
    BATTERY_BALANCED = 4 => "BatteryBalanced",
    CUSTOM = 5 => "Custom",
    TURBO = 6 => "Turbo",
    MAXIMUM_POWER = 7 => "MaximumPower",
    GAMING = 8 => "Gaming",
    CREATOR = 9 => "Creator",
});

wire_enum!(WindowsPowerMode {
    BEST_POWER_EFFICIENCY = 0 => "BestPowerEfficiency",
    BALANCED = 1 => "Balanced",
    BEST_PERFORMANCE = 2 => "BestPerformance",
});

wire_enum!(CustomPerformanceLevel {
    LOW = 0 => "Low",
    MEDIUM = 1 => "Medium",
    HIGH = 2 => "High",
    BOOST = 3 => "Boost",
});

wire_enum!(FanMode {
    AUTOMATIC = 0 => "Automatic",
    FIXED = 1 => "Fixed",
    MANUAL_CURVE = 2 => "ManualCurve",
    MAXIMUM = 3 => "Maximum",
    STOP = 4 => "Stop",
});

impl FanMode {
    /// Managed `FanControlPowerPolicy.IsAvailable` (`FanControlPowerPolicy.cs:6-22`).
    pub fn available_on(
        self,
        source: PowerSource,
        supports_manual_fans_on_limited_power: bool,
    ) -> bool {
        match source {
            PowerSource::PluggedIn => matches!(
                self,
                Self::AUTOMATIC | Self::FIXED | Self::MANUAL_CURVE | Self::MAXIMUM
            ),
            PowerSource::UsbC => {
                self == Self::AUTOMATIC
                    || supports_manual_fans_on_limited_power
                        && matches!(self, Self::FIXED | Self::MANUAL_CURVE | Self::MAXIMUM)
            }
            PowerSource::Battery => matches!(self, Self::AUTOMATIC | Self::FIXED | Self::MAXIMUM),
        }
    }
}

wire_enum!(LightingEffect {
    OFF = 0 => "Off",
    STATIC = 1 => "Static",
    SPECTRUM = 2 => "Spectrum",
    BREATHING = 3 => "Breathing",
    REACTIVE = 4 => "Reactive",
    TIDAL = 5 => "Tidal",
    STARLIGHT = 6 => "Starlight",
    WAVE = 7 => "Wave",
    WHEEL = 8 => "Wheel",
    RIPPLE = 9 => "Ripple",
    AMBIENT_AWARENESS = 10 => "AmbientAwareness",
    AUDIO_METER = 11 => "AudioMeter",
    FIRE = 12 => "Fire",
    SEQUENCE = 13 => "Sequence",
});

wire_enum!(LogoLightingMode {
    STATIC = 0 => "Static",
    BREATHING = 1 => "Breathing",
    OFF = 2 => "Off",
});

wire_enum!(
    /// `BreathingColorMode` and `StarlightColorMode` share these values.
    ColorMode {
        SINGLE = 0 => "Single",
        DUAL = 1 => "Dual",
        RANDOM = 2 => "Random",
    }
);

wire_enum!(
    /// `ReactiveLightingDuration` and `StarlightLightingDuration`.
    LightingDuration {
        SHORT = 0 => "Short",
        MEDIUM = 1 => "Medium",
        LONG = 2 => "Long",
    }
);

wire_enum!(
    /// `LightingDirection` and `LightingRotationDirection`: two values each.
    Direction {
        FIRST = 0 => "Leftward",
        SECOND = 1 => "Rightward",
    }
);

wire_enum!(AmbientAwarenessRegion {
    FULL = 0 => "Full",
    LEFT = 1 => "Left",
    TOP = 2 => "Top",
    RIGHT = 3 => "Right",
    BOTTOM = 4 => "Bottom",
});

wire_enum!(FunctionKeyPrimaryMode {
    FUNCTION_KEYS = 0 => "FunctionKeys",
    MULTIMEDIA_KEYS = 1 => "MultimediaKeys",
});

wire_enum!(ApplicationActivationMode {
    FOREGROUND = 0 => "Foreground",
    WHILE_RUNNING_AFTER_ACTIVATION = 1 => "WhileRunningAfterActivation",
});

wire_enum!(ThxEqualizerPreset {
    GAME = 0 => "Game",
    MOVIE = 1 => "Movie",
    MUSIC = 2 => "Music",
    VOICE = 3 => "Voice",
    CUSTOM = 4 => "Custom",
});

wire_enum!(ThxApplicationAudioOutput {
    GAME = 0 => "Game",
    MOVIE = 1 => "Movie",
    MUSIC = 2 => "Music",
    VOICE = 3 => "Voice",
    CUSTOM = 4 => "Custom",
    IMMERSIVE = 5 => "Immersive",
    COMPETITIVE = 6 => "Competitive",
});

/// Managed `PowerSource` without `ExternalUnknown`, which selects no profile.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PowerSource {
    PluggedIn,
    Battery,
    UsbC,
}

impl PowerSource {
    pub const ALL: [Self; 3] = [Self::PluggedIn, Self::Battery, Self::UsbC];

    /// Managed `PowerSourcePolicy.IsExternal`.
    pub fn is_external(self) -> bool {
        self != Self::Battery
    }
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct RgbColor {
    pub red: u8,
    pub green: u8,
    pub blue: u8,
}

/// Constructed only inside `-10000..=10000`, as the managed record is.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct GpuClockOffsets {
    pub core_megahertz: i32,
    pub memory_megahertz: i32,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct GpuClockTuning {
    pub enabled: bool,
    /// `None` is a `null` the validator reports.
    pub offsets: Option<GpuClockOffsets>,
}

/// Constructed only with a defined CPU level and a GPU level below Boost.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct CustomPerformance {
    pub cpu: CustomPerformanceLevel,
    pub gpu: CustomPerformanceLevel,
    pub maximum_fans: bool,
    pub gpu_clock_offsets: Option<GpuClockOffsets>,
}

impl CustomPerformance {
    /// Managed `CustomPerformanceSettings.Default`.
    pub const DEFAULT: Self = Self {
        cpu: CustomPerformanceLevel::MEDIUM,
        gpu: CustomPerformanceLevel::HIGH,
        maximum_fans: false,
        gpu_clock_offsets: None,
    };
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct FanCurvePoint {
    pub temperature_celsius: i32,
    pub speed_rpm: i32,
}

/// Constructed only as a valid managed `ManualFanCurve`.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ManualFanCurve {
    pub cpu: Vec<FanCurvePoint>,
    pub gpu: Vec<FanCurvePoint>,
    pub use_higher_temperature_for_both: bool,
}

impl ManualFanCurve {
    /// Managed `ManualFanCurve.Default`.
    pub fn default_curve() -> Self {
        let points = |temperatures: [i32; 6], speeds: [i32; 6]| {
            temperatures
                .into_iter()
                .zip(speeds)
                .map(|(temperature_celsius, speed_rpm)| FanCurvePoint {
                    temperature_celsius,
                    speed_rpm,
                })
                .collect()
        };
        Self {
            cpu: points(
                [40, 50, 60, 70, 80, 90],
                [2000, 2100, 2600, 3400, 4400, 5400],
            ),
            gpu: points(
                [35, 45, 55, 65, 75, 85],
                [2000, 2100, 2700, 3600, 4500, 5400],
            ),
            use_higher_temperature_for_both: true,
        }
    }
}

/// Constructed only as a valid managed `FanControlSettings`: a defined mode,
/// a speed on the 2000..=5500 RPM grid, and 5400 RPM under Maximum.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FanControl {
    pub mode: FanMode,
    pub fixed_speed_rpm: i32,
    pub manual_curve: ManualFanCurve,
}

impl FanControl {
    pub const MINIMUM_SPEED_RPM: i32 = 2000;
    pub const MAXIMUM_SPEED_RPM: i32 = 5400;
    pub const MAXIMUM_SUPPORTED_SPEED_RPM: i32 = 5500;
    pub const SPEED_STEP_RPM: i32 = 100;
    const DEFAULT_FIXED_SPEED_RPM: i32 = 3700;

    /// Managed `FanControlSettings.ValidateSpeed`.
    pub fn valid_speed(speed_rpm: i32) -> bool {
        (Self::MINIMUM_SPEED_RPM..=Self::MAXIMUM_SUPPORTED_SPEED_RPM).contains(&speed_rpm)
            && speed_rpm % Self::SPEED_STEP_RPM == 0
    }

    /// Managed `FanControlSettings.FromLegacy`; `None` for an undefined mode,
    /// where the managed constructor throws.
    pub fn from_legacy(mode: FanMode) -> Option<Self> {
        mode.is_defined().then(|| Self {
            mode,
            fixed_speed_rpm: if mode == FanMode::MAXIMUM {
                Self::MAXIMUM_SPEED_RPM
            } else {
                Self::DEFAULT_FIXED_SPEED_RPM
            },
            manual_curve: ManualFanCurve::default_curve(),
        })
    }
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct IndependentLighting {
    pub brightness: Option<u8>,
    pub logo_mode: Option<LogoLightingMode>,
    pub vapor_chamber_lighting_enabled: Option<bool>,
    pub touchpad_status_indicator_enabled: Option<bool>,
}

impl IndependentLighting {
    /// Managed `IndependentLightingSettings.IsWellFormed`.
    pub fn is_well_formed(&self) -> bool {
        (self.brightness.is_some()
            || self.logo_mode.is_some()
            || self.vapor_chamber_lighting_enabled.is_some()
            || self.touchpad_status_indicator_enabled.is_some())
            && self.brightness.is_none_or(|value| value <= 100)
            && self.logo_mode.is_none_or(LogoLightingMode::is_defined)
    }
}

/// Managed `LightingEffectParameters`, discriminated by `kind`.
#[derive(Clone, Debug, PartialEq)]
pub enum LightingParameters {
    Off,
    Static {
        color: RgbColor,
    },
    SpectrumCycling,
    Breathing {
        color_mode: ColorMode,
        primary_color: Option<RgbColor>,
        secondary_color: Option<RgbColor>,
    },
    Reactive {
        color: RgbColor,
        duration: LightingDuration,
    },
    Tidal {
        primary_color: RgbColor,
        secondary_color: RgbColor,
        random_colors: bool,
        direction: Direction,
    },
    Starlight {
        color_mode: ColorMode,
        primary_color: Option<RgbColor>,
        secondary_color: Option<RgbColor>,
        duration: LightingDuration,
    },
    Wave {
        direction: Direction,
    },
    Wheel {
        direction: Direction,
    },
    Ripple {
        color: RgbColor,
    },
    AmbientAwareness {
        region: AmbientAwarenessRegion,
    },
    AudioMeter {
        color_boost: f64,
        decay_milliseconds: i32,
    },
    Fire,
    Sequence {
        layout_id: Option<String>,
        timeline_base64: Option<String>,
        looped: bool,
    },
}

#[derive(Clone, Debug, PartialEq)]
pub struct LightingSettings {
    pub effect: LightingEffect,
    pub color: RgbColor,
    pub brightness: u8,
    pub parameters: Option<LightingParameters>,
    pub logo_mode: Option<LogoLightingMode>,
    pub independent_controls: Option<IndependentLighting>,
    pub controls_keyboard_effect: bool,
}

impl LightingSettings {
    /// Managed `LightingSettingsFactory.Off`.
    pub fn off() -> Self {
        Self::of(
            LightingEffect::OFF,
            RgbColor::default(),
            0,
            LightingParameters::Off,
        )
    }

    /// A factory result: effect data only, every other member at its default.
    pub fn of(
        effect: LightingEffect,
        color: RgbColor,
        brightness: u8,
        parameters: LightingParameters,
    ) -> Self {
        Self {
            effect,
            color,
            brightness,
            parameters: Some(parameters),
            logo_mode: None,
            independent_controls: None,
            controls_keyboard_effect: false,
        }
    }
}

/// Constructed only with a timeout of 1..=60 minutes or none.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct LightingPowerPolicy {
    pub turn_off_when_display_turns_off: bool,
    pub idle_timeout_minutes: Option<i32>,
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct DisplaySettings {
    pub refresh_rate_key: Option<String>,
    pub hdr_enabled: Option<bool>,
    pub color_profile_key: Option<String>,
    pub brightness_percent: Option<u8>,
}

impl DisplaySettings {
    /// Managed `DisplayProfileSettings.IsConfigured`.
    pub fn is_configured(&self) -> bool {
        self.refresh_rate_key.is_some()
            || self.hdr_enabled.is_some()
            || self.color_profile_key.is_some()
            || self.brightness_percent.is_some()
    }
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct GamingModeKeyPolicy {
    pub block_windows_key: bool,
    pub block_alt_tab: bool,
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct LaptopBehavior {
    pub startup_animation_enabled: Option<bool>,
    pub function_key_primary: Option<FunctionKeyPrimaryMode>,
    pub gaming_mode_enabled: Option<bool>,
    pub gaming_mode_key_policy: Option<GamingModeKeyPolicy>,
}

impl LaptopBehavior {
    /// Managed `LaptopBehaviorProfileSettings.IsConfigured`.
    pub fn is_configured(&self) -> bool {
        self.startup_animation_enabled.is_some()
            || self.function_key_primary.is_some()
            || self.gaming_mode_enabled.is_some()
            || self.gaming_mode_key_policy.is_some()
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ThxEnhancement {
    pub enabled: bool,
    pub level_percent: i32,
}

/// One gain list per `ThxEqualizerPreset`, in its order.
#[derive(Clone, Debug, PartialEq)]
pub struct ThxEqualizerCurves(pub [Vec<f64>; 5]);

#[derive(Clone, Debug, PartialEq)]
pub struct ThxEndpoint {
    pub spatial_enabled: bool,
    pub equalizer_enabled: bool,
    pub selected_preset: ThxEqualizerPreset,
    pub equalizer_curves: ThxEqualizerCurves,
    pub voice_clarity: ThxEnhancement,
    pub sound_normalization: Option<ThxEnhancement>,
}

/// Constructed only with the normalised name managed's constructor stores.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ThxApplicationRule {
    pub executable_name: String,
    pub output: ThxApplicationAudioOutput,
}

#[derive(Clone, Debug, PartialEq)]
pub struct ThxAudioProfile {
    pub application_routing_enabled: bool,
    pub speakers: ThxEndpoint,
    pub headphones: ThxEndpoint,
    pub application_rules: Vec<ThxApplicationRule>,
}

/// Managed binds `null` to any reference member inside the THX settings and
/// reports all of them with one validation message.
#[derive(Clone, Debug, PartialEq)]
pub enum ThxAudio {
    Typed(std::boxed::Box<ThxAudioProfile>),
    NullMember,
}

#[derive(Clone, Debug, PartialEq)]
pub struct PowerProfile {
    pub performance: PerformancePreset,
    pub fans: FanMode,
    /// `None` is a `null` that managed normalisation reports.
    pub lighting: Option<LightingSettings>,
    pub display: Option<DisplaySettings>,
    pub stop_fans_in_battery_saver: bool,
    pub custom_performance: Option<CustomPerformance>,
    pub gpu_clock_tuning: Option<GpuClockTuning>,
    pub fan_control: Option<FanControl>,
    pub lighting_power_policy: Option<LightingPowerPolicy>,
    pub laptop_behavior: Option<LaptopBehavior>,
    pub battery_protection_enabled: Option<bool>,
    pub thx_audio: Option<ThxAudio>,
    /// Schemas 12 to 14 only; current documents and requests leave it clear.
    pub windows_power_mode: Option<WindowsPowerMode>,
}

impl PowerProfile {
    /// Managed `EffectiveLaptopBehavior`.
    pub fn effective_laptop_behavior(&self) -> LaptopBehavior {
        self.laptop_behavior.unwrap_or_default()
    }
}

/// Constructed only inside 50..=99 percent.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct BatteryPolicy {
    pub charge_limit_percent: i32,
}

#[derive(Clone, Debug, Default, PartialEq)]
pub struct ApplicationOverride {
    pub performance: Option<PerformancePreset>,
    pub custom_performance: Option<CustomPerformance>,
    pub gpu_clock_tuning: Option<GpuClockTuning>,
    pub fans: Option<FanMode>,
    pub fan_control: Option<FanControl>,
    pub lighting: Option<LightingSettings>,
    pub display: Option<DisplaySettings>,
    pub gaming_mode_enabled: Option<bool>,
    pub gaming_mode_key_policy: Option<GamingModeKeyPolicy>,
}

impl ApplicationOverride {
    /// Managed `ApplicationPowerProfileOverride.IsConfigured`.
    pub fn is_configured(&self) -> bool {
        self.performance.is_some()
            || self.custom_performance.is_some()
            || self.gpu_clock_tuning.is_some()
            || self.fans.is_some()
            || self.fan_control.is_some()
            || self.lighting.is_some()
            || self
                .display
                .as_ref()
                .is_some_and(DisplaySettings::is_configured)
            || self.gaming_mode_enabled.is_some()
            || self.gaming_mode_key_policy.is_some()
    }
}

#[derive(Clone, Debug, Default, PartialEq)]
pub struct ApplicationOverrides {
    pub plugged_in: Option<ApplicationOverride>,
    pub battery: Option<ApplicationOverride>,
    pub usb_c: Option<ApplicationOverride>,
}

impl ApplicationOverrides {
    pub fn for_source(&self, source: PowerSource) -> Option<&ApplicationOverride> {
        match source {
            PowerSource::PluggedIn => self.plugged_in.as_ref(),
            PowerSource::Battery => self.battery.as_ref(),
            PowerSource::UsbC => self.usb_c.as_ref(),
        }
    }

    /// Managed `ApplicationProfileOverrideSet.IsConfigured`.
    pub fn is_configured(&self) -> bool {
        PowerSource::ALL.into_iter().any(|source| {
            self.for_source(source)
                .is_some_and(ApplicationOverride::is_configured)
        })
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct ApplicationProfile {
    /// The GUID in managed's lowercase hyphenated form.
    pub id: String,
    pub name: String,
    pub executable_path: String,
    pub activation_mode: ApplicationActivationMode,
    pub overrides: ApplicationOverrides,
    pub enabled: bool,
}

impl ApplicationProfile {
    pub const MAXIMUM_NAME_LENGTH: usize = 80;
    pub const MAXIMUM_EXECUTABLE_PATH_LENGTH: usize = 32767;
    pub const MAXIMUM_PROFILES: usize = 128;
    pub const EMPTY_ID: &'static str = "00000000-0000-0000-0000-000000000000";

    /// The view managed's selection reads.
    pub fn rule(&self) -> crate::application_context::Rule<'_> {
        crate::application_context::Rule {
            id: &self.id,
            executable_path: &self.executable_path,
            enabled: self.enabled,
        }
    }
}

/// Managed `WindowsPowerModeAssociations`, indexed by `PerformancePreset`.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct WindowsPowerModes(pub [Option<WindowsPowerMode>; 10]);

impl WindowsPowerModes {
    /// The six presets schemas 12 to 14 stored an association for.
    pub const LEGACY_ROLES: [PerformancePreset; 6] = [
        PerformancePreset::SILENT,
        PerformancePreset::BALANCED,
        PerformancePreset::PERFORMANCE,
        PerformancePreset::BATTERY_SAVER,
        PerformancePreset::BATTERY_BALANCED,
        PerformancePreset::CUSTOM,
    ];
}

#[derive(Clone, Debug, PartialEq)]
pub struct ProfileSet {
    pub plugged_in: PowerProfile,
    pub battery: PowerProfile,
    pub usb_c: PowerProfile,
    /// `None` is a `null` the validator reports.
    pub battery_policy: Option<BatteryPolicy>,
    /// The schema 4 migration source; current documents store `null`.
    pub lighting_power_policy: Option<LightingPowerPolicy>,
    pub application_profiles: Vec<ApplicationProfile>,
    pub windows_power_modes: WindowsPowerModes,
}

impl ProfileSet {
    pub fn for_source(&self, source: PowerSource) -> &PowerProfile {
        match source {
            PowerSource::PluggedIn => &self.plugged_in,
            PowerSource::Battery => &self.battery,
            PowerSource::UsbC => &self.usb_c,
        }
    }

    pub fn for_source_mut(&mut self, source: PowerSource) -> &mut PowerProfile {
        match source {
            PowerSource::PluggedIn => &mut self.plugged_in,
            PowerSource::Battery => &mut self.battery,
            PowerSource::UsbC => &mut self.usb_c,
        }
    }

    /// Managed `ProfileDefaults.CreateSafeDefaults`.
    pub fn safe_defaults() -> Self {
        let profile = |performance, custom_performance| PowerProfile {
            performance,
            fans: FanMode::AUTOMATIC,
            lighting: Some(LightingSettings::off()),
            display: Some(DisplaySettings::default()),
            stop_fans_in_battery_saver: false,
            custom_performance,
            gpu_clock_tuning: None,
            fan_control: None,
            lighting_power_policy: Some(LightingPowerPolicy::default()),
            laptop_behavior: None,
            battery_protection_enabled: Some(true),
            thx_audio: None,
            windows_power_mode: None,
        };
        Self {
            plugged_in: profile(
                PerformancePreset::BALANCED,
                Some(CustomPerformance::DEFAULT),
            ),
            battery: profile(PerformancePreset::BATTERY_SAVER, None),
            usb_c: profile(PerformancePreset::BALANCED, None),
            battery_policy: Some(BatteryPolicy {
                charge_limit_percent: 80,
            }),
            lighting_power_policy: None,
            application_profiles: Vec::new(),
            windows_power_modes: WindowsPowerModes::default(),
        }
    }
}
