//! Managed `ProfileAutomationState`'s active application profile over the
//! typed set: the selection the Service keeps between foreground reports and
//! profile saves, and the intent it resolves for the current power source.
//!
//! The selection rules are [`crate::application_context`]'s; this binds them
//! to `ProfileSet` with managed's `OrdinalIgnoreCase` table. Pure: no clock,
//! device or storage. Leases, triggers and hardware application stay in the
//! host, and the save path's predicate is
//! [`crate::application_context::intent_changed`] over the desired state the
//! host resolves from a [`Resolution`].

use super::model::*;
use super::normalize::Invalid;
use super::{resolve, text};
use crate::application_context::{self, Rule};
use std::borrow::Cow;
use std::string::String;

/// The active pair, owned across reports and saves.
pub type ApplicationContext = application_context::ApplicationContext<String>;

/// A reported path not already in `NormalizeExecutablePath` form. Managed
/// normalises such a path with `Path.GetFullPath` before matching; native
/// refuses it rather than match a spelling no saved rule can hold.
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

/// What managed resolves for the active application profile.
#[derive(Clone, Debug, PartialEq)]
pub struct Resolution<'a> {
    /// `ActiveApplicationProfileId`.
    pub application_profile_id: Option<&'a str>,
    /// The profile `Resolve` starts from (`ProfileAutomationState.cs:332-344`):
    /// the base profile for the power source with the active application's
    /// override for that source applied. `None` on `ExternalUnknown`, where
    /// managed builds the base from confirmed firmware state and applies no
    /// application override.
    pub desired: Option<Cow<'a, PowerProfile>>,
    /// `EffectiveLaptopBehavior` (`ProfileAutomationState.cs:99-110`).
    pub laptop_behavior: LaptopBehavior,
}

/// `GetApplicationOverride` (`ProfileAutomationState.cs:632-639`).
fn application_override<'a>(
    profiles: &'a ProfileSet,
    id: &str,
    source: PowerSource,
) -> Option<&'a ApplicationOverride> {
    profiles
        .application_profiles
        .iter()
        .find(|profile| profile.rule().supplies(id))?
        .overrides
        .for_source(source)
}

/// The active application intent on `source`; `None` is `ExternalUnknown`.
/// Fails where managed's `ApplicationProfileResolution.Apply` throws: an
/// override naming an undefined legacy fan mode.
pub fn resolve<'a>(
    context: &'a ApplicationContext,
    profiles: &'a ProfileSet,
    source: Option<PowerSource>,
) -> Result<Resolution<'a>, Invalid> {
    let active = context.active();
    let desired = source
        .map(|source| {
            let baseline = profiles.for_source(source);
            match active.and_then(|active| application_override(profiles, active.id, source)) {
                // `ApplicationProfileResolution.Apply` returns a null override's
                // baseline itself.
                None => Ok(Cow::Borrowed(baseline)),
                Some(value) => resolve::apply(baseline, value).map(Cow::Owned),
            }
        })
        .transpose()?;
    let laptop_behavior = desired
        .as_deref()
        .map(PowerProfile::effective_laptop_behavior)
        .unwrap_or_default();
    Ok(Resolution {
        application_profile_id: active.map(|active| active.id),
        desired,
        laptop_behavior,
    })
}
