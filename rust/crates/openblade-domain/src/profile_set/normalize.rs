//! Managed `ProfileStore` normalisation: the shape every stored and accepted
//! `ProfileSet` is brought to before validation.

use super::lighting;
use super::model::*;
use std::string::String;

/// A managed exception message from normalisation or validation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Invalid(pub String);

impl Invalid {
    pub(super) fn new(message: impl Into<String>) -> Self {
        Self(message.into())
    }
}

/// Managed `NormalizeLegacyGpuClockTuning`: Custom offsets saved inside the
/// Custom settings move to the profile's own tuning.
fn normalize_gpu_clock_tuning(profile: &mut PowerProfile) {
    if profile.gpu_clock_tuning.is_some() || profile.performance != PerformancePreset::CUSTOM {
        return;
    }
    if let Some(custom) = &mut profile.custom_performance
        && let Some(offsets) = custom.gpu_clock_offsets.take()
    {
        profile.gpu_clock_tuning = Some(GpuClockTuning {
            enabled: true,
            offsets: Some(offsets),
        });
    }
}

/// Managed `ProfileStore.NormalizeLegacyBatteryProfile`. Returns whether the
/// set changed, which managed reports as a migration.
pub fn normalize_legacy_battery_profile(
    profiles: &mut ProfileSet,
    battery_protection_default: Option<bool>,
) -> Result<bool, Invalid> {
    let original = profiles.clone();
    profiles.battery.performance = match profiles.battery.performance {
        PerformancePreset::SILENT => PerformancePreset::BATTERY_SAVER,
        PerformancePreset::BALANCED => PerformancePreset::BATTERY_BALANCED,
        other => other,
    };
    for source in PowerSource::ALL {
        let Some(settings) = &mut profiles.for_source_mut(source).lighting else {
            // `ArgumentNullException.ThrowIfNull(settings)`.
            return Err(Invalid::new("Value cannot be null. (Parameter 'settings')"));
        };
        lighting::normalize_legacy_visibility(settings);
    }
    let legacy_policy = profiles.lighting_power_policy.unwrap_or_default();
    for source in PowerSource::ALL {
        let profile = profiles.for_source_mut(source);
        profile.display.get_or_insert_default();
        // A limited-power profile may hold no Custom intent; only an actual
        // Custom selection there needs the default before migration.
        if source == PowerSource::PluggedIn
            || source == PowerSource::UsbC && profile.performance == PerformancePreset::CUSTOM
        {
            profile
                .custom_performance
                .get_or_insert(CustomPerformance::DEFAULT);
        }
        if source.is_external() {
            normalize_gpu_clock_tuning(profile);
        }
        profile.lighting_power_policy.get_or_insert(legacy_policy);
        if profile.battery_protection_enabled.is_none() {
            profile.battery_protection_enabled = battery_protection_default;
        }
    }
    let migrated = PowerSource::ALL
        .into_iter()
        .any(|source| profiles.for_source(source) != original.for_source(source))
        || original.lighting_power_policy.is_some();
    if migrated {
        profiles.lighting_power_policy = None;
    }
    Ok(migrated)
}

/// Managed `ProfileStore.ClearLegacyWindowsPowerModes`.
pub fn clear_legacy_windows_power_modes(profiles: &mut ProfileSet) {
    for source in PowerSource::ALL {
        profiles.for_source_mut(source).windows_power_mode = None;
    }
}

/// Managed `LightingSettingsFactory.PreserveLegacyIndependentProfileIntent`.
pub fn preserve_legacy_independent_profile_intent(profiles: &mut ProfileSet) {
    for source in PowerSource::ALL {
        if let Some(settings) = &mut profiles.for_source_mut(source).lighting {
            lighting::preserve_legacy_independent_intent(settings);
        }
    }
    for application in &mut profiles.application_profiles {
        let overrides = &mut application.overrides;
        for value in [
            &mut overrides.plugged_in,
            &mut overrides.battery,
            &mut overrides.usb_c,
        ] {
            if let Some(settings) = value.as_mut().and_then(|value| value.lighting.as_mut()) {
                lighting::preserve_legacy_independent_intent(settings);
            }
        }
    }
}
