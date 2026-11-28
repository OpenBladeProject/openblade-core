//! Managed `ProfileSetValidator`. Every message is the managed text, because
//! the Tray shows the Service's reply as written.
//!
//! The split is managed's: [`Validator::validate`] checks a whole set against
//! the device, [`Validator::validate_stored_custom_intent`] checks stored
//! intent structurally, and [`Validator::validate_changed_thermal_intent`]
//! applies the device to the thermal intent a request changes.

use super::encode;
use super::json;
use super::lighting;
use super::model::*;
use super::normalize::Invalid;
use super::policy::ValidationPolicy;
use super::resolve;
use super::text;
use std::format;
use std::string::String;

type Checked = Result<(), Invalid>;

fn invalid<T>(message: impl Into<String>) -> Result<T, Invalid> {
    Err(Invalid::new(message))
}

const BLACK: RgbColor = RgbColor {
    red: 0,
    green: 0,
    blue: 0,
};

pub use crate::application_context::SamePath;

pub struct Validator {
    policy: ValidationPolicy,
    stored_thermal: ValidationPolicy,
    same_path: SamePath,
}

impl Validator {
    pub fn new(policy: ValidationPolicy, same_path: SamePath) -> Self {
        Self {
            policy,
            stored_thermal: ValidationPolicy::stored_thermal(),
            same_path,
        }
    }

    pub fn policy(&self) -> &ValidationPolicy {
        &self.policy
    }

    /// Managed `Validate`.
    pub fn validate(&self, profiles: &ProfileSet) -> Checked {
        self.validate_core(profiles, true)
    }

    /// Managed `ValidateStoredCustomIntent`: persisted thermal intent is
    /// structural data, so an unrelated edit never discards it.
    pub fn validate_stored_custom_intent(&self, profiles: &ProfileSet) -> Checked {
        self.validate_core(profiles, false)
    }

    /// Managed `ValidateChangedThermalIntent`.
    pub fn validate_changed_thermal_intent(
        &self,
        requested: &ProfileSet,
        saved: &ProfileSet,
    ) -> Checked {
        for source in PowerSource::ALL {
            let next = requested.for_source(source);
            let prior = saved.for_source(source);
            self.validate_changed_thermal_profile(next, prior, source, false)?;
            for application in &requested.application_profiles {
                let Some(next_override) = application.overrides.for_source(source) else {
                    continue;
                };
                let prior_override = saved
                    .application_profiles
                    .iter()
                    .find(|item| item.id == application.id)
                    .and_then(|item| item.overrides.for_source(source));
                let explicit_thermal_changed = next_override.performance
                    != prior_override.and_then(|value| value.performance)
                    || next_override.fans != prior_override.and_then(|value| value.fans)
                    || next_override.fan_control.as_ref()
                        != prior_override.and_then(|value| value.fan_control.as_ref())
                    || next_override.custom_performance
                        != prior_override.and_then(|value| value.custom_performance);
                let resolved = resolve::apply(next, next_override)?;
                if let Some(explicit) = next_override.custom_performance
                    && Some(explicit) != prior_override.and_then(|value| value.custom_performance)
                {
                    let mut candidate = resolved.clone();
                    candidate.custom_performance = Some(explicit);
                    self.validate_custom_performance(&candidate, "changed application", true)?;
                }
                let prior_resolved = match prior_override {
                    Some(value) => resolve::apply(prior, value)?,
                    None => prior.clone(),
                };
                self.validate_changed_thermal_profile(
                    &resolved,
                    &prior_resolved,
                    source,
                    explicit_thermal_changed,
                )?;
            }
        }
        Ok(())
    }

    fn validate_changed_thermal_profile(
        &self,
        requested: &PowerProfile,
        saved: &PowerProfile,
        source: PowerSource,
        force: bool,
    ) -> Checked {
        let unchanged = requested.performance == saved.performance
            && requested.fans == saved.fans
            && requested.fan_control == saved.fan_control
            && requested.custom_performance == saved.custom_performance;
        if !force && unchanged {
            return Ok(());
        }
        Self::validate_performance_fan_combination(
            &self.policy,
            requested.performance,
            requested.fans,
            source,
        )?;
        Self::validate_fan_control(&self.policy, requested, "changed")?;
        let mut candidate = requested.clone();
        if requested.performance != PerformancePreset::CUSTOM
            && requested.custom_performance == saved.custom_performance
        {
            candidate.custom_performance = None;
        }
        self.validate_custom_performance(&candidate, "changed", true)
    }

