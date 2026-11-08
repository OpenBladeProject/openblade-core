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
