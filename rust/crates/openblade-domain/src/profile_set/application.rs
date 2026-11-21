//! Managed `ProfileAutomationState`'s active application profile over the
//! typed set: the selection the Service keeps between foreground reports and
//! profile saves, and the profile layer it resolves for the current power
//! sources and confirmed firmware state.
//!
//! The selection rules are [`crate::application_context`]'s; this binds them
//! to `ProfileSet` with managed's `OrdinalIgnoreCase` table. Pure: no clock,
//! device or storage. Leases, triggers, manual overrides, the temporary
//! battery overrides and hardware application stay in the host. The save
//! path's predicate is [`crate::application_context::intent_changed`]; while
//! no override is active its desired term compares [`desired_intent`], which
//! keeps each setting's source as managed's `DesiredDeviceState` does.

use super::model::*;
use super::normalize::Invalid;
use super::{resolve, text};
use crate::application_context::{self, Rule};
use crate::automation::{self, Provenance, ValueOrigin};
use std::borrow::Cow;
use std::string::String;

/// The active pair, owned across reports and saves.
pub type ApplicationContext = application_context::ApplicationContext<String>;

/// A reported path not already in `NormalizeExecutablePath` form. Managed
/// normalises such a path with `Path.GetFullPath` before matching; native
/// refuses it rather than match a spelling no saved rule can hold. Managed's
/// IPC codec refuses the same paths (`IpcMessageCodec.cs:288-291`,
/// :1180-1194), so the two differ only for an in-process caller.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Unnormalized;

fn rules(profiles: &ProfileSet) -> impl Iterator<Item = Rule<'_>> {
    profiles
        .application_profiles
        .iter()
        .map(ApplicationProfile::rule)
}

/// Managed `SetApplicationContext` (`ProfileAutomationState.cs:216-236`) for a
/// foreground report or, with `None`, its reset. Returns whether the active
/// profile changed.
pub fn report(
    context: &mut ApplicationContext,
    profiles: &ProfileSet,
    reported: Option<&str>,
) -> Result<bool, Unnormalized> {
    if reported.is_some_and(|path| {
        !text::is_white_space(path) && !text::is_normalized_executable_path(path)
    }) {
        return Err(Unnormalized);
    }
    Ok(context.report(rules(profiles), reported, text::same_ordinal_ignoring_case))
}

/// Managed `UpdateProfiles` (`ProfileAutomationState.cs:130-143`) for a newly
/// stored set. Returns whether the active profile was dropped.
pub fn update(context: &mut ApplicationContext, profiles: &ProfileSet) -> bool {
    context.update(rules(profiles), text::same_ordinal_ignoring_case)
}

/// The two power sources managed's resolution reads. `None` is
/// `ExternalUnknown`, which has no power-profile slot of its own.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Power {
    /// `ProfileAutomationState.PowerSource`. `EffectiveLaptopBehavior`
    /// (`ProfileAutomationState.cs:99-110`, through `GetActiveProfile` at
    /// :563-569), `GetApplicationOverride` (:632-641) and `DesiredBattery`
    /// (:84-89) read only this one.
    pub state: Option<PowerSource>,
    /// `ServiceControlRuntime.ProjectUnclassifiedIntent`
    /// (`ServiceControlRuntime.UnclassifiedExternalPower.cs:151-152`): then
    /// `ResolveAutomationDesired` (`ServiceControlRuntime.cs:19483-19490`)
    /// passes `ExternalUnknown` as `Resolve`'s observed source, even while an
    /// explicit unclassified override leaves the state source classified.
    pub project_unclassified: bool,
}

impl Power {
    /// The state source with no projection.
    pub const fn retained(state: Option<PowerSource>) -> Self {
        Self {
            state,
            project_unclassified: false,
        }
    }

    /// `observedPowerSource ?? PowerSource` (`ProfileAutomationState.cs:329`).
    fn desired(self) -> Option<PowerSource> {
        if self.project_unclassified {
            None
        } else {
            self.state
        }
    }
}

/// Confirmed firmware state as managed `Resolve` reads it
/// (`ConfirmedDeviceState`): what the device last reported, each member absent
/// when unknown. It shapes the profile layer only where managed reads it: the
/// unknown-external base profile and sources, Custom settings a profile does
/// not carry, and lighting a profile does not set. The default is managed's
/// all-unknown `ConfirmedDeviceState(null, null, null, null)`.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Confirmed<'a> {
    pub performance: Option<PerformancePreset>,
    pub fans: Option<FanMode>,
    pub lighting: Option<&'a LightingSettings>,
    pub custom_performance: Option<CustomPerformance>,
}