    fn validate_core(&self, profiles: &ProfileSet, validate_custom_capabilities: bool) -> Checked {
        for (source, name) in [
            (PowerSource::PluggedIn, "plugged-in"),
            (PowerSource::Battery, "battery"),
            (PowerSource::UsbC, "low-power"),
        ] {
            self.validate_profile(
                profiles.for_source(source),
                name,
                source,
                validate_custom_capabilities,
            )?;
        }
        if profiles.battery_policy.is_none() {
            return invalid("The battery policy is missing.");
        }
        // The model cannot hold an out-of-range charge limit or idle timeout.
        for (preset, mode) in PerformancePreset::ALL
            .iter()
            .zip(profiles.windows_power_modes.0)
        {
            if mode.is_some_and(|mode| !mode.is_defined()) {
                return invalid(format!(
                    "The Windows power mode associated with {} is undefined.",
                    preset.name().unwrap_or_default()
                ));
            }
        }
        self.validate_application_profiles(profiles, validate_custom_capabilities)
    }

    /// Managed `ValidatePerformanceFanCombination(performance, fans, source)`.
    pub fn validate_performance_fan_combination(
        policy: &ValidationPolicy,
        performance: PerformancePreset,
        fans: FanMode,
        source: PowerSource,
    ) -> Checked {
        use FanMode as Fan;
        let on_battery = source == PowerSource::Battery;
        // Managed `PerformancePowerSourcePolicy.IsAllowed`.
        let allowed = performance.is_defined()
            && (!on_battery
                || matches!(
                    performance,
                    PerformancePreset::BATTERY_SAVER | PerformancePreset::BATTERY_BALANCED
                ));
        if !allowed {
            return invalid("The performance preset is not valid for this power source.");
        }
        if fans == Fan::MANUAL_CURVE
            && policy.manual_fan_curve_requires_full_power
            && source != PowerSource::PluggedIn
        {
            return invalid(
                "Manual fan curves require confirmed full external power on this backend.",
            );
        }
        if !fans.available_on(source, policy.supports_manual_fans_on_limited_power) {
            return invalid("The selected fan mode is unavailable on this power source.");
        }
        if !policy.supported_fan_modes.contains(&fans) {
            return invalid(
                "The selected fan mode is not supported by the active device capability.",
            );
        }
        if policy
            .supported_performance_presets
            .as_ref()
            .is_some_and(|presets| !presets.contains(&performance))
        {
            return invalid(
                "The performance preset is not supported by the active device capability.",
            );
        }
        let has_preset_manual_range = matches!(fans, Fan::FIXED | Fan::MANUAL_CURVE | Fan::MAXIMUM)
            && !policy.fan_speed_ranges_by_performance_preset.is_empty()
            && policy
                .fan_speed_range_for(performance)
                .is_some_and(|range| range.is_well_formed());
        if !has_preset_manual_range
            && !policy
                .supported_performance_fan_combinations
                .contains(&(performance, fans))
        {
            return invalid(
                "The performance and fan combination is not supported by the active validation policy.",
            );
        }
        Ok(())
    }

    fn validate_profile(
        &self,
        profile: &PowerProfile,
        name: &str,
        source: PowerSource,
        validate_custom_capabilities: bool,
    ) -> Checked {
        let thermal = if validate_custom_capabilities {
            &self.policy
        } else {
            &self.stored_thermal
        };
        if let Err(Invalid(message)) = Self::validate_performance_fan_combination(
            thermal,
            profile.performance,
            profile.fans,
            source,
        ) {
            return invalid(format!("The {name} profile is invalid: {message}"));
        }
        self.validate_lighting(
            profile.lighting.as_ref(),
            name,
            !validate_custom_capabilities,
        )?;
        self.validate_custom_performance(profile, name, validate_custom_capabilities)?;
        Self::validate_gpu_clock_tuning(profile, name, source)?;
        Self::validate_fan_control(thermal, profile, name)?;
        Self::validate_display(profile.display.as_ref(), name)?;
        if profile
            .laptop_behavior
            .and_then(|behavior| behavior.function_key_primary)
            .is_some_and(|mode| !mode.is_defined())
        {
            return invalid(format!("The {name} function key behavior is undefined."));
        }
        if profile
            .thx_audio
            .as_ref()
            .is_some_and(|audio| !thx_is_well_formed(audio, self.same_path))
        {
            return invalid(format!("The {name} THX audio settings are invalid."));
        }
        if profile.battery_protection_enabled.is_none() {
            return invalid(format!(
                "The {name} profile battery protection setting is missing."
            ));
        }
        Ok(())
    }

