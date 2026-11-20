//! Managed `ProfileValidationPolicy` and the `DeviceCapabilities` members it
//! reads. Capabilities describe saved-intent admission only; they grant no
//! command or write authority.

use super::lighting::RZ09_0581_LAYOUT;
use super::model::*;
use std::vec::Vec;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct FanSpeedRange {
    pub minimum_rpm: i32,
    pub maximum_rpm: i32,
    pub step_rpm: i32,
}

impl FanSpeedRange {
    /// Managed `FanSpeedRange.Standard`.
    pub const STANDARD: Self = Self {
        minimum_rpm: FanControl::MINIMUM_SPEED_RPM,
        maximum_rpm: FanControl::MAXIMUM_SPEED_RPM,
        step_rpm: FanControl::SPEED_STEP_RPM,
    };

    pub fn is_well_formed(&self) -> bool {
        self.minimum_rpm >= FanControl::MINIMUM_SPEED_RPM
            && self.maximum_rpm <= FanControl::MAXIMUM_SUPPORTED_SPEED_RPM
            && self.minimum_rpm <= self.maximum_rpm
            && self.step_rpm == FanControl::SPEED_STEP_RPM
            && self.minimum_rpm % self.step_rpm == 0
            && self.maximum_rpm % self.step_rpm == 0
    }

    pub fn supports(&self, speed_rpm: i32) -> bool {
        self.is_well_formed()
            && (self.minimum_rpm..=self.maximum_rpm).contains(&speed_rpm)
            && speed_rpm % self.step_rpm == 0
    }
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct CustomPerformanceCapabilities {
    /// CPU and GPU level pairs.
    pub allowed_combinations: Vec<(CustomPerformanceLevel, CustomPerformanceLevel)>,
    pub supports_maximum_fans: bool,
}

impl CustomPerformanceCapabilities {
    fn combinations(cpu: &[CustomPerformanceLevel], except_high_high: bool) -> Self {
        use CustomPerformanceLevel as Level;
        let gpu = [Level::LOW, Level::MEDIUM, Level::HIGH];
        Self {
            allowed_combinations: cpu
                .iter()
                .flat_map(|cpu| gpu.map(|gpu| (*cpu, gpu)))
                .filter(|pair| !(except_high_high && *pair == (Level::HIGH, Level::HIGH)))
                .collect(),
            supports_maximum_fans: false,
        }
    }

    /// Managed `CustomPerformanceCapabilities.Supports`. The model only holds
    /// well-formed settings, so that half of the managed check is implied.
    pub fn supports(&self, settings: &CustomPerformance) -> bool {
        !self.allowed_combinations.is_empty()
            && (!settings.maximum_fans || self.supports_maximum_fans)
            && self
                .allowed_combinations
                .contains(&(settings.cpu, settings.gpu))
    }
}

/// The `DeviceCapabilities` members profile validation depends on.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Capabilities {
    pub performance_presets: Vec<PerformancePreset>,
    pub fan_modes: Vec<FanMode>,
    pub lighting_effects: Vec<LightingEffect>,
    pub fan_speed_range: Option<FanSpeedRange>,
    pub fan_speed_ranges_by_performance_preset: Vec<(PerformancePreset, FanSpeedRange)>,
    pub custom_performance: Option<CustomPerformanceCapabilities>,
    pub keyboard_lighting_layout_id: Option<&'static str>,
    pub supports_breathing_color_selection: bool,
    pub supported_breathing_color_modes: Option<Vec<ColorMode>>,
    pub supports_wave_direction_selection: bool,
    pub supports_independent_lighting_controls: bool,
    pub supports_vapor_chamber_lighting: bool,
    pub supports_touchpad_status_indicator: bool,
    pub reports_performance_with_manual_fans: bool,
    pub supports_manual_fans_on_limited_power: bool,
    pub supports_battery_saver_fan_stop: bool,
}