/// What managed resolves for the active application profile.
#[derive(Clone, Debug, PartialEq)]
pub struct Resolution<'a> {
    /// `ActiveApplicationProfileId`.
    pub application_profile_id: Option<&'a str>,
    /// The profile `Resolve` starts from (`ProfileAutomationState.cs:329-344`).
    /// On a classified desired source, its base profile with the active
    /// application's override applied. On `ExternalUnknown`, managed
    /// `CreateObservedFallbackProfile` (:554-561) with no override: each of
    /// performance, fans and lighting is the confirmed value or else the saved
    /// USB-C one, Custom settings are the confirmed ones only, battery
    /// protection is `DesiredBattery`'s, and every other member is unset.
    pub profile: Cow<'a, PowerProfile>,
    /// `EffectiveLaptopBehavior`, from the state source alone: a projection
    /// to `ExternalUnknown` leaves it on the retained classified profile.
    pub laptop_behavior: LaptopBehavior,
    source: Option<PowerSource>,
    application_override: Option<&'a ApplicationOverride>,
    confirmed: Confirmed<'a>,
    battery_policy: Option<BatteryPolicy>,
    battery_protection_enabled: bool,
}

impl Resolution<'_> {
    /// `Resolve`'s source, `observedPowerSource ?? PowerSource`; `None` is
    /// `ExternalUnknown`.
    pub fn desired_source(&self) -> Option<PowerSource> {
        self.source
    }

    /// The active application's override that [`Self::profile`] applies:
    /// `GetApplicationOverride` for the desired source, and `None` on
    /// `ExternalUnknown`, where no override applies.
    pub fn application_override(&self) -> Option<&ApplicationOverride> {
        self.application_override
    }
}

/// `GetApplicationOverride` (`ProfileAutomationState.cs:632-641`).
fn application_override<'a>(
    context: &ApplicationContext,
    profiles: &'a ProfileSet,
    source: PowerSource,
) -> Option<&'a ApplicationOverride> {
    let active = context.active()?;
    profiles
        .application_profiles
        .iter()
        .find(|profile| profile.rule().supplies(active.id))?
        .overrides
        .for_source(source)
}

fn merged<'a>(
    profiles: &'a ProfileSet,
    source: PowerSource,
    value: Option<&ApplicationOverride>,
) -> Result<Cow<'a, PowerProfile>, Invalid> {
    let baseline = profiles.for_source(source);
    match value {
        // `ApplicationProfileResolution.Apply` returns a null override's
        // baseline itself.
        None => Ok(Cow::Borrowed(baseline)),
        Some(value) => resolve::apply(baseline, value).map(Cow::Owned),
    }
}

/// Managed `CreateObservedFallbackProfile` (`ProfileAutomationState.cs:554-561`)
/// through the `PowerProfile(Performance, Fans, Lighting)` constructor, whose
/// other members keep their defaults.
fn observed_fallback(
    profiles: &ProfileSet,
    confirmed: Confirmed<'_>,
    battery_protection_enabled: bool,
) -> PowerProfile {
    let usb_c = &profiles.usb_c;
    PowerProfile {
        performance: confirmed.performance.unwrap_or(usb_c.performance),
        fans: confirmed.fans.unwrap_or(usb_c.fans),
        lighting: confirmed.lighting.or(usb_c.lighting.as_ref()).cloned(),
        display: None,
        stop_fans_in_battery_saver: false,
        custom_performance: confirmed.custom_performance,
        gpu_clock_tuning: None,
        fan_control: None,
        lighting_power_policy: None,
        laptop_behavior: None,
        battery_protection_enabled: Some(battery_protection_enabled),
        thx_audio: None,
        windows_power_mode: None,
    }
}