    fn validate_custom_performance(
        &self,
        profile: &PowerProfile,
        name: &str,
        validate_capabilities: bool,
    ) -> Checked {
        use CustomPerformanceLevel as Level;
        let custom = profile.custom_performance;
        if profile.performance == PerformancePreset::CUSTOM {
            if custom.is_some_and(|custom| custom.maximum_fans)
                && profile.fans != FanMode::AUTOMATIC
            {
                return invalid(format!(
                    "The {name} Custom maximum-fan flag requires Automatic fan mode, not fixed targets or a curve."
                ));
            }
            if custom.is_none() {
                return invalid(format!(
                    "The {name} custom performance CPU/GPU levels are missing or invalid."
                ));
            }
        }
        let Some(custom) = custom.filter(|_| validate_capabilities) else {
            return Ok(());
        };
        let unsupported = match &self.policy.custom_performance {
            Some(admitted) => !admitted.supports(&custom),
            // A storage-only policy never admits a new combination or flag.
            None => {
                custom.cpu == Level::BOOST
                    || (custom.cpu, custom.gpu) == (Level::HIGH, Level::HIGH)
                    || custom.maximum_fans
            }
        };
        if unsupported {
            return invalid(format!(
                "The {name} custom performance combination is not supported by this device."
            ));
        }
        Ok(())
    }

    fn validate_gpu_clock_tuning(
        profile: &PowerProfile,
        name: &str,
        source: PowerSource,
    ) -> Checked {
        let Some(tuning) = profile.gpu_clock_tuning else {
            return Ok(());
        };
        if !source.is_external() {
            return invalid(format!(
                "The {name} profile cannot configure GPU clock tuning."
            ));
        }
        if tuning.offsets.is_none() {
            return invalid(format!("The {name} GPU clock tuning settings are invalid."));
        }
        if profile
            .custom_performance
            .is_some_and(|custom| custom.gpu_clock_offsets.is_some())
        {
            return invalid(format!(
                "The {name} profile contains both current and legacy GPU clock tuning settings."
            ));
        }
        Ok(())
    }

    fn validate_fan_control(
        policy: &ValidationPolicy,
        profile: &PowerProfile,
        name: &str,
    ) -> Checked {
        use FanMode as Fan;
        let legacy;
        let settings = match &profile.fan_control {
            Some(settings) => settings,
            None => {
                // `FanControlSettings.FromLegacy` on an undefined mode.
                legacy = FanControl::from_legacy(profile.fans).ok_or_else(|| {
                    Invalid::new("Specified argument was out of the range of valid values. (Parameter 'mode')")
                })?;
                &legacy
            }
        };
        if profile.fans == Fan::STOP || settings.mode == Fan::STOP {
            return invalid("Fan stop is selected only through the Battery Saver preference.");
        }
        let legacy_maximum = profile.fans == Fan::MAXIMUM
            && settings.mode == Fan::FIXED
            && settings.fixed_speed_rpm == FanControl::MAXIMUM_SPEED_RPM;
        if settings.mode != profile.fans && !legacy_maximum {
            return invalid(format!(
                "The {name} fan settings do not match the selected fan mode."
            ));
        }
        let manual = matches!(settings.mode, Fan::FIXED | Fan::MANUAL_CURVE | Fan::MAXIMUM);
        let Some(range) = policy.fan_speed_range_for(profile.performance) else {
            if !policy.fan_speed_ranges_by_performance_preset.is_empty() && manual {
                return invalid(format!(
                    "The {name} performance preset does not support manual fan control."
                ));
            }
            return Ok(());
        };
        let maximum_admitted = policy.supported_fan_modes.contains(&Fan::MAXIMUM);
        let supported = match settings.mode {
            // The legacy Maximum alias is intent, never a guessed 5400 target.
            Fan::FIXED => {
                legacy_maximum && maximum_admitted || range.supports(settings.fixed_speed_rpm)
            }
            Fan::MAXIMUM => maximum_admitted,
            Fan::MANUAL_CURVE => settings
                .manual_curve
                .cpu
                .iter()
                .chain(&settings.manual_curve.gpu)
                .all(|point| range.supports(point.speed_rpm)),
            _ => true,
        };
        if !supported {
            return invalid(format!(
                "The {name} fan settings contain a speed outside the device range."
            ));
        }
        Ok(())
    }

