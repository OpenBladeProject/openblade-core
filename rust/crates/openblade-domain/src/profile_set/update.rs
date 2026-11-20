//! The intent half of managed `ServiceControlRuntime.UpdateProfilesAsync`:
//! what a requested set must satisfy before the Service stores it. Storage,
//! publication and any hardware application belong to the host.

use super::decode::{self, Dialect};
use super::json::{self, Json, Malformed};
use super::model::*;
use super::normalize::{self, Invalid};
use super::policy::Capabilities;
use super::validate::{self, SamePath, Validator};

/// Managed's reply to a stored set.
pub const STORED_MESSAGE: &str = "Profiles stored without automatic hardware application.";

/// Managed `RecordRejectedIntent`: the reply text for a refused set.
pub fn rejection_message(reason: &Invalid) -> std::string::String {
    std::format!("The requested intent was rejected: {}", reason.0)
}

/// The `profiles` member of an `updateProfiles` frame, bound as managed's
/// IPC reader binds it and admitted as its body validation admits it.
/// A frame this refuses gets no result; managed closes after a malformed
/// reply that carries no request identifier.
pub fn decode_request(payload: &[u8], same_path: SamePath) -> Result<ProfileSet, Malformed> {
    let frame = json::parse(payload)?;
    let profiles =
        decode::profile_set(frame.member("profiles").ok_or(Malformed)?, Dialect::Request)?;
    if validate::admits_request(&profiles, same_path) {
        Ok(profiles)
    } else {
        Err(Malformed)
    }
}

/// A `ProfileSet` value outside a frame, for callers that hold the JSON.
pub fn decode_profiles(profiles: &Json, dialect: Dialect) -> Result<ProfileSet, Malformed> {
    decode::profile_set(profiles, dialect)
}

/// Normalises `requested` and checks it against the saved set. `validator`
/// is the Service's stored-intent validator. `capabilities` are the current
/// device's, when one is verified; `edit` is the validator built from them,
/// or `validator` again when there is none.
pub fn admit(
    mut requested: ProfileSet,
    saved: &ProfileSet,
    validator: &Validator,
    edit: &Validator,
    capabilities: Option<&Capabilities>,
) -> Result<ProfileSet, Invalid> {
    normalize::normalize_legacy_battery_profile(&mut requested, None)?;
    validator.validate_stored_custom_intent(&requested)?;
    edit.validate_changed_thermal_intent(&requested, saved)?;
    if requested.battery.stop_fans_in_battery_saver
        && !saved.battery.stop_fans_in_battery_saver
        && !capabilities.is_some_and(|capabilities| capabilities.supports_battery_saver_fan_stop)
    {
        return Err(Invalid::new(
            "Battery Saver fan stop requires explicit support from the current device.",
        ));
    }
    for source in PowerSource::ALL {
        let (next, prior) = (requested.for_source(source), saved.for_source(source));
        let Some(custom) = next.custom_performance else {
            continue;
        };
        let changed = next.custom_performance != prior.custom_performance
            || next.performance == PerformancePreset::CUSTOM
                && prior.performance != PerformancePreset::CUSTOM;
        let supported = capabilities
            .and_then(|capabilities| capabilities.custom_performance.as_ref())
            .is_some_and(|admitted| admitted.supports(&custom));
        if changed && !supported {
            return Err(Invalid::new(
                "Changed Custom performance intent requires explicit support from the current device.",
            ));
        }
    }
    Ok(requested)
}