/// The active application intent on `power` with `confirmed` firmware state.
/// Fails where managed's `ApplicationProfileResolution.Apply` throws: an
/// override naming an undefined legacy fan mode.
pub fn resolve<'a>(
    context: &'a ApplicationContext,
    profiles: &'a ProfileSet,
    power: Power,
    confirmed: Confirmed<'a>,
) -> Result<Resolution<'a>, Invalid> {
    let application_override = power
        .state
        .and_then(|source| application_override(context, profiles, source));
    let state = power
        .state
        .map(|source| merged(profiles, source, application_override))
        .transpose()?;
    let laptop_behavior = state
        .as_deref()
        .map(PowerProfile::effective_laptop_behavior)
        .unwrap_or_default();
    // `DesiredBattery` (:84-89) reads `GetActiveProfile`, the state source's
    // base, whatever source `Resolve` takes.
    let battery_protection_enabled = power
        .state
        .and_then(|source| profiles.for_source(source).battery_protection_enabled)
        .unwrap_or(true);
    // Without a projection both sources are the state's; with one, `Resolve`
    // takes neither the state's profile nor its override.
    let source = power.desired();
    let (profile, application_override) = match (source, state) {
        (Some(_), Some(state)) => (state, application_override),
        _ => (
            Cow::Owned(observed_fallback(
                profiles,
                confirmed,
                battery_protection_enabled,
            )),
            None,
        ),
    };
    Ok(Resolution {
        application_profile_id: context.active().map(|active| active.id),
        profile,
        laptop_behavior,
        source,
        application_override,
        confirmed,
        battery_policy: profiles.battery_policy,
        battery_protection_enabled,
    })
}

/// One resolved setting and where it came from: managed `ResolvedSetting`.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Setting<T> {
    pub value: T,
    pub source: Provenance,
}

/// The profile layer of managed `Resolve` (`ProfileAutomationState.cs:322-478`):
/// every `DesiredDeviceState` member `Resolve` returns while no manual
/// override, recovered HyperBoost intent or temporary battery override is
/// active, less the temporary full charge. Display, THX audio and the
/// gaming-mode key policy are not among them, so a change to only those is no
/// change here, while removing an override equal to its base still is: the
/// value stays and its source moves.
///
/// It is the desired term of the save-path predicate only while no such
/// override is active. An active override can hide a profile change from
/// managed (a persistent Silent override over an edited rule preset) or
/// surface one this omits (a persistent Custom override takes the profile's
/// Custom settings even when the profile preset is not Custom). The host
/// composes the full term: performance, fans, fan control, lighting and
/// battery layer over each member's [`Setting::source`]; Custom settings and
/// GPU tuning layer over the raw [`Resolution::profile`], not this intent,
/// then pass through [`gpu_clock_settings`] again; fan control in a mode an
/// override selects goes through [`with_mode`].
#[derive(Clone, Debug, PartialEq)]
pub struct DesiredIntent<'a> {
    /// Source `ApplicationProfile` when the override sets the preset, Custom
    /// settings or GPU tuning (:377-390); on `ExternalUnknown`, confirmed
    /// firmware state when it reports a preset (:430-439).
    pub performance: Setting<PerformancePreset>,
    /// While Custom is selected, the profile's Custom settings, else the
    /// confirmed ones, else managed's default (:391-398).
    pub custom_performance: Option<CustomPerformance>,
    /// The profile's tuning on classified external power only, with legacy
    /// Custom offsets moved into it (:400-416).
    pub gpu_clock_tuning: Option<GpuClockTuning>,
    /// Source `ApplicationProfile` when the override sets the fan mode or
    /// fan control (:417-428); on `ExternalUnknown`, confirmed firmware state
    /// when it reports a mode (:430-439); on USB-C an unavailable mode is
    /// Automatic from the power profile (:440-447).
    pub fans: Setting<FanMode>,
    /// The profile's fan control, or the legacy default for its mode, in the
    /// resolved mode (:454-468).
    pub fan_control: FanControl,
    /// `ResolveLightingWithPowerContext` (:480-512): the profile's lighting,
    /// from `ApplicationProfile` when the override sets it; confirmed lighting
    /// when the profile has none; on `ExternalUnknown`, confirmed lighting
    /// before the fallback profile's. No value is managed's `null`.
    pub lighting: Setting<Option<&'a LightingSettings>>,
    /// `DesiredBattery` (:84-89) without its temporary members.
    pub battery_policy: Option<BatteryPolicy>,
    pub battery_protection_enabled: bool,
}

