//! Managed `ProfileAutomationState`'s active application profile over the
//! typed set: the selection the Service keeps between foreground reports and
//! profile saves, and the intent it resolves for the current power sources.
//!
//! The selection rules are [`crate::application_context`]'s; this binds them
//! to `ProfileSet` with managed's `OrdinalIgnoreCase` table. Pure: no clock,
//! device or storage. Leases, triggers, manual overrides, confirmed firmware
//! state and hardware application stay in the host. The save path's predicate
//! is [`crate::application_context::intent_changed`]; its desired term
//! compares [`desired_intent`], which keeps each setting's source as managed's
//! `DesiredDeviceState` does.

use super::model::*;
use super::normalize::Invalid;
use super::{resolve, text};
use crate::application_context::{self, Rule};
use crate::automation::{self, Provenance};
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
/// `ExternalUnknown`, which selects no saved profile.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Power {
    /// `ProfileAutomationState.PowerSource`. `EffectiveLaptopBehavior`
    /// (`ProfileAutomationState.cs:99-110`, through `GetActiveProfile` at
    /// :563-569) and `GetApplicationOverride` (:632-641) read only this one.
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

/// What managed resolves for the active application profile.
#[derive(Clone, Debug, PartialEq)]
pub struct Resolution<'a> {
    /// `ActiveApplicationProfileId`.
    pub application_profile_id: Option<&'a str>,
    /// The profile `Resolve` starts from (`ProfileAutomationState.cs:329-344`):
    /// the base profile for the desired source with the active application's
    /// override applied. `None` when the desired source is `ExternalUnknown`,
    /// where managed builds the base from confirmed firmware state and applies
    /// no application override.
    pub profile: Option<Cow<'a, PowerProfile>>,
    /// `EffectiveLaptopBehavior`, from the state source alone: a projection
    /// to `ExternalUnknown` leaves it on the retained classified profile.
    pub laptop_behavior: LaptopBehavior,
    source: Option<PowerSource>,
    application_override: Option<&'a ApplicationOverride>,
    battery_policy: Option<BatteryPolicy>,
    battery_protection_enabled: bool,
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

/// The active application intent on `power`. Fails where managed's
/// `ApplicationProfileResolution.Apply` throws: an override naming an
/// undefined legacy fan mode.
pub fn resolve<'a>(
    context: &'a ApplicationContext,
    profiles: &'a ProfileSet,
    power: Power,
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
    // Without a projection both sources are the state's; with one, `Resolve`
    // takes neither the state's profile nor its override.
    let source = power.desired();
    Ok(Resolution {
        application_profile_id: context.active().map(|active| active.id),
        profile: state.filter(|_| source.is_some()),
        laptop_behavior,
        source,
        application_override: application_override.filter(|_| source.is_some()),
        battery_policy: profiles.battery_policy,
        // `DesiredBattery` reads `GetActiveProfile`, the state source's base.
        battery_protection_enabled: power
            .state
            .and_then(|source| profiles.for_source(source).battery_protection_enabled)
            .unwrap_or(true),
    })
}

/// One resolved setting and where it came from: managed `ResolvedSetting`.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Setting<T> {
    pub value: T,
    pub source: Provenance,
}