    /// Managed `ValidateLighting`.
    pub fn validate_lighting(
        &self,
        lighting: Option<&LightingSettings>,
        name: &str,
        allow_stored_independent_intent: bool,
    ) -> Checked {
        let policy = &self.policy;
        let Some(lighting) = lighting else {
            return invalid(format!("The {name} lighting settings are missing."));
        };
        if !lighting.effect.is_defined() {
            return invalid(format!("The {name} lighting effect is undefined."));
        }
        if let Some(independent) = lighting.independent_controls
            && (!independent.is_well_formed()
                || !allow_stored_independent_intent
                    && (!policy.supports_independent_lighting_controls
                        || independent.vapor_chamber_lighting_enabled.is_some()
                            && !policy.supports_vapor_chamber_lighting
                        || independent.touchpad_status_indicator_enabled.is_some()
                            && !policy.supports_touchpad_status_indicator))
        {
            return invalid(format!(
                "The {name} independent lighting settings are invalid or unavailable."
            ));
        }
        if policy.supports_independent_lighting_controls
            && (!lighting.controls_keyboard_effect || policy.supported_lighting_effects.is_empty())
            || allow_stored_independent_intent
                && lighting.independent_controls.is_some()
                && !lighting.controls_keyboard_effect
        {
            // The saved effect stays as unmanaged intent beside the
            // independently opted-in fields.
            if !lighting::is_well_formed(lighting) {
                return invalid(format!(
                    "The {name} independent lighting settings are invalid."
                ));
            }
            return Ok(());
        }
        if !policy.supported_lighting_effects.contains(&lighting.effect) {
            return invalid(format!(
                "The {name} lighting effect is not supported by the active validation policy."
            ));
        }
        if lighting.brightness > 100
            || lighting.effect != LightingEffect::OFF && lighting.brightness == 0
        {
            return invalid(format!(
                "The {name} lighting brightness must be between 1 and 100 percent when lighting is on."
            ));
        }
        let consumes_color = matches!(
            lighting.effect,
            LightingEffect::STATIC
                | LightingEffect::BREATHING
                | LightingEffect::REACTIVE
                | LightingEffect::RIPPLE
                | LightingEffect::TIDAL
                | LightingEffect::STARLIGHT
        );
        if !consumes_color && lighting.color != BLACK {
            return invalid(format!(
                "The {name} lighting effect does not accept a color."
            ));
        }
        if lighting.effect == LightingEffect::OFF && lighting.brightness != 0 {
            return invalid(format!(
                "The {name} lighting brightness must be zero when lighting is off."
            ));
        }
        if !lighting::is_well_formed(lighting) {
            return invalid(format!(
                "The {name} lighting effect parameters are invalid."
            ));
        }
        match &lighting.parameters {
            Some(LightingParameters::Breathing { color_mode, .. })
                if !policy.supported_breathing_color_modes.contains(color_mode) =>
            {
                invalid(format!(
                    "The {name} lighting policy does not support this Breathing color mode."
                ))
            }
            Some(LightingParameters::Wave { direction })
                if !policy.supports_wave_direction_selection && *direction != Direction::SECOND =>
            {
                invalid(format!(
                    "The {name} lighting policy supports the upstream Wave direction only."
                ))
            }
            Some(LightingParameters::Sequence { layout_id, .. })
                if layout_id.as_deref() != policy.keyboard_lighting_layout_id =>
            {
                invalid(format!(
                    "The {name} imported animation targets a different keyboard layout."
                ))
            }
            _ => Ok(()),
        }
    }

