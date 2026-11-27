//! Managed `WindowsPowerModeAutomationCoordinator.Observe` over the raw status
//! members (`Snapshot::power_mode_automation`).
//!
//! A status is confirmed only with Active control on a verified device, a
//! power source that is PluggedIn, Battery or UsbC and was not merely
//! retained, no unconfirmed Performance write, and a confirmed performance
//! preset. FixedMaximum reports no preset, so it is unconfirmed here too: the
//! association stays stored and applies once a preset is confirmed again.
//!
//! The managed status codec refuses a status with malformed members, and a
//! refused status never reaches `Observe`; its watcher marks the automation
//! unavailable instead. Every member read here that managed would refuse makes
//! the status unconfirmed (`None`) the same way: an association list the
//! profile-set codec refuses or that names an undefined mode, a legacy
//! per-profile `windowsPowerMode` that is not cleared, a malformed or
//! undefined observation reason or confirmed preset, and a null, unsorted,
//! duplicated or undefined unconfirmed-write list. Managed reads an absent
//! unconfirmed-write list as empty (its default) and refuses only an explicit
//! null, and so does this decoder.
//!
//! Managed binds status members case-insensitively. Like every native status
//! reader, this one reads only the camelCase names the Service writes: a
//! member spelled with other casing is not read, so an `ObservationReason` or
//! `UnconfirmedWrites` reads as absent here where managed would bind it.
use super::{Mode, Observation, Supply, Target};
use crate::service_status::PowerModeFields;
use openblade_domain::profile_set::{decode, json};
use serde_json::Value;

/// Managed `SessionControlAccess.Active`.
const ACTIVE: i32 = 0;
/// Managed `DeviceObservationReason.RetainedPowerSource`.
const RETAINED_POWER_SOURCE: i64 = 3;
/// Managed `DeviceSubsystem.Performance`.
const PERFORMANCE: i64 = 0;
/// The defined `DeviceSubsystem` values.
const SUBSYSTEMS: std::ops::RangeInclusive<i64> = 0..=3;
/// The defined `PerformancePreset` values.
const PRESETS: std::ops::RangeInclusive<i64> = 0..=9;

pub(super) fn decode(
    fields: PowerModeFields<'_>,
    associations: &mut Associations,
) -> Option<Observation> {
    // Members managed would refuse make the whole status unreadable, whatever
    // the confirmation members say, so they are checked first.
    let modes = associations.decode(fields.windows_power_modes?)?;
    if fields
        .legacy
        .iter()
        .any(|legacy| legacy.is_some_and(|value| !value.is_null()))
    {
        return None;
    }
    let reason = match fields.observation_reason {
        None => 0,
        Some(value) => value.as_i64().filter(|reason| (0..=3).contains(reason))?,
    };
    // Managed defaults an absent list to empty and requires a present one to
    // be a list of defined subsystems, sorted and unique.
    let unconfirmed = match fields.unconfirmed_writes {
        None => Vec::new(),
        Some(value) => value
            .as_array()?
            .iter()
            .map(|subsystem| {
                subsystem
                    .as_i64()
                    .filter(|value| SUBSYSTEMS.contains(value))
            })
            .collect::<Option<Vec<_>>>()?,
    };
    if unconfirmed.windows(2).any(|pair| pair[0] >= pair[1]) {
        return None;
    }
    let performance = match fields.confirmed_performance {
        None | Some(Value::Null) => None,
        Some(value) => Some(value.as_i64().filter(|preset| PRESETS.contains(preset))?),
    };
    if fields.control_access != ACTIVE
        || !fields.device_verified
        || reason == RETAINED_POWER_SOURCE
        || unconfirmed.contains(&PERFORMANCE)
    {
        return None;
    }
    let supply = match fields.active_power_source {
        1 => Supply::Dc,
        0 | 2 => Supply::Ac,
        _ => return None,
    };
    let performance = u8::try_from(performance?).ok()?;
    Some(Observation {
        target: Target {
            supply,
            performance,
            mode: modes[usize::from(performance)],
        },
        source: u8::try_from(fields.active_power_source).ok()?,
    })
}

/// The last association list and its decoding. A status arrives at every
/// change and the list rarely changes, so an unchanged list is compared, not
/// serialized and parsed again.
#[derive(Default)]
pub(super) struct Associations(Option<(Value, Option<[Option<Mode>; 10]>)>);

impl Associations {
    fn decode(&mut self, value: &Value) -> Option<[Option<Mode>; 10]> {
        if let Some((last, modes)) = &self.0
            && last == value
        {
            return *modes;
        }
        let modes = associations(value);
        self.0 = Some((value.clone(), modes));
        modes
    }
}

/// Managed `WindowsPowerModeAssociations` by `PerformancePreset`. The managed
/// codec refuses a missing list and an undefined mode (`ValidWindowsPowerModes`).
fn associations(value: &Value) -> Option<[Option<Mode>; 10]> {
    let bytes = serde_json::to_vec(value).ok()?;
    let parsed = json::parse(&bytes).ok()?;
    let decoded = decode::windows_power_modes(&parsed).ok()?;
    let mut modes = [None; 10];
    for (mode, decoded) in modes.iter_mut().zip(decoded.0) {
        *mode = match decoded {
            None => None,
            Some(decoded) => Some(Mode::from_wire(decoded.0)?),
        };
    }
    Some(modes)
}
