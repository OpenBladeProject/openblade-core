//! Stateless selection and decoded performance eligibility. Host objects, lifetimes and device admission stay outside.

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
        // Both confirmed external sources admit every defined preset. Model
        // capability and native command admission remain host-owned.
        0 | 2 => matches!(preset, 0..=9),
        // Battery: BatterySaver, BatteryBalanced.
        1 => matches!(preset, 3 | 4),
        // Unknown external power and unmapped legacy values.
        _ => false,
    }
}

/// Fresh Windows AC presence whose 07/8C adapter query completed without a
/// classification (managed `PowerSource.ExternalUnknown`). It selects no
/// power-profile slot and is never promoted to full or limited power.
pub const UNCLASSIFIED_EXTERNAL: i32 = 3;

/// Explicit-override eligibility. On unclassified external power managed
/// admits only ordinary explicit overrides (`ProfileAutomationState`
/// `IsOrdinaryExternalOverride`: every AC preset except the battery presets);
/// the host rejects GPU tuning there. Profile eligibility stays
/// `performance_source_eligible`, which is frozen behind the FFI and keeps
/// refusing source 3.
pub fn override_source_eligible(preset: i32, source: i32) -> bool {
    if source == UNCLASSIFIED_EXTERNAL {
        matches!(preset, 0..=2 | 5..=9)
    } else {
        performance_source_eligible(preset, source)
    }
}

/// Whether a temporary override saved on class `saved` lapses once `observed`
/// is the fresh power class. A classified class retires a temporary saved on
/// any other class. Unclassified external power keeps an external temporary,
/// as managed keeps its automation source until the adapter is classified,
/// and retires only one saved on battery (managed expires transients once
/// when AC arrives without a classification). Other classes retire nothing.
/// A temporary saved while unclassified retires on the first classified
/// class: native keeps no Windows power generation, so it cannot prove what
/// managed `PreserveOverridesForExternalClassification` needs to keep it.
pub fn temporary_expired(saved: i32, observed: i32) -> bool {
    match observed {
        0..=2 => saved != observed,
        UNCLASSIFIED_EXTERNAL => saved == 1,
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