    fn validate_display(display: Option<&DisplaySettings>, name: &str) -> Checked {
        // No display settings is the unconfigured display of an older profile.
        let Some(display) = display else {
            return Ok(());
        };
        for (key, setting) in [
            (&display.refresh_rate_key, "refresh rate"),
            (&display.color_profile_key, "color profile"),
        ] {
            if key.as_ref().is_some_and(|key| {
                key.is_empty() || text::utf16_length(key) > 1024 || text::has_control(key)
            }) {
                return invalid(format!(
                    "The {name} display {setting} identifier is invalid."
                ));
            }
        }
        if display
            .brightness_percent
            .is_some_and(|percent| percent > 100)
        {
            return invalid(format!(
                "The {name} display brightness must be from 0 to 100 percent."
            ));
        }
        Ok(())
    }

    fn validate_application_profiles(
        &self,
        profiles: &ProfileSet,
        validate_custom_capabilities: bool,
    ) -> Checked {
        let applications = &profiles.application_profiles;
        if applications.len() > ApplicationProfile::MAXIMUM_PROFILES {
            return invalid("The application profile collection is missing or too large.");
        }
        for (index, application) in applications.iter().enumerate() {
            let name = format!("application profile {}", index + 1);
            let earlier = &applications[..index];
            if application.id == ApplicationProfile::EMPTY_ID
                || earlier.iter().any(|seen| seen.id == application.id)
            {
                return invalid(format!("The {name} identifier is missing or duplicated."));
            }
            if text::is_white_space(&application.name)
                || text::utf16_length(&application.name) > ApplicationProfile::MAXIMUM_NAME_LENGTH
                || text::has_control(&application.name)
            {
                return invalid(format!("The {name} name is invalid."));
            }
            if !application.activation_mode.is_defined() || !application.overrides.is_configured() {
                return invalid(format!("The {name} activation or overrides are invalid."));
            }
            let path = &application.executable_path;
            if let Some(reason) = executable_path_fault(path) {
                return invalid(format!("The {name} executable path is invalid: {reason}"));
            }
            if !text::is_normalized_executable_path(path)
                || earlier
                    .iter()
                    .any(|seen| (self.same_path)(&seen.executable_path, path))
            {
                return invalid(format!(
                    "The {name} executable path is not normalized or is duplicated."
                ));
            }
            for (source, label) in [
                (PowerSource::PluggedIn, "plugged-in"),
                (PowerSource::Battery, "battery"),
                (PowerSource::UsbC, "low-power"),
            ] {
                let Some(value) = application.overrides.for_source(source) else {
                    continue;
                };
                let name = format!("{name} {label} override");
                if !value.is_configured() {
                    return invalid(format!("The {name} is empty."));
                }
                let resolved = resolve::apply(profiles.for_source(source), value)?;
                self.validate_profile(&resolved, &name, source, validate_custom_capabilities)?;
            }
        }
        Ok(())
    }
}

/// The `ArgumentException` text of managed `NormalizeExecutablePath`, for the
/// arguments it refuses before it normalises anything.
fn executable_path_fault(path: &str) -> Option<&'static str> {
    if text::is_white_space(path) {
        return Some(
            "The value cannot be an empty string or composed entirely of whitespace. (Parameter 'value')",
        );
    }
    let trimmed = path.trim();
    let bytes = trimmed.as_bytes();
    let separator = |byte| matches!(byte, b'\\' | b'/');
    // `Path.IsPathFullyQualified`: a drive root, or a leading separator
    // followed by another or by the `?` of an object-namespace path.
    let qualified = bytes.len() >= 3
        && bytes[0].is_ascii_alphabetic()
        && bytes[1] == b':'
        && separator(bytes[2])
        || bytes.len() >= 2 && separator(bytes[0]) && (separator(bytes[1]) || bytes[1] == b'?');
    if text::utf16_length(trimmed) > ApplicationProfile::MAXIMUM_EXECUTABLE_PATH_LENGTH
        || text::has_control(trimmed)
        || !qualified
    {
        return Some(
            "An application profile requires one absolute executable path. (Parameter 'value')",
        );
    }
    // Trailing separators, periods and spaces never survive `GetFullPath`.
    let leaf = trimmed.trim_end_matches(['\\', '/', '.', ' ']);
    if !leaf.to_ascii_lowercase().ends_with(".exe") {
        return Some(
            "An application profile path must identify an executable. (Parameter 'value')",
        );
    }
    None
}

