//! Read-only Windows power evidence for the Service status projection.
//!
//! This module held an unwired lifecycle worker that coalesced power and
//! session callbacks into its own `Activity`. It was retired when the
//! power-setting supplier landed: the installed Service's one source of
//! low-power truth is the lifecycle epoch and gate in `origin_lifetime`,
//! which the SCM handler feeds directly, and nothing consumed the worker.

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) enum PowerSource {
    Battery,
    ExternalUnknown,
    Unknown,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) struct Observation {
    pub(super) power: PowerSource,
    pub(super) console_session: Option<u32>,
}

impl Observation {
    fn from_windows(ac_line_status: Option<u8>, console_session: Option<u32>) -> Self {
        Self {
            power: match ac_line_status {
                Some(0) => PowerSource::Battery,
                Some(1) => PowerSource::ExternalUnknown,
                _ => PowerSource::Unknown,
            },
            console_session,
        }
    }
}

#[cfg(all(not(test), feature = "lifecycle-evidence-compile-check"))]
#[path = "lifecycle/native.rs"]
pub(super) mod native;

#[cfg(test)]
mod tests;