/// Managed `WithMode` (`ProfileAutomationState.cs:522-531`): `settings` in
/// `mode`, at the maximum speed when that mode is Maximum.
pub fn with_mode(settings: &FanControl, mode: FanMode) -> FanControl {
    if settings.mode == mode {
        return settings.clone();
    }
    FanControl {
        mode,
        fixed_speed_rpm: if mode == FanMode::MAXIMUM {
            FanControl::MAXIMUM_SPEED_RPM
        } else {
            settings.fixed_speed_rpm
        },
        manual_curve: settings.manual_curve.clone(),
    }
}

/// Managed `Resolve`'s GPU clock step (`ProfileAutomationState.cs:400-415`)
/// after its layers are chosen: `custom_performance` is the selected Custom
/// settings (present only while Custom is resolved) and `gpu_clock_tuning` the
/// first tuning present among the layers. On classified external power the
/// tuning stays, or else the Custom settings' legacy offsets move into an
/// enabled tuning; elsewhere there is no tuning and the offsets stay.
pub fn gpu_clock_settings(
    source: Option<PowerSource>,
    mut custom_performance: Option<CustomPerformance>,
    gpu_clock_tuning: Option<GpuClockTuning>,
) -> (Option<CustomPerformance>, Option<GpuClockTuning>) {
    if !source.is_some_and(PowerSource::is_external) {
        return (custom_performance, None);
    }
    if gpu_clock_tuning.is_none()
        && let Some(settings) = custom_performance.as_mut()
        && let Some(offsets) = settings.gpu_clock_offsets.take()
    {
        return (
            custom_performance,
            Some(GpuClockTuning {
                enabled: true,
                offsets: Some(offsets),
            }),
        );
    }
    (custom_performance, gpu_clock_tuning)
}

fn provenance(from_application: bool) -> Provenance {
    automation::performance(false, false, from_application).provenance
}