/// Managed `ThxAudioProfileSettings.IsWellFormed`.
pub fn thx_is_well_formed(audio: &ThxAudio, same_name: SamePath) -> bool {
    const SPEAKER_BANDS: usize = 7;
    const HEADPHONE_BANDS: usize = 10;
    const MAXIMUM_RULES: usize = 256;
    const MAXIMUM_RULE_BYTES: usize = 4096;
    let ThxAudio::Typed(audio) = audio else {
        return false;
    };
    let enhancement = |value: ThxEnhancement| (0..=100).contains(&value.level_percent);
    let endpoint = |value: &ThxEndpoint, speaker: bool| {
        let bands = value.equalizer_curves.0[0].len();
        (bands == HEADPHONE_BANDS || speaker && bands == SPEAKER_BANDS)
            && value.selected_preset.is_defined()
            && enhancement(value.voice_clarity)
            && speaker == value.sound_normalization.is_some()
            && value.sound_normalization.is_none_or(enhancement)
            && value.equalizer_curves.0.iter().all(|curve| {
                curve.len() == bands
                    && curve
                        .iter()
                        .all(|gain| gain.is_finite() && (-12.0..=12.0).contains(gain))
            })
    };
    let rules = &audio.application_rules;
    // The model normalises each rule name on construction, as managed does.
    let distinct = rules.iter().enumerate().all(|(index, rule)| {
        !rules[..index]
            .iter()
            .any(|seen| same_name(&seen.executable_name, &rule.executable_name))
    });
    endpoint(&audio.speakers, true)
        && endpoint(&audio.headphones, false)
        && rules.len() <= MAXIMUM_RULES
        && rules.iter().all(|rule| rule.output.is_defined())
        && distinct
        && json::write(&encode::thx_rules(rules), false).len() <= MAXIMUM_RULE_BYTES
}

/// Managed `IpcMessageCodec.ValidateRequest` for `UpdateProfilesRequest`: a
/// set that fails it never reaches the Service, which answers a malformed
/// frame and closes.
pub fn admits_request(profiles: &ProfileSet, same_path: SamePath) -> bool {
    let display = |display: Option<&DisplaySettings>| {
        display.is_none_or(|display| {
            display
                .brightness_percent
                .is_none_or(|percent| percent <= 100)
        })
    };
    let override_admitted = |value: Option<&ApplicationOverride>| {
        value.is_none_or(|value| {
            value.is_configured()
                && value.performance.is_none_or(PerformancePreset::is_defined)
                && value.gpu_clock_tuning.is_none_or(|tuning| tuning.offsets.is_some())
                && value.fans.is_none_or(FanMode::is_defined)
                // Managed leaves an override that selects Stop without fan
                // settings to an exception no reply follows.
                && !(value.fans == Some(FanMode::STOP) && value.fan_control.is_none())
                && value.lighting.as_ref().is_none_or(lighting::is_well_formed)
                && display(value.display.as_ref())
        })
    };
    let applications = &profiles.application_profiles;
    let power_profiles = PowerSource::ALL.map(|source| profiles.for_source(source));
    profiles
        .windows_power_modes
        .0
        .iter()
        .all(|mode| mode.is_none_or(WindowsPowerMode::is_defined))
        && power_profiles.iter().all(|profile| {
            profile.windows_power_mode.is_none() && display(profile.display.as_ref())
        })
        && applications.len() <= ApplicationProfile::MAXIMUM_PROFILES
        && applications.iter().enumerate().all(|(index, application)| {
            let earlier = &applications[..index];
            application.id != ApplicationProfile::EMPTY_ID
                && !earlier.iter().any(|seen| seen.id == application.id)
                && !text::is_white_space(&application.name)
                && text::utf16_length(&application.name) <= ApplicationProfile::MAXIMUM_NAME_LENGTH
                && !text::has_control(&application.name)
                && application.activation_mode.is_defined()
                && application.overrides.is_configured()
                && PowerSource::ALL
                    .into_iter()
                    .all(|source| override_admitted(application.overrides.for_source(source)))
                && text::is_normalized_executable_path(&application.executable_path)
                && !earlier
                    .iter()
                    .any(|seen| same_path(&seen.executable_path, &application.executable_path))
        })
}