impl Capabilities {
    /// What the managed composition admits for exact RZ09-0581 (`1532:02E0`):
    /// `ProductionBatteryFactory.CreateWindows()` for the thermal members and
    /// `ProductionLightingFactory.CreateWindows()` for the lighting members.
    /// The shared vector `profile-capabilities-rz09-0581.json` pins every
    /// member against those two factories.
    ///
    /// Battery Saver fan stop is the factory value. Managed raises it at run
    /// time once Fan Stop is admitted; this composition has no Fan Stop.
    pub fn rz09_0581() -> Self {
        use CustomPerformanceLevel as Level;
        use PerformancePreset as Preset;
        Self {
            performance_presets: Vec::from([
                Preset::SILENT,
                Preset::BALANCED,
                Preset::PERFORMANCE,
                Preset::BATTERY_SAVER,
                Preset::BATTERY_BALANCED,
                Preset::CUSTOM,
            ]),
            fan_modes: Vec::from([
                FanMode::AUTOMATIC,
                FanMode::FIXED,
                FanMode::MANUAL_CURVE,
                FanMode::MAXIMUM,
            ]),
            lighting_effects: LightingEffect::ALL.to_vec(),
            fan_speed_range: Some(FanSpeedRange::STANDARD),
            fan_speed_ranges_by_performance_preset: Vec::new(),
            custom_performance: Some(CustomPerformanceCapabilities::combinations(
                &[Level::LOW, Level::MEDIUM, Level::HIGH],
                true,
            )),
            keyboard_lighting_layout_id: Some(RZ09_0581_LAYOUT),
            supports_breathing_color_selection: true,
            supported_breathing_color_modes: None,
            supports_wave_direction_selection: true,
            supports_independent_lighting_controls: false,
            supports_vapor_chamber_lighting: false,
            supports_touchpad_status_indicator: false,
            reports_performance_with_manual_fans: false,
            supports_manual_fans_on_limited_power: true,
            supports_battery_saver_fan_stop: false,
        }
    }

    /// Managed `GetFanIntentSpeedRange`.
    fn fan_intent_speed_range(&self, performance: PerformancePreset) -> Option<FanSpeedRange> {
        range_for(
            self.fan_speed_range,
            &self.fan_speed_ranges_by_performance_preset,
            performance,
        )
    }

    /// Managed `ManualFanCurveCapabilityPolicy.SupportsPreset`.
    fn supports_manual_curve(&self, performance: PerformancePreset) -> bool {
        self.fan_intent_speed_range(performance)
            .is_some_and(|range| range.is_well_formed())
            && self.fan_modes.contains(&FanMode::MANUAL_CURVE)
            && (!self.reports_performance_with_manual_fans
                || self.performance_presets.contains(&performance))
    }
}

fn range_for(
    shared: Option<FanSpeedRange>,
    by_preset: &[(PerformancePreset, FanSpeedRange)],
    performance: PerformancePreset,
) -> Option<FanSpeedRange> {
    if by_preset.is_empty() {
        shared
    } else {
        by_preset
            .iter()
            .find_map(|(preset, range)| (*preset == performance).then_some(*range))
    }
}

/// Managed `ProfileValidationPolicy`.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ValidationPolicy {
    pub supported_performance_fan_combinations: Vec<(PerformancePreset, FanMode)>,
    pub supported_fan_modes: Vec<FanMode>,
    pub manual_fan_curve_requires_full_power: bool,
    pub supports_manual_fans_on_limited_power: bool,
    pub supported_performance_presets: Option<Vec<PerformancePreset>>,
    pub supported_lighting_effects: Vec<LightingEffect>,
    pub supports_independent_lighting_controls: bool,
    pub supports_vapor_chamber_lighting: bool,
    pub supports_touchpad_status_indicator: bool,
    pub custom_performance: Option<CustomPerformanceCapabilities>,
    pub fan_speed_range: Option<FanSpeedRange>,
    pub fan_speed_ranges_by_performance_preset: Vec<(PerformancePreset, FanSpeedRange)>,
    pub keyboard_lighting_layout_id: Option<&'static str>,
    pub supported_breathing_color_modes: Vec<ColorMode>,
    pub supports_wave_direction_selection: bool,
}

impl ValidationPolicy {
    /// The managed constructor: everything it does not take keeps a default.
    fn new(
        combinations: Vec<(PerformancePreset, FanMode)>,
        lighting_effects: Vec<LightingEffect>,
        custom_performance: Option<CustomPerformanceCapabilities>,
    ) -> Self {
        let mut supported_fan_modes: Vec<FanMode> =
            combinations.iter().map(|pair| pair.1).collect();
        supported_fan_modes.sort();
        supported_fan_modes.dedup();
        Self {
            supported_performance_fan_combinations: combinations,
            supported_fan_modes,
            manual_fan_curve_requires_full_power: false,
            supports_manual_fans_on_limited_power: true,
            supported_performance_presets: None,
            supported_lighting_effects: lighting_effects,
            supports_independent_lighting_controls: false,
            supports_vapor_chamber_lighting: false,
            supports_touchpad_status_indicator: false,
            custom_performance,
            fan_speed_range: None,
            fan_speed_ranges_by_performance_preset: Vec::new(),
            keyboard_lighting_layout_id: None,
            supported_breathing_color_modes: ColorMode::ALL.to_vec(),
            supports_wave_direction_selection: true,
        }
    }