/// The profile layer of managed `Resolve` for `resolution`.
/// `supports_manual_fans_on_limited_power` is the device capability
/// `ResolveAutomationDesired` passes. Fails where managed's
/// `FanControlSettings.FromLegacy` throws.
pub fn desired_intent<'a>(
    resolution: &'a Resolution<'_>,
    supports_manual_fans_on_limited_power: bool,
) -> Result<DesiredIntent<'a>, Invalid> {
    let profile = &*resolution.profile;
    let source = resolution.source;
    let confirmed = resolution.confirmed;
    let from = resolution.application_override;
    let external_unknown = source.is_none();
    // :430-439: a power-profile value confirmed firmware state reports.
    let observed = |setting: Provenance, reported: bool| {
        if external_unknown && reported && setting == Provenance::PowerProfile {
            Provenance::ConfirmedFirmwareState
        } else {
            setting
        }
    };
    let performance = Setting {
        value: profile.performance,
        source: observed(
            provenance(from.is_some_and(|value| {
                value.performance.is_some()
                    || value.custom_performance.is_some()
                    || value.gpu_clock_tuning.is_some()
            })),
            confirmed.performance.is_some(),
        ),
    };
    let (custom_performance, gpu_clock_tuning) = gpu_clock_settings(
        source,
        (performance.value == PerformancePreset::CUSTOM).then(|| {
            profile
                .custom_performance
                .or(confirmed.custom_performance)
                .unwrap_or(CustomPerformance::DEFAULT)
        }),
        profile.gpu_clock_tuning,
    );
    let mut fans = Setting {
        value: profile.fans,
        source: observed(
            provenance(
                from.is_some_and(|value| value.fans.is_some() || value.fan_control.is_some()),
            ),
            confirmed.fans.is_some(),
        ),
    };
    if source == Some(PowerSource::UsbC)
        && !fans
            .value
            .available_on(PowerSource::UsbC, supports_manual_fans_on_limited_power)
    {
        fans = Setting {
            value: FanMode::AUTOMATIC,
            source: Provenance::PowerProfile,
        };
    }
    let fan_control = match &profile.fan_control {
        Some(settings) => with_mode(settings, fans.value),
        None => with_mode(
            &FanControl::from_legacy(profile.fans).ok_or_else(|| {
                Invalid::new(
                    "Specified argument was out of the range of valid values. (Parameter 'mode')",
                )
            })?,
            fans.value,
        ),
    };
    // Managed skips the profile value on unknown external power while
    // confirmed lighting is known; the selection then takes the confirmed
    // value either way.
    let profile_lighting = profile.lighting.as_ref();
    let lighting = automation::lighting(
        profile_lighting.is_some(),
        confirmed.lighting.is_some(),
        false,
        false,
        external_unknown,
        from.is_some_and(|value| value.lighting.is_some()),
    );
    Ok(DesiredIntent {
        performance,
        custom_performance,
        gpu_clock_tuning,
        fans,
        fan_control,
        lighting: Setting {
            value: match lighting.origin {
                ValueOrigin::Confirmed => confirmed.lighting.or(profile_lighting),
                _ => profile_lighting,
            },
            source: lighting.provenance,
        },
        battery_policy: resolution.battery_policy,
        battery_protection_enabled: resolution.battery_protection_enabled,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fixed(speed: i32) -> FanControl {
        FanControl {
            fixed_speed_rpm: speed,
            ..FanControl::from_legacy(FanMode::FIXED).unwrap()
        }
    }

    #[test]
    fn with_mode_keeps_matching_settings_and_raises_maximum_to_full_speed() {
        let settings = fixed(3000);
        assert_eq!(with_mode(&settings, FanMode::FIXED), settings);
        assert_eq!(
            with_mode(&settings, FanMode::MAXIMUM),
            FanControl {
                mode: FanMode::MAXIMUM,
                fixed_speed_rpm: FanControl::MAXIMUM_SPEED_RPM,
                ..settings.clone()
            }
        );
        assert_eq!(
            with_mode(&settings, FanMode::AUTOMATIC),
            FanControl {
                mode: FanMode::AUTOMATIC,
                ..settings
            }
        );
    }

    #[test]
    fn legacy_offsets_move_into_tuning_on_classified_external_power_only() {
        let offsets = GpuClockOffsets {
            core_megahertz: 7,
            memory_megahertz: 8,
        };
        let custom = CustomPerformance {
            gpu_clock_offsets: Some(offsets),
            ..CustomPerformance::DEFAULT
        };
        let tuning = GpuClockTuning {
            enabled: false,
            offsets: None,
        };
        let moved = (
            Some(CustomPerformance::DEFAULT),
            Some(GpuClockTuning {
                enabled: true,
                offsets: Some(offsets),
            }),
        );
        for source in [PowerSource::PluggedIn, PowerSource::UsbC] {
            assert_eq!(gpu_clock_settings(Some(source), Some(custom), None), moved);
            assert_eq!(
                gpu_clock_settings(Some(source), Some(custom), Some(tuning)),
                (Some(custom), Some(tuning))
            );
            assert_eq!(
                gpu_clock_settings(Some(source), None, Some(tuning)),
                (None, Some(tuning))
            );
        }
        for source in [Some(PowerSource::Battery), None] {
            assert_eq!(
                gpu_clock_settings(source, Some(custom), Some(tuning)),
                (Some(custom), None)
            );
        }
    }

    #[test]
    fn confirmed_state_fills_what_an_unvalidated_profile_lacks() {
        // A set the store refuses: Custom without levels (:391-398) and no
        // lighting (`ResolveLightingWithPowerContext`).
        let mut profiles = ProfileSet::safe_defaults();
        profiles.plugged_in.performance = PerformancePreset::CUSTOM;
        profiles.plugged_in.custom_performance = None;
        profiles.plugged_in.lighting = None;
        let context = ApplicationContext::default();
        let power = Power::retained(Some(PowerSource::PluggedIn));
        let resolution = resolve(&context, &profiles, power, Confirmed::default()).unwrap();
        let intent = desired_intent(&resolution, true).unwrap();
        assert_eq!(intent.custom_performance, Some(CustomPerformance::DEFAULT));
        assert_eq!(
            intent.lighting,
            Setting {
                value: None,
                source: Provenance::ConfirmedFirmwareState,
            }
        );

        let lighting = LightingSettings::off();
        let custom = CustomPerformance {
            cpu: CustomPerformanceLevel::LOW,
            ..CustomPerformance::DEFAULT
        };
        let confirmed = Confirmed {
            lighting: Some(&lighting),
            custom_performance: Some(custom),
            ..Confirmed::default()
        };
        let resolution = resolve(&context, &profiles, power, confirmed).unwrap();
        let intent = desired_intent(&resolution, true).unwrap();
        assert_eq!(intent.custom_performance, Some(custom));
        assert_eq!(
            intent.lighting,
            Setting {
                value: Some(&lighting),
                source: Provenance::ConfirmedFirmwareState,
            }
        );
    }
}
