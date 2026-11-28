//! Managed `ApplicationProfileResolution.Apply` over the whole profile. The
//! bounded merge is the crate's existing policy seam, which the managed host
//! reaches through the native boundary; the remaining members are merged
//! here as `NativeProfilePolicy.Apply` merges them.

use super::model::*;
use super::normalize::Invalid;
use crate::{
    DisplayProjection, FanControlProjection, FanControlSource, ProfileOverride, ProfileProjection,
};
use std::string::String;

fn display(value: &DisplaySettings) -> DisplayProjection<'_> {
    DisplayProjection {
        refresh_rate_key: value.refresh_rate_key.as_deref(),
        hdr_enabled: value.hdr_enabled,
        color_profile_key: value.color_profile_key.as_deref(),
        brightness_percent: value.brightness_percent.map(i32::from),
    }
}

fn fan_control(value: &FanControl) -> FanControlProjection {
    FanControlProjection {
        mode: value.mode.0,
        fixed_speed_rpm: value.fixed_speed_rpm,
    }
}

/// The profile an application override selects on top of `baseline`.
pub fn apply(
    baseline: &PowerProfile,
    value: &ApplicationOverride,
) -> Result<PowerProfile, Invalid> {
    let behavior = baseline.effective_laptop_behavior();
    let resolution = crate::apply(
        Some(ProfileProjection {
            performance: baseline.performance.0,
            fans: baseline.fans.0,
            fan_control: baseline.fan_control.as_ref().map(fan_control),
            display: baseline.display.as_ref().map(display),
            gaming_mode_enabled: behavior.gaming_mode_enabled,
        }),
        Some(ProfileOverride {
            performance: value.performance.map(|preset| preset.0),
            fans: value.fans.map(|mode| mode.0),
            fan_control: value.fan_control.as_ref().map(fan_control),
            display: value.display.as_ref().map(display),
            gaming_mode_enabled: value.gaming_mode_enabled,
        }),
    )
    // `FanControlSettings.FromLegacy` on an undefined mode.
    .map_err(|_| {
        Invalid::new("Specified argument was out of the range of valid values. (Parameter 'mode')")
    })?;
    let merged = resolution.profile;
    let fans = FanMode(merged.fans);
    let shown = merged.display.unwrap_or_default();
    Ok(PowerProfile {
        performance: PerformancePreset(merged.performance),
        fans,
        fan_control: match resolution.fan_control_source {
            FanControlSource::None => None,
            FanControlSource::Baseline => baseline.fan_control.clone(),
            FanControlSource::Override => value.fan_control.clone(),
            FanControlSource::LegacyDefault => FanControl::from_legacy(fans),
        },
        display: Some(DisplaySettings {
            refresh_rate_key: shown.refresh_rate_key.map(String::from),
            hdr_enabled: shown.hdr_enabled,
            color_profile_key: shown.color_profile_key.map(String::from),
            brightness_percent: shown.brightness_percent.map(|percent| percent as u8),
        }),
        laptop_behavior: Some(
            if value.gaming_mode_enabled.is_some() || value.gaming_mode_key_policy.is_some() {
                LaptopBehavior {
                    gaming_mode_enabled: merged.gaming_mode_enabled,
                    gaming_mode_key_policy: value
                        .gaming_mode_key_policy
                        .or(behavior.gaming_mode_key_policy),
                    ..behavior
                }
            } else {
                behavior
            },
        ),
        custom_performance: value.custom_performance.or(baseline.custom_performance),
        gpu_clock_tuning: value.gpu_clock_tuning.or(baseline.gpu_clock_tuning),
        lighting: value.lighting.clone().or_else(|| baseline.lighting.clone()),
        ..baseline.clone()
    })
}