    /// Managed `CreateUnverifiedSafeDefaults`.
    pub fn unverified_safe_defaults() -> Self {
        use FanMode as Fan;
        use PerformancePreset as Preset;
        let manual = [Fan::MAXIMUM, Fan::FIXED, Fan::MANUAL_CURVE];
        let mut combinations: Vec<(Preset, Fan)> = Vec::new();
        for preset in Preset::ALL {
            combinations.push((*preset, Fan::AUTOMATIC));
        }
        for preset in [
            Preset::BALANCED,
            Preset::SILENT,
            Preset::PERFORMANCE,
            Preset::CUSTOM,
        ] {
            combinations.extend(manual.map(|fan| (preset, fan)));
        }
        for preset in [Preset::BATTERY_SAVER, Preset::BATTERY_BALANCED] {
            combinations.extend([(preset, Fan::MAXIMUM), (preset, Fan::FIXED)]);
        }
        let mut custom =
            CustomPerformanceCapabilities::combinations(CustomPerformanceLevel::ALL, false);
        custom.supports_maximum_fans = true;
        Self::new(combinations, Vec::from([LightingEffect::OFF]), Some(custom))
    }

    /// The managed `StoredThermalValidator` policy: every defined preset with
    /// every fan mode a profile can save.
    pub fn stored_thermal() -> Self {
        let combinations = PerformancePreset::ALL
            .iter()
            .flat_map(|preset| {
                FanMode::ALL
                    .iter()
                    .filter(|fan| **fan != FanMode::STOP)
                    .map(|fan| (*preset, *fan))
            })
            .collect();
        Self::new(combinations, Vec::from([LightingEffect::OFF]), None)
    }

    /// Managed `CreateForDeviceCapabilities(battery, lighting)`.
    pub fn for_capabilities(battery: &Capabilities, lighting: &Capabilities) -> Self {
        let mut combinations =
            Self::unverified_safe_defaults().supported_performance_fan_combinations;
        combinations.retain(|(preset, fan)| {
            *fan != FanMode::MANUAL_CURVE || battery.supports_manual_curve(*preset)
        });
        let mut effects = Vec::from([LightingEffect::OFF]);
        effects.extend(
            lighting
                .lighting_effects
                .iter()
                .filter(|effect| effect.is_defined() && **effect != LightingEffect::OFF),
        );
        Self {
            fan_speed_range: battery.fan_speed_range,
            fan_speed_ranges_by_performance_preset: battery
                .fan_speed_ranges_by_performance_preset
                .clone(),
            keyboard_lighting_layout_id: lighting.keyboard_lighting_layout_id,
            supported_breathing_color_modes: lighting
                .supported_breathing_color_modes
                .clone()
                .unwrap_or_else(|| {
                    if lighting.supports_breathing_color_selection {
                        ColorMode::ALL.to_vec()
                    } else {
                        Vec::from([ColorMode::RANDOM])
                    }
                }),
            supports_wave_direction_selection: lighting.supports_wave_direction_selection,
            supports_independent_lighting_controls: lighting.supports_independent_lighting_controls,
            supports_vapor_chamber_lighting: lighting.supports_vapor_chamber_lighting,
            supports_touchpad_status_indicator: lighting.supports_touchpad_status_indicator,
            supported_fan_modes: battery.fan_modes.clone(),
            manual_fan_curve_requires_full_power: battery.reports_performance_with_manual_fans,
            supports_manual_fans_on_limited_power: battery.supports_manual_fans_on_limited_power,
            supported_performance_presets: Some(battery.performance_presets.clone()),
            ..Self::new(
                combinations,
                effects,
                // A device without Custom admits no combination at all.
                Some(battery.custom_performance.clone().unwrap_or_default()),
            )
        }
    }

    /// Managed `GetFanSpeedRange`.
    pub fn fan_speed_range_for(&self, performance: PerformancePreset) -> Option<FanSpeedRange> {
        range_for(
            self.fan_speed_range,
            &self.fan_speed_ranges_by_performance_preset,
            performance,
        )
    }
}
