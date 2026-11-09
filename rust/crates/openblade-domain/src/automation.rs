//! Stateless selection of host-owned values. No value, lifetime or device admission crosses here.

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ValueOrigin {
    Profile,
    Confirmed,
    Temporary,
    Persistent,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Provenance {
    ConfirmedFirmwareState,
    PowerProfile,
    ApplicationProfile,
    TemporaryOverride,
    PersistentOverride,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Selection {
    pub origin: ValueOrigin,
    pub provenance: Provenance,
}

fn profile_source(from_application: bool) -> Provenance {
    if from_application {
        Provenance::ApplicationProfile
    } else {
        Provenance::PowerProfile
    }
}

/// Check a decoded performance candidate using the host's signed enum values.
pub fn performance_source_eligible(preset: i32, source: i32) -> bool {
    match source {
        // Plugged in: Silent, Balanced, Performance, Custom, Turbo,
        // MaximumPower, Gaming, Creator.
        0 => matches!(preset, 0..=2 | 5..=9),
        // Battery: BatterySaver, BatteryBalanced.
        1 => matches!(preset, 3 | 4),
        // USB-C: Balanced.
        2 => preset == 1,
        // Unknown external power and unmapped legacy values.
        _ => false,
    }
}

/// Select an already source-compatible performance override; scalar values stay host-owned.
pub fn performance(temporary: bool, persistent: bool, from_application: bool) -> Selection {
    overrides(temporary, persistent).unwrap_or(Selection {
        origin: ValueOrigin::Profile,
        provenance: profile_source(from_application),
    })
}

fn overrides(temporary: bool, persistent: bool) -> Option<Selection> {
    if persistent {
        Some(Selection {
            origin: ValueOrigin::Persistent,
            provenance: Provenance::PersistentOverride,
        })
    } else if temporary {
        Some(Selection {
            origin: ValueOrigin::Temporary,
            provenance: Provenance::TemporaryOverride,
        })
    } else {
        None
    }
}

/// Unknown external power uses observed lighting and ignores saved/manual intent.
/// Presence describes the container/value, never its scalar contents.
pub fn lighting(
    profile: bool,
    confirmed: bool,
    temporary: bool,
    persistent: bool,
    external_unknown: bool,
    from_application: bool,
) -> Selection {
    if external_unknown {
        return if confirmed {
            Selection {
                origin: ValueOrigin::Confirmed,
                provenance: Provenance::ConfirmedFirmwareState,
            }
        } else {
            Selection {
                origin: ValueOrigin::Profile,
                provenance: Provenance::PowerProfile,
            }
        };
    }
    overrides(temporary, persistent).unwrap_or(if profile {
        Selection {
            origin: ValueOrigin::Profile,
            provenance: profile_source(from_application),
        }
    } else {
        Selection {
            origin: ValueOrigin::Confirmed,
            provenance: Provenance::ConfirmedFirmwareState,
        }
    })
}