/// The profile layer of managed `Resolve` (`ProfileAutomationState.cs:322-478`)
/// for a classified desired source: every `DesiredDeviceState` member before
/// manual overrides and confirmed firmware state apply. Display, THX audio
/// and the gaming-mode key policy are not among them, so a change to only
/// those is no change here, while removing an override equal to its base
/// still is: the value stays and its source moves.
///
/// The host layers on manual overrides, recovered HyperBoost intent, the
/// temporary battery overrides and full charge, and confirmed firmware state,
/// which supplies Custom settings when the profile has none and lighting when
/// [`DesiredIntent::lighting`] has no value. With none of those in effect,
/// equal intents resolve equal `DesiredDeviceState`s and unequal ones unequal.
#[derive(Clone, Debug, PartialEq)]
pub struct DesiredIntent<'a> {
    /// Source `ApplicationProfile` when the override sets the preset, Custom
    /// settings or GPU tuning (:377-390).
    pub performance: Setting<PerformancePreset>,
    /// The profile's Custom settings while Custom is selected (:391-398).
    pub custom_performance: Option<CustomPerformance>,
    /// The profile's tuning on external power only, with legacy Custom
    /// offsets moved into it (:400-416).
    pub gpu_clock_tuning: Option<GpuClockTuning>,
    /// Source `ApplicationProfile` when the override sets the fan mode or
    /// fan control (:417-428); on USB-C an unavailable mode is Automatic from
    /// the power profile (:440-447).
    pub fans: Setting<FanMode>,
    /// The profile's fan control in the resolved mode (:454-468).
    pub fan_control: FanControl,
    /// Source `ApplicationProfile` when the override sets lighting (:448-453).
    /// No value is managed's selection of confirmed firmware lighting.
    pub lighting: Setting<Option<&'a LightingSettings>>,
    /// `DesiredBattery` (:84-89) without its temporary members.
    pub battery_policy: Option<BatteryPolicy>,
    pub battery_protection_enabled: bool,
}

/// Managed `WithMode` (`ProfileAutomationState.cs:522-531`).
fn with_mode(settings: &FanControl, mode: FanMode) -> FanControl {
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

fn provenance(from_application: bool) -> Provenance {
    automation::performance(false, false, from_application).provenance
}

/// The desired term of managed's save-path predicate for `resolution`, or
/// `None` when the desired source is `ExternalUnknown` and managed resolves
/// from confirmed firmware state. `supports_manual_fans_on_limited_power` is
/// the device capability `ResolveAutomationDesired` passes. Fails where
/// managed's `FanControlSettings.FromLegacy` throws.
pub fn desired_intent<'a>(
    resolution: &'a Resolution<'_>,
    supports_manual_fans_on_limited_power: bool,
) -> Result<Option<DesiredIntent<'a>>, Invalid> {
    let (Some(source), Some(profile)) = (resolution.source, resolution.profile.as_deref()) else {
        return Ok(None);
    };
    let from = resolution.application_override;
    let performance = Setting {
        value: profile.performance,
        source: provenance(from.is_some_and(|value| {
            value.performance.is_some()
                || value.custom_performance.is_some()
                || value.gpu_clock_tuning.is_some()
        })),
    };
    let mut custom_performance = profile
        .custom_performance
        .filter(|_| performance.value == PerformancePreset::CUSTOM);
    let mut gpu_clock_tuning = profile.gpu_clock_tuning.filter(|_| source.is_external());
    if source.is_external()
        && gpu_clock_tuning.is_none()
        && let Some(settings) = custom_performance.as_mut()
        && let Some(offsets) = settings.gpu_clock_offsets.take()
    {
        gpu_clock_tuning = Some(GpuClockTuning {
            enabled: true,
            offsets: Some(offsets),
        });
    }
    let mut fans = Setting {
        value: profile.fans,
        source: provenance(
            from.is_some_and(|value| value.fans.is_some() || value.fan_control.is_some()),
        ),
    };
    if source == PowerSource::UsbC
        && !fans
            .value
            .available_on(source, supports_manual_fans_on_limited_power)
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
    let lighting = automation::lighting(
        profile.lighting.is_some(),
        false,
        false,
        false,
        false,
        from.is_some_and(|value| value.lighting.is_some()),
    );
    Ok(Some(DesiredIntent {
        performance,
        custom_performance,
        gpu_clock_tuning,
        fans,
        fan_control,
        lighting: Setting {
            value: profile.lighting.as_ref(),
            source: lighting.provenance,
        },
        battery_policy: resolution.battery_policy,
        battery_protection_enabled: resolution.battery_protection_enabled,
    }))
}
